namespace Clippy;

/// <summary>
/// Assert-based self-check for the elementary-stream parsers, on synthetic byte sequences only.
/// Deliberately a switchable mode rather than a test framework: the project is a single NativeAOT
/// executable with no test infrastructure, and these parsers are the riskiest part of the ring buffer
/// (silent data loss here would be hard to tell apart from a buffer or mux bug later on).
/// </summary>
internal static class ParserSelfTest
{
    public static int Run()
    {
        var failures = 0;
        failures += Check("H264: NAL split and IDR detection", H264SplitAndIdr);
        failures += Check("H264: escaped start code inside payload", H264EscapedStartCode);
        failures += Check("H264: split across chunk boundary", H264ChunkBoundary);
        failures += Check("H264: split inside a 4-byte start code", H264SplitInsideStartCode);
        failures += Check("ADTS: frame split and lengths", AdtsSplitAndLengths);
        failures += Check("ADTS: frame split across chunk boundary", AdtsChunkBoundary);
        failures += Check("ADTS: false sync inside payload ignored", AdtsFalseSync);

        Console.WriteLine(failures == 0 ? "PARSER SELFTEST: OK" : $"PARSER SELFTEST: FAILED ({failures})");
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

    private static readonly byte[] Sc3 = [0x00, 0x00, 0x01];
    private static readonly byte[] Sc4 = [0x00, 0x00, 0x00, 0x01];

    /// <summary>Builds a NAL with the given nal_unit_type and body.</summary>
    private static byte[] Nal(int type, params byte[] body)
    {
        var result = new byte[1 + body.Length];
        result[0] = (byte)(0x60 | type); // nal_ref_idc=3, so type 5 still reads as IDR
        body.CopyTo(result.AsSpan(1));
        return result;
    }

    private static List<(byte[] Data, bool IsIdr)> ParseH264(byte[] stream)
    {
        var packets = new List<(byte[], bool)>();
        var parser = new H264AnnexBParser();
        parser.Append(stream, 1.25, (data, _, isIdr, _) => packets.Add((data, isIdr)));
        parser.Flush(1.25, (data, _, isIdr, _) => packets.Add((data, isIdr)));
        return packets;
    }

    private static string? H264SplitAndIdr()
    {
        var stream = new List<byte>();
        stream.AddRange(Sc4);
        stream.AddRange(Nal(5, 0xAA, 0xBB));   // IDR
        stream.AddRange(Sc4);
        stream.AddRange(Nal(1, 0xCC));        // non-IDR
        stream.AddRange(Sc3);
        stream.AddRange(Nal(1, 0xDD, 0xEE));   // non-IDR, 3-byte start code

        var got = ParseH264([.. stream]);
        if (got.Count != 3)
            return $"expected 3 NALs, got {got.Count}";

        if (!got[0].IsIdr || got[1].IsIdr || got[2].IsIdr)
            return $"keyframe flags wrong: {got[0].IsIdr},{got[1].IsIdr},{got[2].IsIdr}";

        if (got[0].Data.Length != 3 || got[0].Data[0] != 0x65)
            return $"first NAL payload wrong: len={got[0].Data.Length} first={got[0].Data[0]:X2}";

        return null;
    }

    private static string? H264EscapedStartCode()
    {
        // 00 00 03 01 is the RBSP escaping of 00 00 01 and must NOT split the NAL.
        var body = new byte[] { 0x00, 0x00, 0x03, 0x01, 0x42 };
        var stream = new List<byte>();
        stream.AddRange(Sc4);
        stream.AddRange(Nal(1, body));
        stream.AddRange(Sc4);
        stream.AddRange(Nal(1, 0x99));

        var got = ParseH264([.. stream]);
        if (got.Count != 2)
            return $"escaped sequence split the NAL: expected 2, got {got.Count}";

        if (got[0].Data.Length != body.Length + 1)
            return $"first NAL length wrong: {got[0].Data.Length} (expected {body.Length + 1})";

        for (var i = 0; i < body.Length; i++)
        {
            if (got[0].Data[i + 1] != body[i])
                return $"payload byte {i} differs: {got[0].Data[i + 1]:X2} != {body[i]:X2}";
        }

        return null;
    }

    private static string? H264ChunkBoundary()
    {
        var stream = new List<byte>();
        stream.AddRange(Sc4);
        stream.AddRange(Nal(5, 0x11, 0x22, 0x33, 0x44));
        stream.AddRange(Sc4);
        stream.AddRange(Nal(1, 0x55, 0x66));
        var bytes = stream.ToArray();

        // One byte at a time: every NAL is necessarily cut by a chunk boundary.
        var packets = new List<(byte[] Data, bool IsIdr)>();
        var parser = new H264AnnexBParser();
        for (var i = 0; i < bytes.Length; i++)
            parser.Append(bytes.AsSpan(i, 1), 1.5, (d, _, k, _) => packets.Add((d, k)));
        parser.Flush(1.5, (d, _, k, _) => packets.Add((d, k)));

        if (packets.Count != 2)
            return $"expected 2 NALs, got {packets.Count} (a NAL was lost or duplicated)";

        if (packets[0].Data.Length != 5 || !packets[0].IsIdr)
            return $"first NAL wrong: len={packets[0].Data.Length} idr={packets[0].IsIdr}";

        if (packets[1].Data.Length != 3 || packets[1].IsIdr)
            return $"second NAL wrong: len={packets[1].Data.Length} idr={packets[1].IsIdr}";

        return null;
    }

    private static string? H264SplitInsideStartCode()
    {
        // The 00 00 00 of a 4-byte start code is cut between chunks; nothing may be lost or duplicated.
        var stream = new List<byte>();
        stream.AddRange(Sc4);
        stream.AddRange(Nal(5, 0xAB, 0xCD));
        stream.AddRange(Sc4);
        stream.AddRange(Nal(1, 0xEF));
        var bytes = stream.ToArray();

        var packets = new List<(byte[] Data, bool IsIdr)>();
        var parser = new H264AnnexBParser();
        for (var i = 0; i < bytes.Length; i++)
            parser.Append(bytes.AsSpan(i, 1), 2.0, (d, _, k, _) => packets.Add((d, k)));
        parser.Flush(2.0, (d, _, k, _) => packets.Add((d, k)));

        if (packets.Count != 2)
            return $"expected 2 NALs, got {packets.Count}";

        if (packets[0].Data.Length != 3 || packets[1].Data.Length != 2)
            return $"payload lengths wrong: {packets[0].Data.Length}, {packets[1].Data.Length}";

        return null;
    }

    /// <summary>Builds a valid ADTS header for a frame of the given total length.</summary>
    private static byte[] AdtsFrame(int totalLength, byte fill)
    {
        var frame = new byte[totalLength];
        Array.Fill(frame, fill);
        frame[0] = 0xFF;
        frame[1] = 0xF1;                       // MPEG-4, layer 0, protection absent
        frame[2] = 0x50;
        frame[3] = (byte)(0x80 | ((totalLength >> 11) & 0x03));
        frame[4] = (byte)((totalLength >> 3) & 0xFF);
        frame[5] = (byte)(((totalLength & 0x07) << 5) | 0x1F);
        frame[6] = 0xFC;
        return frame;
    }

    private static List<byte[]> ParseAdts(byte[] stream, int chunk)
    {
        var packets = new List<byte[]>();
        var parser = new AdtsFrameParser();
        for (var i = 0; i < stream.Length; i += chunk)
            parser.Append(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)), 3.5,
                (d, _, _) => packets.Add(d));
        return packets;
    }

    private static string? AdtsSplitAndLengths()
    {
        var sizes = new[] { 100, 250, 61, 400, 128 };
        var stream = new List<byte>();
        foreach (var size in sizes)
            stream.AddRange(AdtsFrame(size, 0x5A));

        var got = ParseAdts([.. stream], int.MaxValue);
        if (got.Count != sizes.Length)
            return $"expected {sizes.Length} frames, got {got.Count}";

        for (var i = 0; i < sizes.Length; i++)
        {
            if (got[i].Length != sizes[i])
                return $"frame {i} length {got[i].Length}, expected {sizes[i]}";
        }

        return null;
    }

    private static string? AdtsChunkBoundary()
    {
        var sizes = new[] { 100, 250, 61, 400 };
        var stream = new List<byte>();
        foreach (var size in sizes)
            stream.AddRange(AdtsFrame(size, 0x5A));
        var bytes = stream.ToArray();

        // One byte at a time cuts headers and payloads alike.
        var got = ParseAdts(bytes, 1);
        if (got.Count != sizes.Length)
            return $"expected {sizes.Length} frames, got {got.Count} (frame lost or duplicated)";

        for (var i = 0; i < sizes.Length; i++)
        {
            if (got[i].Length != sizes[i])
                return $"frame {i} length {got[i].Length}, expected {sizes[i]}";
        }

        return null;
    }

    private static string? AdtsFalseSync()
    {
        // A frame whose payload contains 0xFF 0xFx must not be split: the declared length governs.
        var frame = AdtsFrame(300, 0x00);
        frame[100] = 0xFF;
        frame[101] = 0xF1; // looks like a sync word
        var stream = new List<byte>();
        stream.AddRange(frame);
        stream.AddRange(AdtsFrame(200, 0x5A));

        var got = ParseAdts([.. stream], int.MaxValue);
        if (got.Count != 2)
            return $"false sync split a frame: expected 2, got {got.Count}";

        if (got[0].Length != 300 || got[1].Length != 200)
            return $"lengths wrong: {got[0].Length}, {got[1].Length}";

        return null;
    }
}

