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
    // Not readonly: Phase 7 scenario B. When the output device disappears, WASAPI fails the capture
    // with AUDCLNT_E_DEVICE_INVALIDATED and the recorder cannot be restarted -- a new one has to be
    // built on whatever device is the default render endpoint now.
    private WasapiRecorder capture;
    private readonly object captureSync = new();
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

    private readonly CaptureDataAvailableHandler handler;

    // Written on the WASAPI callback thread, read on the recording thread in Dispose, so these need
    // atomic or volatile access. They were plain fields, which is a data race. Note volatile cannot
    // apply to double, so the first-value latch uses Interlocked instead.
    private int bufferCount;
    private long totalBytes;
    private long firstBufferHundredNanos;

    // Gap filling. Packets are ~10 ms and the device clock drifts by tens of ppm, so anything below
    // the threshold is left alone on purpose: correcting it would nudge every packet and turn a
    // measured drift into injected error. The cap keeps a multi-minute silence from materialising as
    // one huge buffer.
    private const double GapThresholdSeconds = 0.04;
    private const double MaxGapFillSeconds = 600;
    private double anchorQpcSeconds;
    private long samplesSoFar;
    private int gapAnchorSet;
    private int BytesPerFrame => BlockAlign;

    // Diagnostic counters for the audio timeline. BytesPerFrame is the endpoint's real frame size
    // (channels * bits/8), recomputed per callback because the endpoint format is not known until
    // the first packet arrives.

    /// <summary>Bytes per PCM sample frame, from the endpoint's real format.</summary>
    public int BlockAlign
    {
        get { lock (captureSync) { return capture.WaveFormat.BlockAlign; } }
    }
    private int packetsSeen;
    private double silencePrependedMs;
    private volatile bool disposed;

    public AudioCapture(Stopwatch stopwatch, double videoStartSeconds)
    {
        this.stopwatch = stopwatch;
        this.videoStartSeconds = videoStartSeconds;

        handler = (buffer, flags, devicePosition, qpcPosition) => OnDataAvailable(buffer, qpcPosition);
        capture = CreateRecorder(handler);
        capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>Builds a loopback recorder on the CURRENT default render endpoint.</summary>
    private static WasapiRecorder CreateRecorder(CaptureDataAvailableHandler handler)
    {
        var render = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        // WasapiRecorderBuilder is the supported path in NAudio 3.1; the older
        // WasapiLoopbackCapture type delivered zero buffers when its WaveFormat was overridden.
        var recorder = new WasapiRecorderBuilder()
            .WithDevice(render)
            .WithLoopbackCapture()
            // NOTE: NAudio's WasapiRecorderBuilder exposes no way to change the buffer size (no
            // BufferMilliseconds, and WithLoopbackCapture takes none), so the 100 ms default stays.
            // It is not needed: the FfmpegAudioEncoder now anchors on qpcPosition rather than on arrival,
            // which removes the buffer age from the timeline instead of trying to shrink it.
            .Build();
        recorder.DataAvailable += handler;
        return recorder;
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

    public string Format
    {
        get
        {
            lock (captureSync)
            {
                var format = capture.WaveFormat;
                return $"{format.Encoding} {format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample} bit";
            }
        }
    }

    /// <summary>The endpoint's real sample rate, used to place each AAC frame on the audio timeline.</summary>
    public int SampleRate => capture.WaveFormat.SampleRate;

    /// <summary>The endpoint's real channel count, which the MFT path has to be configured for.</summary>
    public int Channels => capture.WaveFormat.Channels;

    /// <summary>
    /// Are the callback's bytes 32-bit float? The MFT wants integer PCM, so the encoder has to know
    /// which conversion to apply, and guessing produces noise rather than an error.
    /// </summary>
    public bool IsFloat32
    {
        get
        {
            var format = capture.WaveFormat;
            if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
                return extensible.SubFormat == IeeeFloatSubtype;
            return format.Encoding == WaveFormatEncoding.IeeeFloat;
        }
    }

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

        // First packet: the measured gap is REPORTED, but no silence is prepended.
        //
        // The silence used to be necessary: in phase 3 audio went straight into ffmpeg, which had no
        // per-packet timestamps, so the only way to move the audio origin onto the video origin was
        // to physically insert silence. Phase 4 changed that -- every audio packet now carries a
        // CaptureClockSeconds taken from the SAME shared Stopwatch as the video, and the ring slices
        // and the muxer align on those timestamps. The gap is therefore already expressed in the
        // timestamps, and prepending silence on top of it double-compensates: measured as a ~0.43 s
        // constant offset between the video flash and the audio beep on the ring-buffer export.
        if (Interlocked.Increment(ref packetsSeen) == 1)
        {
            var gapSeconds = stopwatch.Elapsed.TotalSeconds - videoStartSeconds;
            Console.WriteLine($"[sync] first WASAPI callback at {stopwatch.Elapsed.TotalSeconds:F3}s " +
                $"(video origin {videoStartSeconds:F3}s, gap {gapSeconds:F3}s; not prepended)");
            Console.WriteLine($"[start] first AUDIO packet: qpcPosition={qpcPosition} " +
                $"qpc={(qpcPosition / 1e7):F6} " +
                $"arrivalStopwatch={stopwatch.Elapsed.TotalSeconds:F3} " +
                $"arrivalQpc={(System.Diagnostics.Stopwatch.GetTimestamp() / 1e7):F6} " +
                $"bufferBytes={buffer.Length} " +
                $"lagBehindArrivalMs={((System.Diagnostics.Stopwatch.GetTimestamp() - qpcPosition) / 10_000.0):F1}");
        }

        // GAP FILLING. Loopback delivers NO packets at all while the system is silent, so a pause
        // would otherwise vanish from the timeline entirely and every later sound would be placed
        // that much too early (measured: a 20 s pause compressed a 60 s clip to 32 s of audio).
        //
        // The packet's qpcPosition is its real render time, on the same 10 MHz scale as
        // Stopwatch.GetTimestamp() (verified: both are 10 MHz from system boot). So a discontinuity is
        // the moment the packet's own time runs ahead of where the accumulated samples say it should
        // be. Only gaps beyond the threshold are filled; smaller differences are clock drift of a
        // few tens of ppm and are deliberately NOT corrected, or every packet would get nudged.
        var packetQpcSeconds = qpcPosition / 10_000_000.0;
        if (Interlocked.CompareExchange(ref gapAnchorSet, 1, 0) == 0)
        {
            anchorQpcSeconds = packetQpcSeconds;
            samplesSoFar = 0;
        }

        var expected = anchorQpcSeconds + (samplesSoFar / (double)SampleRate);
        var gap = packetQpcSeconds - expected;
        if (gap > GapThresholdSeconds)
        {
            var fillSeconds = Math.Min(gap, MaxGapFillSeconds);
            var fillSamples = (int)Math.Round(fillSeconds * SampleRate);
            if (fillSamples > 0)
            {
                Console.WriteLine($"[gap] {fillSeconds * 1000:F0} ms of silence inserted at " +
                    $"{stopwatch.Elapsed.TotalSeconds:F3}s (packet was {gap * 1000:F0} ms late)");
                sink?.Invoke(new byte[fillSamples * BytesPerFrame], qpcPosition);
                samplesSoFar += fillSamples;
            }
        }

        samplesSoFar += copy.Length / BytesPerFrame;

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

    /// <summary>
    /// Phase 7 scenario B: the output device was unplugged or the default changed, and WASAPI ended
    /// the stream with AUDCLNT_E_DEVICE_INVALIDATED (0x88890004).
    ///
    /// Unplugging headphones must not kill the capture or the process, so a new recorder is built on
    /// whatever is the default render endpoint now and the sink carries on. The gap is left to the
    /// existing gap-filling path in OnDataAvailable: it is detected from qpcPosition, which keeps
    /// the timeline honest, and re-anchoring here would discard the sample count so far.
    /// </summary>
    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (captureSync)
        {
            if (disposed)
                return;

            var failure = e.Exception;
            Console.WriteLine($"[wasapi] capture stopped: {failure?.GetType().Name ?? "no error"} " +
                $"({failure?.Message ?? "clean stop"})");

            if (failure is not null)
            {
                var hresult = failure.HResult & 0xFFFFFFFF;
                var invalidated = hresult == 0x88890004 || hresult == 0x88890008;
                Console.WriteLine(invalidated
                    ? "[wasapi] device invalidated -- reconnecting to the default render endpoint"
                    : "[wasapi] unexpected failure -- not reconnecting, capture stays dead");

                if (!invalidated)
                    return;
            }

            try
            {
                capture.DataAvailable -= handler;
                capture.RecordingStopped -= OnRecordingStopped;
                try
                {
                    capture.Dispose();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    // Already torn down by the OS; nothing left to release.
                }

                var replacement = CreateRecorder(handler);
                replacement.RecordingStopped += OnRecordingStopped;
                capture = replacement;
                capture.StartRecording();
                Console.WriteLine($"[wasapi] reconnected, format now [{Format}]");
            }
            catch (Exception ex)
            {
                // No render endpoint at all (all outputs disabled). Leave the capture dead but keep
                // the process alive: video recording continues, and the video ring still exports.
                Console.WriteLine($"[wasapi] reconnect failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (captureSync)
        {
            if (disposed)
                return;
            disposed = true;

            capture.DataAvailable -= handler;
            capture.RecordingStopped -= OnRecordingStopped;
            try
            {
                capture.StopRecording();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // Recording already stopped, or the endpoint disappeared; nothing left to flush.
            }

            capture.Dispose();
        }

        Console.WriteLine(
            $"Audio: {bufferCount} buffers, {totalBytes} bytes, started at {Interlocked.Read(ref firstBufferHundredNanos) / 10_000.0:F3}s on the capture clock.");
    }
}

