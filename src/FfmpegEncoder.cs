using System.Diagnostics;

namespace Clippy;

/// <summary>
/// Writes captured frames straight into a child ffmpeg process as raw BGRA over stdin.
/// No ring buffer: the caller serialises writes, and a slow encoder simply drops capture pace.
/// </summary>
internal sealed class FfmpegEncoder : IDisposable
{
    private readonly Process process;
    private readonly Stream stdin;
    private readonly string outputPath;
    private readonly Stopwatch stopwatch;
    private readonly StreamWriter timingLog;
    private readonly StreamWriter debugLog;
    private int frameCount;
    private bool disposed;

    private FfmpegEncoder(
        Process process,
        Stream stdin,
        string outputPath,
        Stopwatch stopwatch,
        StreamWriter timingLog,
        StreamWriter debugLog)
    {
        this.process = process;
        this.stdin = stdin;
        this.outputPath = outputPath;
        this.stopwatch = stopwatch;
        this.timingLog = timingLog;
        this.debugLog = debugLog;
    }

    public string OutputPath => outputPath;

    public static string TimingLogPath(string outputPath) => Path.ChangeExtension(outputPath, ".timing.csv");

    public static string DebugLogPath(string outputPath) => Path.ChangeExtension(outputPath, ".ffmpeg-debug.log");

    public static FfmpegEncoder Start(string outputPath, int width, int height, string encoder, Stopwatch stopwatch, string fpsMode = "vfr")
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

        // Reconnect on any dropped frame so the stream never desyncs mid-file.
        foreach (var argument in new[]
        {
            // debug level is the whole point here: it is the only level that prints per-packet timing.
            "-hide_banner", "-loglevel", "debug", "-y",
            // VFR: with wallclock timestamps, bursty WGC delivery gives several frames the same PTS,
            // and the CFR default would dedupe them on top of that.
            // Do NOT use -fps_mode passthrough here: with NVENC and sparse delivery it stalls and
            // never writes the moov atom, leaving an unplayable file.
            "-f", "rawvideo", "-pix_fmt", "bgra",
            "-s", $"{width}x{height}",
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
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (debugLog)
                    debugLog.WriteLine(e.Data);
        };
        process.BeginErrorReadLine();

        var stdin = process.StandardInput.BaseStream;
        return new FfmpegEncoder(process, stdin, outputPath, stopwatch, timingLog, debugLog);
    }

    public void Write(byte[] bgraPixels, double systemTimeMs)
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(FfmpegEncoder));

        var captureMs = stopwatch.Elapsed.TotalMilliseconds;
        stdin.Write(bgraPixels);
        frameCount++;
        timingLog!.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{frameCount},{captureMs:F3},{stopwatch.Elapsed.TotalMilliseconds:F3},{systemTimeMs:F3},{bgraPixels.Length}"));
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
        timingLog.Dispose();
        debugLog.Dispose();
        var stderr = string.Join(Environment.NewLine, File.ReadAllLines(debugPath));

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {stderr}");

        if (stderr.Length > 0)
            Console.WriteLine($"ffmpeg log: {debugPath}");

        process.Dispose();
    }
}
