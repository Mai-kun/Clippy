using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Clippy;

/// <summary>
/// One ordered event log shared by every thread (WGC frame callback, WASAPI callback, ffmpeg stderr
/// readers), stamped from the same Stopwatch as the recording itself.
///
/// ponytail: appends under one lock to a StreamWriter with AutoFlush. A contention bug is exactly
/// what this is meant to expose, so the log deliberately does NOT add per-event locking of its own
/// beyond the single append lock -- if two threads appear in one instant, that is the signal.
/// </summary>
internal static class EventLog
{
    private static readonly object Gate = new();
    private static StreamWriter? writer;
    private static long sequence;

    public static string? Path { get; private set; }

    public static void Start(string path)
    {
        Path = path;
        writer = new StreamWriter(path, append: false) { AutoFlush = true };
        Mark("LOG", "start");
    }

    public static void Mark(string name, string? detail = null)
    {
        var w = writer;
        var clock = Clock;
        if (w is null || clock is null)
            return;

        lock (Gate)
        {
            w.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{Interlocked.Increment(ref sequence)},{clock.Elapsed.TotalMilliseconds:F3}," +
                $"{Environment.CurrentManagedThreadId},{Thread.CurrentThread.Name ?? "-"},{name},{detail}"));
        }
    }

    /// <summary>The one clock every timestamp comes from: the recording's own Stopwatch.</summary>
    public static Stopwatch? Clock { get; set; }
}
