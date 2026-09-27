using System.Buffers.Binary;

namespace Clippy;

/// <summary>
/// Minimal non-fragmented MP4 writer. One pass, everything in memory, so no fragmentation is
/// needed: ftyp, then mdat with every sample back to back, then moov carrying explicit sample
/// tables built from the capture clock of each packet.
///
/// Explicit tables are the whole point. Wrapping a raw elementary stream in ffmpeg produced streams
/// with no duration and silently dropped the audio (see the -shortest trap), because the input
/// carries no timestamp for ffmpeg to honour. Here stts durations come from the differences between
/// neighbouring CaptureClockSeconds, so a variable frame rate is represented exactly rather than
/// averaged into a nominal fps.
/// </summary>
internal sealed class Mp4Writer
{
    private const uint Timescale = 1_000_000; // microseconds: matches the clock resolution directly

    private readonly List<byte[]> samples = [];
    private readonly List<ulong> timesMicroseconds = [];
    private readonly List<byte[]> parameterSets = [];

    private readonly List<byte[]> audioSamples = [];
    private readonly List<ulong> audioTimes = [];
    private byte[]? audioSpecificConfig;
    private int audioSampleRate;
    private int audioChannels;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int AudioSampleRate => audioSampleRate;

    public int AudioChannels => audioChannels;
    public int SampleCount => samples.Count + audioSamples.Count;

    /// <summary>
    /// Adds one AAC sample from a complete ADTS frame. The 7-byte ADTS header is stripped: mp4a
    /// carries raw AAC plus a single AudioSpecificConfig, and keeping the per-frame header would make
    /// every sample a second, redundant copy of that configuration.
    /// </summary>
    public void AddAudioSample(ReadOnlySpan<byte> adtsFrame, double captureSeconds)
    {
        if (adtsFrame.Length < 7)
            throw new ArgumentException("ADTS frame is shorter than its header", nameof(adtsFrame));

        // ISO/IEC 13818-7 ADTS header, byte by byte. Verified against the real fixture header
        // (FF F1 50 40 3B 5F FC) and against `ffprobe` on the same file, which reports
        // 44100 Hz / 1 channel. Byte 2 is 0b0101_0000:
        //   bits 7-6  profile                 -> 01
        //   bits 5-2  sampling_frequency_index-> 0100 = 4 = 44100 Hz
        //   bit  1    private_bit             -> 0   (it comes AFTER the index, not before)
        //   bit  0    channel_configuration MSB-> 0
        // and byte 3 (0x40) bits 7-6 give the remaining channel bits, so channel = 1 (mono).
        // An earlier revision wrongly placed private_bit between profile and the index, shifting the
        // index down one bit: 16000 Hz instead of 44100. The container then declared a rate the data
        // was not encoded for, and the decoder failed with "scalefactor bands 48 exceeds limit 43".
        var profile = (adtsFrame[2] >> 6) & 0x03;
        var frequencyIndex = (adtsFrame[2] >> 2) & 0x0F;
        var channelConfig = ((adtsFrame[2] & 0x01) << 2) | ((adtsFrame[3] & 0xC0) >> 6);

        audioSpecificConfig ??= [.. AudioSpecificConfig(profile, frequencyIndex, channelConfig)];
        audioSampleRate = SampleRateForIndex(frequencyIndex);
        audioChannels = channelConfig;

        Store(audioSamples, audioTimes, adtsFrame[7..].ToArray(), captureSeconds);
    }

    /// <summary>5 bits audio object type, 4 bits sampling frequency index, 4 bits channel config.</summary>
    private static byte[] AudioSpecificConfig(int objectType, int frequencyIndex, int channelConfig) =>
    [
        (byte)((objectType << 3) | ((frequencyIndex >> 1) & 0x07)),
        (byte)(((frequencyIndex & 0x01) << 7) | (channelConfig << 3)),
    ];

    private static readonly int[] SampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    ];

    private static int SampleRateForIndex(int index) => index < SampleRates.Length ? SampleRates[index] : 0;

    /// <summary>Adds one video sample. NAL units are stored length-prefixed, as avc1 requires.</summary>
    public void AddVideoSample(ReadOnlySpan<byte> nalUnits, double captureSeconds)
    {
        Store(LengthPrefixed(nalUnits), captureSeconds);
    }

    /// <summary>Appends several NAL units to one sample, each length-prefixed as avc1 requires.</summary>
    public void AddVideoSample(IEnumerable<ReadOnlyMemory<byte>> nalUnits, double captureSeconds)
    {
        var payload = new List<byte>();
        foreach (var nal in nalUnits)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, nal.Length);
            payload.AddRange(length.ToArray());
            payload.AddRange(nal.ToArray());
        }
        Store([.. payload], captureSeconds);
    }

    private static byte[] LengthPrefixed(ReadOnlySpan<byte> nal)
    {
        var payload = new byte[4 + nal.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, nal.Length);
        nal.CopyTo(payload.AsSpan(4));
        return payload;
    }

    /// <summary>Records the SPS/PPS used to build the avcC box.</summary>
    public void SetParameterSets(byte[] sps, byte[] pps)
    {
        parameterSets.Clear();
        parameterSets.Add(sps);
        parameterSets.Add(pps);
    }

    /// <summary>
    /// Display size, passed in rather than parsed out of the SPS. The width/height live in
    /// pic_width_in_mbs_minus1 as exp-Golomb values, and a hand-rolled bit extraction silently
    /// produced 16x16 for a 1920x1080 stream. The capture already knows the real frame size.
    /// </summary>
    public void SetDimensions(int width, int height)
    {
        Width = width;
        Height = height;
    }

    private void Store(byte[] payload, double captureSeconds) =>
        Store(samples, timesMicroseconds, payload, captureSeconds);

    private static void Store(List<byte[]> store, List<ulong> times, byte[] payload, double captureSeconds)
    {
        store.Add(payload);
        times.Add((ulong)Math.Round(captureSeconds * Timescale));
    }

    /// <summary>
    /// Serialises the file: ftyp, then mdat with every video and audio sample back to back, then moov.
    ///
    /// Tracks of different duration are written as they are and NOT trimmed to a common length. Video
    /// and audio are captured independently and need not end on the same CaptureClockSeconds; players
    /// handle a shorter track by holding its last frame or silence. Truncating would silently throw
    /// away real recorded data, so the difference is preserved deliberately and reported by the caller.
    /// </summary>
    public byte[] Build()
    {
        if (samples.Count == 0 && audioSamples.Count == 0)
            throw new InvalidOperationException("Cannot build an MP4 with no samples.");

        var mdatPayloadSize = (uint)(samples.Sum(s => (uint)s.Length) + audioSamples.Sum(s => (uint)s.Length));
        var mdatOffset = (ulong)BuildFtyp().Length; // ftyp precedes mdat

        var videoOffsets = new List<uint>(samples.Count);
        var running = mdatOffset + 8; // mdat header
        foreach (var sample in samples)
        {
            videoOffsets.Add((uint)running);
            running += (uint)sample.Length;
        }

        var audioOffsets = new List<uint>(audioSamples.Count);
        foreach (var sample in audioSamples)
        {
            audioOffsets.Add((uint)running);
            running += (uint)sample.Length;
        }

        var output = new MemoryStream();
        Copy(BuildFtyp(), output);
        // Both tracks live in the same mdat, back to back. Passing only the video samples here while
        // sizing mdat from both made the box declare more bytes than the file contained, which is why
        // the muxer produced "moov atom not found" for any two-track file.
        WriteMdat(output, mdatPayloadSize, samples.Concat(audioSamples));
        Copy(BuildMoov(videoOffsets, audioOffsets), output);
        return output.ToArray();
    }

    /// <summary>MemoryStream has no CopyTo; this writes its contents to another stream.</summary>
    private static void Copy(byte[] bytes, Stream target) => target.Write(bytes);

    /// <summary>
    /// Writes a full box (version + flags) and returns its complete bytes, header included.
    /// The fixed boxes must go through here: emitting their payload alone, without the 8-byte
    /// size+type header, produced a moov whose first child read back as size 0.
    /// </summary>
    private static byte[] FullBox(string type, byte version, int flags, Action<MemoryStream> body)
    {
        using var ms = new MemoryStream();
        WriteFullBox(ms, version, flags);
        body(ms);
        using var wrapper = new MemoryStream();
        WriteBox(wrapper, type, payload => payload.Write(ms.GetBuffer(), 0, (int)ms.Length));
        return wrapper.ToArray();
    }

    private static void W32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void W16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }

    private static void WI16(Stream s, short v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, v);
        s.Write(b);
    }

    private static byte[] BuildFtyp()
    {
        using var ms = new MemoryStream();
        WriteBox(ms, "ftyp", body =>
        {
            body.Write("isom"u8);
            W32(body, 0x200);
            foreach (var brand in new[] { "isom", "iso2", "avc1", "mp41" })
                body.Write(System.Text.Encoding.ASCII.GetBytes(brand));
        });
        return ms.ToArray();
    }

    private static void WriteMdat(Stream output, uint payloadSize, IEnumerable<byte[]> payloadSamples)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, payloadSize + 8);
        "mdat"u8.CopyTo(header[4..]);
        output.Write(header);

        // The sample bytes themselves go here, back to back, before the moov. Writing only the header
        // produces a file with an empty mdat, which is why an early version had "moov atom not found".
        foreach (var sample in payloadSamples)
            output.Write(sample, 0, sample.Length);
    }


    private static readonly uint[] Matrix =
    [
        0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000,
    ];

    private byte[] BuildMoov(List<uint> videoOffsets, List<uint> audioOffsets)
    {
        var hasAudio = audioSamples.Count > 0;
        var videoDuration = DurationOf(timesMicroseconds);
        var audioDuration = DurationOf(audioTimes);
        // The movie duration is the longer of the two: the tracks themselves keep their own lengths.
        var duration = Math.Max(videoDuration, audioDuration);

        var mvhd = FullBox("mvhd", 0, 0, mvhd =>
        {
            W32(mvhd, 0);            // creation time
            W32(mvhd, 0);            // modification time
            W32(mvhd, Timescale);
            W32(mvhd, (uint)duration);
            W32(mvhd, 0x00010000);    // rate 1.0
            W16(mvhd, 0x0100);        // volume
            mvhd.Write(new byte[10]);                                   // reserved
            foreach (var m in Matrix)
                W32(mvhd, m);
            // pre_defined is 24 bytes per spec; writing 12 left mvhd at 96 and ffmpeg rejected it.
            for (var i = 0; i < 24; i++)
            W32(mvhd, hasAudio ? 3u : 2u); // next track id
        });

        using var moov = new MemoryStream();
        WriteBox(moov, "moov", body =>
        {
            body.Write(mvhd);
            if (samples.Count > 0)
            {
                body.Write(BuildTrak(1u, videoDuration, isVideo: true,
                    BuildStbl(samples, timesMicroseconds, videoOffsets, BuildStsd)));
            }

            if (hasAudio)
            {
                body.Write(BuildTrak(2u, audioDuration, isVideo: false,
                    BuildStbl(audioSamples, audioTimes, audioOffsets, BuildAudioStsd)));
            }
        });
        return moov.ToArray();
    }

    private static ulong DurationOf(List<ulong> times) =>
        times.Count < 2 ? 0 : times[^1] - times[0];

    private byte[] BuildTrak(uint trackId, ulong duration, bool isVideo, byte[] stbl)
    {
        var tkhd = FullBox("tkhd", 0, 0x000007, tkhd => // enabled, in movie, in preview
        {
            W32(tkhd, 0);
            W32(tkhd, 0);
            W32(tkhd, trackId);
            W32(tkhd, 0);
            W32(tkhd, (uint)duration);
            tkhd.Write(new byte[8]);                                     // reserved
            W16(tkhd, 0);              // layer
            W16(tkhd, 0);              // alternate group
            W16(tkhd, isVideo ? (ushort)0 : (ushort)0x0100);
            W16(tkhd, 0);              // reserved
            foreach (var m in Matrix)
                W32(tkhd, m);
            W32(tkhd, (uint)(Width << 16));
            W32(tkhd, (uint)(Height << 16));
        });

        var mdhd = FullBox("mdhd", 0, 0, mdhd =>
        {
            W32(mdhd, 0);
            W32(mdhd, 0);
            W32(mdhd, Timescale);
            W32(mdhd, (uint)duration);
            W16(mdhd, 0x55C4);          // language 'und'
            W16(mdhd, 0);                   // pre_defined
        });

        var hdlr = FullBox("hdlr", 0, 0, hdlr =>
        {
            W32(hdlr, 0);
            hdlr.Write(isVideo ? "vide"u8 : "soun"u8);
            hdlr.Write(new byte[12]);
            hdlr.Write(isVideo ? "VideoHandler"u8 : "SoundHandler"u8);
            hdlr.WriteByte(0);
        });

        // dref is a full box: version/flags, entry count, then the entry boxes themselves. The 'url '
        // entry with flag 1 means the media data is in this same file, so no URL string follows.
        using var url = new MemoryStream();
        WriteBox(url, "url ", u => W32(u, 1));
        var dref = FullBox("dref", 0, 0, dref =>
        {
            W32(dref, 1);
            dref.Write(url.GetBuffer(), 0, (int)url.Length);
        });

        using var dinf = new MemoryStream();
        WriteBox(dinf, "dinf", body => body.Write(dref));

        // A media header box is mandatory: vmhd for video, smhd for audio.
        var header = isVideo
            ? FullBox("vmhd", 0, 1, h =>
            {
                W16(h, 0);
                W16(h, 0);
                W16(h, 0);
                W16(h, 0);
            })
            : FullBox("smhd", 0, 0, h =>
            {
                W16(h, 0); // balance
                W16(h, 0); // reserved
            });

        using var minf = new MemoryStream();
        WriteBox(minf, "minf", body =>
        {
            body.Write(header);
            body.Write(dinf.ToArray());
            body.Write(stbl);
        });

        using var mdia = new MemoryStream();
        WriteBox(mdia, "mdia", body =>
        {
            body.Write(mdhd);
            body.Write(hdlr);
            body.Write(minf.ToArray());
        });

        using var trak = new MemoryStream();
        WriteBox(trak, "trak", body =>
        {
            body.Write(tkhd);
            body.Write(mdia.ToArray());
        });
        return trak.ToArray();
    }

    private byte[] BuildStbl(List<byte[]> trackSamples, List<ulong> trackTimes, List<uint> sampleOffsets, Func<byte[]> stsd)
    {
        // stts: durations come from the deltas between neighbouring capture times, so a variable
        // frame rate is stored exactly. Run-length encoded, as the format requires.
        var durations = new List<(uint Count, uint Delta)>();
        for (var i = 0; i < trackSamples.Count; i++)
        {
            var next = i + 1 < trackTimes.Count ? trackTimes[i + 1] : trackTimes[i] + LastDelta(trackTimes);
            var delta = (uint)Math.Max(1, next - trackTimes[i]);
            if (durations.Count > 0 && durations[^1].Delta == delta)
                durations[^1] = (durations[^1].Count + 1, delta);
            else
                durations.Add((1, delta));
        }

        var stts = FullBox("stts", 0, 0, stts =>
        {
            W32(stts, (uint)durations.Count);
            foreach (var (count, delta) in durations)
            {
                W32(stts, count);
                W32(stts, delta);
            }
        });

        var stsz = FullBox("stsz", 0, 0, stsz =>
        {
            W32(stsz, 0);            // sample_size 0: per-sample table
            W32(stsz, (uint)trackSamples.Count);
            foreach (var sample in trackSamples)
                W32(stsz, (uint)sample.Length);
        });

        // stsc: one entry describing the single chunk that holds every sample. samples_per_chunk must
        // be the full sample count: with 1 here the file described exactly one packet and ffprobe read
        // one packet out of ninety, because samples_per_chunk says how many samples each chunk holds.
        var stsc = FullBox("stsc", 0, 0, stsc =>
        {
            W32(stsc, 1);
            W32(stsc, 1);                            // first_chunk
            W32(stsc, (uint)trackSamples.Count);    // samples_per_chunk
            W32(stsc, 1);                            // sample_description_index
        });

        var stco = FullBox("stco", 0, 0, stco =>
        {
            W32(stco, 1);
            W32(stco, sampleOffsets[0]);
        });

        using var stbl = new MemoryStream();
        WriteBox(stbl, "stbl", body =>
        {
            body.Write(stsd());
            body.Write(stts);
            body.Write(stsc);
            body.Write(stsz);
            body.Write(stco);
        });
        return stbl.ToArray();
    }

    private static uint LastDelta(List<ulong> times)
    {
        if (times.Count < 2)
            return 33333; // ~30 fps fallback for a single-sample track
        return (uint)Math.Max(1, times[^1] - times[^2]);
    }

    private byte[] BuildStsd()
    {
        using var avcC = new MemoryStream();
        avcC.WriteByte(1);                                  // configurationVersion
        avcC.WriteByte(parameterSets[0][1]);                // AVCProfileIndication (from SPS)
        avcC.WriteByte(parameterSets[0][2]);                // profile_compatibility
        avcC.WriteByte(parameterSets[0][3]);                // AVCLevelIndication
        avcC.WriteByte(0xFF);                               // 6 bits reserved + lengthSizeMinusOne = 3
        avcC.WriteByte(0xE1);                               // 3 bits reserved + numOfSPS = 1
        var sps = parameterSets[0];
        avcC.WriteByte((byte)(sps.Length >> 8));
        avcC.WriteByte((byte)(sps.Length & 0xFF));
        avcC.Write(sps);
        var pps = parameterSets[1];
        avcC.WriteByte(1);                                  // numOfPPS
        avcC.WriteByte((byte)(pps.Length >> 8));
        avcC.WriteByte((byte)(pps.Length & 0xFF));
        avcC.Write(pps);

        using var avc1 = new MemoryStream();
        avc1.Write(new byte[6]);                            // reserved
        W16(avc1, 1);     // data_reference_index
        avc1.Write(new byte[16]);                           // pre_defined / reserved
        W16(avc1, (ushort)Width);
        W16(avc1, (ushort)Height);
        W32(avc1, 0x00480000); // 72 dpi horizontal
        W32(avc1, 0x00480000); // 72 dpi vertical
        W32(avc1, 0);     // reserved
        W16(avc1, 1);     // frame_count
        avc1.Write(new byte[32]);                           // compressorname
        W16(avc1, 0x0018); // depth
        WI16(avc1, -1);     // pre_defined

        // avc1 is a plain box wrapping the visual sample entry, and the avcC configuration rides
        // inside it. Writing the entry fields straight into the stream, with no 'avc1' header, is
        // what left ffmpeg reporting "invalid size 0 in stsd".
        using var avc1Box = new MemoryStream();
        WriteBox(avc1Box, "avc1", entry =>
        {
            entry.Write(avc1.GetBuffer(), 0, (int)avc1.Length);
            WriteBox(entry, "avcC", body => body.Write(avcC.ToArray()));
        });

        // stsd is a full box: version/flags, entry count, then the sample entries.
        var stsd = FullBox("stsd", 0, 0, stsd =>
        {
            W32(stsd, 1);     // entry count
            stsd.Write(avc1Box.GetBuffer(), 0, (int)avc1Box.Length);
        });
        return stsd;
    }

    /// <summary>
    /// Builds the esds box. Its payload is a tree of MPEG-4 descriptors, NOT nested ISO boxes: each
    /// descriptor is a 1-byte tag followed by a length whose bytes carry a continuation bit (7 payload
    /// bits per byte, high bit set on all but the last). That is a different length encoding from the
    /// 4-byte size+type used everywhere else, which is why Mp4BoxWalker needs its own branch for esds.
    /// </summary>
    private byte[] BuildEsds()
    {
        var config = audioSpecificConfig ?? throw new InvalidOperationException("No audio sample was added.");

        var decoderSpecific = Descriptor(0x05, d => d.Write(config));
        var decoderConfig = Descriptor(0x04, d =>
        {
            d.WriteByte(0x40);                    // objectTypeIndication: MPEG-4 audio
            d.WriteByte(0x15);                    // streamType=5 (audio), upStream=0, reserved=1
            d.WriteByte(0);
            d.WriteByte(0);
            d.WriteByte(0);                      // bufferSizeDB
            W32(d, 0);                           // maxBitrate
            W32(d, 0);                           // avgBitrate
            d.Write(decoderSpecific);
        });
        var slConfig = Descriptor(0x06, d => d.WriteByte(0x02));
        var esDescriptor = Descriptor(0x03, d =>
        {
            W16(d, 0);                           // ES_ID
            d.WriteByte(0);                       // flags
            d.Write(decoderConfig);
            d.Write(slConfig);
        });

        return FullBox("esds", 0, 0, esds => esds.Write(esDescriptor));
    }

    /// <summary>Wraps a payload in a descriptor tag plus the 7-bits-per-byte variable length encoding.</summary>
    private static byte[] Descriptor(byte tag, Action<MemoryStream> body)
    {
        using var payload = new MemoryStream();
        body(payload);
        var bytes = payload.ToArray();

        var length = bytes.Length;
        var encoded = new List<byte>();
        do
        {
            encoded.Insert(0, (byte)(length & 0x7F));
            length >>= 7;
        }
        while (length > 0);

        using var result = new MemoryStream();
        result.WriteByte(tag);
        for (var i = 0; i < encoded.Count; i++)
            result.WriteByte((byte)(encoded[i] | (i == encoded.Count - 1 ? 0 : 0x80)));
        result.Write(bytes);
        return result.ToArray();
    }

    private byte[] BuildAudioStsd()
    {
        using var esdsBox = new MemoryStream();
        WriteBox(esdsBox, "esds", body => body.Write(BuildEsds()));

        // AudioSampleEntry, matched field by field against a real ffmpeg-produced m4a:
        //   6 reserved | 2 data_reference_index | 8 reserved | 2 channelcount (+24) | 2 samplesize
        //   | 2 pre_defined | 2 reserved | 4 samplerate 16.16 (+32)   = 28 bytes of fields.
        // The reserved block after the index is 8 bytes (2x uint32), not 4: writing 4 put
        // channelcount at +20 and ffmpeg reported "0 channels, 1000000 Hz".
        using var mp4a = new MemoryStream();
        mp4a.Write(new byte[6]);                            // reserved[6]
        W16(mp4a, 1);                                      // data_reference_index
        mp4a.Write(new byte[8]);                            // reserved[2] as two uint32
        W16(mp4a, (ushort)audioChannels);                   // channelcount, box offset 24
        W16(mp4a, 16);                                     // samplesize, 26
        W16(mp4a, 0);                                      // pre_defined, 28
        W16(mp4a, 0);                                      // reserved, 30
        W32(mp4a, (uint)(audioSampleRate << 16));           // samplerate 16.16, 32
        mp4a.Write(esdsBox.ToArray());

        using var entry = new MemoryStream();
        WriteBox(entry, "mp4a", body => body.Write(mp4a.ToArray()));

        return FullBox("stsd", 0, 0, s =>
        {
            W32(s, 1);                                      // entry count
            s.Write(entry.GetBuffer(), 0, (int)entry.Length);
        });
    }

    private static void WriteBox(Stream stream, string type, Action<MemoryStream> body)
    {
        using var payload = new MemoryStream();
        body(payload);
        var bytes = payload.ToArray();
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(bytes.Length + 8));
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header[4..]);
        stream.Write(header);
        stream.Write(bytes);
    }

    private static void WriteFullBox(Stream stream, byte version, int flags)
    {
        stream.WriteByte(version);
        stream.WriteByte((byte)((flags >> 16) & 0xFF));
        stream.WriteByte((byte)((flags >> 8) & 0xFF));
        stream.WriteByte((byte)(flags & 0xFF));
    }
}




