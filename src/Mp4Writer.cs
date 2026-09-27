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

    public int Width { get; private set; }

    public int Height { get; private set; }
    public int SampleCount => samples.Count;

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

    private void Store(byte[] payload, double captureSeconds)
    {
        samples.Add(payload);
        timesMicroseconds.Add((ulong)Math.Round(captureSeconds * Timescale));
    }

    /// <summary>Serialises the file. ftyp + mdat first, moov last.</summary>
    public byte[] Build()
    {
        if (samples.Count == 0)
            throw new InvalidOperationException("Cannot build an MP4 with no samples.");

        var mdatPayloadSize = (uint)samples.Sum(s => (uint)s.Length);
        var mdatOffset = (ulong)BuildFtyp().Length; // ftyp precedes mdat

        var offsets = new List<uint>(samples.Count);
        var running = mdatOffset + 8; // mdat header
        foreach (var sample in samples)
        {
            offsets.Add((uint)running);
            running += (uint)sample.Length;
        }

        var output = new MemoryStream();
        Copy(BuildFtyp(), output);
        WriteMdat(output, mdatPayloadSize, samples);
        Copy(BuildMoov(mdatOffset, mdatPayloadSize, offsets), output);
        return output.ToArray();
    }

    /// <summary>MemoryStream has no CopyTo; this writes its contents to another stream.</summary>
    private static void Copy(byte[] bytes, Stream target) => target.Write(bytes);

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

    private byte[] BuildMoov(ulong mdatOffset, uint mdatPayloadSize, List<uint> sampleOffsets)
    {
        var duration = TotalDuration();

        using var mvhd = new MemoryStream();
        WriteFullBox(mvhd, version: 0, flags: 0);
        W32(mvhd, 0);            // creation time
        W32(mvhd, 0);            // modification time
        W32(mvhd, Timescale);
        W32(mvhd, (uint)duration);
        W32(mvhd, 0x00010000);    // rate 1.0
        W16(mvhd, 0x0100);        // volume
        mvhd.Write(new byte[10]);                                   // reserved
        foreach (var m in Matrix)
            W32(mvhd, m);
        for (var i = 0; i < 6; i++)
        {
            mvhd.WriteByte(0);
            mvhd.WriteByte(0);
        }
        W32(mvhd, 2);             // next track id

        using var moov = new MemoryStream();
        WriteBox(moov, "moov", body =>
        {
            body.Write(mvhd.ToArray());
            body.Write(BuildTrak(1, duration, isVideo: true, BuildVideoStbl(sampleOffsets)));
        });
        return moov.ToArray();
    }

    private ulong TotalDuration()
    {
        if (timesMicroseconds.Count < 2)
            return 0;
        return timesMicroseconds[^1] - timesMicroseconds[0];
    }

    private byte[] BuildTrak(uint trackId, ulong duration, bool isVideo, byte[] stbl)
    {
        using var tkhd = new MemoryStream();
        WriteFullBox(tkhd, 0x000007, 0); // enabled, in movie, in preview
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

        using var mdhd = new MemoryStream();
        WriteFullBox(mdhd, 0, 0);
        W32(mdhd, 0);
        W32(mdhd, 0);
        W32(mdhd, Timescale);
        W32(mdhd, (uint)duration);
        W16(mdhd, 0x55C4);          // language 'und'

        using var hdlr = new MemoryStream();
        WriteFullBox(hdlr, 0, 0);
        W32(hdlr, 0);
        hdlr.Write("vide"u8);
        hdlr.Write(new byte[12]);
        hdlr.Write("VideoHandler"u8);
        hdlr.WriteByte(0);

        using var dinf = new MemoryStream();
        WriteBox(dinf, "dinf", body =>
        {
            using var dref = new MemoryStream();
            WriteFullBox(dref, 0, 0);
            W32(dref, 1);
            WriteBox(dref, "url ", url => W32(url, 1));
            body.Write(dref.ToArray());
        });

        using var minf = new MemoryStream();
        WriteBox(minf, "minf", body =>
        {
            using var vmhd = new MemoryStream();
            WriteFullBox(vmhd, 0, 1);
            W16(vmhd, 0);
            W16(vmhd, 0);
            W16(vmhd, 0);
            W16(vmhd, 0);
            body.Write(vmhd.ToArray());
            body.Write(dinf.ToArray());
            body.Write(stbl);
        });

        using var mdia = new MemoryStream();
        WriteBox(mdia, "mdia", body =>
        {
            body.Write(mdhd.ToArray());
            body.Write(hdlr.ToArray());
            body.Write(minf.ToArray());
        });

        using var trak = new MemoryStream();
        WriteBox(trak, "trak", body =>
        {
            body.Write(tkhd.ToArray());
            body.Write(mdia.ToArray());
        });
        return trak.ToArray();
    }

    private byte[] BuildVideoStbl(List<uint> sampleOffsets)
    {
        // stts: durations come from the deltas between neighbouring capture times, so a variable
        // frame rate is stored exactly. Run-length encoded, as the format requires.
        var durations = new List<(uint Count, uint Delta)>();
        for (var i = 0; i < samples.Count; i++)
        {
            var next = i + 1 < timesMicroseconds.Count ? timesMicroseconds[i + 1] : timesMicroseconds[i] + LastDelta();
            var delta = (uint)Math.Max(1, next - timesMicroseconds[i]);
            if (durations.Count > 0 && durations[^1].Delta == delta)
                durations[^1] = (durations[^1].Count + 1, delta);
            else
                durations.Add((1, delta));
        }

        using var stts = new MemoryStream();
        WriteFullBox(stts, 0, 0);
        W32(stts, (uint)durations.Count);
        foreach (var (count, delta) in durations)
        {
            W32(stts, count);
            W32(stts, delta);
        }

        using var stsz = new MemoryStream();
        WriteFullBox(stsz, 0, 0);
        W32(stsz, 0);            // sample_size 0: per-sample table
        W32(stsz, (uint)samples.Count);
        foreach (var sample in samples)
            W32(stsz, (uint)sample.Length);

        // stsc: one entry says every sample is a chunk of one sample. One chunk holds all samples.
        using var stsc = new MemoryStream();
        WriteFullBox(stsc, 0, 0);
        W32(stsc, 1);
        W32(stsc, 1);             // first_chunk
        W32(stsc, 1);             // samples_per_chunk
        W32(stsc, 1);             // sample_description_index

        using var stco = new MemoryStream();
        WriteFullBox(stco, 0, 0);
        W32(stco, 1);
        W32(stco, sampleOffsets[0]);

        using var stbl = new MemoryStream();
        WriteBox(stbl, "stbl", body =>
        {
            body.Write(BuildStsd());
            body.Write(stts.ToArray());
            body.Write(stsc.ToArray());
            body.Write(stsz.ToArray());
            body.Write(stco.ToArray());
        });
        return stbl.ToArray();
    }

    private uint LastDelta()
    {
        if (timesMicroseconds.Count < 2)
            return 33333; // ~30 fps fallback for a single-sample clip
        return (uint)Math.Max(1, timesMicroseconds[^1] - timesMicroseconds[^2]);
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
        WriteBox(avc1, "avcC", body => body.Write(avcC.ToArray()));

        using var stsd = new MemoryStream();
        WriteFullBox(stsd, 0, 0);
        W32(stsd, 1);     // entry count
        WriteBox(stsd, "avc1", body => body.Write(avc1.ToArray()));
        return stsd.ToArray();
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


