#if LINUX
using System.Buffers;
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// The Linux audio half: the default sink's monitor (what the speakers play) re-encoded to AAC by
/// ffmpeg, frame by frame, into the ring buffer.
///
/// The input is PulseAudio's "-f pulse" demuxer, which under PipeWire is exactly the monitor of
/// whatever sink <c>pactl get-default-sink</c> names -- the same system sound the Windows loopback
/// captures, no per-application plumbing. The output is raw ADTS on stdout, the same contract the
/// Windows FfmpegAudioEncoder gives the muxer.
///
/// Timestamps follow the Windows audio timeline rule: frame k sits at anchor + k * 1024/48000
/// seconds, where the anchor is the arrival instant of the first frame. AAC-LC frames are always
/// 1024 samples, so the ordinal is exact regardless of how ffmpeg batches its writes, and a stretch
/// of digital silence -- which the monitor delivers as real samples, never as a gap -- cannot make
/// the timeline drift.
/// </summary>
internal sealed class LinuxAudioPipeline : IDisposable
{
    public const int SampleRate = 48000;
    public const int SamplesPerFrame = 1024;

    public RingBuffer Ring { get; } = new(LinuxVideoPipeline.RamHeadSeconds, isVideo: false);

    /// <summary>ADTS frames in versus frames pushed (they are the same frames; kept for the accounting print).</summary>
    public (int FramesIn, int FramesOut) Counts => (framesIn, framesIn);

    private Process? ffmpeg;
    private readonly Stopwatch stopwatch;
    private volatile bool stopped;
    private int framesIn;
    private double anchorSeconds;
    private int anchorSet;

    public LinuxAudioPipeline(Stopwatch stopwatch) => this.stopwatch = stopwatch;

    /// <summary>
    /// The monitor source for the system's sound: "&lt;default-sink&gt;.monitor". An explicit
    /// config.AudioDevice names a sink, which gets the same ".monitor" treatment -- unless the user
    /// already wrote the full monitor source name. An unresolvable device falls back to the default
    /// sink, because a stale config value must not cost the user their audio.
    /// </summary>
    public static string ResolveMonitorSource(string configured)
    {
        var defaultSink = RunPactl("get-default-sink")?.Trim();
        if (string.IsNullOrEmpty(defaultSink))
        {
            Console.WriteLine("Audio: could not read the default sink from pactl; falling back to PulseAudio's @DEFAULT_MONITOR@.");
            return "";
        }

        if (string.IsNullOrWhiteSpace(configured) ||
            string.Equals(configured, "default", StringComparison.OrdinalIgnoreCase))
            return $"{defaultSink}.monitor";

        if (configured.EndsWith(".monitor", StringComparison.Ordinal))
            return configured;

        var sources = RunPactl("list", "short", "sources") ?? "";
        var candidate = $"{configured}.monitor";
        if (sources.Contains(candidate, StringComparison.Ordinal))
            return candidate;

        Console.WriteLine($"Audio: configured device '{configured}' has no monitor source; using the default sink's ({defaultSink}).");
        return $"{defaultSink}.monitor";
    }

    private static string? RunPactl(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo { FileName = "pactl", RedirectStandardOutput = true };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts ffmpeg: the monitor in, AAC ADTS out. Returns false when ffmpeg could not be started.
    /// </summary>
    public bool Start(string monitorSource, string? logDirectory, RingBuffer ring)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "debug", "-y",
            "-f", "pulse",
            // @DEFAULT_MONITOR@ is PulseAudio's magic name for the default sink's monitor -- the
            // right fallback when pactl could not be consulted at all, and never the microphone.
            "-i", string.IsNullOrEmpty(monitorSource) ? "@DEFAULT_MONITOR@" : monitorSource,
            "-thread_queue_size", "1024",
            // Re-encoded here, muxed later with -c copy, so the encoder only has to be cheap.
            "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
            // Raw ADTS elementary stream on stdout, like the Windows audio path: the ring slices
            // these frames in memory.
            "-f", "adts", "pipe:1",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            ffmpeg = Process.Start(startInfo);
            if (ffmpeg is null)
                return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"Audio: could not start ffmpeg ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }

        var debugLog = LogPaths.CreateCapped(LogPaths.Resolve(logDirectory, "clippy-audio", ".ffmpeg-debug.log"));
        ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (debugLog)
                debugLog.WriteLine(e.Data);
        };
        ffmpeg.BeginErrorReadLine();

        Console.WriteLine("Audio: capturing the system sound as AAC.");
        StartDrain(ffmpeg, ring);
        return true;
    }

    private void StartDrain(Process ffmpegProcess, RingBuffer target)
    {
        var stdout = ffmpegProcess.StandardOutput.BaseStream;
        var parser = new AdtsFrameParser();
        var buffer = new byte[32 * 1024];
        var thread = new Thread(() =>
        {
            try
            {
                int read;
                while (!stopped && (read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    var arrival = stopwatch.Elapsed.TotalSeconds;
                    parser.Append(buffer.AsSpan(0, read), arrival, (frame, length, captureSeconds) =>
                    {
                        // The timeline anchor is the first frame's arrival; every later frame is
                        // placed by its ordinal, never by its arrival time.
                        if (Interlocked.CompareExchange(ref anchorSet, 1, 0) == 0)
                            anchorSeconds = captureSeconds;

                        var k = Interlocked.Increment(ref framesIn) - 1;
                        var time = anchorSeconds + k * SamplesPerFrame / (double)SampleRate;

                        var rented = ArrayPool<byte>.Shared.Rent(length);
                        frame.AsSpan(0, length).CopyTo(rented);
                        // ADTS frames are self-contained: every packet is a keyframe.
                        target.Push(rented, length, time, isKeyframe: true);
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // ffmpeg closed stdout on shutdown; packets already pushed stay in the ring.
            }
        })
        { IsBackground = true, Name = "clippy-audio-drain" };
        thread.Start();
    }

    /// <summary>The Windows encoder's reconciliation print, fed from the same counters.</summary>
    public void PrintSampleAccounting() =>
        Console.WriteLine($"[audioacct] adtsFramesOut={framesIn} anchored at {anchorSeconds:F3}s.");

    public void WaitForDrain(TimeSpan timeout)
    {
    }

    public void Dispose()
    {
        stopped = true;
        if (ffmpeg is { HasExited: false } process)
        {
            try { process.StandardInput.Close(); } catch (Exception) { /* the encoder never reads stdin */ }
            if (!process.WaitForExit(5000))
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
        }
        ffmpeg?.Dispose();
    }
}
#endif
