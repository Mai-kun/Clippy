namespace Clippy;

/// <summary>
/// Self-check for the ring buffer on synthetic packets: timestamps and keyframe flags are generated,
/// so the trim rules can be exercised exactly at their boundaries. No real capture involved.
///
/// Includes a mutation check: the "never exceeds maxDuration" assertion is re-run with the trim
/// disabled and the test FAILS if that run passes. Without it a broken trim would still report green.
/// </summary>
internal static class RingBufferSelfTest
{
    private const double MaxSeconds = 190.0; // 3 min 10 s
    private const double Fps = 30.0;

    public static int Run()
    {
        var failures = 0;
        failures += Check("video: buffer never exceeds maxDuration", VideoRespectsMaxDuration);
        failures += Check("video: head is always a keyframe after trim", VideoHeadIsKeyframe);
        failures += Check("video: boundary packet is deterministic", BoundaryIsDeterministic);
        failures += Check("video: mutation check (trim disabled must fail)", TrimMutationIsCaught);
        failures += Check("audio: trim leaves no time gap", AudioTrimHasNoGaps);
        failures += Check("slice: honours the requested range", SliceRespectsRange);
        failures += Check("buffer: padding past Length never leaks", PaddingDoesNotLeak);
    failures += Check("buffer: concurrent Push while slicing", ConcurrentPushAndSlice);
    failures += Check("spool: evicted packets survive on disk and rejoin the head", SpoolRoundTripsEvictedPackets);
    failures += Check("spool: segments older than the history limit are deleted", SpoolPrunesOldSegments);

        Console.WriteLine(failures == 0 ? "RING SELFTEST: OK" : $"RING SELFTEST: FAILED ({failures})");
        return failures;
    }

    private static int Check(string name, Func<string?> test)
    {
        try
        {
            var error = test();
            if (error is null)
            {
                Console.WriteLine($"[ OK ] {name}");
                return 0;
            }

            Console.WriteLine($"[FAIL] {name}: {error}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static byte[] Rented(int size = 64) => System.Buffers.ArrayPool<byte>.Shared.Rent(size);

    /// <summary>
    /// One thread pushes continuously while another slices, which is what the live recorder does.
    /// Slice returns the ring's own StoredPacket objects, so a torn read here would show up as an
    /// out-of-range list access or a packet whose length does not match its buffer. Before the ring
    /// was made thread-safe this test reproduced both.
    /// </summary>
    private static string? ConcurrentPushAndSlice()
    {
        var ring = new RingBuffer(MaxSeconds, isVideo: true);
        var error = (string?)null;
        Exception? thrown = null;
        using var cts = new CancellationTokenSource();

        var pusher = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 20000; i++)
                {
                    ring.Push(Rented(), 64, i / Fps, i % 30 == 0);

                    // Without a yield the pusher finishes before the slicer really gets going, and
                    // the test passes while proving nothing. Yielding widens the overlap window.
                    if (i % 8 == 0)
                    {
                        Thread.Yield();
                    }
                }
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        var slicer = new Thread(() =>
        {
            try
            {
                while (!pusher.IsAlive && !cts.IsCancellationRequested)
                {
                }

                var slices = 0;
                var seen = 0;
                while (pusher.IsAlive)
                {
                    var slice = ring.Slice(0, double.MaxValue);
                    slices++;
                    seen += slice.Count;

                    // Every returned packet must be readable and self-consistent. The ring owns these
                    // buffers, so Length must fit inside Data; a torn read would break that.
                    foreach (var packet in slice)
                    {
                        if (packet.Data is null || packet.Length < 0 || packet.Length > packet.Data.Length)
                        {
                            error = $"torn packet: Length={packet.Length}, Data.Length={packet.Data?.Length}";
                            return;
                        }

                        if (packet.Span.Length != packet.Length)
                        {
                            error = "Span length disagrees with Length";
                            return;
                        }
                    }
                }

                // A green result is worthless if the two threads never actually overlapped. Slice
                // copies every packet, so it is far more expensive than it was and completes fewer
                // times per push batch; a handful of full slices taken while the pusher is still
                // running is already substantial overlap.
                if (slices < 3 || seen < 1000)
                {
                    error = $"slicer got {slices} slices / {seen} packets: no real overlap, test proves nothing";
                }
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        });

        pusher.Start();
        slicer.Start();
        pusher.Join();
        slicer.Join();
        cts.Cancel();

        if (thrown is not null)
        {
            return $"{thrown.GetType().Name}: {thrown.Message}";
        }

        return error;
    }

    /// <summary>Pushes `seconds` of 30 fps video, an I-frame every second, tracking the widest span.</summary>
    private static (RingBuffer Ring, double WorstSpan) PushVideo(double seconds)
    {
        var ring = new RingBuffer(MaxSeconds, isVideo: true);
        var worst = 0.0;
        for (var i = 0; i < (int)(seconds * Fps); i++)
        {
            ring.Push(Rented(), 64, i / Fps, i % 30 == 0);
            if (ring.Count > 1)
                worst = Math.Max(worst, ring.NewestSeconds - ring.OldestSeconds);
        }
        return (ring, worst);
    }

    private static string? VideoRespectsMaxDuration()
    {
        var (ring, worst) = PushVideo(seconds: 240);
        if (worst > MaxSeconds + 1e-9)
        {
            ring.Clear();
            return $"buffer span reached {worst:F3} s, over the {MaxSeconds} s limit";
        }

        // 4 minutes of pushing must still hold roughly a full window.
        if (ring.Count < Fps * 100)
        {
            var c = ring.Count;
            ring.Clear();
            return $"only {c} packets retained, expected about {Fps * MaxSeconds:F0}";
        }

        ring.Clear();
        return null;
    }

    private static string? VideoHeadIsKeyframe()
    {
        var (ring, _) = PushVideo(seconds: 240);
        var first = ring.Packets[0];
        if (!first.IsKeyframe)
        {
            ring.Clear();
            return "oldest packet is not a keyframe";
        }

        ring.Clear();
        return null;
    }

    private static string? BoundaryIsDeterministic()
    {
        // A packet landing exactly on the limit must behave the same on every run: it survives while
        // newest-oldest <= max, and is dropped the moment the span exceeds max. No tolerance, no race.
        var keep = new RingBuffer(10.0, isVideo: false);
        keep.Push(Rented(), 8, 0.0, true);
        keep.Push(Rented(), 8, 10.0, true); // span == limit exactly
        var keptExactly = keep.Count;

        var drop = new RingBuffer(10.0, isVideo: false);
        drop.Push(Rented(), 8, 0.0, true);
        drop.Push(Rented(), 8, 10.0000001, true); // span a hair over the limit
        var keptJustOver = drop.Count;

        keep.Clear();
        drop.Clear();

        if (keptExactly != 2)
            return $"packet exactly on the boundary was dropped: kept {keptExactly}";

        if (keptJustOver != 1)
            return $"packet just over the boundary was kept: kept {keptJustOver}";

        return null;
    }

    private static string? TrimMutationIsCaught()
    {
        // Give the assertion above teeth: with the trim disabled the same pattern must blow past the
        // limit. If this ever passes, the maxDuration test is vacuous.
        var ring = new RingBuffer(MaxSeconds, isVideo: true) { TrimDisabledForTest = true };
        var worst = 0.0;
        for (var i = 0; i < (int)(240 * Fps); i++)
        {
            ring.Push(Rented(), 64, i / Fps, i % 30 == 0);
            if (ring.Count > 1)
                worst = Math.Max(worst, ring.NewestSeconds - ring.OldestSeconds);
        }

        ring.Clear();
        return worst > MaxSeconds
            ? null
            : $"mutation not detected: with trim disabled the span stayed at {worst:F3} s";
    }


    private static string? AudioTrimHasNoGaps()
    {
        var ring = new RingBuffer(MaxSeconds, isVideo: false);
        for (var i = 0; i < (int)(240 * Fps); i++)
            ring.Push(Rented(), 32, i / Fps, true);

        var view = ring.Packets;
        for (var i = 1; i < view.Count; i++)
        {
            var gap = view[i].CaptureClockSeconds - view[i - 1].CaptureClockSeconds;
            // Every retained audio packet is one frame apart; a hole would show as a bigger gap.
            if (Math.Abs(gap - 1.0 / Fps) > 1e-6)
            {
                ring.Clear();
                return $"time gap of {gap:F6} s between packets {i - 1} and {i} (expected {1.0 / Fps:F6})";
            }
        }

        ring.Clear();
        return null;
    }

    private static string? SliceRespectsRange()
    {
        var ring = new RingBuffer(MaxSeconds, isVideo: true);
        for (var i = 0; i < 1000; i++)
            ring.Push(Rented(), 16, i / Fps, i % 30 == 0);

        var slice = ring.Slice(from: 10.0, to: 20.0);
        ring.Clear();

        if (slice.Count == 0)
            return "slice returned nothing";

        foreach (var packet in slice)
        {
            if (packet.CaptureClockSeconds < 10.0 || packet.CaptureClockSeconds > 20.0)
                return $"packet at {packet.CaptureClockSeconds:F3} s is outside the requested range";
        }

        return null;
    }

    private static string? PaddingDoesNotLeak()
    {
        // A rented array is longer than the logical length, so slicing must use 0..Length. Otherwise
        // pool padding bleeds into the exported elementary stream.
        var ring = new RingBuffer(MaxSeconds, isVideo: true);
        var rented = Rented(64);
        rented[10] = 0xAB; // inside the logical payload
        rented[40] = 0xCD; // past Length
        ring.Push(rented, 16, 1.0, true);

        var slice = ring.Slice(0.0, 2.0);
        var copy = slice[0].ToArrayAndRelease();
        ring.Clear();

        if (copy.Length != 16)
            return $"slice length {copy.Length}, expected 16";

        if (copy[10] != 0xAB)
            return $"payload byte lost: {copy[10]:X2}";

        for (var i = 11; i < copy.Length; i++)
        {
            if (copy[i] == 0xCD)
                return "pool padding leaked past Length into the slice";
        }

        return null;
    }

    private static string TestSpoolDirectory() =>
        Path.Combine(Path.GetTempPath(), "Clippy", "spool-test-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover test folder is not worth failing a green test run over.
        }
    }

    /// <summary>
    /// Tier 1 (RAM head) + Tier 2 (disk spool) round trip: 10 s of packets pushed through a 3 s RAM
    /// head must all come back from the hybrid slice exactly once, in order, payload intact. The
    /// evicted packets are precisely the ones the old Trim threw away, so a lost or duplicated
    /// packet here means a gap or a repeated access unit in an exported clip.
    /// </summary>
    private static string? SpoolRoundTripsEvictedPackets()
    {
        var dir = TestSpoolDirectory();
        try
        {
            using var spool = new DiskSpooler("roundtrip", maxHistorySeconds: 190, directory: dir);
            var ring = new RingBuffer(maxSeconds: 3, isVideo: true) { OnEvicted = spool.Evict };
            const int total = (int)(10 * Fps);

            for (var i = 0; i < total; i++)
            {
                var data = Rented(32);
                data[0] = (byte)(i % 251);
                ring.Push(data, 32, i / Fps, i % 30 == 0);
            }

            var merged = DiskSpooler.SliceHybrid(ring, spool, 0, 1000);
            ring.Clear();

            if (merged.Count != total)
                return $"merged {merged.Count} packets, expected {total} (gap or duplicate at the RAM/disk boundary)";

            for (var i = 0; i < merged.Count; i++)
            {
                var expected = i / Fps;
                if (Math.Abs(merged[i].CaptureClockSeconds - expected) > 1e-9)
                    return $"packet {i} sits at {merged[i].CaptureClockSeconds:F4}s, expected {expected:F4}s";
                if (merged[i].Span[0] != (byte)(i % 251))
                    return $"payload corrupted at packet {i}: {merged[i].Span[0]} instead of {i % 251}";
            }

            return null;
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// With 1 s segments and a 3 s history, 20 s of pushes must leave only the last few seconds on
    /// disk: too wide a window means the spool grows without bound, too narrow a window means an
    /// F9 export would silently lose its oldest seconds.
    /// </summary>
    private static string? SpoolPrunesOldSegments()
    {
        var dir = TestSpoolDirectory();
        try
        {
            using var spool = new DiskSpooler("prune", maxHistorySeconds: 3, directory: dir, segmentSeconds: 1);
            var ring = new RingBuffer(maxSeconds: 1, isVideo: false) { OnEvicted = spool.Evict };
            const int total = (int)(20 * Fps);

            for (var i = 0; i < total; i++)
                ring.Push(Rented(16), 16, i / Fps, isKeyframe: true);

            spool.Flush();
            var tail = spool.ReadRange(0, 1000);
            ring.Clear();

            if (tail.Count == 0)
                return "spool is empty after 20 s of pushes";

            var newest = tail[^1].CaptureClockSeconds;
            var oldestKept = tail[0].CaptureClockSeconds;

            // 3 s of history plus at most one segment of granularity: reaching back 6 s or more
            // means the old segments were never deleted.
            if (oldestKept <= newest - 6)
                return $"old segments not deleted: the disk tail still reaches back to {oldestKept:F1}s (newest {newest:F1}s)";

            // The tail must still hold the last few seconds, not almost nothing.
            if (oldestKept >= newest - 2)
                return $"only {newest - oldestKept:F1}s left on disk; the tail was pruned too aggressively";

            return null;
        }
        finally
        {
            TryDelete(dir);
        }
    }
}
