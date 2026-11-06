#if LINUX
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// The Linux backend: wires the portal capture, the two pipelines, the hybrid buffer and the
/// trigger mechanisms into the same run/export shape the Windows ScreenCapture follows. Every
/// cross-platform building block -- RingBuffer, DiskSpooler, TrackAligner, Mp4Writer, the parsers,
/// ClipsQuota -- is used exactly as Windows uses it, so a Linux clip and a Windows clip are the
/// same bytes by construction.
/// </summary>
internal sealed class LinuxPlatformBackend : IPlatformBackend
{
    // On Linux the process always has the terminal it was started from and Wayland owns scaling, so
    // neither of the two Windows startup rituals has a counterpart here.
    public void InitializeConsole(string[] args) { }

    public void InitializeDpi() { }

    public int RunVideo(PlatformRecordingOptions options) => new LinuxRecorder(options).Run();

    /// <summary>
    /// --capture: the portal stream opened and decoded by gst, but no ffmpeg and no rings -- the
    /// diagnostic that separates "the capture works" from "the encode works".
    /// </summary>
    public int RunCaptureOnly(TimeSpan? duration)
    {
        Console.WriteLine("Capture: opening the portal screen stream (no encoder, no files).");
        LinuxPortal.Session session;
        using var portal = LinuxPortal.Create(Path.Combine(AppContext.BaseDirectory, "portal_token.txt"));
        try
        {
            session = portal.StartSession();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Capture: the portal session failed ({ex.Message}).");
            return 1;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "gst-launch-1.0",
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-v", "pipewiresrc", $"fd={session.PipeWireFd}", "keepalive-time=1000", "!",
            "video/x-raw", "!", "videoconvert", "!", "video/x-raw,format=BGRx", "!",
            "fakesink", "sync=false",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var gst = Process.Start(startInfo);
        if (gst is null)
        {
            Console.WriteLine("Capture: gst-launch could not be started.");
            return 1;
        }

        Console.WriteLine("Capture: frames are flowing through gst (stderr shows the negotiated caps). Ctrl+C stops.");
        using var waited = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; waited.Set(); };
        if (duration is { } timeout)
            waited.Wait(timeout);
        else
            waited.Wait();
        Console.WriteLine("Capture: done.");
        return 0;
    }

    /// <summary>
    /// The Windows diagnostics refuse here; the Linux ones -- the IPC client behind --save-short,
    /// --save-long and --stop -- are answered by the running recorder through its socket.
    /// </summary>
    public int RunPlatformCommand(string[] args, out bool handled)
    {
        if (args.FirstOrDefault(a => a is "--save-short" or "--save") is { } shortFlag)
        {
            handled = true;
            var path = LinuxHotkeyService.SendCommand("SHORT");
            if (path is not null)
                Console.WriteLine(path);
            return path is null ? 1 : 0;
        }

        if (args.Contains("--save-long"))
        {
            handled = true;
            var path = LinuxHotkeyService.SendCommand("LONG");
            if (path is not null)
                Console.WriteLine(path);
            return path is null ? 1 : 0;
        }

        if (args.Contains("--stop"))
        {
            handled = true;
            var result = LinuxHotkeyService.SendCommand("STOP");
            Console.WriteLine(result is null ? "Recorder not stopped." : "Recorder stopped.");
            return result is null ? 1 : 0;
        }

        string? flag = args.FirstOrDefault(a => a is "--smoke" or "--test-nvenc-native"
            or "--test-mft-video-wgc" or "--test-mft-video" or "--test-mft-audio" or "--audio-probe");
        if (flag is null)
        {
            handled = false;
            return 0;
        }

        Console.WriteLine($"{flag} is a Windows-only diagnostic (Media Foundation / NVENC / WASAPI); it cannot run on Linux.");
        handled = true;
        return 1;
    }
}

/// <summary>
/// One recording run on Linux. Owns the hybrid buffer (the same 32 s RAM head and 190 s total
/// replay depth as Windows), both pipelines, every clip trigger, and the export that turns ring
/// contents into an MP4 -- the same algorithm, step for step, as ScreenCapture.ExportClip.
/// </summary>
internal sealed partial class LinuxRecorder : IDisposable, LinuxTrayIcon.IHost
{
    // Same limits as ScreenCapture, for the same reasons: 32 s of RAM head serves a 30 s short clip
    // with margin; the disk tail stretches the total replay depth past any clip length.
    private const double MaxCaptureStalenessSeconds = 5.0;
    private const long MinFreeSpaceBytes = 300L * 1024 * 1024;

    private readonly PlatformRecordingOptions options;
    private readonly ClippyConfig config;
    // Not readonly: "Select Clips Folder..." re-points the recorder at the chosen directory live.
    private string outputDirectory;
    private readonly string logsDirectory;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly ManualResetEventSlim stopped = new(false);

    private LinuxPortal? portal;
    private LinuxVideoPipeline? video;
    private LinuxAudioPipeline? audio;
    private LinuxTrayIcon? tray;
    private DiskSpooler? videoSpool;
    private DiskSpooler? audioSpool;
    private RingBuffer? videoRing;
    private RingBuffer? audioRing;
    private LinuxHotkeyService? hotkeys;
    private Timer? durationTimer;
    private Timer? midExportTimer;

    /// <summary>Serialises exports and answers a second trigger with busy instead of a queue.</summary>
    private readonly object exportGate = new();
    private bool exporting;

    public LinuxRecorder(PlatformRecordingOptions options)
    {
        this.options = options;
        config = options.Config;
        outputDirectory = Path.GetDirectoryName(options.OutputPath) ?? config.ResolveOutputFolder();
        // The clips folder is only implied by the clip's path; nothing else creates it, and an
        // export that fails on a missing directory would be a miserable way to lose a clip.
        try { Directory.CreateDirectory(outputDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Recording: could not create the clips folder {outputDirectory} ({ex.Message}).");
        }
        var logsFolder = config.LogsFolder;
        logsDirectory = System.IO.Path.IsPathRooted(logsFolder)
            ? logsFolder
            : Path.Combine(AppContext.BaseDirectory, logsFolder);
    }

    public int Run()
    {
        // The .desktop entry is what makes the portal picker say "Clippy" instead of "an
        // unidentifiable application"; it is only written when missing or moved.
        LinuxDesktopIntegration.EnsureDesktopFile();

        Console.WriteLine($"Recording [{config.VideoEncoder}] into a hybrid ring: " +
            $"{LinuxVideoPipeline.RamHeadSeconds:F0}s RAM head + disk spool to {LinuxVideoPipeline.RingSeconds:F0}s total.");
        Console.WriteLine("Nothing is written until an export happens (--save-short saves " +
            $"{config.ShortClipSeconds:F0} s, --save-long saves {config.LongClipSeconds:F0} s).");

        Console.CancelKeyPress += OnCancel;

        // Tier 2 first: the rings need their eviction sink at construction time, because the very
        // first trim can happen on the very first push. Identical wiring to ScreenCapture.
        videoSpool = new DiskSpooler("video", LinuxVideoPipeline.RingSeconds - LinuxVideoPipeline.RamHeadSeconds);
        audioSpool = new DiskSpooler("audio", LinuxVideoPipeline.RingSeconds - LinuxVideoPipeline.RamHeadSeconds);
        videoRing = new RingBuffer(LinuxVideoPipeline.RamHeadSeconds, isVideo: true) { OnEvicted = videoSpool.Evict };
        audioRing = new RingBuffer(LinuxVideoPipeline.RamHeadSeconds, isVideo: false) { OnEvicted = audioSpool.Evict };

        try
        {
            if (!options.AudioCaptureOnly)
            {
                if (!StartVideo())
                    return 1;
            }

            if (options.WithAudio && !StartAudio())
                Console.WriteLine("Audio: recording continues without sound.");

            hotkeys = new LinuxHotkeyService(HandleTrigger, config);
            hotkeys.Start();

            // The tray is optional in every sense: no bar, no icon, no problem. When there is one,
            // left click saves the short clip and the menu mirrors the Windows tray.
            if (options.Tray)
            {
                tray = new LinuxTrayIcon(this);
                tray.Start();
            }

            if (options.Duration is { } timeout)
            {
                durationTimer = new Timer(_ => Stop($"--record {timeout.TotalSeconds:F0}s elapsed"),
                    null, timeout, Timeout.InfiniteTimeSpan);
            }

            // --export-at exports while the recording is still running, same as on Windows.
            if (options.ExportAt is { } at)
            {
                midExportTimer = new Timer(_ => GuardedExport(options.ExportDuration ?? 10),
                    null, TimeSpan.FromSeconds(at), Timeout.InfiniteTimeSpan);
            }

            stopped.Wait();
            Console.WriteLine("Recording stopped.");

            // Let the drains hand over what ffmpeg already emitted before any final export slices.
            video?.WaitForDrain(TimeSpan.FromSeconds(3));
            audio?.WaitForDrain(TimeSpan.FromSeconds(3));
            audio?.PrintSampleAccounting();

            // With an explicit --record N the file IS the point of the run; the infinite mode exits
            // without saving, exactly like the Windows tray path, because every save there was an
            // explicit trigger.
            if (options.Duration is { } requested)
            {
                ExportClip(requested.TotalSeconds);
            }
            else
            {
                Console.WriteLine("Exiting without saving a clip. Use --save-short / --save-long to save one.");
            }

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
            Dispose();
        }
    }

    private bool StartVideo()
    {
        LinuxPortal.Session session;
        try
        {
            // Held for the whole run: disposing it closes the D-Bus connection and the portal
            // kills the capture session with it.
            portal = LinuxPortal.Create(Path.Combine(AppContext.BaseDirectory, "portal_token.txt"));
            session = portal.StartSession();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Video: the portal session failed ({ex.Message}).");
            Console.WriteLine("Video: no screen capture is possible, so the recorder cannot run.");
            return false;
        }
        sessionFd = session.PipeWireFd;
        sessionId = session.NodeId;
        sessionWidth = session.Width;
        sessionHeight = session.Height;

        video = new LinuxVideoPipeline(stopwatch);
        if (!video.Start(session.PipeWireFd, session.NodeId, session.Width, session.Height, config, logsDirectory, videoRing!))
        {
            Console.WriteLine("Video: the capture pipeline could not be started.");
            video.Dispose();
            video = null;
            return false;
        }

        return true;
    }

    private bool StartAudio()
    {
        audio = new LinuxAudioPipeline(stopwatch);
        var monitor = LinuxAudioPipeline.ResolveMonitorSource(config.AudioDevice);
        if (!audio.Start(monitor, logsDirectory, options.AudioCaptureOnly ? new RingBuffer(1, isVideo: false) : audioRing!))
        {
            audio.Dispose();
            audio = null;
            return false;
        }
        return true;
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        Stop("Ctrl+C received");
    }

    private void Stop(string reason)
    {
        Console.WriteLine($"Stopping: {reason}.");
        stopped.Set();
    }

    /// <summary>
    /// The single entry point every trigger funnels into. The IPC path needs the saved file's path
    /// back (the CLI prints it), so the export runs on the caller's thread under the export gate;
    /// evdev and signal threads get the same treatment and simply drop the return value. A request
    /// that arrives while an export is running is refused, never queued -- the Windows hotkey
    /// worker behaves the same way, and a queue would silently turn ten key presses into ten
    /// delayed exports of the same window.
    /// </summary>
    private string HandleTrigger(LinuxHotkeyService.Trigger trigger)
    {
        if (trigger == LinuxHotkeyService.Trigger.Stop)
        {
            Stop("the IPC socket asked for it");
            return "OK stopping";
        }

        var seconds = trigger == LinuxHotkeyService.Trigger.Short ? config.ShortClipSeconds : config.LongClipSeconds;
        lock (exportGate)
        {
            if (exporting)
                return "ERR busy: an export is already running";
            exporting = true;
        }

        try
        {
            var path = ExportClip(seconds);
            return path is null ? "ERR export failed, see the console" : $"OK {path}";
        }
        finally
        {
            lock (exportGate)
                exporting = false;
        }
    }

    /// <summary>Runs an export from a timer callback, where an escaping exception would kill the process.</summary>
    private void GuardedExport(double seconds)
    {
        try
        {
            HandleTrigger(seconds <= config.ShortClipSeconds
                ? LinuxHotkeyService.Trigger.Short
                : LinuxHotkeyService.Trigger.Long);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Export failed: {ex.GetType().Name}: {ex.Message}");
            ReleaseExportMemory();
        }
    }

    /// <summary>
    /// Writes the last <paramref name="durationSeconds"/> still held in the rings to an MP4 file.
    /// Step-for-step the ScreenCapture.ExportClip algorithm: staleness, free space, hybrid slices,
    /// keyframe snap, one shared zero, silence padding, Mp4Writer, notifications, quota.
    /// </summary>
    public string? ExportClip(double durationSeconds)
    {
        if (video is null || videoRing is null || video.Sps is null || video.Pps is null)
        {
            Console.WriteLine("Export: nothing to export yet (recorder not started or no SPS/PPS seen).");
            return null;
        }

        // Snapshot the clock first, then both slices. Slice copies, so the packets stay valid even
        // though the drain threads keep pushing while this runs.
        var to = stopwatch.Elapsed.TotalSeconds;
        var from = to - durationSeconds;

        // A capture that has not produced a frame for this long is broken; exporting its ring would
        // hand the user a plausible-looking but stale clip.
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

        // A 3-minute clip is a few hundred MB, and a half-written mp4 is worse than no clip.
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outputDirectory));
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

        var videoPackets = DiskSpooler.SliceHybrid(videoRing!, videoSpool, from, to);
        var audioPackets = DiskSpooler.SliceHybrid(audioRing!, audioSpool, from, to);

        var (captured, accessUnits) = video.TimingCounts;
        Console.WriteLine($"Export: access units in {captured}, pushed {accessUnits} (1:1 on Linux by construction).");

        // An empty audio slice produces a VIDEO-ONLY mp4, not a refusal, exactly as on Windows.
        if (videoPackets.Count == 0)
        {
            Console.WriteLine("Export: video slice is empty, nothing to write.");
            ReleaseExportMemory();
            return null;
        }

        var audioMissing = audioPackets.Count == 0;
        Console.WriteLine($"Export: video slice {videoPackets.Count} pkts, " +
            $"clock [{videoPackets[0].CaptureClockSeconds:F3} .. {videoPackets[^1].CaptureClockSeconds:F3}] s, " +
            $"first is keyframe: {(videoPackets[0].IsKeyframe ? "True" : "False")}");
        if (audioMissing)
            Console.WriteLine("Export: no audio in range; writing a VIDEO-ONLY clip on purpose.");
        else
            Console.WriteLine($"Export: audio slice {audioPackets.Count} pkts, " +
                $"clock [{audioPackets[0].CaptureClockSeconds:F3} .. {audioPackets[^1].CaptureClockSeconds:F3}] s");

        // KNOWN AND EXPECTED: the clip advances to the first keyframe at or after the requested
        // start, because a slice beginning on a P/B frame decodes as garbage until the next I-frame.
        var start = videoPackets.FindIndex(p => p.IsKeyframe);
        if (start < 0)
        {
            Console.WriteLine("Export: no keyframe in the requested window, refusing to write.");
            ReleaseExportMemory();
            return null;
        }

        var writer = new Mp4Writer();
        writer.SetParameterSets(video.Sps, video.Pps, video.Vps);
        writer.SetDimensions(video.Width, video.Height);

        // The encoder tells us which NAL classification applies: a VPS exists exactly when HEVC.
        var hevc = video.Vps is not null;

        var videoTimes = new List<double>();
        foreach (var packet in videoPackets.Skip(start))
        {
            if (!IsPictureNal(packet.Span, hevc))
                continue;
            videoTimes.Add(packet.CaptureClockSeconds);
        }

        var alignment = TrackAligner.Align(videoTimes, audioPackets.Select(p => p.CaptureClockSeconds).ToList());
        Console.WriteLine($"[align] t0 (shared zero) = {alignment.Zero:F3} s, video {alignment.VideoTimes.Count} pictures, " +
            $"audio {alignment.AudioTimes.Count} frames, lead-in {alignment.AudioLeadIn * 1000:F0} ms");

        // One MP4 sample per access unit: the non-VCL NALs that PRECEDE a VCL belong to it.
        var pending = new List<ReadOnlyMemory<byte>>();
        var timeIndex = 0;
        foreach (var packet in videoPackets.Skip(start))
        {
            var isVcl = IsPictureNal(packet.Span, hevc);
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

        // The clip's zero is ALWAYS the video keyframe: audio recorded before it is DROPPED, and
        // silence is inserted only when the first surviving audio packet is still after it.
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
            // The canonical silent stereo AAC-LC raw block; a zeroed payload would be invalid AAC.
            const int headerLength = 7;
            byte[] silentPayload = [0x21, 0x10, 0x04, 0x60, 0x8C, 0x1C];
            var silentFrame = new byte[headerLength + silentPayload.Length];
            finalAudio[0].AsSpan(0, headerLength).CopyTo(silentFrame);
            silentPayload.CopyTo(silentFrame, headerLength);
            var secondsPerFrame = 1024.0 / 48000.0;
            var frames = (int)Math.Ceiling(leadIn / secondsPerFrame);
            Console.WriteLine($"Export: padding audio head with {leadIn * 1000:F0} ms of silence " +
                $"({frames} AAC frames) so both tracks start at the same instant");

            for (var k = 0; k < frames; k++)
                writer.AddAudioSample(silentFrame, k * secondsPerFrame);
        }

        for (var i = 0; i < finalAudio.Count; i++)
            writer.AddAudioSample(finalAudio[i], finalAudioTimes[i]);

        var path = PlatformClipPath.ClipPath(outputDirectory, DateTime.Now);
        writer.BuildToFile(path);
        Console.WriteLine($"Export: wrote {path} ({writer.SampleCount} samples)");

        var clipSeconds = ActualClipSeconds(alignment);

        // The heap keeps a whole clip's worth of buffers until the next collection otherwise; the
        // Windows path pays the same once-per-export pause, and malloc_trim is glibc's half of the
        // working-set trim EmptyWorkingSet does there.
        ReleaseExportMemory();

        // Only after the bytes are on disk: a sound before this point would promise a clip that
        // does not exist yet.
        if (config.PlaySoundNotification)
            LinuxNotifications.PlayExportSaved(config.CustomSoundPath, Path.GetTempPath());

        // The keyframe snap trims the head (up to one GOP, ~1 s), so the real clip always lands
        // just UNDER the requested length -- 29.4 s for a 30 s F8. Ceiling reports the duration
        // that was asked for; rounding would advertise the trimming as a shorter clip.
        var totalSeconds = (int)Math.Ceiling(clipSeconds);
        try
        {
            LinuxNotifications.ShowClipSaved(config, "Clip Saved",
                $"{ClipStamp(totalSeconds)} · {FileMegabytes(path):F1} MB");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Export: notification failed ({ex.GetType().Name}: {ex.Message}).");
        }

        // After the file exists, not before: a quota check that ran first could delete the clip
        // this export is about to write.
        ClipsQuota.Enforce(outputDirectory, config.MaxClipsFolderSizeGB);

        return path;
    }

    /// <summary>Same NAL classification as ScreenCapture: pictures end an access unit, everything else rides along.</summary>
    private static bool IsPictureNal(ReadOnlySpan<byte> nal, bool hevc)
    {
        if (nal.Length < 2)
            return false;
        return hevc ? HevcNal.IsVcl(HevcNal.Type(nal)) : (nal[0] & 0x1F) is >= 1 and <= 5;
    }

    /// <summary>The clip's real length: last sample time plus one frame interval, video track winning.</summary>
    private static double ActualClipSeconds(TrackAlignment alignment)
    {
        var videoEnd = 0.0;
        if (alignment.VideoTimes.Count > 0)
        {
            var last = alignment.VideoTimes[^1];
            var frame = alignment.VideoTimes.Count > 1
                ? (alignment.VideoTimes[^1] - alignment.VideoTimes[0]) / (alignment.VideoTimes.Count - 1)
                : 1.0 / 25.0;
            videoEnd = last + (frame > 0 ? frame : 1.0 / 25.0);
        }

        var audioEnd = alignment.AudioTimes.Count > 0
            ? alignment.AudioTimes[^1] + 1024.0 / 48000.0
            : 0.0;

        return Math.Max(videoEnd, audioEnd);
    }

    /// <summary>m:ss for the notification, from a whole-second count rounded to the nearest second.</summary>
    private static string ClipStamp(int totalSeconds) => $"{totalSeconds / 60}:{totalSeconds % 60:D2}";

    private static double FileMegabytes(string path)
    {
        try
        {
            return new FileInfo(path).Length / (1024.0 * 1024.0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Hands back everything an export allocated, on every path out of it. GC.Collect is the same
    /// call the Windows path makes; malloc_trim returns the freed pages to the OS, which is the
    /// half EmptyWorkingSet does on Windows.
    /// </summary>
    private static void ReleaseExportMemory()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        malloc_trim(0);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int malloc_trim(nuint pad);

    // --- LinuxTrayIcon.IHost ---

    ClippyConfig LinuxTrayIcon.IHost.Config => config;

    string LinuxTrayIcon.IHost.ClipsFolder => outputDirectory;

    string LinuxTrayIcon.IHost.LogsFolder => logsDirectory;

    string LinuxTrayIcon.IHost.ConfigPath => ClippyConfig.Path;

    string LinuxTrayIcon.IHost.SaveClip(bool shortClip)
    {
        var seconds = shortClip ? config.ShortClipSeconds : config.LongClipSeconds;
        lock (exportGate)
        {
            if (exporting)
                return "";
            exporting = true;
        }

        try
        {
            return ExportClip(seconds) ?? "";
        }
        finally
        {
            lock (exportGate)
                exporting = false;
        }
    }

    /// <summary>
    /// Hot-swaps the captured audio device. The pipeline restarts in place against the same ring,
    /// so the audio timeline continues on the shared clock and clips cut across the switch stay
    /// in sync; the old pipeline's final frames are flushed by its Dispose.
    /// </summary>
    void LinuxTrayIcon.IHost.SetAudioDevice(string device)
    {
        Console.WriteLine($"Audio: switching to '{device}'.");
        lock (audioRestartGate)
        {
            audio?.Dispose();
            audio = null;
            config.AudioDevice = device;
            StartAudio();
        }
    }

    private readonly object audioRestartGate = new();

    /// <summary>
    /// Applies bitrate/codec/encoder changes by restarting the video pipeline in place: the old
    /// gst dies completely (Dispose waits for its exit, so the NVENC session and its VRAM are
    /// released) before the new one starts, and the ring is reused -- the replay history and the
    /// shared clock survive the switch, clips cut across it stay aligned. The encoder name is read
    /// back from the config by the pipeline's own Start, so copying all three into the config
    /// before restarting is what makes a live encoder switch possible at all.
    /// </summary>
    void LinuxTrayIcon.IHost.SetVideoEncoding(int bitrateMbps, string codec, string encoder)
    {
        if (bitrateMbps == config.VideoBitrateMbps &&
            string.Equals(codec, config.VideoCodec, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(encoder, config.VideoEncoder, StringComparison.OrdinalIgnoreCase) &&
            video is not null)
            return; // nothing changed

        lock (videoRestartGate)
        {
            config.VideoBitrateMbps = bitrateMbps;
            config.VideoCodec = codec;
            config.VideoEncoder = encoder;
            video?.Dispose();
            video = null;

            if (sessionFd is null)
            {
                Console.WriteLine("Video: settings saved; the pipeline restarts with the next recording.");
                return;
            }

            video = new LinuxVideoPipeline(stopwatch);
            if (!video.Start(sessionFd.Value, sessionId, sessionWidth, sessionHeight, config, logsDirectory, videoRing!))
            {
                Console.WriteLine("Video: the restarted pipeline failed; recording continues without video.");
                video.Dispose();
                video = null;
                return;
            }
            Console.WriteLine($"Video: restarted [{(config.UseHevc ? "HEVC" : "H.264")}] at {bitrateMbps} Mbit/s " +
                $"[{config.VideoEncoder}].");
        }
    }

    private readonly object videoRestartGate = new();
    private int? sessionFd;
    private uint sessionId;
    private int sessionWidth, sessionHeight;

    // The sink list feeds every menu rebuild, and Plasma re-queries the menu on a timer: without
    // a cache that is one pactl process per poll. Fifteen seconds is fresh enough for hotplugs.
    private List<(string Name, string Description)>? sinkCache;
    private DateTime sinkCacheTime = DateTime.MinValue;
    private static readonly TimeSpan SinkCacheTtl = TimeSpan.FromSeconds(15);

    IReadOnlyList<(string Name, string Description)> LinuxTrayIcon.IHost.ListAudioSinks() =>
        ListAudioSinksCached();

    /// <summary>
    /// The sink list feeds every menu rebuild, and Plasma re-queries the menu on a timer: without
    /// a cache that is one pactl process per poll. Fifteen seconds is fresh enough for hotplugs.
    /// The pairs come from `pactl list sinks` (LinuxAudioPipeline.ListSinks): the technical Name
    /// for the config and the `-f pulse` monitor source, the Description for the menu label --
    /// never the short table, whose third column is "PipeWire" for every device.
    /// </summary>
    private IReadOnlyList<(string Name, string Description)> ListAudioSinksCached()
    {
        if (sinkCache is not null && DateTime.UtcNow - sinkCacheTime < SinkCacheTtl)
            return sinkCache;

        var sinks = LinuxAudioPipeline.ListSinks();
        if (sinks.Count == 0)
            return sinks; // a failed probe is not cached: the next menu open retries
        sinkCache = sinks;
        sinkCacheTime = DateTime.UtcNow;
        return sinks;
    }

    void LinuxTrayIcon.IHost.RequestExit() => Stop("the tray asked for it");

    /// <summary>
    /// Points the recorder at the folder the user just picked. Exports write to it from the next
    /// save on; the spoolers and rings keep running unchanged, so nothing about the replay depth
    /// or the shared clock moves.
    /// </summary>
    void LinuxTrayIcon.IHost.SetClipsFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Recording: could not create the clips folder {folder} ({ex.Message}).");
        }
        outputDirectory = folder;
        Console.WriteLine($"Recording: clips now go to {outputDirectory}.");
    }

    /// <summary>The tray picked a new hotkey pair: push it into every live capture backend at once.</summary>
    void LinuxTrayIcon.IHost.RebindHotkeys(string shortKey, string longKey)
    {
        if (hotkeys is null)
        {
            Console.WriteLine("Settings: no hotkey service to rebind; takes effect next session.");
            return;
        }
        hotkeys.Rebind(shortKey, longKey);
    }

    /// <summary>
    /// Starts a fresh copy of this executable with the same arguments, then quits this one -- the
    /// same restart the Windows tray performs: the new process launches BEFORE this one stops, so
    /// there is no window where nothing is recording, and the user's own arguments are replayed
    /// verbatim.
    /// </summary>
    void LinuxTrayIcon.IHost.RestartProcess()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.WriteLine("Tray: restart needs a real executable path, which this run does not have.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
                startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
            Console.WriteLine("Tray: a new instance was started; this one is shutting down.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            // Staying alive is the right answer here: quitting after a failed launch would take
            // the recorder away over a menu item that did not work.
            Console.WriteLine($"Tray: could not start a new instance ({ex.Message}); still running.");
            return;
        }

        Stop("Restart was chosen in the tray");
    }

    public void Dispose()
    {
        // Audio first, then video, then the spools: the same teardown order as ScreenCapture, so an
        // export still in flight finds the spoolers usable for as long as the encoders could push.
        hotkeys?.Dispose();
        tray?.Dispose();
        tray = null;
        audio?.Dispose();
        audio = null;
        video?.Dispose();
        video = null;
        portal?.Dispose();
        portal = null;
        durationTimer?.Dispose();
        midExportTimer?.Dispose();
        videoSpool?.Dispose();
        videoSpool = null;
        audioSpool?.Dispose();
        audioSpool = null;
        stopped.Dispose();
    }
}
#endif
