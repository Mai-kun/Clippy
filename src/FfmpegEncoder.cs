using System.Diagnostics;

namespace Clippy;

/// <summary>
/// Writes captured frames straight into a child ffmpeg process as raw BGRA over stdin.
/// No ring buffer: the caller serialises writes, and a slow encoder simply drops capture pace.
///
/// Audio is NOT handled here: it runs in its own process (AudioEncoder). Two live inputs in one
/// ffmpeg deadlocked — measured 6 audio packets read out of ~450 while video flowed fine.
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly Process process;
    private readonly Stream stdin;
    private readonly string outputPath;
    private readonly Stopwatch stopwatch;
    private readonly StreamWriter timingLog;
    private readonly StreamWriter debugLog;
    private readonly FfmpegStartupGate gate;
    private int frameCount;
    private int startedWrites;
    private bool disposed;

    private FfmpegEncoder(
        Process process,
        Stream stdin,
        string outputPath,
        Stopwatch stopwatch,
        StreamWriter timingLog,
        StreamWriter debugLog,
        FfmpegStartupGate gate)
    {
        this.process = process;
        this.stdin = stdin;
        this.outputPath = outputPath;
        this.stopwatch = stopwatch;
        this.timingLog = timingLog;
        this.debugLog = debugLog;
        // The SAME instance the stderr handler feeds, otherwise WaitForReady would wait on a gate
        // that never receives anything.
        this.gate = gate;
    }

    public string OutputPath => outputPath;

    public static string TimingLogPath(string outputPath) => Path.ChangeExtension(outputPath, ".timing.csv");

    public static string DebugLogPath(string outputPath) => Path.ChangeExtension(outputPath, ".ffmpeg-debug.log");

    public static FfmpegEncoder Start(
        string outputPath,
        int width,
        int height,
        string encoder,
        Stopwatch stopwatch,
        string fpsMode = "vfr")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
            File.Delete(outputPath);

        // Per-frame ground truth for PTS verification: swCaptureMs is the Stopwatch reading when the
        // frame arrived, swWriteDoneMs is when the last byte hit ffmpeg's stdin, systemTimeMs is the
        // WGC SystemRelativeTime (Windows' own capture clock), so pipeline delay is measurable.
        var timingPath = TimingLogPath(outputPath);
        var timingLog = new StreamWriter(timingPath, append: false) { AutoFlush = true };
        timingLog.WriteLine("frame,swCaptureMs,swWriteDoneMs,systemTimeMs,payloadBytes");

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
        {
            // debug level is the only level that prints per-packet timing ("*** dropping frame N at ts M"),
            // which is how the frame loss below was diagnosed. It is written to <clip>.ffmpeg-debug.log.
            "-hide_banner", "-loglevel", "debug", "-y",
            // passthrough (the default) keeps every wallclock PTS exactly as WGC produced it.
            // vfr instead resamples onto the 1/25 grid implied by the rawvideo 25 tbr default and
            // drops whatever does not fit: measured 658 frames captured -> 346 encoded.
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-s", $"{width}x{height}",
            // Confirmed over three 40-60 s runs: 0 dropped frames, PTS drift -0.71 us/frame, and a
            // constant ~9 ms pipeline delay (GPU copy + 8 MB write) against the capture instant.
            // That offset is constant, so it shifts the whole timeline without accumulating.
            // It is a demuxer option, so it must precede the -i it applies to.
            "-use_wallclock_as_timestamps", "1",
            "-thread_queue_size", "1024",
            "-i", "-",
            "-an", "-fps_mode", fpsMode,
            "-c:v", encoder, "-preset", "p1", "-b:v", "8M",
            "-maxrate", "12M", "-bufsize", "16M",
            "-g", "60", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            outputPath,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg. Is it installed and on PATH?");

        // ffmpeg's debug log is the evidence for where frames go; keep it next to the video.
        // The handler must be attached before BeginErrorReadLine, and the async reader owns stderr
        // from then on, so Dispose must not call ReadToEnd.
        var debugLog = new StreamWriter(Path.ChangeExtension(outputPath, ".ffmpeg-debug.log"), append: false)
        {
            AutoFlush = true,
        };
        var gate = new FfmpegStartupGate();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            gate.OnLine(e.Data);
            lock (debugLog)
                debugLog.WriteLine(e.Data);
        };
        process.BeginErrorReadLine();

        var stdin = process.StandardInput.BaseStream;
        return new FfmpegEncoder(process, stdin, outputPath, stopwatch, timingLog, debugLog, gate);
    }

    /// <summary>
    /// Blocks until ffmpeg opened its input. MUST be called only after the first Write: a rawvideo
    /// input is probed by actually reading a frame, so ffmpeg cannot reach the ready marker until
    /// data arrives. Waiting before writing would deadlock against the write that unblocks it.
    /// </summary>
    public bool WaitForReady(TimeSpan timeout)
    {
        var ready = gate.WaitForReady(timeout);
        if (!ready)
            Console.WriteLine("Video: ffmpeg did not report input-ready within the timeout; continuing anyway.");
        return ready;
    }

    public void Write(byte[] bgraPixels, double systemTimeMs)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(FfmpegEncoder));

        var index = ++startedWrites;
        var captureMs = stopwatch.Elapsed.TotalMilliseconds;

        // Log BEFORE the pipe write: the row is proof the write was attempted. A row with no
        // matching DONE is a write that is still blocked inside stdin.Write.
        timingLog!.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{index},{captureMs:F3},0,{systemTimeMs:F3},{bgraPixels.Length}"));
        EventLog.Mark("WRITE_ENTER", index.ToString());
        EventLog.Mark("STDIN_BEFORE", index.ToString());

        stdin.Write(bgraPixels);

        EventLog.Mark("STDIN_AFTER", index.ToString());
        frameCount++;
        timingLog.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{index},{captureMs:F3},{stopwatch.Elapsed.TotalMilliseconds:F3},{systemTimeMs:F3},{bgraPixels.Length}"));
        EventLog.Mark("WRITE_DONE", index.ToString());
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        // Closing stdin is ffmpeg's end-of-stream signal; it then finalises the mp4 moov atom.
        try
        {
            stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ffmpeg died first; the exit code below reports it.
        }

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("ffmpeg did not exit within 30s and was killed.");
        }

        // stderr is consumed by the async reader above; wait for it to drain so the log is complete.
        process.WaitForExit();

        // Release the file handles before reading: a live StreamWriter locks its file exclusively.
        var debugPath = DebugLogPath(outputPath);
        // The stderr reader thread writes debugLog under lock(debugLog); Dispose must take the same
        // lock or it can dispose the writer mid-write.
        lock (debugLog)
        {
            timingLog.Dispose();
            debugLog.Dispose();
        }
        var stderr = string.Join(Environment.NewLine, File.ReadAllLines(debugPath));

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {stderr}");

        if (stderr.Length > 0)
            Console.WriteLine($"ffmpeg log: {debugPath}");

        process.Dispose();
    }
}
