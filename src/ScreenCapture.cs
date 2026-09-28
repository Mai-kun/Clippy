using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Clippy;

internal sealed class ScreenCapture : IDisposable
{
    private const int FrameSaveInterval = 30;
    private const int FramePoolBufferCount = 2;

    private readonly object sync = new();
    private readonly ID3D11Device d3d11Device;
    private readonly ID3D11DeviceContext d3d11Context;
    private readonly IDirect3DDevice direct3DDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;
    private readonly string outputDirectory;
    private readonly ClippyConfig config;

    /// <summary>The settings this recorder was started with, for the tray menu to reuse.</summary>
    public ClippyConfig Config => config;
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    // ONE hardware zero for both tracks, taken at construction, before WGC and WASAPI exist.
    //
    // The Stopwatch origin used to be re-derived lazily at first use, long after the D3D and audio
    // devices had been created, so the "zero" sat hundreds of milliseconds away from when the
    // recording actually began, and that entire delay was folded into every timestamp. Both tracks
    // now subtract this single value from their own native clocks -- the video from the compositor's
    // SystemRelativeTime, the audio from WASAPI's qpcPosition -- so they share one physical instant
    // and no C# dispatch delay can leak into either.
    private readonly double masterZeroSeconds =
        System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    // Video mode: ffmpeg is started lazily on the first frame, when the true capture size is known.
    // ffmpeg stamps frames on read, so no warm-up buffer and no frame-rate guess are needed.
    private string? videoPath;
    private string? videoEncoder;
    private string fpsMode = "passthrough";
    private bool withAudio;
    private AudioCapture? audio;
    private AudioEncoder? audioEncoder;
    private string? videoOnlyPath;
    private string? audioOnlyPath;
    private bool audioCaptureOnly;
    private int discardedAudioBuffers;
    private double videoStartSeconds;
    /// <summary>
    /// Writes the last <paramref name="durationSeconds"/> still held in the rings to an MP4 file.
    /// Called from the phase 6 hotkey worker thread, so it must not assume it owns the rings.
    /// </summary>
    public string? ExportClip(double durationSeconds)
    {
        if (videoRing is null || audioRing is null || encoder?.Sps is null || encoder.Pps is null)
        {
            Console.WriteLine("Export: nothing to export yet (recorder not started or no SPS/PPS seen).");
            return null;
        }

        // Snapshot the clock first, then both slices. Slice copies, so the packets stay valid even
        // though the drain threads keep pushing while this runs.
        var to = stopwatch.Elapsed.TotalSeconds;
        var from = to - durationSeconds;

        // STALENESS. If the pipeline died -- ffmpeg exited, NVENC reset, the frame pool stalled --
        // the ring simply stops filling and still looks healthy. Without this check F9 happily cuts a
        // clip out of frozen frames and the user gets a video that plays but shows nothing new.
        if (videoRing is { Count: > 0 })
        {
            var staleFor = to - videoRing.NewestSeconds;
            if (staleFor > MaxCaptureStalenessSeconds)
            {
                Console.WriteLine($"Export: capture pipeline FROZEN -- the newest frame is " +
                    $"{staleFor:F1}s old (limit {MaxCaptureStalenessSeconds:F0}s). " +
                    "Refusing to export stale frames; the encoder or the capture source has stopped.");
                return null;
            }
        }

        // FREE SPACE. A 3-minute clip is a few hundred MB, and a half-written mp4 is worse than
        // no clip, so check before building anything.
        try
        {
            var target = Path.GetFullPath(outputDirectory);
            var root = Path.GetPathRoot(target);
            if (root is not null)
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (free < MinFreeSpaceBytes)
                {
                    Console.WriteLine($"Export: not enough free space on {root} -- " +
                        $"{free / 1048576:F0} MB available, " +
                        $"{MinFreeSpaceBytes / 1048576:F0} MB required. Clip not written.");
                    return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // If the free-space check itself cannot run, do not block a working export over it.
            Console.WriteLine($"Export: free-space check skipped ({ex.GetType().Name}: {ex.Message}).");
        }

        var videoPackets = videoRing.Slice(from, to);
        var audioPackets = audioRing.Slice(from, to);

        // Raw ring data first, so a bad slice is visible here and not as an ffprobe error later.
        var (captured, accessUnits) = encoder.TimingCounts;

        // A few frames still in flight is normal: the drain thread reads stdout in chunks and a
        // couple of access units are always behind when the export happens mid-recording. Only a
        // larger gap means the encoder actually lost or reordered frames, which would break the
        // FIFO pairing and invalidate every timestamp in the clip.
        const int inFlightTolerance = 5;
        var unpaired = captured - accessUnits;
        Console.WriteLine(unpaired > inFlightTolerance
            ? $"Export: frames in {captured}, access units out {accessUnits} -- MISMATCH of {unpaired}, " +
              "the encoder lost or reordered frames and these timestamps cannot be trusted"
            : $"Export: frames in {captured}, access units out {accessUnits} (1:1 within tolerance)");

        // DELIBERATE CHOICE: an empty audio slice produces a VIDEO-ONLY mp4, not a refusal. Loopback
        // capture intermittently delivers zero buffers, and a user who asked to save a clip wants the
        // clip; discarding the video because audio was missing is the worse failure. An empty video
        // slice is still fatal -- there is nothing to save at all.
        if (videoPackets.Count == 0)
        {
            Console.WriteLine("Export: video slice is empty, nothing to write.");
            return null;
        }

        var audioMissing = audioPackets.Count == 0;
        Console.WriteLine($"Export: video slice {videoPackets.Count} pkts, " +
            $"clock [{videoPackets[0].CaptureClockSeconds:F3} .. {videoPackets[^1].CaptureClockSeconds:F3}] s, " +
            $"first is keyframe: {(videoPackets[0].IsKeyframe ? "True" : "False")}");

        if (audioMissing)
        {
            Console.WriteLine("Export: no audio in range; writing a VIDEO-ONLY clip on purpose.");
        }
        else
        {
            Console.WriteLine($"Export: audio slice {audioPackets.Count} pkts, " +
                $"clock [{audioPackets[0].CaptureClockSeconds:F3} .. {audioPackets[^1].CaptureClockSeconds:F3}] s");
        }

        // KNOWN AND EXPECTED: the clip is advanced to the first keyframe at or after the requested
        // start, because a slice beginning on a P/B frame decodes as garbage until the next I-frame.
        // With a ~1 s GOP this costs about a second of video at the head of every export, which is
        // why an export's video duration runs shorter than its audio. This is NOT A/V drift: do not
        // "fix" it, and do not read it as desynchronisation.
        var start = videoPackets.FindIndex(p => p.IsKeyframe);
        if (start < 0)
        {
            Console.WriteLine("Export: no keyframe in the requested window, refusing to write.");
            return null;
        }

        var writer = new Mp4Writer();
        writer.SetParameterSets(encoder.Sps, encoder.Pps);
        writer.SetDimensions(videoWidth, videoHeight);

        // ONE shared zero for both tracks, not a per-track origin. See TrackAligner for why, and for
        // why neither track is privileged: whichever starts later defines t=0 and the other is cut.
        var videoTimes = new List<double>();
        foreach (var packet in videoPackets.Skip(start))
        {
            var nalType = packet.Span[0] & 0x1F;
            if (nalType is >= 1 and <= 5)
            {
                videoTimes.Add(packet.CaptureClockSeconds);
            }
        }

        var alignment = TrackAligner.Align(videoTimes, audioPackets.Select(p => p.CaptureClockSeconds).ToList());

        // Full provenance of the alignment, so a shift can be attributed to a specific stage instead
        // of guessed at. Everything here is an absolute capture time on the shared clock.
        var rawVideoStart = videoPackets[0].CaptureClockSeconds;
        var rawVideoEnd = videoPackets[^1].CaptureClockSeconds;
        var rawAudioStart = audioPackets.Count > 0 ? audioPackets[0].CaptureClockSeconds : double.NaN;
        var rawAudioEnd = audioPackets.Count > 0 ? audioPackets[^1].CaptureClockSeconds : double.NaN;
        var videoSnappedStart = videoTimes.Count > 0 ? videoTimes[0] : double.NaN;

        Console.WriteLine($"[align] video slice raw  [{rawVideoStart:F3} .. {rawVideoEnd:F3}] s");
        Console.WriteLine($"[align] video after keyframe snap: {videoSnappedStart:F3} s " +
            $"(advanced {(videoSnappedStart - rawVideoStart) * 1000:F0} ms from the slice start)");
        Console.WriteLine($"[align] audio slice raw  [{rawAudioStart:F3} .. {rawAudioEnd:F3}] s");
        Console.WriteLine($"[align] t0 (shared zero) = {alignment.Zero:F3} s, " +
            $"dropped video {alignment.DroppedVideo * 1000:F0} ms, audio {alignment.DroppedAudio * 1000:F0} ms");
        Console.WriteLine($"[align] first sample to Mp4Writer: video {FirstOrNaN(alignment.VideoTimes):F3} s, " +
            $"audio {FirstOrNaN(alignment.AudioTimes):F3} s");

        Console.WriteLine($"Export: zero at {alignment.Zero:F3}s, " +
            $"dropped {alignment.DroppedVideo * 1000:F0} ms of video, {alignment.DroppedAudio * 1000:F0} ms of audio");

        // One MP4 sample per access unit. A VCL NAL is one picture; the non-VCL NALs that PRECEDE it
        // belong to it, so they buffer until the VCL arrives. Emitting on the VCL is what keeps the
        // boundaries honest: a non-VCL NAL after a VCL already starts the NEXT access unit.
        var pending = new List<ReadOnlyMemory<byte>>();
        var timeIndex = 0;
        foreach (var packet in videoPackets.Skip(start))
        {
            var nalType = packet.Span[0] & 0x1F;
            var isVcl = nalType is >= 1 and <= 5;
            pending.Add(packet.Data.AsMemory(0, packet.Length));

            if (isVcl && timeIndex < alignment.VideoTimes.Count)
            {
                writer.AddVideoSample(pending, alignment.VideoTimes[timeIndex++]);
                pending.Clear();
            }
            else if (isVcl)
            {
                pending.Clear();
            }
        }

        // Build the audio track's packets and their timestamps in ONE pass over one source of truth.
        // These used to be two independent lists (all audioPackets paired with all AudioTimes), so
        // filtering one without the other silently discarded or mislabelled samples -- a change to
        // the drop rule had no effect on the bytes actually written.
        //
        // The clip's zero is ALWAYS the video keyframe: H.264 cannot be decoded from anything
        // earlier, so no clip can begin before it, and the audio track has to be made to agree.
        // Audio recorded before the keyframe is DROPPED; silence is inserted only when the first
        // surviving audio packet is still after the keyframe, which is a real gap.
        var t0 = alignment.Zero;
        var finalAudio = new List<byte[]>();
        var finalAudioTimes = new List<double>();
        var droppedAudioPackets = 0;
        foreach (var packet in audioPackets)
        {
            var relative = packet.CaptureClockSeconds - t0;
            if (relative < 0)
            {
                droppedAudioPackets++;
                continue;
            }

            finalAudio.Add(packet.Span.ToArray());
            finalAudioTimes.Add(relative);
        }

        var leadIn = finalAudioTimes.Count > 0 ? finalAudioTimes[0] : 0.0;
        Console.WriteLine($"Export: t0 = video keyframe at {t0:F3}s; dropped {droppedAudioPackets} " +
            $"audio packets recorded before it; first surviving audio at {leadIn * 1000:F1} ms");

        if (leadIn > 0.0005 && finalAudio.Count > 0)
        {
            // A real ADTS frame with a zeroed payload is a valid silent frame, and reusing the first
            // frame's own header keeps the sample rate and channel count exactly right.
            var frameLength = finalAudio[0].Length;
            const int headerLength = 7;
            var silentFrame = new byte[frameLength];
            finalAudio[0].AsSpan(0, headerLength).CopyTo(silentFrame);
            var secondsPerFrame = 1024.0 / 48000.0;
            var frames = (int)Math.Ceiling(leadIn / secondsPerFrame);
            Console.WriteLine($"Export: padding audio head with {leadIn * 1000:F0} ms of silence " +
                $"({frames} AAC frames) so both tracks start at the same instant");

            for (var k = 0; k < frames; k++)
            {
                writer.AddAudioSample(silentFrame, k * secondsPerFrame);
            }
        }

        if (finalAudio.Count != finalAudioTimes.Count)
        {
            throw new InvalidOperationException(
                $"audio packet/timestamp desync: {finalAudio.Count} vs {finalAudioTimes.Count}");
        }

        for (var i = 0; i < finalAudio.Count; i++)
        {
            writer.AddAudioSample(finalAudio[i], finalAudioTimes[i]);
        }

        var path = Path.Combine(outputDirectory, $"clip-{DateTime.Now:HHmmssfff}.mp4");
        File.WriteAllBytes(path, writer.Build());
        Console.WriteLine($"Export: wrote {path} ({writer.SampleCount} samples)");

        // Only after the bytes are on disk: a beep before this point would tell the player the clip
        // is safe when it is not.
        if (config.PlaySoundNotification)
        {
            ExportNotification.PlayExportSaved();
            Console.WriteLine("Export: played the confirmation sound.");
        }

        return path;
    }

    private FfmpegEncoder? encoder;

    // Phase 4: the encoders feed these in-memory rings instead of writing files, and a clip is
    // produced by slicing them. The only thing the two share is the capture clock on each packet.
    private RingBuffer? videoRing;
    private RingBuffer? audioRing;
    private int videoWidth;
    private int videoHeight;

    // Ring capacity: longer than any clip we export, so an export never hits the trim.
    private const double RingSeconds = 190;

    // Phase 7 safety limits. A capture that has not produced a frame for this long is broken, and
    // exporting its ring would hand the user a plausible-looking but stale clip.
    private const double MaxCaptureStalenessSeconds = 5.0;

    // A 3-minute clip runs to a few hundred MB, and a half-written mp4 is worse than no clip.
    private const long MinFreeSpaceBytes = 300L * 1024 * 1024;

    /// <summary>
    /// The Stopwatch origin expressed in the QPC scale (100 ns units since boot). Video frames carry
    /// the compositor's SystemRelativeTime and audio packets carry WASAPI's qpcPosition, both on that
    /// scale, so subtracting this once puts every timestamp onto the shared Stopwatch.
    /// </summary>
    private double? stopwatchZeroQpcSeconds;

    private static double FirstOrNaN(IReadOnlyList<double> values) => values.Count > 0 ? values[0] : double.NaN;

    private double StopwatchZeroQpcSeconds =>
        stopwatchZeroQpcSeconds ??= (System.Diagnostics.Stopwatch.GetTimestamp() -
            (long)(stopwatch.Elapsed.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)) / 10_000_000.0;

    // Phase 6 hotkeys and the optional timed mid-recording export (the same call, different trigger).
    private bool hotkeysEnabled;
    private double? exportAtSeconds;
    private double? exportDurationSeconds;


    private TimeSpan lastFrameTime;
    private TimeSpan lastFpsLogTime;
    private int framesSinceFpsLog;
    private int frameCount;
    private bool disposed;

    private ScreenCapture(string outputDirectory, ClippyConfig? config = null)
    {
        this.config = config ?? ClippyConfig.Load();
        this.outputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);

        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is unavailable on this system.");

        d3d11Device = CreateD3D11Device(out d3d11Context);
        direct3DDevice = CaptureInterop.CreateDirect3DDevice(d3d11Device);
        item = CaptureInterop.CreateItemForPrimaryMonitor();
        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            item.Size);
        session = framePool.CreateCaptureSession(item);

        // Seeded from the monitor so the very first frame is not mistaken for a resolution change
        // and does not trigger a pointless encoder restart before recording has begun.
        videoWidth = item.Size.Width;
        videoHeight = item.Size.Height;

        framePool.FrameArrived += OnFrameArrived;
        item.Closed += OnItemClosed;
    }

    public static int Run(TimeSpan? duration)
    {
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "output");
        using var capture = new ScreenCapture(outputDirectory);
        return capture.Capture(duration);
    }

    /// <summary>Records a raw video stream: every captured frame is piped to ffmpeg.</summary>
    public static int RunVideo(TimeSpan duration, string outputPath, string encoderName, string fpsMode = "passthrough", bool withAudio = false, bool audioCaptureOnly = false, bool hotkeys = false, double? exportAt = null, double? exportDuration = null, ClippyConfig? config = null, bool tray = false)
    {
        // Without audio there is no mux, so the encoder writes a raw Annex B stream. Naming that file
        // .mp4 would be actively misleading: the bytes are correct but the extension lies.
        var videoPath = withAudio
            ? outputPath
            : Path.ChangeExtension(outputPath, ".h264");

        using var capture = new ScreenCapture(Path.GetDirectoryName(videoPath)!, config)
        {
            videoPath = videoPath,
            videoEncoder = encoderName,
            fpsMode = fpsMode,
            withAudio = withAudio,
            audioCaptureOnly = audioCaptureOnly,
            hotkeysEnabled = hotkeys,
            exportAtSeconds = exportAt,
            exportDurationSeconds = exportDuration,
        };

        // The tray owns the process lifetime: "Exit" has to run the same teardown as Ctrl+C, or
        // stopping from the menu would leave the ffmpeg pipes and the ring buffer unwritten.
        using var trayIcon = tray ? new TrayIcon(capture.Config, Path.GetDirectoryName(videoPath)!, capture.RequestStop) : null;
        trayIcon?.Start();
        trayIcon?.HideConsole();

        return capture.Capture(duration);
    }

    private int Capture(TimeSpan? duration)
    {
        Console.WriteLine($"Capture output: {outputDirectory}");
        Console.WriteLine("Waiting for frames from Windows.Graphics.Capture.");

        // One trace for every thread of this recording, all stamped from the same Stopwatch.
        EventLog.Clock = stopwatch;
        EventLog.Start(Path.Combine(outputDirectory, $"events-{DateTime.Now:yyyyMMdd-HHmmssfff}.csv"));

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Stop("Ctrl+C received");
        };
        Console.CancelKeyPress += cancelHandler;

        if (videoPath is not null)
        {
            Console.WriteLine($"Recording [{videoEncoder}] into an in-memory ring, max {RingSeconds:F0}s.");
            Console.WriteLine("Nothing is written until an export happens (F9 saves 30 s, F10 saves 3 min).");
        }

        try
        {
            session.StartCapture();
            using var timer = duration is { } timeout
                ? new Timer(_ => Stop($"capture duration {timeout.TotalSeconds:F0}s elapsed"), null, timeout, Timeout.InfiniteTimeSpan)
                : null;

            using var midExport = exportAtSeconds is { } at
                ? new Timer(_ => ExportClip(exportDurationSeconds ?? 10), null, TimeSpan.FromSeconds(at), Timeout.InfiniteTimeSpan)
                : null;

            // Phase 6: the low-level keyboard hook only records the key and signals an event; a
            // dedicated worker thread performs the export. Not a pool thread -- an export takes
            // seconds, and the pool is shared with everything else in the process.
            HotkeyService? hotkeys = null;
            if (hotkeysEnabled)
            {
                hotkeys = new HotkeyService(seconds => ExportClip(seconds), config);
                hotkeys.Start();
                Console.WriteLine("Hotkeys: F9 saves 30 s, F10 saves 3 min.");
            }

            stopped.Wait();
            Console.WriteLine($"Capture stopped after {frameCount} frames.");

            // Phase 4: the file is produced by slicing the rings, not by the encoders. Let the drains
            // hand over what ffmpeg already emitted, then export whatever the rings still hold.
            encoder?.WaitForDrain(TimeSpan.FromSeconds(3));
            audioEncoder?.WaitForDrain(TimeSpan.FromSeconds(3));
            audioEncoder?.PrintSampleAccounting();
            ExportClip(duration?.TotalSeconds ?? 10);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;

            // Stop the callback first: once the session is disposed no new frame can be delivered, so
            // taking the lock afterwards is bounded instead of racing an in-flight Write.
            framePool.FrameArrived -= OnFrameArrived;

            // Tear down INSIDE the same lock OnFrameArrived uses. Doing it outside let Dispose race a
            // concurrent Write -- closing stdin while another thread writes to it -- which is exactly
            // the kind of timing-dependent fault that makes a run pass or hang at random.
            lock (sync)
            {
                disposed = true;

                // Audio first: it feeds the audio ffmpeg's stdin, which must close before it exits.
                audio?.Dispose();
                audio = null;
                audioEncoder?.Dispose();
                audioEncoder = null;

                // Close ffmpeg's stdin after the last frame so it finalises the mp4 container.
                encoder?.Dispose();
                encoder = null;
            }

            if (withAudio && videoOnlyPath is not null && audioOnlyPath is not null)
                MuxVideoAndAudio(videoOnlyPath, audioOnlyPath, videoPath!);
            else if (audioCaptureOnly)
                Console.WriteLine($"Bisection run: {discardedAudioBuffers} WASAPI buffers received and discarded; no mux.");
        }
    }

    /// <summary>
    /// Combines the two recordings into the final file.
    ///
    /// No -itsoffset: both ffmpeg processes stamped their packets with the same system clock
    /// (-use_wallclock_as_timestamps) starting at the same instant, so both files already share one
    /// zero. An offset here would paper over a real misalignment instead of measuring it, so the
    /// start_time of each input is compared instead and reported.
    /// </summary>
    private void MuxVideoAndAudio(string videoInput, string audioInput, string output)
    {
        if (!File.Exists(audioInput))
        {
            Console.WriteLine("Mux skipped: no audio file was produced.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", videoInput,
            "-i", audioInput,
            // -c copy: both streams are already encoded, and re-encoding here would add delay.
            // -shortest: without it a long silent tail would pad the file.
            "-map", "0:v:0", "-map", "1:a:0",
            "-c", "copy", "-shortest",
            "-movflags", "+faststart",
            output,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for muxing.");
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Mux ffmpeg did not exit within 120s and was killed.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Mux ffmpeg exited with {process.ExitCode}: {stderr.Trim()}");

        // Report the two start times so A/V alignment is a measured number, not an assumption.
        var starts = new[] { videoInput, audioInput }
            .Select(path => $"{(Path.GetFileNameWithoutExtension(path))}={ReadStartSeconds(path):F4}s")
            .ToArray();
        Console.WriteLine($"Muxed -> {output} (stream start times: {string.Join(", ", starts)})");
    }

    private static double ReadStartSeconds(string path)
    {
        var probe = new ProcessStartInfo
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-v", "error",
            "-show_entries", "format=start_time",
            "-of", "csv=p=0",
            path,
        })
        {
            probe.ArgumentList.Add(argument);
        }

        using var process = Process.Start(probe)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return double.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
    {
        EventLog.Mark("FB_ENTER");
        lock (sync)
        {
            EventLog.Mark("FB_LOCK");
            if (disposed)
                return;

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            if (frame.ContentSize.Width != videoWidth || frame.ContentSize.Height != videoHeight)
            {
                sender.Recreate(
                    direct3DDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    FramePoolBufferCount,
                    frame.ContentSize);

                // An avc1 track has one resolution for its whole life, so frames of two different
                // sizes can never share one MP4. The old ring is dropped rather than trimmed, and
                // the encoder is restarted with the new -video_size, or ffmpeg would keep writing
                // the old geometry and every frame after this one would be mis-scaled.
                RestartVideoPipeline(frame.ContentSize.Width, frame.ContentSize.Height);
                Console.WriteLine($"[wgc] Resolution changed to {frame.ContentSize.Width}x" +
                    $"{frame.ContentSize.Height}. Video ring reset, encoder restarted.");
                return;
            }

            var currentFrame = ++frameCount;
            LogFrameTiming();

            if (videoPath is not null)
            {
                // The video origin on the shared Stopwatch, taken before anything else in this frame.
                // AudioCapture needs it at construction to prepend the right amount of silence, and the
                // GPU copy below costs milliseconds that would otherwise land in the measured gap.
                if (currentFrame == 1)
                    videoStartSeconds = stopwatch.Elapsed.TotalSeconds;

                var videoTexture = CaptureInterop.GetTexture(frame.Surface);
                var pixels = CopyTextureToCpu(videoTexture, out var frameVideoWidth, out var frameVideoHeight);
                videoTexture.Dispose();

                if (encoder is null)
                {
                    videoWidth = frameVideoWidth;
                    videoHeight = frameVideoHeight;
                    // Phase 4: both encoders feed in-memory rings. A clip is produced by slicing
                    // those rings, not by reading back files.
                    videoRing = new RingBuffer(RingSeconds, isVideo: true);
                    audioRing = new RingBuffer(RingSeconds, isVideo: false);

                    if (withAudio)
                    {
                        // Bisection mode: real WASAPI buffers arrive and the callback really runs,
                        // but no audio ffmpeg exists and nothing is written anywhere. Isolates
                        // "Clippy's own threads upset video capture" from "two ffmpeg processes".
                        // Elementary streams, not containers (phase 4.0): the ring buffer slices these
                        // byte streams directly, so there is no container to carry timestamps.
                        videoOnlyPath = Path.ChangeExtension(videoPath, ".video.h264");

                        if (audioCaptureOnly)
                        {
                            audio = new AudioCapture(stopwatch, videoStartSeconds);
                            audio.Sink = (buffer, qpc) => Interlocked.Increment(ref discardedAudioBuffers);
                            audioEncoder = null;
                            audioOnlyPath = null;
                            Console.WriteLine($"Audio: capture-only [{audio.Format}], buffers will be discarded");
                        }
                        else
                        {
                            audioOnlyPath = Path.ChangeExtension(videoPath, ".audio.aac");
                            audio = new AudioCapture(stopwatch, videoStartSeconds);
                            audioEncoder = AudioEncoder.Start(audioOnlyPath, audio, stopwatch, audioRing, masterZeroSeconds);
                            audio.Sink = (buffer, qpc) => audioEncoder!.Write(buffer, qpc);
                            // An f32le input probes without data, so audio reaches ready on its own.
                            audioEncoder.WaitForReady(TimeSpan.FromSeconds(15));
                            Console.WriteLine($"Audio: system loopback [{audio.Format}]");
                        }

                        encoder = FfmpegEncoder.StartWithFallback(
                            videoOnlyPath,
                            videoWidth,
                            videoHeight,
                            videoEncoder ?? config.VideoEncoder,
                            stopwatch,
                            fpsMode);
                        encoder.StartDrain(videoRing);
                        Console.WriteLine($"Encoding {videoWidth}x{videoHeight} into ring (max {RingSeconds:F0}s)");
                    }
                    else
                    {
                        encoder = FfmpegEncoder.StartWithFallback(
                            videoPath,
                            videoWidth,
                            videoHeight,
                            videoEncoder ?? config.VideoEncoder,
                            stopwatch,
                            fpsMode);

                        // Same as the audio branch: without the drain there is no ring, no SPS/PPS and
                        // therefore no export at all. Hotkeys work in this mode too, producing the
                        // deliberate video-only clip when the audio ring is empty.
                        encoder.StartDrain(videoRing);
                        Console.WriteLine($"Encoding {videoWidth}x{videoHeight} into ring, no audio (max {RingSeconds:F0}s)");
                    }
                }

                // SystemRelativeTime is Windows' own capture timestamp: the same clock ffmpeg reads
                // -use_wallclock_as_timestamps against, so it is the reference for PTS verification.
                var isFirstFrame = currentFrame == 1;

                // Startup cross-check: the compositor's own QPC for the first video frame, against
                // the first audio packet's QPC. Their difference is the whole A/V offset, measured
                // directly on the hardware clock, with no alignment or muxer in the way.
                if (isFirstFrame)
                {
                    Console.WriteLine($"[start] first VIDEO frame: sysRelTicks={frame.SystemRelativeTime.Ticks} " +
                        $"qpc={(frame.SystemRelativeTime.Ticks / 1e7):F6} " +
                        $"arrivalStopwatch={stopwatch.Elapsed.TotalSeconds:F3} " +
                        $"arrivalQpc={(System.Diagnostics.Stopwatch.GetTimestamp() / 1e7):F6} " +
                        $"stopwatchZeroQpc={StopwatchZeroQpcSeconds:F6}");
                }

                // Enqueue before Write so the queue order is exactly the byte order into ffmpeg.
                //
                // The time is the frame's OWN SystemRelativeTime -- the QPC at which the compositor
                // rendered it -- not the moment OnFrameArrived runs. Taking the arrival time stamps
                // every frame later than it really appeared, by however long the event dispatch and
                // the GPU copy took, and that lands directly on the A/V offset. SystemRelativeTime is
                // 100 ns units from boot, the same 10 MHz scale as the audio's qpcPosition, so the
                // stopwatch origin is subtracted once and both tracks share one hardware clock.
                var frameQpcSeconds = frame.SystemRelativeTime.Ticks / (double)System.Diagnostics.Stopwatch.Frequency;
                encoder.EnqueueCaptureTime(frameQpcSeconds - masterZeroSeconds);
                encoder.Write(pixels, frame.SystemRelativeTime.TotalMilliseconds);

                // The first write is what lets ffmpeg finish probing and open its input; only then is
                // it meaningful to wait for readiness. Both processes are confirmed up before any
                // further frames go in, so neither can miss early data.
                if (isFirstFrame)
                {
                    encoder.WaitForReady(TimeSpan.FromSeconds(15));
                    if (audio is not null)
                    {
                        audio.Start();
                        Console.WriteLine("Both encoders are reading; recording.");
                    }
                }

                return;
            }

            if (currentFrame % FrameSaveInterval != 0)
                return;

            using var texture = CaptureInterop.GetTexture(frame.Surface);
            var fileName = Path.Combine(outputDirectory, $"frame-{currentFrame:D6}-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
            SavePng(texture, fileName);
            Console.WriteLine($"Saved {Path.GetFileName(fileName)}.");
        }
    }

    private void OnItemClosed(GraphicsCaptureItem _, object __)
    {
        Stop("capture item closed");
    }

    private void LogFrameTiming()
    {
        var now = stopwatch.Elapsed;
        if (lastFrameTime != TimeSpan.Zero)
        {
            var interval = now - lastFrameTime;
            framesSinceFpsLog++;

            var elapsedSinceLog = now - lastFpsLogTime;
            if (elapsedSinceLog >= TimeSpan.FromSeconds(1))
            {
                Console.WriteLine($"Capture FPS: {framesSinceFpsLog / elapsedSinceLog.TotalSeconds:F1}; last interval: {interval.TotalMilliseconds:F1} ms.");
                framesSinceFpsLog = 0;
                lastFpsLogTime = now;
            }
        }

        lastFrameTime = now;
    }

    /// <summary>
    /// Asks the capture to finish and tear down. Public because the tray's Exit item calls it from
    /// its own message-pump thread, and it must go through the same path as Ctrl+C so the ffmpeg
    /// pipes are drained and the ring's contents are not thrown away.
    /// </summary>
    public void RequestStop() => Stop("exit requested from the tray menu");

    private void Stop(string reason)
    {
        if (stopped.IsSet)
            return;

        Console.WriteLine($"Stopping capture: {reason}.");
        stopped.Set();
    }

    /// <summary>
    /// Phase 7 scenario A: the display changed resolution, so the video track has to start over.
    ///
    /// Only the video pipeline is rebuilt. The audio capture and its ring are untouched, because
    /// audio is resolution-independent -- tearing those down would drop sound that is still valid
    /// and would re-run the WASAPI start-up, which is exactly where the A/V offset comes from.
    ///
    /// Called with the capture lock already held, like the rest of OnFrameArrived.
    /// </summary>
    private void RestartVideoPipeline(int newWidth, int newHeight)
    {
        var hadEncoder = encoder is not null;
        var ring = videoRing;

        if (hadEncoder && ring is not null)
        {
            // The old encoder is stopped and its process reaped BEFORE the ring is cleared: the
            // drain thread reads from that ring, so clearing it under a live reader would race.
            encoder.Dispose();
            encoder = null;
            ring.Clear();
        }

        videoWidth = newWidth;
        videoHeight = newHeight;

        if (!hadEncoder || videoPath is null || ring is null)
            return;

        encoder = FfmpegEncoder.StartWithFallback(
            videoOnlyPath ?? videoPath,
            newWidth,
            newHeight,
            videoEncoder ?? config.VideoEncoder,
            stopwatch,
            fpsMode);
        encoder.StartDrain(ring);
    }

    /// <summary>Stages a GPU texture into system RAM as tightly packed BGRA rows.</summary>
    private byte[] CopyTextureToCpu(ID3D11Texture2D texture, out int width, out int height)
    {
        var description = texture.Description;
        var stagingDescription = description;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDescription.MiscFlags = ResourceOptionFlags.None;

        using var stagingTexture = d3d11Device.CreateTexture2D(stagingDescription);
        d3d11Context.CopyResource(stagingTexture, texture);

        var mapped = d3d11Context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            width = checked((int)description.Width);
            height = checked((int)description.Height);
            var rowLength = checked(width * 4);
            var pixels = new byte[checked(rowLength * height)];
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(mapped.DataPointer, checked(row * (int)mapped.RowPitch)),
                    pixels,
                    row * rowLength,
                    rowLength);
            }

            return pixels;
        }
        finally
        {
            d3d11Context.Unmap(stagingTexture, 0);
        }
    }

    private void SavePng(ID3D11Texture2D texture, string path)
    {
        var pixels = CopyTextureToCpu(texture, out var width, out var height);
        PngWriter.Write(path, width, height, pixels);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            item.Closed -= OnItemClosed;
            framePool.FrameArrived -= OnFrameArrived;
            encoder?.Dispose();
            encoder = null;
            session.Dispose();
            framePool.Dispose();
            direct3DDevice.Dispose();
            d3d11Context.Dispose();
            d3d11Device.Dispose();
            stopped.Dispose();
        }
    }

    private static ID3D11Device CreateD3D11Device(out ID3D11DeviceContext context)
    {
        var result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out var device,
            out _,
            out context);

        if (result.Failure)
            throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Description}");

        return device;
    }
}

internal static partial class CaptureInterop
{
    private const uint MonitorDefaultToPrimary = 1;

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static GraphicsCaptureItem CreateItemForPrimaryMonitor()
    {
        var monitor = MonitorFromPoint(new Point { X = 0, Y = 0 }, MonitorDefaultToPrimary);
        if (monitor == 0)
            throw new InvalidOperationException("Primary HMONITOR was not found.");

        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();

        ThrowIfFailed(interop.CreateForMonitor(monitor, GraphicsCaptureItemIid, out var item));
        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item)!;
        }
        finally
        {
            MarshalInspectable<GraphicsCaptureItem>.DisposeAbi(item);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3d11Device)
    {
        using var dxgiDevice = d3d11Device.QueryInterface<IDXGIDevice>();
        ThrowIfFailed(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var device));
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(device)!;
        }
        finally
        {
            MarshalInspectable<IDirect3DDevice>.DisposeAbi(device);
        }
    }

    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        ThrowIfFailed(access.GetInterface(Texture2DIid, out var texture));
        return new ID3D11Texture2D(texture);
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint direct3DDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [GeneratedComInterface]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [GeneratedComInterface]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out nint result);
    }
}

internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Write(string path, int width, int height, byte[] bgraPixels)
    {
        var scanlineLength = checked(width * 4 + 1);
        var scanlines = new byte[checked(scanlineLength * height)];

        for (var y = 0; y < height; y++)
        {
            var sourceOffset = y * width * 4;
            var destinationOffset = y * scanlineLength + 1;
            for (var x = 0; x < width; x++)
            {
                var source = sourceOffset + x * 4;
                var destination = destinationOffset + x * 4;
                scanlines[destination] = bgraPixels[source + 2];
                scanlines[destination + 1] = bgraPixels[source + 1];
                scanlines[destination + 2] = bgraPixels[source];
                scanlines[destination + 3] = bgraPixels[source + 3];
            }
        }

        using var output = File.Create(path);
        output.Write(Signature);
        WriteChunk(output, "IHDR", CreateHeader(width, height));

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(scanlines);

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static byte[] CreateHeader(int width, int height)
    {
        var header = new byte[13];
        WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        WriteUInt32BigEndian(output, (uint)data.Length);
        output.Write(typeBytes);
        output.Write(data);

        var crc = CalculateCrc(typeBytes, data);
        WriteUInt32BigEndian(output, crc);
    }

    private static uint CalculateCrc(byte[] type, byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (var value in type)
            crc = UpdateCrc(crc, value);
        foreach (var value in data)
            crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        return crc;
    }

    private static void WriteUInt32BigEndian(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        WriteUInt32BigEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteUInt32BigEndian(Span<byte> buffer, uint value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }
}
