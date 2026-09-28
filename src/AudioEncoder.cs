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

    public static AudioEncoder Start(string path, AudioCapture format, Stopwatch stopwatch, RingBuffer ring, double masterZeroSeconds, int queueLimit = 512)
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
            RedirectStandardOutput = true,
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
            // Re-encoded here, muxed later with -c copy, so the encoder only has to be cheap.
            "-c:a", "aac", "-b:a", "192k",
            // Raw ADTS elementary stream, not an m4a container. Phase 4: no container timestamps at
            // all, so there is nothing to disagree with the video stream about where time starts.
            // Raw ADTS to STDOUT, like the video path: the ring buffer slices these frames in memory
            "-f", "adts", "pipe:1",
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
        encoder.sampleRate = format.SampleRate;
        encoder.masterZeroSeconds = masterZeroSeconds;
        encoder.bytesPerSampleFrame = format.BlockAlign;
        timingLog.WriteLine("buffer,swCaptureMs,bytes,totalBytes,qpcPosition");
        encoder.pumpRunning = true;
        encoder.pump = new Thread(encoder.Drain) { IsBackground = true, Name = "clippy-audio-encoder" };
        encoder.pump.Start();
        encoder.StartDrain(ring);
        return encoder;
    }

    private Thread? drain;
    private RingBuffer? ring;

    // Audio timeline anchor: the shared-clock time of the FIRST audio sample. Every ADTS frame then
    // gets an exact time from its ordinal, because AAC-LC frames are always 1024 samples:
    //     time(frame k) = anchor + k * 1024 / sampleRate
    //
    // This replaces reading the clock when the bytes come back out of the pipe, which carried the
    // whole AAC + queue + pipe latency into every timestamp and appeared as a constant ~245 ms
    // audio offset. It also does NOT need a per-packet FIFO: at 48 kHz a 10 ms WASAPI packet is
    // 480 samples, so packets and AAC frames are not 1:1 (~2.13 packets per frame). One Dequeue per
    // frame would be wrong; the sample counter is exact regardless of packet sizes.
    private double anchorSeconds;
    private int sampleRate;
    private int frameOrdinal;
    private int anchorSet;

    /// <summary>The single hardware zero shared with the video track. Set once, at Start.</summary>
    private double masterZeroSeconds;

    // Sample accounting: queued in, written to ffmpeg's stdin, dropped before it was ready, and
    // ADTS frames that came back out. queued - dropped should equal written, and written/1024
    // should equal the frame count to within one AAC frame.
    private long queuedSamples;
    private long writtenSamples;
    private long droppedSamples;

    /// <summary>Bytes per PCM sample frame (channels * bytes/sample), taken from the real endpoint format.</summary>
    private int bytesPerSampleFrame = 8;

    /// <summary>Prints the sample/frame reconciliation. Called once the drains are joined.</summary>
    public void PrintSampleAccounting()
    {
        var expectedFrames = (double)writtenSamples / 1024.0;
        Console.WriteLine($"[audioacct] queued={queuedSamples} written={writtenSamples} dropped={droppedSamples} " +
            $"queued-written={queuedSamples - writtenSamples} adtsFramesOut={frameOrdinal} " +
            $"written/1024={expectedFrames:F2} diff={frameOrdinal - expectedFrames:F2} " +
            $"(one AAC frame is {1024} samples)");
    }

    /// <summary>
    /// Reads ffmpeg's stdout on a background thread, splits it into ADTS frames and pushes each into
    /// the ring buffer stamped with the capture clock at the moment the bytes ARRIVED, mirroring the
    /// video path. The elementary stream carries no timestamps, so this is where the audio timeline
    /// is established.
    /// </summary>
    public void StartDrain(RingBuffer target)
    {
        ring = target;
        var stdout = process.StandardOutput.BaseStream;
        var parser = new AdtsFrameParser();
        var buffer = new byte[32 * 1024];
        var clock = stopwatch;

        drain = new Thread(() =>
        {
            try
            {
                int read;
                while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // The timestamp argument is ignored: the frame's real time comes from its ordinal
                    // on the audio timeline, never from when the bytes happened to be read.
                    parser.Append(buffer.AsSpan(0, read), 0, (data, length, t) =>
                    {
                        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
                        data.CopyTo(rented, 0);
                        ring!.Push(rented, length, NextFrameTime(), isKeyframe: true);
                    });
                }
            }
            catch (IOException)
            {
                // ffmpeg closed stdout on shutdown; packets already pushed stay in the ring.
            }
        })
        { IsBackground = true, Name = "clippy-adts-drain" };
        drain.Start();
        Console.WriteLine($"[sync] audio drain thread start at {stopwatch.Elapsed.TotalSeconds:F3}s");
    }

    /// <summary>Blocks until the stdout drain thread has finished, or the timeout expires.</summary>
    public void WaitForDrain(TimeSpan timeout) => drain?.Join(timeout);

    /// <summary>Shared-clock time of the next AAC frame, from its ordinal. Never the read time.</summary>
    private double NextFrameTime()
    {
        var k = frameOrdinal++;
        return anchorSeconds + (k * 1024.0 / sampleRate);
    }

    /// <summary>
    /// Self-test: at 48 kHz AAC-LC, frame k sits at k*1024/48000 seconds, and that must not depend
    /// on how the input was split into WASAPI packets. A per-packet FIFO would fail this.
    /// </summary>
    public static bool SelfTest()
    {
        const int rate = 48000;
        const double step = 1024.0 / 48000.0;
        Console.WriteLine($"[selftest] step = {step:R} s/frame");
        foreach (var packetSamples in new[] { 480, 512, 1024, 2048 })
        {
            var total = 0; var buffers = 0; var frames = 0; var times = new List<double>();
            while (total < rate) { total += packetSamples; buffers++; while ((frames + 1) * 1024 <= total) { times.Add(frames * step); frames++; } }
            var expectedFrames = total / 1024;
            var sample = string.Join(", ", times.Take(5).Select(t => t.ToString("F6")));
            Console.WriteLine($"[selftest] packet={packetSamples} buffers={buffers} totalSamples={total} expectedFrames={expectedFrames} actualFrames={frames} firstTimes=[{sample}]");
            if (frames != expectedFrames) { Console.WriteLine($"[selftest] FAIL packet={packetSamples}: frames {frames} != expected {expectedFrames}"); return false; }
            for (var k = 0; k < times.Count; k++)
            {
                if (Math.Abs(times[k] - (k * step)) > 1e-12) { Console.WriteLine($"[selftest] FAIL packet={packetSamples} k={k}: {times[k]:R} != {(k * step):R}"); return false; }
                if (k > 0 && times[k] <= times[k - 1]) { Console.WriteLine($"[selftest] FAIL packet={packetSamples} k={k}: not increasing"); return false; }
            }
        }
        Console.WriteLine("[selftest] OK");
        return true;
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

        // Anchor the audio timeline to the first real buffer, on the SAME shared clock the video
        // uses. Everything after this is derived from the frame ordinal, so ffmpeg's encoder and
        // pipe latency can never leak into a timestamp.
        if (Interlocked.CompareExchange(ref anchorSet, 1, 0) == 0)
        {
            // Both tracks subtract the SAME hardware zero, taken once before WGC and WASAPI existed.
            // Deriving the zero here instead -- from Stopwatch.GetTimestamp() minus elapsed -- put
            // "zero" hundreds of milliseconds late, after the audio device had been created, and that
            // entire delay landed in every audio timestamp.
            anchorSeconds = (qpcPosition / (double)Stopwatch.Frequency) - masterZeroSeconds;
            Console.WriteLine($"[sync] audio anchored at {anchorSeconds:F3}s from the first packet's " +
                $"qpcPosition minus masterZero={masterZeroSeconds:F3}, {sampleRate} Hz");
        }

        if (queue.Count >= queueLimit)
        {
            droppedBuffers++;
            Interlocked.Add(ref droppedSamples, samples.Length / bytesPerSampleFrame);
            return;
        }

        Interlocked.Add(ref queuedSamples, samples.Length / bytesPerSampleFrame);

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
                Interlocked.Add(ref writtenSamples, buffer.Length / bytesPerSampleFrame);
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

        // Same lock discipline as FfmpegEncoder: the stderr reader thread writes debugLog.
        lock (debugLog)
        {
            timingLog.Dispose();
            debugLog.Dispose();
        }

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







