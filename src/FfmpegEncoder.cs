using System.Buffers;
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
            FileName = FfmpegLocator.Executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
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
            // Per-frame wallclock PTS, unchanged from parts A-C: confirmed 0 dropped frames, no PTS
            // drift (-0.71 us/frame) and a constant ~9 ms pipeline delay. Phase 4.2 briefly removed it
            // to test whether absolute timestamps broke -force_key_frames; it did not, so it stays.
            "-use_wallclock_as_timestamps", "1",
            "-thread_queue_size", "1024",
            "-i", "-",
            "-an", "-fps_mode", fpsMode,
            "-c:v", encoder, "-preset", "p1", "-b:v", "8M",
            "-maxrate", "12M", "-bufsize", "16M",
            // -g is a FRAME count, and WGC delivers a variable frame rate (measured 11-55 fps), so a
            // keyframe every N frames would land at an unpredictable interval in seconds. Phase 4.2
            // needs clip cutting accurate in real time, so the I-frame interval is forced in seconds.
            //
            // -forced-idr is REQUIRED with h264_nvenc: without it this ffmpeg build silently ignores
            // -force_key_frames. Measured on a clean CFR source, 15 s at 25 fps:
            //   h264_nvenc + -force_key_frames            ->  2 I-frames  (broken)
            //   h264_nvenc + -force_key_frames -forced-idr 1 -> 15 I-frames  (correct)
            //   libx264    + -force_key_frames            -> 15 I-frames  (correct, so it is NVENC-specific)
            "-force_key_frames", "expr:gte(t,n_forced*1)",
            "-forced-idr", "1",
            "-pix_fmt", "yuv420p",
            // Raw Annex B elementary stream, not an MP4 container. Phase 4: an elementary stream has
            // no container timestamps at all, which is exactly the problem mp4 caused in B/C (it
            // rebased start_time to 0 while m4a kept epoch, destroying the A/V relationship).
            // Raw Annex B to STDOUT, not to a file: the ring buffer slices these NAL units in memory.
            // An elementary stream carries no timestamps at all, which is exactly why every packet
            // gets its CaptureClockSeconds stamped here, on arrival, outside ffmpeg.
            "-f", "h264", "pipe:1",
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

    private Thread? drain;
    private byte[]? sps;
    private byte[]? pps;
    private RingBuffer? ring;

    // Real capture times, taken in OnFrameArrived before any processing, in the order the frames were
    // handed to ffmpeg. The elementary stream on stdout carries no timestamps at all, and timestamping
    // at Read() time is far too coarse: one 64K read covers several frames, so they all collapse onto
    // the same clock value and the muxer sees duplicate DTS. So the time is carried across here, in
    // order, and each VCL NAL on the way out consumes the next one.
    private readonly Queue<double> captureTimes = new();
    private int captureTimeCount;
    private int accessUnitCount;

    /// <summary>
    /// Records the capture time of a frame that is about to be written to ffmpeg. Must be called
    /// immediately before <see cref="Write"/> so the queue order matches the byte order.
    /// </summary>
    public void EnqueueCaptureTime(double captureClockSeconds)
    {
        lock (captureTimes)
        {
            var n = captureTimeCount++;
            if (n < 10)
            {
                Console.WriteLine($"[vq] in  #{n} ts={captureClockSeconds:F4} depth={captureTimes.Count}");
            }

            captureTimes.Enqueue(captureClockSeconds);
        }
    }

    /// <summary>Frames written in, versus access units seen out. Equal means no drops or reordering.</summary>
    public (int Captured, int AccessUnits) TimingCounts => (captureTimeCount, accessUnitCount);

    /// <summary>
    /// Reads ffmpeg's stdout on a background thread, splits it into NAL units and pushes each into the
    /// ring buffer stamped with the capture clock at the moment the bytes ARRIVED. The elementary stream
    /// carries no timestamps at all, so this is the only place the timeline is established.
    /// </summary>
    public void StartDrain(RingBuffer target)
    {
        ring = target;
        var stdout = process.StandardOutput.BaseStream;
        var parser = new H264AnnexBParser();
        var buffer = new byte[64 * 1024];
        var clock = stopwatch;

        drain = new Thread(() =>
        {
            try
            {
                int read;
                while ((read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    parser.Append(buffer.AsSpan(0, read), clock.Elapsed.TotalSeconds, Push);
                }

                parser.Flush(clock.Elapsed.TotalSeconds, Push);
            }
            catch (IOException)
            {
                // ffmpeg closed stdout on shutdown; packets already pushed stay in the ring.
            }
        })
        { IsBackground = true, Name = "clippy-h264-drain" };
        drain.Start();
        Console.WriteLine($"[sync] video drain thread start at {clock.Elapsed.TotalSeconds:F3}s");
    }

    private void Push(byte[] data, int length, bool isIdr, double readClockSeconds)
    {
        var type = data[0] & 0x1F;
        if (type == 7)
        {
            if (sps is null)
            {
                sps = data;
                Console.WriteLine($"[params] first SPS seen at {readClockSeconds:R} ({length} bytes)");
            }
        }
        else if (type == 8)
        {
            if (pps is null)
            {
                pps = data;
                Console.WriteLine($"[params] first PPS seen at {readClockSeconds:R} ({length} bytes)");
            }
        }

        // Only a VCL NAL is a picture, so only a VCL NAL consumes a capture time. Non-VCL NALs travel
        // with whatever picture follows them and must not shift the timeline.
        var isVcl = type is >= 1 and <= 5;
        var captureSeconds = readClockSeconds;
        if (isVcl)
        {
            lock (captureTimes)
            {
                if (captureTimes.Count > 0)
                {
                    captureSeconds = captureTimes.Dequeue();
                }

                var m = accessUnitCount++;
                if (m < 10)
                {
                    // remaining AFTER this dequeue: how many timestamps were still waiting when the
                    // encoder produced this access unit. A large value means the encoder is lagging
                    // behind the capture; a small one means they are in step.
                    Console.WriteLine($"[vq] out #{m} ts={captureSeconds:F4} remaining={captureTimes.Count}");
                }
            }
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        data.CopyTo(rented, 0);
        ring!.Push(rented, length, captureSeconds, isIdr);
    }

    /// <summary>Parameter sets captured from the live stream, needed for the muxer avcC box.</summary>
    public byte[]? Sps => sps;

    public byte[]? Pps => pps;

    /// <summary>Blocks until the stdout drain thread has finished, or the timeout expires.</summary>
    public void WaitForDrain(TimeSpan timeout) => drain?.Join(timeout);



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




