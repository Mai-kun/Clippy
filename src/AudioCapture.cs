using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clippy;

/// <summary>
/// System audio via WASAPI loopback, on the same capture clock as the video.
///
/// ponytail: raw PCM goes to ffmpeg's audio stdin, so there is no WAV header and nothing to patch.
/// The endpoint's own mix format is passed through to ffmpeg verbatim rather than resampling: the
/// smoke test shows it is float32 48 kHz stereo here, and converting it would add a resampler and
/// its own latency to the A/V comparison. Upgrade path: insert a resampler if a non-48 kHz device
/// must be supported, and keep the -f/-ar/-ac arguments derived from Format.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly Stopwatch stopwatch;
    private readonly WasapiRecorder capture;
    // Wallclock reading (same Stopwatch as the video) of the first video frame written. Audio that
    // starts later must be shifted back onto the video's origin, otherwise the muxer rebases each
    // stream to its own zero and the real A/V offset is lost. Measured per run, never hardcoded.
    private readonly double videoStartSeconds;

    /// <summary>
    /// Set after construction, read on the WASAPI callback thread. volatile because the assignment
    /// happens on the recording thread while the callback may already be running.
    /// </summary>
    private Action<byte[], long>? sink;
    public Action<byte[], long>? Sink
    {
        get => sink;
        set => sink = value;
    }

    private readonly StreamWriter? timingLog;
    private readonly CaptureDataAvailableHandler handler;

    // Written on the WASAPI callback thread, read on the recording thread in Dispose, so these need
    // atomic or volatile access. They were plain fields, which is a data race. Note volatile cannot
    // apply to double, so the first-value latch uses Interlocked instead.
    private int bufferCount;
    private long totalBytes;
    private long firstBufferHundredNanos;
    private int packetsSeen;
    private double silencePrependedMs;
    private volatile bool disposed;

    public AudioCapture(Stopwatch stopwatch, double videoStartSeconds)
    {
        this.stopwatch = stopwatch;
        this.videoStartSeconds = videoStartSeconds;
        this.timingLog = null;

        var render = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        // WasapiRecorderBuilder is the supported path in NAudio 3.1; the older
        // WasapiLoopbackCapture type delivered zero buffers when its WaveFormat was overridden.
        capture = new WasapiRecorderBuilder()
            .WithDevice(render)
            .WithLoopbackCapture()
            .Build();
        // Stored as a field so Dispose can unsubscribe the exact same delegate instance.
        handler = (buffer, flags, devicePosition, qpcPosition) => OnDataAvailable(buffer, qpcPosition);
        capture.DataAvailable += handler;
    }

    /// <summary>ffmpeg input arguments for the endpoint's real PCM layout, e.g. "-f f32le -ar 48000 -ac 2".</summary>
    public IEnumerable<string> FormatArguments
    {
        get
        {
            var format = capture.WaveFormat;
            var sampleFormat = ToFfmpegSampleFormat(format);
            return [$"-f", sampleFormat, "-ar", format.SampleRate.ToString(), "-ac", format.Channels.ToString()];
        }
    }

    /// <summary>
    /// Maps a WASAPI mix format onto an ffmpeg raw sample format. Loopback endpoints usually report
    /// WAVE_FORMAT_EXTENSIBLE with a 32-bit float sub-format, which has no direct ffmpeg name, so the
    /// sub-format GUID decides. Fails loudly rather than guessing: a wrong -f silently plays noise.
    /// </summary>
    private static string ToFfmpegSampleFormat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
        {
            // KSDATAFORMAT_SUBTYPE_PCM and _IEEE_FLOAT. Written out rather than taken from a NAudio
            // helper: the GUIDs are fixed by the Windows SDK and must match or ffmpeg reads noise.
            if (extensible.SubFormat == IeeeFloatSubtype)
                return "f32le";

            if (extensible.SubFormat == PcmSubtype)
                return format.BitsPerSample switch
                {
                    > 16 => "s32le",
                    _ => "s16le",
                };

            throw new NotSupportedException($"Unsupported extensible sub-format {extensible.SubFormat}.");
        }

        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => "f32le",
            WaveFormatEncoding.Pcm when format.BitsPerSample > 16 => "s32le",
            WaveFormatEncoding.Pcm => "s16le",
            _ => throw new NotSupportedException($"Unsupported WASAPI sample encoding {format.Encoding}."),
        };
    }

    public string Format => $"{capture.WaveFormat.Encoding} {capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, {capture.WaveFormat.BitsPerSample} bit";

    /// <summary>Isolates the loopback from ffmpeg: proves whether WASAPI delivers buffers at all.</summary>
    public static int RunAudioProbe()
    {
        var stopwatch = Stopwatch.StartNew();
        long bytes = 0;
        var buffers = 0;
        var firstMs = -1.0;
        using var capture = new AudioCapture(stopwatch, 0)
        {
            Sink = (buffer, qpc) =>
            {
                if (firstMs < 0)
                    firstMs = stopwatch.Elapsed.TotalMilliseconds;
                Interlocked.Add(ref bytes, buffer.Length);
                Interlocked.Increment(ref buffers);
            },
        };
        Console.WriteLine($"format: {capture.Format}");
        Console.WriteLine($"ffmpeg input: {string.Join(' ', capture.FormatArguments)}");
        capture.Start();
        Thread.Sleep(3000);
        capture.Dispose();
        Console.WriteLine($"buffers={buffers} bytes={bytes} firstBufferAt={firstMs:F1} ms");
        return buffers > 0 ? 0 : 1;
    }

    public void Start() => capture.StartRecording();

    // WasapiRecorder hands over a ReadOnlySpan that is only valid for this callback, plus the
    // packet's QPC position. The span is copied because the queued write outlives the callback.
    private void OnDataAvailable(ReadOnlySpan<byte> buffer, long qpcPosition)
    {
        // Captured in capture-only mode too, where nothing is written anywhere: the callback still
        // runs on the MMCSS thread, and that thread's activity is what this trace has to expose.
        EventLog.Mark("AUDIO_CB", buffer.Length.ToString());
        var copy = buffer.ToArray();
        Interlocked.Increment(ref bufferCount);
        Interlocked.Add(ref totalBytes, copy.Length);
        if (firstBufferHundredNanos == 0)
            Interlocked.CompareExchange(ref firstBufferHundredNanos, qpcPosition, 0);

        // First packet: emit the measured gap as silence so the audio origin lands on the video's
        // first frame. Derived from the shared Stopwatch every run, so it tracks this run's real start
        // difference rather than a hardcoded constant. If audio began BEFORE video there is nothing
        // to pre-pend; that case is reported instead of passing unnoticed.
        if (Interlocked.Increment(ref packetsSeen) == 1)
        {
            var gapSeconds = stopwatch.Elapsed.TotalSeconds - videoStartSeconds;
            Console.WriteLine($"[sync] first WASAPI callback at {stopwatch.Elapsed.TotalSeconds:F3}s " +
                $"(video origin {videoStartSeconds:F3}s, gap {gapSeconds:F3}s)");
            PrependSilence(gapSeconds);
            if (gapSeconds < 0)
                Console.WriteLine($"Audio: started {gapSeconds * 1000:F1} ms BEFORE video; cannot shift back.");
        }

        // The QPC position is the packet's true capture time on the same system clock that
        // -use_wallclock_as_timestamps reads, which is what keeps audio on the video timeline.
        sink?.Invoke(copy, qpcPosition);
    }

    /// <summary>Feeds leading silence so the stream starts `seconds` earlier than the first real packet.</summary>
    private void PrependSilence(double seconds)
    {
        if (seconds <= 0)
            return;

        var format = capture.WaveFormat;
        var frameSize = format.Channels * (format.BitsPerSample / 8);
        var frames = (int)Math.Round(seconds * format.SampleRate);
        if (frames <= 0)
            return;

        var silence = new byte[frames * frameSize]; // all-zero == digital silence for PCM and float
        silencePrependedMs += seconds * 1000;
        sink?.Invoke(silence, 0);
        Console.WriteLine($"Audio: prepended {seconds * 1000:F1} ms of silence to align with the video origin.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        capture.DataAvailable -= handler;
        try
        {
            capture.StopRecording();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Recording already stopped, or the endpoint disappeared; nothing left to flush.
        }

        capture.Dispose();
        Console.WriteLine(
            $"Audio: {bufferCount} buffers, {totalBytes} bytes, started at {Interlocked.Read(ref firstBufferHundredNanos) / 10_000.0:F3}s on the capture clock.");
    }
}
