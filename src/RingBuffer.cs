using System.Buffers;

namespace Clippy;

/// <summary>
/// Time-ordered packet store. One instance per stream: video and audio are never interleaved here,
/// their only shared reference is the capture clock stamped on each packet.
///
/// Trim rules differ by stream, because only video has inter-frame dependencies:
///  - video: drop by time, then advance the head to the first keyframe so a slice never starts inside
///    a GOP. An ADTS audio frame is self-contained, so audio trims purely by time.
/// </summary>
internal sealed class RingBuffer
{
    private readonly List<StoredPacket> packets = [];
    private readonly double maxSeconds;
    private readonly bool isVideo;
    private int head;

    /// <summary>Set by the self-test to prove the trim assertions can actually fail.</summary>
    internal bool TrimDisabledForTest;

    public RingBuffer(double maxSeconds, bool isVideo)
    {
        this.maxSeconds = maxSeconds;
        this.isVideo = isVideo;
    }

    public int Count => packets.Count - head;

    public double OldestSeconds => Count > 0 ? packets[head].CaptureClockSeconds : 0;

    public double NewestSeconds => Count > 0 ? packets[^1].CaptureClockSeconds : 0;

    public IReadOnlyList<StoredPacket> Packets
    {
        get
        {
            var view = new List<StoredPacket>(Count);
            for (var i = head; i < packets.Count; i++)
                view.Add(packets[i]);
            return view;
        }
    }

    /// <summary>
    /// The two encoders' drain threads push here while an export may be slicing at the same time,
    /// so every access to the list is serialised. Slice returns the ring's own StoredPacket objects
    /// (callers must not release them), which is why the lock can only cover the list traversal.
    /// </summary>
    private readonly object gate = new();

    /// <summary>Takes ownership of a rented buffer.</summary>
    public void Push(byte[] rented, int length, double captureClockSeconds, bool isKeyframe)
    {
        lock (gate)
        {
            packets.Add(new StoredPacket(rented, length, captureClockSeconds, isKeyframe));
            if (!TrimDisabledForTest)
            {
                Trim();
            }
        }
    }

    public void Trim()
    {
        while (Count > 1)
        {
            var span = packets[^1].CaptureClockSeconds - packets[head].CaptureClockSeconds;
            if (span <= maxSeconds)
                break;
            packets[head].Release();
            head++;
        }

        // A slice must start on a keyframe, otherwise it decodes from garbage until the next I-frame.
        if (isVideo)
        {
            while (Count > 1 && !packets[head].IsKeyframe)
            {
                packets[head].Release();
                head++;
            }
        }

        Compact();
    }

    /// <summary>
    /// Packets whose capture time falls in [from, to], COPIED into fresh buffers.
    ///
    /// Copies, not references, and that is deliberate. Trim() returns evicted buffers to the
    /// ArrayPool, so a slice holding the ring's own packets can be invalidated by a concurrent Push
    /// the moment the lock is released. Today ExportClip reads its slice synchronously, so this is
    /// safe by accident -- but phase 6 calls ExportClip from a hotkey on another thread, where the
    /// window is real. Copying a few hundred kilobytes once every few minutes is nothing; a
    /// use-after-return from the pool corrupts a clip in a way nobody can reproduce.
    /// </summary>
    public List<StoredPacket> Slice(double from, double to)
    {
        var result = new List<StoredPacket>();
        lock (gate)
        {
            for (var i = head; i < packets.Count; i++)
            {
                var t = packets[i].CaptureClockSeconds;
                if (t < from || t > to)
                    continue;
                var src = packets[i];
                var copy = new byte[src.Length];
                src.Span.CopyTo(copy);
                result.Add(new StoredPacket(copy, src.Length, src.CaptureClockSeconds, src.IsKeyframe));
            }
        }

        return result;
    }

    public void Clear()
    {
        for (var i = head; i < packets.Count; i++)
            packets[i].Release();
        packets.Clear();
        head = 0;
    }

    /// <summary>Drops consumed prefixes so the backing list cannot grow without bound.</summary>
    private void Compact()
    {
        if (head == 0)
            return;
        if (head >= 1024 || head * 2 >= packets.Count)
        {
            packets.RemoveRange(0, head);
            head = 0;
        }
    }
}
