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
}
