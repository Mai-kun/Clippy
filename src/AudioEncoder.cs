using System.Collections.Concurrent;
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// Encodes system audio in its own ffmpeg process, writing raw PCM to its own stdin.
///
/// Why a separate process: one ffmpeg with two live inputs deadlocked. With both open at once it
/// read 6 audio packets out of ~450 while video flowed perfectly, because the demuxer interleaves
/// reads and one side starves the other. Two processes have one input each, so neither blocks the
/// other. PTS still come from -use_wallclock_as_timestamps, the same system clock as the video, so
/// both files start at the same wallclock zero and the final -c copy mux needs no offset.
///
/// ponytail: WASAPI delivers 10 ms packets, so a bounded queue (default 512, ~5 s) absorbs bursts
/// without unbounded growth. Overflow is counted, never silent.
/// </summary>
internal sealed class AudioEncoder : IDisposable
{
    private readonly Process process;
    private readonly Stream stdin;
    private readonly Stopwatch stopwatch;
    private readonly StreamWriter timingLog;
    private readonly StreamWriter debugLog;
    private readonly string debugLogPath;
    private readonly FfmpegStartupGate gate;
    private readonly ConcurrentQueue<byte[]> queue = new();
    private readonly AutoResetEvent signal = new(false);
    private readonly int queueLimit;
    private Thread? pump;
    private volatile bool pumpRunning;
    private int bufferCount;
    private int droppedBuffers;
    private bool disposed;

    private AudioEncoder(
        Process process,
        Stream stdin,
        Stopwatch stopwatch,
        StreamWriter timingLog,
        StreamWriter debugLog,
        string debugLogPath,
        FfmpegStartupGate gate,
        int queueLimit)
    {
        this.process = process;
        this.stdin = stdin;
        this.stopwatch = stopwatch;
        this.timingLog = timingLog;
        this.debugLog = debugLog;
        this.debugLogPath = debugLogPath;
        this.gate = gate;
        this.queueLimit = queueLimit;
    }

    public static AudioEncoder Start(string path, AudioCapture format, Stopwatch stopwatch, int queueLimit = 512)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.Delete(path);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var arguments = new List<string>
        {
            "-hide_banner", "-loglevel", "debug", "-y",
            // Identical mechanism to video: each buffer is stamped by the wallclock at read time,
            // which is what puts both files on one timeline. Demuxer option, so it precedes -i.
            "-use_wallclock_as_timestamps", "1",
            "-thread_queue_size", "1024",
        };
        arguments.AddRange(format.FormatArguments);
        arguments.AddRange(
        [
            "-i", "-",
            "-vn",
            // Muxed later with -c copy, so the encoder here only has to be cheap and stable.
            "-c:a", "aac", "-b:a", "192k",
            "-movflags", "+faststart",
            path,
        ]);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg. Is it installed and on PATH?");

        var debugLogPath = Path.ChangeExtension(path, ".ffmpeg-debug.log");
        var debugLog = new StreamWriter(debugLogPath, append: false) { AutoFlush = true };
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

        var timingLog = new StreamWriter(Path.ChangeExtension(path, ".timing.csv"), append: false) { AutoFlush = true };
        var encoder = new AudioEncoder(
            process,
            process.StandardInput.BaseStream,
            stopwatch,
            timingLog,
            debugLog,
            debugLogPath,
            gate,
            queueLimit);
        timingLog.WriteLine("buffer,swCaptureMs,bytes,totalBytes,qpcPosition");
        encoder.pumpRunning = true;
        encoder.pump = new Thread(encoder.Drain) { IsBackground = true, Name = "clippy-audio-encoder" };
        encoder.pump.Start();
        return encoder;
    }

    /// <summary>
    /// Blocks until ffmpeg has opened its input. Safe here because an f32le input probes without
    /// data (the log shows "frames:0"), so ffmpeg reaches the ready marker on its own.
    /// </summary>
    public bool WaitForReady(TimeSpan timeout) => gate.WaitForReady(timeout);

    /// <summary>Queues one buffer. Never blocks: a dedicated thread owns ffmpeg's stdin.</summary>
    public void Write(byte[] samples, long qpcPosition)
    {
        if (disposed)
            return;

        if (queue.Count >= queueLimit)
        {
            droppedBuffers++;
            return;
        }

        var now = stopwatch.Elapsed.TotalMilliseconds;
        queue.Enqueue(samples);
        signal.Set();

        lock (timingLog)
        {
            timingLog.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{++bufferCount},{now:F3},{samples.Length},{0},{qpcPosition}"));
        }
    }

    private void Drain()
    {
        while (pumpRunning || !queue.IsEmpty)
        {
            signal.WaitOne(200);
            while (queue.TryDequeue(out var buffer))
            {
                try
                {
                    // No Flush: ~100 tiny pipe writes per second was measurably worse for ffmpeg
                    // than letting the pipe coalesce them. The stream is byte-oriented, so both the
                    // content and the order are unchanged.
                    stdin.Write(buffer, 0, buffer.Length);
                }
                catch (IOException)
                {
                    // ffmpeg closed stdin; stop rather than spin on a broken pipe.
                    return;
                }
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        // Drain what is queued first: those buffers are real audio, and closing stdin early would
        // silently cut the tail of the recording.
        pumpRunning = false;
        signal.Set();
        pump?.Join(TimeSpan.FromSeconds(10));

        try
        {
            stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ffmpeg already gone; the exit code below reports it.
        }

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Audio ffmpeg did not exit within 30s and was killed.");
        }

        timingLog.Dispose();
        debugLog.Dispose();

        if (process.ExitCode != 0)
        {
            var errors = File.ReadAllLines(debugLogPath)
                .Where(line => line.Contains("Error", StringComparison.OrdinalIgnoreCase))
                .Take(3);
            throw new InvalidOperationException(
                $"Audio ffmpeg exited with {process.ExitCode}: {string.Join(" | ", errors)}");
        }

        Console.WriteLine(droppedBuffers > 0
            ? $"Audio: {bufferCount} buffers, {droppedBuffers} dropped (encoder could not keep up)."
            : $"Audio: {bufferCount} buffers encoded, none dropped.");
    }
}

