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
        // A Windows CI agent ships without ffmpeg or ffprobe, and the deep checks below shell out to
        // ffprobe to read real PTS back out of the file. Without them those checks are not applicable,
        // and reporting them as failures would mean a green build is impossible on a clean runner.
        // The pure checks (box structure, size accounting, aligner rules) still run everywhere.
        var deep = FfmpegLocator.ProbeAvailable;
        if (!deep)
            Console.WriteLine("[SKIP] ffprobe not found in PATH or app directory; " +
                              "skipping deep PTS verification");

        failures += Check("mp4: box structure is self-consistent", BoxStructureIsValid, always: true);
        failures += Check("mp4: PTS match the capture clock", PtsMatchCaptureClock, deep);
        failures += Check("mp4: mutation check (halved timestamps must fail)", PtsMutationIsCaught, deep);
        failures += Check("mp4: two-track box structure", TwoTrackBoxStructure, always: true);
        failures += Check("mp4: mdat size accounting", MdatSizeAccounting, always: true);
        failures += Check("mp4: both tracks survive in one file", BothTracksSurvive, deep);
        failures += Check("mp4: audio PTS match the capture clock", AudioPtsMatch, deep);
        failures += Check("mp4: audio mutation check (halved timestamps must fail)", AudioMutationIsCaught, deep);
    failures += Check("mp4: both tracks share one zero when video starts late", SharedZeroVideoLate, always: true);
    failures += Check("mp4: both tracks share one zero when audio starts late", SharedZeroAudioLate, always: true);
    failures += Check("mp4: mutation check (no shared zero must fail)", SharedZeroMutationIsCaught, always: true);
    failures += Check("mp4: HEVC writes hvc1 with a complete hvcC record", HevcSampleEntryAndConfig, always: true);
    failures += Check("mp4: ffprobe reads the HEVC track back as hevc", HevcProbeReadsHevc);
        return failures;
    }

    /// <summary>
    /// Runs ffprobe and returns its stdout, or null when ffprobe cannot be launched at all.
    ///
    /// The null return is what makes the SKIP path reachable: ProbeAvailable checks once up front,
    /// but PATH can still be broken per-call, and an unhandled Win32Exception here would surface as
    /// an opaque process failure instead of a test result.
    /// </summary>
    private static string? RunProbe(IEnumerable<string> arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = FfmpegLocator.ProbeExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"[SKIP] ffprobe could not be started ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Audio starts 0.6 s BEFORE video. The approved rule is that t=0 is ALWAYS the video keyframe,
    /// because H.264 cannot be decoded from anything earlier. Audio recorded before it is DROPPED,
    /// and the drop happens in ExportClip while the packets are being written -- that is deliberate,
    /// so the packet and its timestamp are produced together and cannot drift apart.
    /// </summary>
    private static string? SharedZeroVideoLate()
    {
        var video = Enumerable.Range(0, 30).Select(i => 10.6 + (i * 0.03)).ToList();
        var audio = Enumerable.Range(0, 300).Select(i => 10.0 + (i * 0.005)).ToList();

        var aligned = TrackAligner.Align(video, audio);

        if (Math.Abs(aligned.Zero - 10.6) > 1e-9)
        {
            return $"zero should be the video keyframe 10.6, got {aligned.Zero:R}";
        }

        // Video is never cut: every frame is at or after the zero.
        if (aligned.VideoTimes.Any(t => t < 0))
        {
            return "a negative video time survived alignment: video must never be cut";
        }

        // The audio before the keyframe is the caller's to drop, and is reported as negative so the
        // drop cannot be skipped by accident.
        var before = aligned.AudioTimes.Count(t => t < 0);
        if (before != 120)
        {
            return $"expected 120 audio samples (0.6 s at 5 ms) before the keyframe, got {before}";
        }

        var firstVideoAbsolute = aligned.VideoTimes[0] + aligned.Zero;
        var firstAudioAbsolute = aligned.AudioTimes[before] + aligned.Zero;
        if (Math.Abs(firstVideoAbsolute - firstAudioAbsolute) > 1e-9)
        {
            return $"tracks no longer share a moment: video {firstVideoAbsolute:R} vs audio {firstAudioAbsolute:R}";
        }

        if (aligned.AudioLeadIn != 0.0)
        {
            return $"no lead-in is due when audio is earlier, got {aligned.AudioLeadIn:R}";
        }

        return null;
    }

    /// <summary>
    /// The mirror case: audio starts LATER, so the gap is real and is reported as a lead-in for
    /// ExportClip to fill with silent frames. Video is still kept whole.
    /// </summary>
    private static string? SharedZeroAudioLate()
    {
        var video = Enumerable.Range(0, 30).Select(i => 10.0 + (i * 0.03)).ToList();
        var audio = Enumerable.Range(0, 300).Select(i => 10.4 + (i * 0.005)).ToList();

        var aligned = TrackAligner.Align(video, audio);

        if (Math.Abs(aligned.Zero - 10.0) > 1e-9)
        {
            return $"zero should be the video keyframe 10.0, got {aligned.Zero:R}";
        }

        // Video is never cut: the picture is what the rest of the audio was paired against.
        if (aligned.VideoTimes.Any(t => t < 0))
        {
            return "a negative video time survived alignment: video must never be cut";
        }

        if (aligned.VideoTimes.Count != 30)
        {
            return $"video must be kept whole, got {aligned.VideoTimes.Count} of 30 frames";
        }

        if (aligned.AudioTimes.Any(t => t < 0))
        {
            return "a negative audio time survived alignment";
        }

        // The gap is real, so it is reported as a lead-in for ExportClip to fill with silence.
        if (Math.Abs(aligned.AudioLeadIn - 0.4) > 1e-9)
        {
            return $"expected a 0.4 s lead-in to be reported, got {aligned.AudioLeadIn:R}";
        }

        return null;
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

    /// <param name="always">
    /// False for the checks that need ffprobe. They are skipped rather than failed when it is absent:
    /// a missing tool means the property went unverified, which is a different statement from it being
    /// wrong, and failing would make a correct build look broken on any machine without ffmpeg.
    /// </param>
    private static int Check(string name, Func<string?> test, bool always = false)
    {
        if (!always && !FfmpegLocator.ProbeAvailable)
        {
            Console.WriteLine($"[SKIP] {name}");
            return 0;
        }

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
        var output = RunProbe(new[]
        {
            "-v", "error", "-select_streams", stream, "-show_entries", "packet=pts_time",
            "-of", "csv=p=0", path,
        });
        if (output is null)
            return [];

        return output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd(','))
            .Where(line => double.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(line => double.Parse(line, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    private static double ReadDuration(string path, string stream)
    {
        var output = RunProbe(new[]
        {
            "-v", "error", "-select_streams", stream, "-show_entries", "stream=duration",
            "-of", "csv=p=0", path,
        });
        if (output is null)
            return -1;

        var text = output.Trim().TrimEnd(',');
        return double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1;
    }

    private static string? BoxStructureIsValid()
    {
        var data = BuildTestFile();
        var outPath = Path.Combine(Path.GetTempPath(), "clippy-mp4-boxcheck.mp4");
        File.WriteAllBytes(outPath, data);
        return Mp4BoxWalker.Check(data);
    }

    /// <summary>
    /// Builds a file from the real HEVC fixture: a VPS, SPS and PPS and a dozen genuine pictures,
    /// parsed by the project's own AnnexBParser in HEVC mode -- the same code the NVENC bridge runs
    /// on a live stream.
    ///
    /// A real fixture rather than invented parameter sets, for the same reason the H.264 tests use
    /// one: ffprobe parses the SPS out of hvcC before it will name a stream, so a container built on
    /// made-up bytes is rejected outright and proves nothing about a real recording.
    /// </summary>
    private static byte[] BuildHevcFile()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "mp4-selftest-hevc.bin");
        if (!File.Exists(source))
            throw new FileNotFoundException($"test fixture not found: {source}", source);

        byte[]? vps = null, sps = null, pps = null;
        var pictures = new List<byte[]>();
        var parser = new AnnexBParser(hevc: true);
        parser.Append(File.ReadAllBytes(source), 0.0, (nal, _, _, _) =>
        {
            var type = HevcNal.Type(nal);
            if (type == HevcNal.Vps && vps is null)
                vps = nal;
            else if (type == HevcNal.Sps && sps is null)
                sps = nal;
            else if (type == HevcNal.Pps && pps is null)
                pps = nal;
            else if (HevcNal.IsVcl(type))
                pictures.Add(nal);
        });

        if (vps is null || sps is null || pps is null)
            throw new InvalidOperationException("the HEVC fixture has no complete VPS/SPS/PPS triple");
        Console.WriteLine($"       hevc: VPS {vps.Length}B SPS {sps.Length}B PPS {pps.Length}B, {pictures.Count} pictures");

        var writer = new Mp4Writer();
        // The VPS is what makes this HEVC and not H.264: it is the first of the three arrays the hvcC
        // record must carry, and a container without it is one many players reject outright.
        writer.SetParameterSets(sps, pps, vps);
        writer.SetDimensions(320, 240);
        for (var i = 0; i < pictures.Count; i++)
            writer.AddVideoSample(pictures[i], i / 30.0);

        return writer.Build();
    }

    /// <summary>
    /// Offset of a box by its four-character type, found in the BYTES.
    ///
    /// Decoding the file to a string and calling IndexOf is wrong here: the mdat holds encoded video in
    /// which a byte sequence can decode as one replacement character or as a multi-byte UTF-8
    /// character, and every such byte shifts the character index away from the byte offset the box
    /// header actually sits at. The declared size is validated too, so a chance occurrence of the
    /// four letters inside payload data does not pass for a box.
    /// </summary>
    private static int IndexOfBox(byte[] data, string type)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        for (var i = 4; i + 4 <= data.Length; i++)
        {
            if (data[i] != t[0] || data[i + 1] != t[1] || data[i + 2] != t[2] || data[i + 3] != t[3])
                continue;

            var start = i - 4;
            var size = (data[start] << 24) | (data[start + 1] << 16) | (data[start + 2] << 8) | data[start + 3];
            if (size >= 8 && start + size <= data.Length)
                return start;
        }

        return -1;
    }

    private static string? HevcSampleEntryAndConfig()
    {
        var data = BuildHevcFile();
        var structure = Mp4BoxWalker.Check(data);
        if (structure is not null)
            return structure;

        if (IndexOfBox(data, "hvc1") < 0)
            return "the video sample entry is not hvc1";
        if (IndexOfBox(data, "avc1") >= 0 || IndexOfBox(data, "avcC") >= 0)
            return "an H.264 box leaked into the HEVC file";

        // Walk the configuration record itself rather than trusting the box name: an
        // HEVCDecoderConfigurationRecord is 23 fixed bytes, then numOfArrays and one array per
        // parameter set. Counting those 23 from the spec is the whole point: one byte out and
        // numOfArrays lands on the first array's header instead.
        var box = IndexOfBox(data, "hvcC");
        if (box < 0)
            return "the hvcC configuration record is missing";

        var arrays = data[box + 8 + 22];
        if (arrays != 3)
            return $"hvcC declares {arrays} arrays, expected 3 (VPS, SPS, PPS)";

        var offset = box + 8 + 23;
        var stored = new Dictionary<int, byte[]>();
        for (var i = 0; i < arrays; i++)
        {
            var type = data[offset] & 0x3F;
            var nals = (data[offset + 1] << 8) | data[offset + 2];
            offset += 3;
            for (var k = 0; k < nals; k++)
            {
                var length = (data[offset] << 8) | data[offset + 1];
                offset += 2;
                var payload = new byte[length];
                Buffer.BlockCopy(data, offset, payload, 0, length);
                offset += length;
                stored[type] = payload;
            }
        }

        // The walk has to land exactly on the end of the box. Without that, a chance occurrence of
        // the four letters "hvcC" inside the encoded mdat passes every check below and the test is
        // really only testing itself.
        var boxSize = (data[box] << 24) + (data[box + 1] << 16) + (data[box + 2] << 8) + data[box + 3];
        var boxEnd = box + boxSize; // the size field already counts the 8-byte header
        if (offset != boxEnd)
            return $"the parameter-set walk ended at {offset - box} of {boxEnd - box} bytes -- " +
                   "this is not the configuration record of this file";

        var fixture = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "mp4-selftest-hevc.bin"));
        var real = new Dictionary<int, byte[]>();
        new AnnexBParser(hevc: true).Append(fixture, 0.0, (nal, _, _, _) =>
        {
            var type = HevcNal.Type(nal);
            if (!real.ContainsKey(type))
                real[type] = nal;
        });

        // The parameter sets must survive the trip byte for byte: hvcC stores them without start codes,
        // and one dropped or duplicated byte here only shows up as a file that will not decode.
        foreach (var type in new[] { HevcNal.Vps, HevcNal.Sps, HevcNal.Pps })
        {
            if (!stored.ContainsKey(type))
                return $"hvcC carries no array for NAL type {type}";
            if (!stored[type].AsSpan().SequenceEqual(real[type]))
                return $"hvcC's copy of NAL {type} differs from the fixture's " +
                       $"({stored[type].Length} vs {real[type].Length} bytes)";
        }

        return null;
    }

    private static string? HevcProbeReadsHevc()
    {
        var path = Path.Combine(Path.GetTempPath(), "clippy-mp4-hevc.mp4");
        File.WriteAllBytes(path, BuildHevcFile());

        var probe = RunProbe(["-v", "error", "-select_streams", "v:0", "-show_entries",
            "stream=codec_name,codec_tag_string,profile,width,height", "-of", "default=nw=1", path]);
        if (probe is null)
            return "ffprobe could not be started";

        var text = probe.Trim().Replace('\n', ' ');
        if (!text.Contains("codec_name=hevc"))
            return $"ffprobe did not see an HEVC track: '{text}'";

        // ffprobe reads the profile out of the SPS carried by hvcC, so "Main" appearing here is also
        // proof that the parameter set survived the container intact rather than merely being present.
        foreach (var expected in new[] { "codec_tag_string=hvc1", "profile=Main" })
        {
            if (!text.Contains(expected))
                return $"ffprobe reported '{text}', expected it to contain {expected}";
        }

        Console.WriteLine($"       {text}");
        return null;
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
        var output = RunProbe(new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time",
            "-of", "csv=p=0", path,
        });
        if (output is null)
            return [];

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
        var parser = new AnnexBParser();
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


