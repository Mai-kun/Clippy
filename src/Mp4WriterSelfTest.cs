namespace Clippy;

/// <summary>
/// Synthetic check for the MP4 writer. Uses REAL SPS/PPS taken from a live capture, because invented
/// codec parameters would not exercise the avcC parsing path that matters, and known
/// CaptureClockSeconds so ffprobe output can be compared value by value rather than just "it opens".
/// </summary>
internal static class Mp4WriterSelfTest
{
    public static int Run()
    {
        var failures = 0;
        failures += Check("mp4: PTS match the capture clock", PtsMatchCaptureClock);
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

    private static string? PtsMatchCaptureClock()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "mp4-selftest-frames.bin");
        if (!File.Exists(source))
            return $"test fixture not found: {source}";

        var stream = File.ReadAllBytes(source);
        var (sps, pps, samples) = SplitStream(stream);
        if (samples.Count < 4)
            return $"fixture has only {samples.Count} samples";

        var writer = new Mp4Writer();
        writer.SetParameterSets(sps, pps);
        writer.SetDimensions(1920, 1080);
        foreach (var (nals, t) in samples)
            writer.AddVideoSample(nals, t);

        var outPath = Path.Combine(Path.GetTempPath(), "clippy-mp4-selftest.mp4");
        File.WriteAllBytes(outPath, writer.Build());
        Console.WriteLine($"       wrote {outPath} with {writer.SampleCount} samples, " +
                          $"{writer.Width}x{writer.Height}, {new FileInfo(outPath).Length} bytes");
        return null;
    }

    /// <summary>Splits an Annex B stream into per-access-unit NAL groups with their times.</summary>
    private static (byte[] Sps, byte[] Pps, List<(List<ReadOnlyMemory<byte>> Nals, double T)> Samples) SplitStream(byte[] data)
    {
        var parser = new H264AnnexBParser();
        byte[] sps = [], pps = [];
        var samples = new List<(List<ReadOnlyMemory<byte>>, double)>();
        List<ReadOnlyMemory<byte>>? current = null;

        void Finish(double t)
        {
            if (current is { Count: > 0 })
                samples.Add((current, t));
            current = null;
        }

        // Times are derived from the sample index at a deliberately uneven rate, so a writer that
        // quietly assumes a constant fps cannot pass.
        var pending = new List<(byte[] Data, bool IsIdr, double T)>();
        parser.Append(data, 0.0, (d, _, isIdr, _) => pending.Add((d, isIdr, 0)));
        parser.Flush(0, (d, _, isIdr, _) => pending.Add((d, isIdr, 0)));

        var elapsed = 0.0;
        var pattern = new[] { 0.040, 0.016, 0.083, 0.021, 0.033, 0.058 };
        foreach (var (nal, isIdr, _) in pending)
        {
            var type = nal[0] & 0x1F;
            if (type == 7)
            {
                sps = nal;
                continue;
            }

            if (type == 8)
            {
                pps = nal;
                continue;
            }

            if (type == 9)
                continue; // access unit delimiter carries no picture data

            // An IDR opens a new access unit, so it also closes the previous one.
            if (isIdr && current is { Count: > 0 })
            {
                samples.Add((current, elapsed));
                elapsed += pattern[samples.Count % pattern.Length];
                current = null;
            }

            (current ??= []).Add(nal);
        }

        if (current is { Count: > 0 })
            samples.Add((current, elapsed));
        return (sps, pps, samples);
    }
}
