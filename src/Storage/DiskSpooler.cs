using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Clippy;

/// <summary>
/// Tier 2 of the hybrid replay buffer -- the disk spool tail.
///
/// The ring (Tier 1) only ever holds the freshest RAM head of the capture; once <see cref="RingBuffer"/>
/// trims a packet it is handed here instead of being returned to the pool. A single background
/// thread appends the evicted packets to segment files under %TEMP%\Clippy\spool (one segment per
/// <see cref="SegmentSeconds"/> of capture time) and deletes whole segments once the combined
/// RAM + disk history would exceed <c>maxHistorySeconds</c>.
///
/// Two invariants make the export path correct, and both matter:
///
///  1. Eviction is synchronous with removal. <see cref="Evict"/> is called by the ring while the
///     ring's own lock is held, so by the time a Slice returns, every packet no longer visible in
///     RAM is already in this spooler's queue. A packet can therefore never be in neither tier.
///  2. <see cref="Flush"/> waits for the queue to be fully written before a read, so ReadRange
///     never sees a half-written record at the tail it is about to stitch onto the RAM head.
///
/// The file format is a flat record stream -- [int32 length][double captureTime][byte keyframe]
/// [payload] -- because the only reader is this class and the index lives in memory; a crash can
/// leave segments behind, which is why the folder is wiped at startup and at exit.
/// </summary>
internal sealed class DiskSpooler : IDisposable
{
    /// <summary>Nominal length of one on-disk segment, matching the RAM head's window.</summary>
    public const double SegmentSeconds = 45.0;

    /// <summary>Where the tail lives. Deliberately in %TEMP%: it is a cache, not user data.</summary>
    public static string RootDirectory => Path.Combine(Path.GetTempPath(), "Clippy", "spool");

    /// <summary>Record header: length, timestamp, keyframe flag.</summary>
    private const int HeaderLength = 13;

    private readonly string directory;
    private readonly string streamName;
    private readonly double maxHistorySeconds;
    private readonly double segmentSeconds;

    /// <summary>Evicted packets awaiting disk. Ownership travels with the packet.</summary>
    private readonly BlockingCollection<StoredPacket> queue = new();

    /// <summary>Guards the segment list, the open writer, and the discard boundary.</summary>
    private readonly object gate = new();

    /// <summary><c>pending</c> = packets accepted but not yet written; <see cref="Flush"/> waits on it.</summary>
    private readonly object pendingGate = new();
    private int pending;

    private readonly List<Segment> segments = [];
    private readonly byte[] headerBuffer = new byte[HeaderLength];
    private FileStream? current;
    private double currentStart;
    private double newestWritten;
    private double discardUpTo = double.NegativeInfinity;
    private int nextSegmentIndex;
    private bool writeErrorLogged;
    private volatile bool disposed;

    private readonly Thread writerThread;

    private readonly record struct Segment(int Index, double Start, double End, string Path);

    public DiskSpooler(
        string streamName,
        double maxHistorySeconds,
        string? directory = null,
        double segmentSeconds = SegmentSeconds)
    {
        this.streamName = streamName;
        this.maxHistorySeconds = maxHistorySeconds;
        this.segmentSeconds = segmentSeconds;
        this.directory = directory ?? RootDirectory;
        Directory.CreateDirectory(this.directory);

        writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = $"clippy-spool-{streamName}",
        };
        writerThread.Start();
    }

    /// <summary>
    /// Deletes the spool folder. Called at startup (a crash can leave stale segments) and at process
    /// exit. Never throws: a locked file costs some temp disk space until the next start, while an
    /// exception here would take down a normal exit for no benefit.
    /// Known ceiling: two Clippy instances share one folder, so the second instance's startup wipe
    /// clears the first instance's tail. One recorder per machine is the supported case.
    /// </summary>
    public static void Cleanup()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Console.WriteLine($"Spool: could not clean {RootDirectory} ({ex.Message}); retried at the next start.");
        }
    }

    /// <summary>
    /// Receives a packet the RAM head just trimmed. Called by <see cref="RingBuffer"/> under the
    /// ring's lock, so it must stay cheap and must not do I/O: bump the pending counter, enqueue,
    /// and let the writer thread own the disk. If this spooler is shutting down the packet is
    /// released here instead, so no rented buffer leaks.
    /// </summary>
    public void Evict(StoredPacket packet)
    {
        if (disposed)
        {
            packet.Release();
            return;
        }

        lock (pendingGate)
        {
            pending++;
        }

        try
        {
            queue.Add(packet);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding won the race: the recorder is shutting down and the tail is no longer
            // needed. Return the buffer rather than leaking it.
            packet.Release();
            lock (pendingGate)
            {
                pending--;
                Monitor.PulseAll(pendingGate);
            }
        }
    }

    /// <summary>Blocks until every queued packet has reached the disk. Export reads only after this.</summary>
    public void Flush()
    {
        lock (pendingGate)
        {
            while (pending > 0)
                Monitor.Wait(pendingGate);
        }

        // pending == 0 means the writer finished the records, but the open segment's FileStream
        // still holds them in its own buffer -- invisible to a second handle. Without this, a read
        // of the segment currently being written comes back empty or short.
        lock (gate)
        {
            current?.Flush();
        }
    }

    /// <summary>
    /// Packets with capture time in [from, to], read back from the segments in time order.
    /// Returns plain GC-owned arrays -- the same contract as <see cref="RingBuffer.Slice"/>: callers
    /// must not release them, and nothing here returns buffers to the pool.
    /// </summary>
    public List<StoredPacket> ReadRange(double from, double to)
    {
        var result = new List<StoredPacket>();
        lock (gate)
        {
            foreach (var segment in segments)
            {
                if (segment.End < from || segment.Start > to)
                    continue;
                ReadSegment(segment.Path, from, to, result);
            }
        }

        return result;
    }

    /// <summary>
    /// Drops the entire tail. Used when the video pipeline restarts after a resolution change:
    /// packets encoded at the old size cannot be muxed into a clip written with the new SPS/PPS,
    /// exactly why the ring itself is cleared there. Packets enqueued up to
    /// <paramref name="discardBeforeTimestamp"/> are discarded even if the writer thread has not
    /// picked them up yet.
    /// </summary>
    public void Reset(double discardBeforeTimestamp)
    {
        lock (gate)
        {
            discardUpTo = Math.Max(discardUpTo, discardBeforeTimestamp);

            while (queue.TryTake(out var stale))
            {
                stale.Release();
                lock (pendingGate)
                {
                    pending--;
                    Monitor.PulseAll(pendingGate);
                }
            }

            current?.Dispose();
            current = null;

            foreach (var segment in segments)
            {
                try
                {
                    File.Delete(segment.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                    // A locked or already-gone file is not a reason to keep stale segments around.
                }
            }

            segments.Clear();
            newestWritten = double.NegativeInfinity;
            nextSegmentIndex = 0;
        }
    }

    /// <summary>
    /// The export's unified view across both tiers: the RAM head first, then everything the disk
    /// still holds for the window, merged into one time-ordered list.
    ///
    /// Order of operations is what makes this gap-free: Slice snapshots the ring, then Flush drains
    /// every packet the ring may have evicted since (eviction enqueues under the ring's own lock, so
    /// anything missing from the head is already queued by the time Slice returns), then ReadRange
    /// reads a disk that is complete for the window. The only possible overlap is a packet evicted
    /// between Slice and Flush that is also in the head snapshot; it is dropped by signature so no
    /// access unit is written into the clip twice.
    /// </summary>
    public static List<StoredPacket> SliceHybrid(RingBuffer ring, DiskSpooler? spool, double from, double to)
    {
        var head = ring.Slice(from, to);
        if (spool is null)
            return head;

        spool.Flush();
        var tail = spool.ReadRange(from, to);
        if (tail.Count == 0)
            return head;
        if (head.Count == 0)
            return tail;

        var headSignatures = new HashSet<(double Time, int Length, bool Keyframe)>(head.Count);
        foreach (var packet in head)
            headSignatures.Add((packet.CaptureClockSeconds, packet.Length, packet.IsKeyframe));
        tail.RemoveAll(p => headSignatures.Contains((p.CaptureClockSeconds, p.Length, p.IsKeyframe)));

        if (tail.Count == 0)
            return head;

        // Two-pointer merge by capture time. On an exact tie the disk copy wins: it was evicted
        // first, so it is the earlier packet of the two.
        var merged = new List<StoredPacket>(tail.Count + head.Count);
        var i = 0;
        var j = 0;
        while (i < tail.Count && j < head.Count)
            merged.Add(tail[i].CaptureClockSeconds <= head[j].CaptureClockSeconds ? tail[i++] : head[j++]);
        while (i < tail.Count)
            merged.Add(tail[i++]);
        while (j < head.Count)
            merged.Add(head[j++]);

        Console.WriteLine($"Spool: hybrid slice merged {tail.Count} packets from disk with {head.Count} from RAM.");
        return merged;
    }


    private void WriteLoop()
    {
        foreach (var packet in queue.GetConsumingEnumerable())
        {
            try
            {
                Write(packet);
            }
            catch (Exception ex)
            {
                // Disk full or the temp folder vanished mid-write. Losing the tail is survivable;
                // losing the recorder is not. Logged once, the packets keep being dropped after it.
                if (!writeErrorLogged)
                {
                    writeErrorLogged = true;
                    Console.WriteLine(
                        $"Spool [{streamName}]: disk write failed ({ex.GetType().Name}: {ex.Message}); " +
                        "the disk tail is degraded until restart.");
                }
            }
            finally
            {
                packet.Release();
                lock (pendingGate)
                {
                    pending--;
                    Monitor.PulseAll(pendingGate);
                }
            }
        }
    }

    private void Write(StoredPacket packet)
    {
        lock (gate)
        {
            var t = packet.CaptureClockSeconds;
            if (t <= discardUpTo)
                return; // queued before a Reset: belongs to a pipeline that no longer exists

            if (current is null || t - currentStart >= segmentSeconds)
                Rotate(t);

            BinaryPrimitives.WriteInt32LittleEndian(headerBuffer, packet.Length);
            BinaryPrimitives.WriteDoubleLittleEndian(headerBuffer.AsSpan(4), t);
            headerBuffer[12] = packet.IsKeyframe ? (byte)1 : (byte)0;
            current!.Write(headerBuffer);
            current.Write(packet.Data, 0, packet.Length);

            newestWritten = Math.Max(newestWritten, t);

            var lastIndex = segments.Count - 1;
            var last = segments[lastIndex];
            segments[lastIndex] = last with { End = Math.Max(last.End, t) };

            Prune();
        }
    }

    private void Rotate(double t)
    {
        current?.Dispose();
        current = null;

        var index = nextSegmentIndex++;
        var path = Path.Combine(directory, $"spool_{streamName}_{index}.tmp");
        current = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024);
        currentStart = t;
        segments.Add(new Segment(index, t, t, path));
    }

    /// <summary>
    /// Deletes whole segments that fell out of the combined RAM + disk history. Segment-level (not
    /// packet-level) deletion on purpose: rewriting a partially old segment to save the nominal
    /// segment length of granularity is not worth the complexity, and keeping a little extra on
    /// disk only costs temp space, never correctness.
    /// </summary>
    private void Prune()
    {
        var cutoff = newestWritten - maxHistorySeconds;

        // Walk from the OLDEST segment forward: the list is time-ordered, so the first segment that
        // reaches into the retained window means every older one is already considered too. The
        // last entry is the open segment and is never a candidate.
        for (var i = 0; i < segments.Count - 1; )
        {
            if (segments[i].End >= cutoff)
                break;

            var segment = segments[i];
            try
            {
                File.Delete(segment.Path);
                segments.RemoveAt(i);
            }
            catch (FileNotFoundException)
            {
                segments.RemoveAt(i);
            }
            catch (DirectoryNotFoundException)
            {
                segments.RemoveAt(i);
            }
            catch (IOException)
            {
                // An export is reading it right now. Keep it listed so the next write retries.
                break;
            }
        }
    }


    private void ReadSegment(string path, double from, double to, List<StoredPacket> result)
    {
        var header = new byte[HeaderLength];
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            while (true)
            {
                // A short read here means either the real end of the file or a record the writer is
                // mid-way through; after Flush the writer is idle, so this is belt-and-braces.
                if (ReadFully(stream, header) < HeaderLength)
                    break;

                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > 64 * 1024 * 1024)
                    break; // torn header: nothing sane can follow it

                var t = BinaryPrimitives.ReadDoubleLittleEndian(header.AsSpan(4));
                var isKeyframe = header[12] != 0;

                var payload = new byte[length];
                if (ReadFully(stream, payload) < length)
                    break; // torn record

                if (t < from || t > to)
                    continue;

                result.Add(new StoredPacket(payload, length, t, isKeyframe));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The segment vanished between listing and opening. Skipped rather than fatal: an export
            // must still produce the clip from whatever history survives.
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                break;
            read += n;
        }

        return read;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        queue.CompleteAdding();
        writerThread.Join(TimeSpan.FromSeconds(10));

        lock (gate)
        {
            current?.Dispose();
            current = null;
        }

        queue.Dispose();
    }
}

