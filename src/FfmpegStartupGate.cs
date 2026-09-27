using System.Collections.Concurrent;

namespace Clippy;

/// <summary>
/// Waits until ffmpeg reports that it opened its input, so writes never race process startup.
///
/// ponytail: matches a literal marker line rather than probing the process. ffmpeg blocks in
/// avformat_find_stream_info until it has input data, so this must be called AFTER the first frame
/// has been written for a rawvideo input (measured: "pos: 8294400 bytes read: frames:1"), otherwise
/// the wait would deadlock against the very write that unblocks it.
/// </summary>
internal sealed class FfmpegStartupGate
{
    private const string ReadyMarker = "Successfully opened the file.";

    private readonly ConcurrentQueue<string> lines = new();
    private readonly AutoResetEvent signal = new(false);
    private volatile bool seen;

    public FfmpegStartupGate() { }

    public void OnLine(string line)
    {
        lines.Enqueue(line);
        if (!seen && line.Contains(ReadyMarker, StringComparison.Ordinal))
        {
            seen = true;
            signal.Set();
        }
    }

    /// <summary>Returns true once ffmpeg is reading, false on timeout. Never throws.</summary>
    public bool WaitForReady(TimeSpan timeout)
    {
        if (seen)
            return true;

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!seen)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
                break;

            signal.WaitOne((int)Math.Min(remaining, 200));

            // Re-scan: the marker may already be queued even when the event was consumed earlier.
            while (lines.TryDequeue(out var line))
            {
                if (line.Contains(ReadyMarker, StringComparison.Ordinal))
                    seen = true;
            }
        }

        return seen;
    }
}
