#if LINUX
using System.Buffers;
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// The Linux audio half: the default sink's monitor (what the speakers play) -- optionally mixed
/// with the microphone -- re-encoded to AAC by ffmpeg, frame by frame, into the ring buffer.
///
/// The input is PulseAudio's "-f pulse" demuxer, which under PipeWire is exactly the monitor of
/// whatever sink <c>pactl get-default-sink</c> names -- the same system sound the Windows loopback
/// captures, no per-application plumbing. With <see cref="ClippyConfig.RecordMicrophone"/>, a
/// second "-f pulse" input carries the microphone and amix blends the two. The output is raw ADTS
/// on stdout, the same contract the Windows FfmpegAudioEncoder gives the muxer.
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
    private StreamWriter? debugLog;
    private readonly Stopwatch stopwatch;
    private volatile bool stopped;
    private int framesIn;
    private double anchorSeconds;
    private int anchorSet;

    // The arguments Start() was given, kept so the pipeline can rebuild itself when ffmpeg dies
    // on its own (suspend/resume, a PipeWire restart).
    private string startMonitorSource = "";
    private string? startMicSource;
    private string? startLogDirectory;
    private RingBuffer startRing = null!;

    // Consecutive self-restarts without a single AAC frame in between: the backoff grows 2 s,
    // 4 s, 8 s and holds at 10 s. Reset the moment the new pipeline delivers a frame.
    private int restartAttempts;

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

    /// <summary>
    /// The sinks with their real names, for the tray's Audio Device menu: `pactl list sinks`
    /// parsed into (technical Name, human Description) pairs. The short table (`pactl list short
    /// sinks`) is NOT used here on purpose: its third column is the sound server ("PipeWire") for
    /// every sink, which is exactly the "three devices called PipeWire" menu bug. `LC_ALL=C` (and
    /// LANG) pin the "Name:"/"Description:" keys against the user's locale translating them.
    /// The Name is the sink id the audio pipeline feeds to `-f pulse -i &lt;name&gt;.monitor`;
    /// the Description is what the menu shows.
    /// </summary>
    public static List<(string Name, string Description)> ListSinks()
    {
        var output = RunPactlLocalized("list", "sinks");
        var sinks = new List<(string, string)>();
        string? name = null;
        string? description = null;

        void FlushCurrent()
        {
            if (!string.IsNullOrEmpty(name))
                sinks.Add((name, description ?? ""));
            name = null;
            description = null;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            // Each block opens with "Sink #<index>"; a block's Name: always precedes its
            // Description:, and property keys inside the block use "key = value" spacing, so a
            // plain prefix match cannot cross into them.
            if (line.StartsWith("Sink #", StringComparison.Ordinal))
            {
                FlushCurrent();
            }
            else if (name is null && line.StartsWith("Name:", StringComparison.Ordinal))
            {
                name = line["Name:".Length..].Trim();
            }
            else if (description is null && line.StartsWith("Description:", StringComparison.Ordinal))
            {
                description = line["Description:".Length..].Trim();
            }
        }
        FlushCurrent();

        if (sinks.Count == 0)
            Console.WriteLine("Audio: pactl listed no sinks; the Audio Device menu will only offer Default (System).");
        return sinks;
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

    /// <summary>RunPactl, with the locale pinned to C so the output's keys never translate.</summary>
    private static string RunPactlLocalized(params string[] arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo { FileName = "pactl", RedirectStandardOutput = true };
            startInfo.Environment["LC_ALL"] = "C";
            startInfo.Environment["LANG"] = "C";
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            if (process is null)
                return "";
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    /// <summary>
    /// The microphone source for the mix, from config.MicrophoneDevice: "default" (or blank)
    /// follows <c>pactl get-default-source</c> -- unless that is a monitor, which is an output
    /// loopback and not a microphone. An explicit name must exist in <see cref="ListSources"/>;
    /// anything unresolvable returns null, which the pipeline reads as "no microphone" rather
    /// than an error.
    /// </summary>
    public static string? ResolveMicSource(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured) ||
            string.Equals(configured, "default", StringComparison.OrdinalIgnoreCase))
        {
            var defaultSource = RunPactl("get-default-source")?.Trim();
            if (string.IsNullOrEmpty(defaultSource))
            {
                Console.WriteLine("Audio: could not read the default source from pactl; the microphone is skipped.");
                return null;
            }
            if (defaultSource.EndsWith(".monitor", StringComparison.Ordinal))
            {
                Console.WriteLine($"Audio: the default source '{defaultSource}' is a monitor loopback, not a microphone; mic mixing is skipped.");
                return null;
            }
            return defaultSource;
        }

        var known = ListSources().Any(s => string.Equals(s.Name, configured, StringComparison.OrdinalIgnoreCase));
        if (!known)
        {
            Console.WriteLine($"Audio: the configured microphone '{configured}' does not exist; mic mixing is skipped.");
            return null;
        }
        return configured;
    }

    /// <summary>
    /// The real input devices for the tray's Microphone menu: <c>pactl list sources</c> under
    /// LC_ALL=C, parsed like ListSinks, with every *.monitor loopback excluded -- those are the
    /// outputs' echoes, not microphones.
    /// </summary>
    public static List<(string Name, string Description)> ListSources()
    {
        var output = RunPactlLocalized("list", "sources");
        var sources = new List<(string, string)>();
        string? name = null;
        string? description = null;

        void FlushCurrent()
        {
            if (!string.IsNullOrEmpty(name) && !name.EndsWith(".monitor", StringComparison.Ordinal))
                sources.Add((name, description ?? ""));
            name = null;
            description = null;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Source #", StringComparison.Ordinal))
            {
                FlushCurrent();
            }
            else if (name is null && line.StartsWith("Name:", StringComparison.Ordinal))
            {
                name = line["Name:".Length..].Trim();
            }
            else if (description is null && line.StartsWith("Description:", StringComparison.Ordinal))
            {
                description = line["Description:".Length..].Trim();
            }
        }
        FlushCurrent();

        if (sources.Count == 0)
            Console.WriteLine("Audio: pactl listed no microphone sources; the Microphone menu will only offer Default (System).");
        return sources;
    }

    /// <summary>
    /// Starts ffmpeg: the sink's monitor (and, when <paramref name="micSource"/> is set, the
    /// microphone mixed over it) in, AAC ADTS out. Returns false when ffmpeg could not be started
    /// at all. A microphone that dies at negotiation is rolled back to system-sound-only -- a clip
    /// without the mic is a pity; a clip without any sound is a loss.
    /// </summary>
    public bool Start(string monitorSource, string? micSource, string? logDirectory, RingBuffer ring)
    {
        startMonitorSource = monitorSource;
        startMicSource = micSource;
        startLogDirectory = logDirectory;
        startRing = ring;
        return StartFfmpegPipeline(withMic: micSource is not null);
    }

    /// <summary>Builds the ffmpeg process (and its drain) against the remembered start arguments.</summary>
    private bool StartFfmpegPipeline(bool withMic)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FfmpegLocator.Executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        // Input options precede the -i they apply to; with two inputs each carries its own
        // queue size. "default" as the monitor falls through to PulseAudio's @DEFAULT_MONITOR@.
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "debug", "-y",
            "-f", "pulse",
            "-thread_queue_size", "1024",
            // @DEFAULT_MONITOR@ is PulseAudio's magic name for the default sink's monitor -- the
            // right fallback when pactl could not be consulted at all, and never the microphone.
            "-i", string.IsNullOrEmpty(startMonitorSource) ? "@DEFAULT_MONITOR@" : startMonitorSource,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (withMic && startMicSource is not null)
        {
            foreach (var argument in new[]
            {
                "-f", "pulse",
                "-thread_queue_size", "1024",
                "-i", startMicSource,
                // Mix the mic over the system sound at full volume both ways: normalize=0 keeps
                // the levels the user set in their desktop, duration=first ends with the system
                // sound, dropout_transition=0 stops amix from swelling the gain when a stream
                // drops. The mix is re-encoded here, muxed later with -c copy.
                "-filter_complex", "[0:a][1:a]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[a]",
                "-map", "[a]",
            })
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        foreach (var argument in new[]
        {
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

        debugLog = LogPaths.CreateCapped(LogPaths.Resolve(startLogDirectory, "clippy-audio", ".ffmpeg-debug.log"));
        ffmpeg.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (debugLog)
                debugLog.WriteLine(e.Data);
        };
        ffmpeg.BeginErrorReadLine();

        if (withMic && startMicSource is not null)
        {
            // Same probe idea as the video pipeline: a rejected input (mic gone, busy device)
            // makes ffmpeg exit within a moment. Roll back to system-sound-only rather than
            // leaving the recording without any audio at all.
            Thread.Sleep(700);
            if (ffmpeg.HasExited)
            {
                Console.WriteLine($"Audio: the microphone mix failed to start (ffmpeg exited with {ffmpeg.ExitCode}); " +
                    "falling back to the system sound only.");
                KillFfmpeg();
                return StartFfmpegPipeline(withMic: false);
            }
        }

        Console.WriteLine(withMic && startMicSource is not null
            ? $"Audio: capturing the system sound + microphone '{startMicSource}' as AAC."
            : "Audio: capturing the system sound as AAC.");
        StartDrain(ffmpeg, startRing);
        return true;
    }

    private void KillFfmpeg()
    {
        if (ffmpeg is { HasExited: false } process)
        {
            try { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
            catch (Exception) { /* already gone */ }
        }
        ffmpeg?.Dispose();
        ffmpeg = null;
    }

    /// <summary>
    /// The ffmpeg process died on its own -- suspend/resume, a PipeWire restart. Wait out the
    /// backoff and rebuild on the ended drain thread; repeated failures back off 2 s, 4 s, 8 s
    /// and hold at 10 s until the sound server is really back.
    /// </summary>
    private void HandlePipelineEnd(string reason)
    {
        if (stopped)
            return;

        var attempt = Interlocked.Increment(ref restartAttempts);
        var delayMs = Math.Min(10_000, 2000 << Math.Min(attempt - 1, 3));
        Console.WriteLine($"Audio: the capture pipeline closed on its own ({reason}); " +
            $"restarting in {delayMs / 1000.0:F0} s (attempt {attempt}).");
        Thread.Sleep(delayMs);
        if (stopped)
            return;

        KillFfmpeg();
        if (StartFfmpegPipeline(withMic: startMicSource is not null))
            Console.WriteLine("Audio: the capture pipeline is back.");
        else
            Console.WriteLine("Audio: the restart failed; the next drain end retries with a longer backoff.");
    }

    private void StartDrain(Process ffmpegProcess, RingBuffer target)
    {
        var stdout = ffmpegProcess.StandardOutput.BaseStream;
        var parser = new AdtsFrameParser();
        var buffer = new byte[32 * 1024];
        var thread = new Thread(() =>
        {
            var endReason = "stdout closed";
            try
            {
                var read = 0;
                while (!stopped && (read = stdout.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // Data flows again: this pipeline lives, reset the restart backoff.
                    Interlocked.Exchange(ref restartAttempts, 0);
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
                if (read == 0)
                    endReason = "stdout reached EOF";
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                endReason = $"{ex.GetType().Name}: {ex.Message}";
                // ffmpeg closed stdout on shutdown; packets already pushed stay in the ring.
            }

            // An EOF we did NOT cause (stopped is set only by our own Dispose) means ffmpeg died
            // underneath us. Rebuild instead of silently recording no sound at all.
            if (!stopped)
                HandlePipelineEnd(endReason);
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
        // KillFfmpeg waits for the exit: without it a killed ffmpeg would sit as a zombie until
        // we exit. The debug log's descriptor follows.
        KillFfmpeg();
        debugLog?.Dispose();
        debugLog = null;
    }
}
#endif
