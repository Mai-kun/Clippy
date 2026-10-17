using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Clippy;

/// <summary>
/// System audio via WASAPI loopback, on the same capture clock as the video.
///
/// ponytail: raw PCM goes to the encoder, so there is no WAV header and nothing to patch. The
/// endpoint's own mix format is passed through verbatim rather than resampled: typical endpoints are
/// float32 48 kHz stereo, and 44.1 kHz or multichannel devices are handled by the MFT path (rate
/// chosen per device, downmix to stereo) instead of a resampler here. The -f/-ar/-ac arguments stay
/// derived from Format for the ffmpeg fallback.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    // WAVEFORMATEX is 18 bytes; wValidBitsPerSample is the first field of the extensible tail that
    // follows it in memory (WaveFormatExtensible is sequential, Pack=2).
    private const int ValidBitsOffset = 18;

    private readonly Stopwatch stopwatch;
    // Not readonly: Phase 7 scenario B. When the output device disappears, WASAPI fails the capture
    // with AUDCLNT_E_DEVICE_INVALIDATED and the recorder cannot be restarted -- a new one has to be
    // built on the configured device (the current default endpoint when none was chosen).
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

    /// <summary>
    /// The endpoint the user asked for (config AudioDevice / tray Audio Device menu): null, empty
    /// or "default" means the system default, anything else is an endpoint ID or friendly name.
    /// Kept so a reconnect after a device loss returns to the CHOSEN device instead of silently
    /// following the system default into a monitor or a virtual cable.
    /// </summary>
    private string? preferredDevice;

    /// <summary>Set when the endpoint's volume is software-only; see CreateRecorder.</summary>
    private volatile AudioEndpointVolume? softwareVolume;

    // ponytail: 40 dB cap so a near-zero slider does not turn the noise floor into a roar.
    private const float MaxVolumeCompensationDb = 40f;

    public AudioCapture(Stopwatch stopwatch, double videoStartSeconds, string? targetDeviceIdOrName = null)
    {
        this.stopwatch = stopwatch;
        this.videoStartSeconds = videoStartSeconds;
        preferredDevice = targetDeviceIdOrName;

        handler = (buffer, flags, devicePosition, qpcPosition) => OnDataAvailable(buffer, qpcPosition);
        capture = CreateRecorder(handler, preferredDevice, "capture start");
        capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>
    /// Builds a loopback recorder on the chosen render endpoint. <paramref name="targetDeviceIdOrName"/>
    /// is the raw config/tray value: null, empty or "default" follows the system default output;
    /// anything else is matched against the active endpoints' ID or friendly name. An unknown device
    /// warns and falls back to the default rather than killing the capture -- a headset unplugged
    /// between the menu being drawn and the click must not end the recording in silence.
    /// </summary>
    private WasapiRecorder CreateRecorder(CaptureDataAvailableHandler handler, string? targetDeviceIdOrName, string reason)
    {
        var render = ResolveDevice(targetDeviceIdOrName);
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

        // An endpoint with no hardware volume (common on USB headsets) applies the Windows slider in
        // software BEFORE loopback, so the clip inherited the listening volume: 23% on the slider was
        // -21 dB in every clip. Kept only for those endpoints; hardware-volume ones already loop back
        // at full level and must not be boosted.
        var endpoint = render.AudioEndpointVolume;
        softwareVolume = !endpoint.HardwareSupport.HasFlag(EEndpointHardwareSupport.Volume) && IsFloat(recorder.WaveFormat)
            ? endpoint
            : null;

        // The one line that answers "what exactly is being captured". Every audio bug report starts
        // with a device whose rate or channel count nobody logged, and a 44.1 kHz or 5.1 endpoint
        // looks identical to a 48 kHz stereo one from the outside. That line, plus the full endpoint
        // list, goes to disk in logs/audio.log -- see LogDeviceDiagnostic.
        LogDeviceDiagnostic(reason, render, recorder.WaveFormat);
        return recorder;
    }

    /// <summary>
    /// The endpoint behind the user's choice. "default" (or nothing) is the system default render
    /// endpoint; anything else is matched, case-insensitively, against the ID first (stable across
    /// reboots and Windows language changes) and then the friendly name (what a hand-edited config
    /// may hold). No match falls back to the default with a warning: a stale value in the config
    /// must cost at most one device, not the whole soundtrack.
    /// </summary>
    private static MMDevice ResolveDevice(string? targetDeviceIdOrName)
    {
        var enumerator = new MMDeviceEnumerator();

        if (string.IsNullOrWhiteSpace(targetDeviceIdOrName)
            || string.Equals(targetDeviceIdOrName, "default", StringComparison.OrdinalIgnoreCase))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        using (var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            foreach (var dev in devices)
            {
                if (string.Equals(dev.ID, targetDeviceIdOrName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(dev.FriendlyName, targetDeviceIdOrName, StringComparison.OrdinalIgnoreCase))
                {
                    return dev;
                }
            }
        }

        Console.WriteLine(
            $"[wasapi] audio device '{targetDeviceIdOrName}' is not among the active render endpoints; falling back to the system default.");
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    /// <summary>All active sound-output endpoints, as (ID, friendly name) -- for the tray menu and the log.</summary>
    public static List<(string Id, string Name)> GetActivePlaybackDevices()
    {
        var result = new List<(string, string)>();
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var dev in devices)
        {
            result.Add((dev.ID, dev.FriendlyName));
        }
        return result;
    }

    /// <summary>
    /// The audio diagnostic trail, written at capture start and at every device switch: which
    /// endpoint was chosen (name and ID), the stream format it delivers, and every OTHER active
    /// render endpoint. On a machine where the browser plays into the headphones while the default
    /// endpoint is a monitor or a virtual cable, this file is the whole bug report -- and the
    /// console is hidden once the recorder lives in the tray, so it is also mirrored through
    /// UpdateService.LogDiagnostic (update.log + event log + crash log).
    /// </summary>
    private static void LogDeviceDiagnostic(string reason, MMDevice device, WaveFormat format)
    {
        var isFloat = IsFloat(format);
        var sampleKind = isFloat ? "float" : "int";
        // The line every "no sound" bug report needs: rate, channels, container width and whether the
        // samples are float. Valid bits appear when they differ from the container, because a 24-bit
        // studio format padded into a 32-bit container would otherwise read as a plain 32-bit one.
        var validBits = ReadValidBits(format);
        var validNote = !isFloat && validBits != format.BitsPerSample ? $", valid={validBits}" : "";
        Console.WriteLine(
            $"[audio] {reason}: '{device.FriendlyName}' ({device.ID}), " +
            $"{format.SampleRate}Hz, {format.Channels}ch, {format.BitsPerSample}bit{validNote} (Float={isFloat})");

        var lines = new List<string>
        {
            $"=== {reason} ===",
            $"Selected device: '{device.FriendlyName}' ({device.ID})",
            $"Stream format: {format.SampleRate} Hz, {format.Channels} ch, {format.BitsPerSample} bit {sampleKind} ({format.Encoding})",
            "Other active render endpoints:",
        };

        var others = 0;
        foreach (var (id, name) in GetActivePlaybackDevices())
        {
            if (string.Equals(id, device.ID, StringComparison.OrdinalIgnoreCase))
                continue;

            lines.Add($"  - '{name}' ({id})");
            others++;
        }
        if (others == 0)
            lines.Add("  (none)");

        try
        {
            var logDir = LogPaths.LogsDirectory;
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "audio.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {string.Join(Environment.NewLine, lines)}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // A diagnostic that cannot be written must never take the capture down with it.
        }

        UpdateService.LogDiagnostic(
            $"Audio ({reason}): '{device.FriendlyName}' ({device.ID}), {format.SampleRate} Hz, " +
            $"{format.Channels} ch, {format.BitsPerSample} bit {sampleKind}, {others} other render endpoints");
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
                    24 => "s24le",   // packed 3-byte samples, exactly as the endpoint delivers them
                    > 16 => "s32le",
                    _ => "s16le",
                };

            throw new NotSupportedException($"Unsupported extensible sub-format {extensible.SubFormat}.");
        }

        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => "f32le",
            WaveFormatEncoding.Pcm when format.BitsPerSample == 24 => "s24le",
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
    public bool IsFloat32 => IsFloat(capture.WaveFormat);

    /// <summary>Sub-format decides under WAVE_FORMAT_EXTENSIBLE; the encoding alone is not enough.</summary>
    private static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.Extensible && format is WaveFormatExtensible extensible)
            return extensible.SubFormat == IeeeFloatSubtype;
        return format.Encoding == WaveFormatEncoding.IeeeFloat;
    }

    /// <summary>Container width in bits: 24 for a packed studio format, 32 for float32 or 24-in-32.</summary>
    public int BitsPerSample => capture.WaveFormat.BitsPerSample;

    /// <summary>Bits of real precision in one sample (WAVEFORMATEXTENSIBLE's wValidBitsPerSample).</summary>
    public int ValidBitsPerSample => ReadValidBits(capture.WaveFormat);

    /// <summary>
    /// wValidBitsPerSample, or BitsPerSample for formats that state no separate precision. NAudio
    /// keeps the field private, so it is read from the marshalled layout: WaveFormatExtensible is
    /// sequential (Pack=2) and wValidBitsPerSample sits right after WAVEFORMATEX's 18 bytes.
    /// </summary>
    private static int ReadValidBits(WaveFormat format)
    {
        if (format is not WaveFormatExtensible extensible)
            return format.BitsPerSample;

        var handle = GCHandle.Alloc(extensible, GCHandleType.Pinned);
        try
        {
            var validBits = Marshal.ReadInt16(handle.AddrOfPinnedObject(), ValidBitsOffset);
            // 0 appears on drivers that leave the field blank; the container width is the honest answer.
            return validBits > 0 ? validBits : format.BitsPerSample;
        }
        finally
        {
            handle.Free();
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
    /// <summary>
    /// Divides the Windows slider back out of a software-volume endpoint's float samples, so the clip
    /// records what the application played rather than what the user chose to hear. Read per packet
    /// because the slider can move during a recording. Integer mix formats are left alone: shared-mode
    /// loopback is float32 on every endpoint seen so far.
    /// </summary>
    private void UndoSoftwareVolume(byte[] samples)
    {
        var volume = softwareVolume;
        if (volume is null)
            return;

        float levelDb;
        try
        {
            if (volume.Mute)
                return;
            levelDb = volume.MasterVolumeLevel;
        }
        catch (COMException)
        {
            return; // device going away; the reconnect path rebuilds everything
        }

        if (levelDb >= -0.01f)
            return;

        var gain = MathF.Pow(10f, Math.Min(-levelDb, MaxVolumeCompensationDb) / 20f);
        var floats = MemoryMarshal.Cast<byte, float>(samples.AsSpan());
        for (var i = 0; i < floats.Length; i++)
            floats[i] *= gain;
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, long qpcPosition)
    {
        // Captured in capture-only mode too, where nothing is written anywhere: the callback still
        // runs on the MMCSS thread, and that thread's activity is what this trace has to expose.
        EventLog.Mark("AUDIO_CB", buffer.Length.ToString());
        var copy = buffer.ToArray();
        UndoSoftwareVolume(copy);
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
    /// the CHOSEN device (the current default endpoint when none was selected) and the sink carries
    /// on. The gap is left to the existing gap-filling path in OnDataAvailable: it is detected from
    /// qpcPosition, which keeps the timeline honest, and re-anchoring here would discard the sample
    /// count so far.
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
                    ? "[wasapi] device invalidated -- reconnecting to the preferred render endpoint"
                    : "[wasapi] unexpected failure -- not reconnecting, capture stays dead");

                if (!invalidated)
                    return;
            }

            try
            {
                DetachRecorder();

                var replacement = CreateRecorder(handler, preferredDevice, "reconnect after device loss");
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

    /// <summary>
    /// Retargets the loopback onto another render endpoint while the recording keeps running --
    /// the tray's Audio Device menu calls this after the config has been updated.
    ///
    /// The timeline anchors (anchorQpcSeconds, samplesSoFar, gapAnchorSet) are deliberately left
    /// untouched, and so is the caller's masterZero: qpcPosition is one global hardware clock
    /// shared by every endpoint, so packets from the new device land on the same timeline, and the
    /// existing gap-filling path in OnDataAvailable absorbs the switch as if the stream had paused.
    /// </summary>
    public void SwitchDevice(string targetDeviceIdOrName)
    {
        lock (captureSync)
        {
            if (disposed)
                return;

            var previousTarget = preferredDevice;
            var oldFormat = capture.WaveFormat;

            try
            {
                DetachRecorder();
                preferredDevice = targetDeviceIdOrName;
                capture = CreateRecorder(handler, preferredDevice, "device switch");
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                // The audio encoder was configured with the FIRST device's layout. A device that
                // disagrees on rate or channels would be fed frames it was never set up for, which
                // sounds wrong rather than failing loudly -- so say it where the user will look.
                var newFormat = capture.WaveFormat;
                if (newFormat.SampleRate != oldFormat.SampleRate || newFormat.Channels != oldFormat.Channels)
                {
                    var warning =
                        $"Audio: switched device but the mix format changed " +
                        $"({oldFormat.SampleRate} Hz/{oldFormat.Channels} ch -> {newFormat.SampleRate} Hz/{newFormat.Channels} ch); " +
                        "the encoder keeps the layout it started with, audio after the switch may sound off.";
                    Console.WriteLine(warning);
                    UpdateService.LogDiagnostic(warning);
                }
            }
            catch (Exception ex)
            {
                // The menu snapshot can go stale between opening and clicking (headset unplugged).
                // The old recorder is already gone, so rebuild the previous device rather than leave
                // the recording without any sound at all.
                preferredDevice = previousTarget;
                Console.WriteLine(
                    $"[wasapi] switch to '{targetDeviceIdOrName}' failed: {ex.GetType().Name}: {ex.Message}; restoring '{previousTarget ?? "default"}'");
                try
                {
                    capture = CreateRecorder(handler, preferredDevice, "fallback after failed switch");
                    capture.RecordingStopped += OnRecordingStopped;
                    capture.StartRecording();
                }
                catch (Exception fallbackEx)
                {
                    UpdateService.LogDiagnostic(
                        $"Audio: switch to '{targetDeviceIdOrName}' failed ({ex.GetType().Name}: {ex.Message}) " +
                        $"and the fallback device is gone too ({fallbackEx.GetType().Name}); audio capture is dead until restart.");
                }
            }
        }
    }

    /// <summary>Unsubscribes and releases the current recorder; the caller holds captureSync.</summary>
    private void DetachRecorder()
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

