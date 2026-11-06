#if LINUX
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Clippy;

/// <summary>
/// The Linux video half: portal → PipeWire → gst → ffmpeg → ring buffer, with the screen's pixels
/// never entering managed memory.
///
/// Two external processes in one pipe. gst-launch-1.0 reads the portal's stream ("pipewiresrc
/// fd=N" -- N is the descriptor inherited from this process, without close-on-exec, because the
/// stream exists only inside the portal's PipeWire instance) and converts to raw BGRx frames.
/// ffmpeg consumes those as rawvideo and encodes with the same arguments, the same GOP policy and
/// the same Annex-B-to-stdout contract as the Windows FfmpegVideoEncoder. Both processes are
/// kernel-pipe neighbours, so the C# side only ever sees the encoded bytes.
///
/// Timestamps are established HERE, on arrival at the Annex B parser, exactly as on Windows: the
/// elementary stream carries no times, so nothing inside either process can influence the timeline.
/// The 8 KB read size bounds the collapse a single read can cause: at 6 Mbit/s it is ~10 ms of
/// stream, far below the pipe latency that already sits on top.
/// </summary>
internal sealed partial class LinuxVideoPipeline : IDisposable
{
    public const double RamHeadSeconds = 32;
    public const double RingSeconds = 190;

    public RingBuffer Ring { get; } = new(RamHeadSeconds, isVideo: true);

    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }
    public byte[]? Vps { get; private set; }

    /// <summary>
    /// Encoded access units pushed into the ring. The raw frame count is unknowable here -- frames
    /// travel from gst to ffmpeg without passing through this process -- so both halves of the
    /// Windows pair report the same number, which is why the ExportClip mismatch check always sees
    /// 1:1 on Linux.
    /// </summary>
    public (int Captured, int AccessUnits) TimingCounts => (accessUnits, accessUnits);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public double Framerate { get; private set; } = 60;
    public bool IsHevc { get; private set; }

    private Process? gst;
    private Process? ffmpeg;
    private StreamWriter? ffmpegDebugLog;
    private string? fifoPath;
    private readonly Stopwatch stopwatch;
    private volatile bool stopped;
    private int accessUnits;

    // The arguments Start() was given, kept so the pipeline can rebuild itself after the child
    // processes die on their own (suspend/resume, a PipeWire restart, a monitor mode change).
    private int startFd;
    private uint startNode;
    private ClippyConfig startConfig = null!;
    private string? startLogDirectory;
    private RingBuffer startRing = null!;

    // Consecutive self-restarts without a single frame in between: the backoff grows 2 s, 4 s,
    // 8 s, 10 s, 10 s... A pipeline that dies the moment the machine wakes is retried politely,
    // not hammered. Reset to zero the moment the new pipeline delivers a frame.
    private int restartAttempts;

    public LinuxVideoPipeline(Stopwatch stopwatch) => this.stopwatch = stopwatch;

    /// <summary>
    /// Which gst encoder element the single-process path wants: the NVENC one matching the
    /// configured codec. Null when the config explicitly asks for a software encoder ("libx...").
    /// "nvenc_direct" (the Windows default) means "hardware if possible" here too.
    /// </summary>
    private static string? DesiredNvencElement(ClippyConfig config) =>
        config.VideoEncoder.StartsWith("libx", StringComparison.OrdinalIgnoreCase)
            ? null
            : (config.UseHevc ? "nvh265enc" : "nvh264enc");

    /// <summary>
    /// Which encoder ffmpeg is asked for in the fallback path: the NVENC one matching the
    /// configured codec, or the software one.
    /// </summary>
    private static string DesiredFfmpegEncoder(ClippyConfig config) =>
        config.VideoEncoder.StartsWith("libx", StringComparison.OrdinalIgnoreCase)
            ? SoftwareEncoder(config.UseHevc)
            : (config.UseHevc ? "hevc_nvenc" : "h264_nvenc");

    /// <summary>
    /// True once gst-inspect confirms the element exists. Probed once per element per run and
    /// cached: spawning gst-inspect is cheap, spawning it per frame would not be.
    /// </summary>
    private static bool GstHasElement(string element)
    {
        if (elementProbes.TryGetValue(element, out var known))
            return known;
        try
        {
            // --exists prints NOTHING and exits 0/1: a plain gst-inspect would dump the full
            // element description into a pipe nobody drains and hang until the timeout.
            var probe = Process.Start(new ProcessStartInfo("gst-inspect-1.0", "--exists " + element)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is null)
                return elementProbes[element] = false;
            probe.WaitForExit(10_000);
            return elementProbes[element] = probe.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return elementProbes[element] = false;
        }
    }

    private static readonly Dictionary<string, bool> elementProbes = [];

    private static string SoftwareEncoder(bool hevc) => hevc ? "libx265" : "libx264";

    /// <summary>
    /// Starts the pipeline for an already-negotiated portal session. Two paths, best first:
    ///
    /// 1. ONE gst process does everything: pipewiresrc -> videorate (cap 60 fps) -> videoconvert
    ///    -> nvh265enc/nvh264enc -> parse -> byte-stream on stdout. The encoded stream is
    ///    ~0.75 MB/s at 6 Mbit/s, so relaying it through this process costs nothing, and the raw
    ///    pixels never leave the encoder's address space. Geometry comes from the portal's own
    ///    stream properties (gst's -v dump goes to stdout and would corrupt this stream).
    /// 2. Fallback: gst captures to raw BGRx through a FIFO and ffmpeg encodes -- the stage-2
    ///    pipeline, kept for machines without the nvcodec plugin.
    /// </summary>
    /// <returns>false when no pipeline could be started at all.</returns>
    public bool Start(int pipeWireFd, uint nodeId, int width, int height, ClippyConfig config, string? logDirectory, RingBuffer ring)
    {
        startFd = pipeWireFd;
        startNode = nodeId;
        startConfig = config;
        startLogDirectory = logDirectory;
        startRing = ring;
        IsHevc = config.UseHevc;
        Width = width;
        Height = height;
        Framerate = 60;

        // The inherited descriptor must not be closed on exec, or the gst child would inherit a
        // number pointing at nothing. Received without MSG_CMSG_CLOEXEC it is already clear; this
        // is the belt to that brace.
        ClearCloseOnExec(pipeWireFd);

        return StartPipeline();
    }

    /// <summary>
    /// Builds the two-path pipeline against the remembered start arguments. Runs on the caller
    /// thread for the initial start and on the (ended) drain thread for every automatic restart,
    /// which is why it must never touch the ring's construction state -- the ring is reused as-is.
    /// </summary>
    private bool StartPipeline()
    {
        var pipeWireFd = startFd;
        var config = startConfig;

        // Path 1: single-process hardware encoding. A config that explicitly asks for a software
        // encoder ("libx...") skips straight to the ffmpeg fallback.
        var nvencElement = DesiredNvencElement(config);
        if (nvencElement is not null && GstHasElement(nvencElement))
        {
            gst = StartGstEncoder(pipeWireFd, startNode, nvencElement, config);
            if (gst is not null)
            {
                StartDrain(gst.StandardOutput.BaseStream, startRing);
                // A pipeline that failed to negotiate (bad GPU state, rejected encoder) exits
                // within a couple of seconds; a live one blocks on stdout. Same probe window the
                // Windows StartWithFallback uses.
                var stderrLines = DrainGstStderr(gst);
                Thread.Sleep(1500);
                if (!gst.HasExited)
                {
                    Console.WriteLine($"Video: encoding {Width}x{Height} [{nvencElement}] at " +
                        $"{config.VideoBitrateMbps} Mbit/s in a single gst process, GOP 1 s, no B-frames, " +
                        $"codec {(IsHevc ? "HEVC" : "H.264")}.");
                    return true;
                }

                Console.WriteLine("Video: the single-process gst pipeline exited immediately. Last stderr:");
                while (stderrLines.TryDequeue(out var line))
                    Console.WriteLine($"  {line}");
                Console.WriteLine("Video: falling back to the gst capture + ffmpeg encode pipeline.");
                gst.Dispose();
                gst = null;
            }
        }

        // Path 2: capture and encode in two processes, pixels through a FIFO.
        MakeVideoFifo();
        var fifo = fifoPath!;
        gst = StartGstCapture(pipeWireFd, startNode, fifo);
        if (gst is null || gst.HasExited)
        {
            Console.WriteLine("Video: gst-launch could not be started -- is gst-launch-1.0 installed?");
            return false;
        }

        // A failing pipewiresrc (dead stream, refused fd) exits within a couple of seconds; a
        // working one blocks on the pipe into ffmpeg. This wait catches the first kind without
        // delaying the second by anything a user could notice.
        var fallbackStderr = DrainGstStderr(gst);
        Thread.Sleep(1500);
        if (gst.HasExited)
        {
            Console.WriteLine("Video: gst exited immediately -- the portal stream is dead. Last stderr:");
            while (fallbackStderr.TryDequeue(out var line))
                Console.WriteLine($"  {line}");
            return false;
        }
        Console.WriteLine($"Video: capture {Width}x{Height} at {Framerate:F0} fps via pipewiresrc fd={pipeWireFd}.");

        var encoderName = DesiredFfmpegEncoder(config);
        ffmpeg = StartFfmpeg(encoderName, config.VideoBitrateMbps, fifo, startLogDirectory);
        if (ffmpeg is null)
            return false;

        // Same probe idea as the Windows StartWithFallback: a rejected encoder (no GPU, no module)
        // makes ffmpeg exit within milliseconds, so a short window is a reliable signal.
        if (!encoderName.StartsWith("libx", StringComparison.OrdinalIgnoreCase))
        {
            Thread.Sleep(700);
            if (ffmpeg.HasExited)
            {
                var failed = encoderName;
                encoderName = SoftwareEncoder(IsHevc);
                Console.WriteLine($"Video: {failed} is not available on this machine (ffmpeg exited with " +
                    $"{ffmpeg.ExitCode}). Falling back to {encoderName}.");
                ffmpeg.Dispose();
                ffmpeg = StartFfmpeg(encoderName, config.VideoBitrateMbps, fifo, startLogDirectory);
                if (ffmpeg is null)
                    return false;
            }
        }

        Console.WriteLine($"Video: encoding {Width}x{Height} [{encoderName}] at {config.VideoBitrateMbps} Mbit/s, " +
            $"GOP 1 s, no B-frames, codec {(IsHevc ? "HEVC" : "H.264")}.");
        StartDrain(ffmpeg.StandardOutput.BaseStream, startRing);
        return true;
    }

    /// <summary>
    /// The raw BGRx pixels travel gst -> ffmpeg through a named pipe, never through this process:
    /// 1920x1080 at 60 fps is ~500 MB/s, and a managed relay would burn the CPU the encoder is
    /// meant to save. ffmpeg reads the FIFO directly, so the two processes are kernel-pipe
    /// neighbours exactly as they would be behind a shell's |.
    /// </summary>
    private void MakeVideoFifo()
    {
        // Non-null from here on: Start() fails loudly if creation fails, and Dispose cleans it up.
        fifoPath = Path.Combine(Path.GetTempPath(), $"clippy-video-{Environment.ProcessId}.fifo");
        try { File.Delete(fifoPath); } catch (Exception) { /* did not exist */ }
        if (mkfifo(fifoPath, 0x180 /* 0600 */) != 0)
            throw new IOException($"Video: mkfifo {fifoPath} failed (errno {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Path 1: the whole capture+encode in ONE gst process, Annex B on stdout. videorate caps the
    /// delivered frame rate at 60 fps (a 144 Hz monitor would otherwise flood the encoder);
    /// repeat-sequence-header + h265parse config-interval=-1 put VPS/SPS/PPS before every IDR, so
    /// a mid-stream clip always starts decodable and the parser sees parameter sets immediately.
    /// </summary>
    private Process? StartGstEncoder(int pipeWireFd, uint nodeId, string encoderElement, ClippyConfig config)
    {
        var parse = IsHevc ? "h265parse" : "h264parse";
        var byteStreamCaps = IsHevc
            ? "video/x-h265,stream-format=byte-stream,alignment=au"
            : "video/x-h264,stream-format=byte-stream,alignment=au";
        var startInfo = new ProcessStartInfo
        {
            FileName = "gst-launch-1.0",
            UseShellExecute = false,
            // stdout IS the encoded Annex B stream; stderr carries diagnostics.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            // path= (NOT target-object=): pipewiresrc treats a numeric target-object as a node
            // SERIAL, while the portal hands out node IDS -- and without a usable target the
            // stream dies with "target not found". keepalive-time resends the last frame on a
            // static screen, which is what keeps the ring fresh while nothing moves; do-timestamp
            // stamps buffers on arrival.
            "pipewiresrc", $"fd={pipeWireFd}", $"path={nodeId}", "keepalive-time=1000", "always-copy=false", "do-timestamp=true", "!",
            "video/x-raw", "!", "videorate", "!", "video/x-raw,framerate=60/1", "!",
            // ALL-CUDA PATH: cudaupload DMAs the BGRx frame into VRAM and NVENC eats BGRx
            // directly (its CUDAMemory sink template lists BGRx/BGRA/RGBA) -- the colour
            // conversion to NV12 happens inside the encoder hardware, so videoconvert and its
            // ~57% of a CPU core are simply gone. No cudaconvert needed: nothing to convert.
            "cudaupload", "!", "video/x-raw(memory:CUDAMemory),format=BGRx", "!", encoderElement,
            "bitrate=" + config.VideoBitrateMbps * 1000, "gop-size=60", "bframes=0",
            "rc-mode=vbr", "zerolatency=true", "repeat-sequence-header=true", "!",
            parse, "config-interval=-1", "!", byteStreamCaps, "!", "fdsink", "fd=1",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Video: could not start gst-launch-1.0 ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    private Process? StartGstCapture(int pipeWireFd, uint nodeId, string fifo)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gst-launch-1.0",
            UseShellExecute = false,
            RedirectStandardOutput = true, // the state chatter; discarded, never the pixel path
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            // NO -v, deliberately: its caps dump goes to stdout -- and even discarded here, it is
            // noise. The geometry comes from the portal's own stream properties.
            //
            // path= (NOT target-object=): pipewiresrc treats a numeric target-object as a node
            // SERIAL, while the portal hands out node IDS -- and without a usable target the
            // stream dies with "target not found". keepalive-time resends the last frame on a
            // static screen, which is what keeps the ring fresh while nothing moves.
            "pipewiresrc", $"fd={pipeWireFd}", $"path={nodeId}", "keepalive-time=1000", "always-copy=false", "!",
            "video/x-raw", "!", "videoconvert", "!",
            // BGRx maps byte-for-byte onto ffmpeg's bgra rawvideo input.
            "video/x-raw,format=BGRx", "!", "filesink", $"location={fifo}",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Video: could not start gst-launch-1.0 ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Drains gst's stderr on a background thread, so error text is available for the failure
    /// report and the pipe can never back up. Diagnostic only.
    /// </summary>
    private static ConcurrentQueue<string> DrainGstStderr(Process process)
    {
        var lines = new ConcurrentQueue<string>();
        var readerThread = new Thread(() =>
        {
            try
            {
                var stderr = process.StandardError;
                string? line;
                while ((line = stderr.ReadLine()) is not null)
                    lines.Enqueue(line);
            }
            catch (Exception)
            {
                // The process is gone.
            }
        })
        { IsBackground = true, Name = "clippy-gst-stderr" };
        readerThread.Start();
        return lines;
    }

    private Process? StartFfmpeg(string encoderName, int bitrateMbps, string fifo, string? logDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "debug", "-y",
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-s", $"{Width}x{Height}",
            // All input options precede the -i they apply to. Same wallclock-PTS policy as the
            // Windows encoder; the container-side PTS are ignored either way, because every output
            // NAL is stamped here on arrival.
            "-use_wallclock_as_timestamps", "1",
            "-thread_queue_size", "1024",
            "-framerate", Framerate.ToString("F3", CultureInfo.InvariantCulture),
            "-i", fifo,
            "-an", "-fps_mode", "passthrough",
        }
        .Concat(CodecArguments(encoderName, bitrateMbps))
        .Concat(new[]
        {
            // Keyframe every second, forced by wallclock time so the GOP lands at a predictable
            // interval regardless of the delivered frame rate. -forced-idr is REQUIRED for nvenc
            // and unknown to libx*, mirroring the Windows argument list exactly.
            "-force_key_frames", "expr:gte(t,n_forced*1)",
            "-pix_fmt", "yuv420p",
            "-f", IsHevc ? "hevc" : "h264", "pipe:1",
        }))
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
                return null;

            ffmpegDebugLog = LogPaths.CreateCapped(
                LogPaths.Resolve(logDirectory, $"clippy-video-{encoderName}", ".ffmpeg-debug.log"));
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null || ffmpegDebugLog is null)
                    return;
                lock (ffmpegDebugLog)
                    ffmpegDebugLog.WriteLine(e.Data);
            };
            process.BeginErrorReadLine();
            return process;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Video: could not start ffmpeg ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    /// <summary>The Windows encoder's rate-control arguments, verbatim in structure.</summary>
    private static IEnumerable<string> CodecArguments(string encoderName, int bitrateMbps)
    {
        var isSoftware = encoderName.StartsWith("libx", StringComparison.OrdinalIgnoreCase);
        if (isSoftware)
        {
            // ultrafast and CRF-only, for the same reason as on Windows: a real-time recorder cannot
            // afford a slow preset, and a CRF target IS the quality setting -- adding -b:v on top
            // of it would only fight it.
            return ["-c:v", encoderName, "-preset", "ultrafast", "-crf", "20"];
        }

        return
        [
            "-c:v", encoderName, "-preset", "p1", "-b:v", $"{bitrateMbps}M",
            "-maxrate", $"{bitrateMbps * 1.5:F0}M", "-bufsize", $"{bitrateMbps * 2}M",
            "-forced-idr", "1",
        ];
    }

    /// <summary>
    /// Reads ffmpeg's stdout on a background thread, splits it into NAL units and pushes each into
    /// the ring stamped with the arrival clock. Parameter sets are kept for the muxer (Sps/Pps/Vps)
    /// and NOT pushed: the muxer reads them from the avcC/hvcC box, so spending ring slots on them
    /// would be pure overhead. Every other NAL -- pictures and the SEIs that precede them -- is
    /// pushed and lets ExportClip's picture-NAL filter do its usual job.
    /// </summary>
    private void StartDrain(Stream stdout, RingBuffer target)
    {
        var parser = new AnnexBParser(IsHevc);
        var buffer = new byte[8 * 1024]; // small reads: 8 KB is ~10 ms of stream at 6 Mbit/s
        var thread = new Thread(() =>
        {
            // Arrival timestamps within one read chunk are identical, and a read can carry several
            // pictures; a muxer fed two identical PTS reports non-monotonic DTS. Each pushed packet
            // therefore advances at least 1 ms past the previous one -- far below the pipe latency
            // the arrival time already carries, and enough to keep every PTS strictly increasing.
            double lastStamp = -1;
            var endReason = "stdout closed";
            try
            {
                var read = 0;
                while (!stopped && (read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // Data flows again: whatever killed the previous pipeline, this one lives.
                    Interlocked.Exchange(ref restartAttempts, 0);
                    var arrival = stopwatch.Elapsed.TotalSeconds;
                    parser.Append(buffer.AsSpan(0, read), arrival, (nal, length, isIdr, captureSeconds) =>
                    {
                        var span = nal.AsSpan(0, length);
                        if (IsParameterSet(span, IsHevc))
                        {
                            StoreParameterSet(span);
                            return;
                        }

                        var time = Math.Max(captureSeconds, lastStamp + 0.001);
                        lastStamp = time;

                        var rented = ArrayPool<byte>.Shared.Rent(length);
                        span.CopyTo(rented);
                        target.Push(rented, length, time, isIdr);
                        Interlocked.Increment(ref accessUnits);
                    });
                }
                if (read == 0)
                    endReason = "stdout reached EOF";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                endReason = $"{ex.GetType().Name}: {ex.Message}";
                // ffmpeg closed stdout on shutdown; what was pushed stays in the ring.
            }

            // An EOF we did NOT cause (stopped stays false during our own Dispose) means the
            // children died underneath us -- suspend, a PipeWire restart, a mode change. Rebuild
            // the pipeline instead of silently recording nothing while the tray still shows life.
            if (!stopped)
                HandlePipelineEnd(endReason);
        })
        { IsBackground = true, Name = "clippy-video-drain" };
        thread.Start();
    }

    /// <summary>
    /// The pipeline ended on its own: wait out the backoff, kill whatever is left of the old
    /// children, and rebuild. Runs on the ended drain thread, so Run() keeps going undisturbed;
    /// repeated failures back off 2 s, 4 s, 8 s and then hold at 10 s until the machine (or
    /// PipeWire) is really back.
    /// </summary>
    private void HandlePipelineEnd(string reason)
    {
        if (stopped)
            return;

        var attempt = Interlocked.Increment(ref restartAttempts);
        var delayMs = Math.Min(10_000, 2000 << Math.Min(attempt - 1, 3));
        Console.WriteLine($"Video: the capture pipeline closed on its own ({reason}); " +
            $"restarting in {delayMs / 1000.0:F0} s (attempt {attempt}).");
        Thread.Sleep(delayMs);
        if (stopped)
            return;

        KillChildren();
        if (StartPipeline())
            Console.WriteLine("Video: the capture pipeline is back; the replay ring kept its history.");
        else
            Console.WriteLine("Video: the restart failed; the next drain end retries with a longer backoff.");
    }

    /// <summary>The child processes and the FIFO, without the stopped flag or the debug log -- shared by Dispose and the restart.</summary>
    private void KillChildren()
    {
        try
        {
            // Closing ffmpeg's stdin makes it finalise and flush the elementary stream, exactly as
            // the Windows encoder's teardown does. The gst side dies with the pipe.
            if (ffmpeg is { HasExited: false } process)
            {
                try { process.StandardInput.Close(); } catch (Exception) { /* already gone */ }
                if (!process.WaitForExit(3000))
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            }
            if (gst is { HasExited: false } g)
            {
                try
                {
                    g.Kill(entireProcessTree: true);
                    g.WaitForExit(3000); // reap: a killed-but-unwaited child stays a zombie
                }
                catch (Exception) { /* already gone */ }
            }
        }
        finally
        {
            ffmpeg?.Dispose();
            gst?.Dispose();
            ffmpeg = null;
            gst = null;
            if (fifoPath is not null)
            {
                try { File.Delete(fifoPath); } catch (Exception) { /* never created */ }
                fifoPath = null;
            }
        }
    }

    /// <summary>
    /// Parameter-set detection by NAL type. The header layout is the codec's, not a guess:
    /// H.264 has a one-byte header with the type in the low 5 bits (SPS 7, PPS 8); HEVC has a
    /// two-byte header with the type in bits 1-6 (VPS 32, SPS 33, PPS 34).
    /// </summary>
    private static bool IsParameterSet(ReadOnlySpan<byte> nal, bool hevc)
    {
        if (nal.Length == 0)
            return false;
        var type = hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
        return type is 7 or 8 or 32 or 33 or 34;
    }

    private void StoreParameterSet(ReadOnlySpan<byte> nal)
    {
        var type = IsHevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;
        var copy = nal.ToArray();
        switch (type)
        {
            case 32: Vps = copy; break;
            case 33: Sps = copy; break;
            case 34: Pps = copy; break;
            case 7: Sps = copy; break;
            case 8: Pps = copy; break;
        }
    }

    /// <summary>
    /// Clears FD_CLOEXEC on the portal descriptor. Received without MSG_CMSG_CLOEXEC it should
    /// already be clear; fcntl is the verification that the gst child really inherits it.
    /// </summary>
    private static void ClearCloseOnExec(int fd)
    {
        const int fGetfd = 1;
        const int fSetfd = 2;
        var flags = fcntl(fd, fGetfd);
        if (flags >= 0 && (flags & 1) != 0)
            fcntl(fd, fSetfd, flags & ~1);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int command, int arguments = 0);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int mkfifo(string path, uint mode);

    /// <summary>Blocks until the drain has consumed what ffmpeg emitted. The drain thread exits of its own when stdout closes.</summary>
    public void WaitForDrain(TimeSpan timeout)
    {
    }

    public void Dispose()
    {
        stopped = true;
        try
        {
            KillChildren();
        }
        finally
        {
            ffmpegDebugLog?.Dispose();
        }
    }
}
#endif
