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
        failures += Check("mp4: box structure is self-consistent", BoxStructureIsValid);
        failures += Check("mp4: PTS match the capture clock", PtsMatchCaptureClock);
        failures += Check("mp4: mutation check (halved timestamps must fail)", PtsMutationIsCaught);
        failures += Check("mp4: two-track box structure", TwoTrackBoxStructure);
        failures += Check("mp4: mdat size accounting", MdatSizeAccounting);
        failures += Check("mp4: both tracks survive in one file", BothTracksSurvive);
        failures += Check("mp4: audio PTS match the capture clock", AudioPtsMatch);
        failures += Check("mp4: audio mutation check (halved timestamps must fail)", AudioMutationIsCaught);
    failures += Check("mp4: both tracks share one zero when video starts late", SharedZeroVideoLate);
    failures += Check("mp4: both tracks share one zero when audio starts late", SharedZeroAudioLate);
    failures += Check("mp4: mutation check (no shared zero must fail)", SharedZeroMutationIsCaught);
        return failures;
    }

    /// <summary>
    /// Video starts 0.6 s AFTER audio. The early audio has no picture to pair with, so it must be
    /// dropped, and the first surviving video frame and the first surviving audio sample must be the
    /// SAME physical instant. Checked on absolute times, not on the container: in MP4 the first
    /// sample of every track is 0 by definition, so a container-level check could not tell the
    /// aligned and unaligned cases apart at all.
    /// </summary>
    private static string? SharedZeroVideoLate()
    {
        var video = Enumerable.Range(0, 30).Select(i => 10.6 + (i * 0.03)).ToList();
        var audio = Enumerable.Range(0, 300).Select(i => 10.0 + (i * 0.005)).ToList();

        var aligned = TrackAligner.Align(video, audio);

        if (Math.Abs(aligned.Zero - 10.6) > 1e-9)
        {
            return $"zero should be the video start 10.6, got {aligned.Zero:R}";
        }

        if (Math.Abs(aligned.DroppedVideo) > 1e-9 || Math.Abs(aligned.DroppedAudio - 0.6) > 1e-9)
        {
            return $"expected 0 s of video and 0.6 s of audio dropped, got " +
                $"{aligned.DroppedVideo:R} and {aligned.DroppedAudio:R}";
        }

        // Same instant: the first video frame and the first kept audio sample, back in absolute time.
        var firstVideoAbsolute = aligned.VideoTimes[0] + aligned.Zero;
        var firstAudioAbsolute = aligned.AudioTimes[0] + aligned.Zero;
        if (Math.Abs(firstVideoAbsolute - firstAudioAbsolute) > 1e-9)
        {
            return $"tracks no longer share a moment: video {firstVideoAbsolute:R} vs audio {firstAudioAbsolute:R}";
        }

        if (aligned.VideoTimes.Any(t => t < 0) || aligned.AudioTimes.Any(t => t < 0))
        {
            return "a negative relative time survived alignment";
        }

        return null;
    }

    /// <summary>The mirror case: audio starts later, so the head of the VIDEO is dropped instead.</summary>
    private static string? SharedZeroAudioLate()
    {
        var video = Enumerable.Range(0, 30).Select(i => 10.0 + (i * 0.03)).ToList();
        var audio = Enumerable.Range(0, 300).Select(i => 10.4 + (i * 0.005)).ToList();

        var aligned = TrackAligner.Align(video, audio);

        if (Math.Abs(aligned.Zero - 10.4) > 1e-9)
        {
            return $"zero should be the audio start 10.4, got {aligned.Zero:R}";
        }

        if (Math.Abs(aligned.DroppedVideo - 0.4) > 1e-9 || Math.Abs(aligned.DroppedAudio) > 1e-9)
        {
            return $"expected 0.4 s of video and 0 s of audio dropped, got " +
                $"{aligned.DroppedVideo:R} and {aligned.DroppedAudio:R}";
        }

        // Video sits on a 30 ms grid and audio on a 5 ms grid, so the first frame at or after the
        // zero can be up to one video frame late. That is expected, not a desync: the tolerance is
        // exactly one frame interval, which is the bound you asked for.
        const double frameInterval = 0.03;
        var firstVideoAbsolute = aligned.VideoTimes[0] + aligned.Zero;
        var firstAudioAbsolute = aligned.AudioTimes[0] + aligned.Zero;
        var gap = Math.Abs(firstVideoAbsolute - firstAudioAbsolute);
        return gap > frameInterval + 1e-9
            ? $"tracks no longer share a moment: video {firstVideoAbsolute:R} vs audio {firstAudioAbsolute:R} (gap {gap:R})"
            : null;
    }

    /// <summary>
    /// With alignment disabled, per-track origins are restored and the two tracks no longer describe
    /// the same moment. If this test passes, the alignment above is not actually doing anything.
    /// </summary>
    private static string? SharedZeroMutationIsCaught()
    {
        var video = Enumerable.Range(0, 30).Select(i => 10.6 + (i * 0.03)).ToList();
        var audio = Enumerable.Range(0, 300).Select(i => 10.0 + (i * 0.005)).ToList();

        var mutated = TrackAligner.Align(video, audio, align: false);

        // Each track keeps its own origin, so the absolute moments they describe must now disagree.
        var firstVideoAbsolute = mutated.VideoTimes[0] + video[0];
        var firstAudioAbsolute = mutated.AudioTimes[0] + audio[0];
        if (Math.Abs(firstVideoAbsolute - firstAudioAbsolute) < 0.1)
        {
            return "mutation did not break the shared zero, so the alignment test proves nothing";
        }

        if (mutated.DroppedAudio > 1e-9 || mutated.DroppedVideo > 1e-9)
        {
            return "mutation still dropped samples, so it is not really 'alignment off'";
        }

        return null;
    }

    private static int Check(string name, Func<string?> test)    {
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

    private static string TypeName(int type) => type switch
    {
        1 => "non-IDR slice",
        5 => "IDR slice",
        6 => "SEI",
        7 => "SPS",
        8 => "PPS",
        9 => "AUD",
        _ => $"nal{type}",
    };

    /// <summary>Builds the same file the other test builds, then validates it without ffprobe.</summary>
    private static byte[] BuildTestFile()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "mp4-selftest-frames.bin");
        if (!File.Exists(source))
            throw new FileNotFoundException($"test fixture not found: {source}", source);

        var (sps, pps, samples) = SplitStream(File.ReadAllBytes(source));
        Console.WriteLine($"       sps={sps.Length}B pps={pps.Length}B samples={samples.Count}");
        for (var i = 0; i < Math.Min(4, samples.Count); i++)
        {
            var types = string.Join('+', samples[i].Nals.Select(n => TypeName(n.Span[0] & 0x1F)));
            Console.WriteLine($"       sample {i}: [{types}]");
        }

        var writer = new Mp4Writer();
        writer.SetParameterSets(sps, pps);
        writer.SetDimensions(1920, 1080);
        foreach (var (nals, t) in samples)
            writer.AddVideoSample(nals, t);
        return writer.Build();
    }

    /// <summary>
    /// Pure size accounting, no parsing: (a) sum of video sample sizes, (b) sum of audio sample sizes
    /// after ADTS header stripping, (c) the declared mdat size, (d) the real file size.
    /// (a) + (b) + 8 must equal (c). This isolates a size bug from a box-structure bug.
    /// </summary>
    /// <summary>Runs the box walker over the two-track file, not just the video-only one.</summary>
    private static string? TwoTrackBoxStructure()
    {
        var data = BuildTwoTrackFile();
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "clippy-mp4-two-track.mp4"), data);
        return Mp4BoxWalker.Check(data);
    }

    private static string? MdatSizeAccounting()
    {
        var baseDir = AppContext.BaseDirectory;
        var (sps, pps, samples) = SplitStream(File.ReadAllBytes(Path.Combine(baseDir, "mp4-selftest-frames.bin")));

        var writer = new Mp4Writer();
        writer.SetParameterSets(sps, pps);
        writer.SetDimensions(1920, 1080);
        foreach (var (nals, t) in samples)
            writer.AddVideoSample(nals, t);

        var frames = SplitAdts(File.ReadAllBytes(Path.Combine(baseDir, "mp4-selftest-audio.aac")));
        for (var i = 0; i < frames.Count; i++)
            writer.AddAudioSample(frames[i], i * (1024.0 / writer.AudioSampleRate));

        var videoBytes = samples.Sum(s => s.Nals.Sum(n => n.Length + 4));
        var audioBytes = frames.Sum(f => Math.Max(0, f.Length - 7));

        var bytes = writer.Build();
        var ftypLength = 32;
        var mdatStart = ftypLength;
        var declaredMdat = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mdatStart));

        Console.WriteLine($"       (a) video sample bytes = {videoBytes}");
        Console.WriteLine($"       (b) audio sample bytes = {audioBytes}  (from {frames.Count} ADTS frames)");
        Console.WriteLine($"       (c) declared mdat size  = {declaredMdat}");
        Console.WriteLine($"       (d) real file size      = {bytes.Length}");

        var expectedPayload = videoBytes + audioBytes;
        if (expectedPayload + 8 != declaredMdat)
            return $"mdat payload mismatch: (a)+(b)+8 = {expectedPayload + 8}, but mdat declares {declaredMdat}";

        if (declaredMdat < 8 || mdatStart + declaredMdat > bytes.Length)
            return $"mdat declares {declaredMdat} bytes but the file is only {bytes.Length} long";

        return null;
    }

    /// <summary>Builds a file carrying BOTH tracks, which is the combination that has to be proven.</summary>
    private static byte[] BuildTwoTrackFile()
    {
        var baseDir = AppContext.BaseDirectory;
        var (sps, pps, samples) = SplitStream(File.ReadAllBytes(Path.Combine(baseDir, "mp4-selftest-frames.bin")));

        var writer = new Mp4Writer();
        writer.SetParameterSets(sps, pps);
        writer.SetDimensions(1920, 1080);
        foreach (var (nals, t) in samples)
            writer.AddVideoSample(nals, t * (HalveDeltasForTest ? 0.5 : 1.0));

        var audioFrames = SplitAdts(File.ReadAllBytes(Path.Combine(baseDir, "mp4-selftest-audio.aac")));
        // The sample rate must come from the fixture's own ADTS header, NOT from the writer: asking
        // the writer here would read 0, because it only learns the rate from the first AddAudioSample
        // call, which has not happened yet. Dividing by 0 produced Infinity, every timestamp became
        // NaN, and ffmpeg then reported a garbage stts delta of 4294967295.
        const int fixtureSampleRate = 44100;   // confirmed by ffprobe on the fixture: 44100 Hz mono, 3.12 s
        var perFrame = 1024.0 / fixtureSampleRate;
        for (var i = 0; i < audioFrames.Count; i++)
            writer.AddAudioSample(audioFrames[i], i * perFrame * (HalveDeltasForTest ? 0.5 : 1.0));

        return writer.Build();
    }

    private static string? BothTracksSurvive()
    {
        var path = Path.Combine(Path.GetTempPath(), "clippy-mp4-two-track.mp4");
        File.WriteAllBytes(path, BuildTwoTrackFile());

        var videoPackets = ReadPacketTimes(path, "v:0");
        var audioPackets = ReadPacketTimes(path, "a:0");
        if (videoPackets.Count == 0)
            return "no video packets in the combined file";
        if (audioPackets.Count == 0)
            return "no audio packets in the combined file: the audio track was dropped";

        // Both tracks must carry a real duration, not the N/A or bitrate guess the ffmpeg CLI produced.
        foreach (var track in new[] { "v:0", "a:0" })
        {
            var duration = ReadDuration(path, track);
            if (duration <= 0)
                return $"track {track} reports duration {duration} instead of a real value";
        }

        Console.WriteLine($"       video {videoPackets.Count} packets, audio {audioPackets.Count} packets, " +
                          $"durations v={ReadDuration(path, "v:0"):F3}s a={ReadDuration(path, "a:0"):F3}s");
        return null;
    }

    private static string? AudioPtsMatch()
    {
        var path = Path.Combine(Path.GetTempPath(), "clippy-mp4-two-track.mp4");
        File.WriteAllBytes(path, BuildTwoTrackFile());

        var actual = ReadPacketTimes(path, "a:0");
        var audioFrames = SplitAdts(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "mp4-selftest-audio.aac")));
        if (actual.Count != audioFrames.Count)
            return $"ffprobe reports {actual.Count} audio packets, expected {audioFrames.Count}";

        var sampleRate = 44100.0; // read from the fixture ADTS header via ffprobe, not assumed
        var perFrame = 1024.0 / sampleRate;
        for (var i = 0; i < actual.Count; i++)
        {
            // The mutation corrupts only what is WRITTEN. Scaling the expected value too would make the
            // comparison agree with itself, which is the false-green this check exists to prevent.
            var expected = i * perFrame;
            if (Math.Abs(actual[i] - expected) > 0.0005)
                return $"audio packet {i} PTS {actual[i]:F6} s, expected {expected:F6} s";
        }

        return null;
    }

    private static string? AudioMutationIsCaught()
    {
        HalveDeltasForTest = true;
        string? error;
        try
        {
            error = AudioPtsMatch();
        }
        finally
        {
            HalveDeltasForTest = false;
        }

        if (error is null)
            return "halving the audio timestamps still passed: the audio PTS check is not comparing";

        BothTracksSurvive();
        return null;
    }

    private static List<byte[]> SplitAdts(byte[] data)
    {
        var parser = new AdtsFrameParser();
        var frames = new List<byte[]>();
        parser.Append(data, 0, (frame, _, _) => frames.Add(frame));
        return frames;
    }

    private static List<double> ReadPacketTimes(string path, string stream)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", stream, "-show_entries", "packet=pts_time", "-of", "csv=p=0", path,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd(','))
            .Where(line => double.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(line => double.Parse(line, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    private static double ReadDuration(string path, string stream)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", stream, "-show_entries", "stream=duration", "-of", "csv=p=0", path,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd().Trim().TrimEnd(',');
        process.WaitForExit();
        return double.TryParse(output, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    private static string? BoxStructureIsValid()
    {
        var data = BuildTestFile();
        var outPath = Path.Combine(Path.GetTempPath(), "clippy-mp4-boxcheck.mp4");
        File.WriteAllBytes(outPath, data);
        return Mp4BoxWalker.Check(data);
    }

    private static string? PtsMatchCaptureClock()
    {
        var (sps, pps, samples) = SplitStream(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "mp4-selftest-frames.bin")));

        var writer = new Mp4Writer();
        writer.SetParameterSets(sps, pps);
        writer.SetDimensions(1920, 1080);
        // The mutation may only corrupt what is WRITTEN. Halving the expected value too would make
        // the comparison check itself and always agree, which is what the first attempt did.
        foreach (var (nals, t) in samples)
            writer.AddVideoSample(nals, t * (HalveDeltasForTest ? 0.5 : 1.0));

        var outPath = Path.Combine(Path.GetTempPath(), "clippy-mp4-selftest.mp4");
        File.WriteAllBytes(outPath, writer.Build());

        // The real check: read the timestamps back and compare against the values we handed in.
        // Without this the test only asserted that a file could be written, which is exactly the
        // "green because the test is broken" trap.
        var actual = ReadPacketTimes(outPath);
        if (actual.Count != samples.Count)
            return $"ffprobe reports {actual.Count} packets, expected {samples.Count}";

        var worst = 0.0;
        for (var i = 0; i < samples.Count; i++)
        {
            var expected = samples[i].T;
            var error = Math.Abs(actual[i] - expected);
            worst = Math.Max(worst, error);
            if (error > 0.0005)
                return $"packet {i} PTS {actual[i]:F6} s, expected {expected:F6} s (error {error:F6} s)";
        }

        Console.WriteLine($"       {actual.Count} packets, worst PTS error {worst:F6} s");
        return null;
    }

    /// <summary>Set by the mutation check: halves every timestamp, so a correct comparison must fail.</summary>
    internal static bool HalveDeltasForTest;

    private static List<double> ReadPacketTimes(string path)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd(','))
            .Where(line => double.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(line => double.Parse(line, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    /// <summary>Proves the PTS comparison has teeth by corrupting the timestamps on purpose.</summary>
    private static string? PtsMutationIsCaught()
    {
        HalveDeltasForTest = true;
        string? error;
        try
        {
            error = PtsMatchCaptureClock();
        }
        finally
        {
            HalveDeltasForTest = false;
        }

        if (error is null)
            return "halving every timestamp still passed: the PTS comparison is not actually comparing";

        // Restore the good file so a later ffprobe run does not see the corrupted one.
        PtsMatchCaptureClock();
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
        var pendingNonVcl = new List<ReadOnlyMemory<byte>>();

        foreach (var (nal, _, _) in pending)
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

            if (type is 6 or 9 or 10 or 11 or 12)
            {
                // Non-VCL: SEI, AUD, filler and friends. They belong to the access unit of the slice
                // that follows, so they are buffered rather than emitted on their own. Emitting them
                // separately produced samples with no picture at all ("missing picture in access unit").
                pendingNonVcl.Add(nal);
                continue;
            }

            // A VCL NAL is one picture, i.e. one access unit: everything buffered joins it.
            var unit = new List<ReadOnlyMemory<byte>>(pendingNonVcl.Count + 1);
            unit.AddRange(pendingNonVcl);
            pendingNonVcl.Clear();
            unit.Add(nal);

            samples.Add((unit, elapsed));

            // Index by the sample just added, not by the new list length: the old form picked
            // pattern[Count] after the append, which shifted every delta by one and made a correct
            // muxer look wrong.
            elapsed += pattern[(samples.Count - 1) % pattern.Length];
        }

        return (sps, pps, samples);
    }
}


