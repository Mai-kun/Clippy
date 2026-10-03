namespace Clippy;

/// <summary>
/// Structural self-check for a written MP4, deliberately independent of ffprobe. ISO BMFF cascades:
/// one wrong size desynchronises every box after it, and a demuxer then reports "nothing found" with no
/// hint where. This walks the file and pinpoints the first box whose size does not reconcile.
///
/// It also reports the first child type it does not expect at a level, which catches a structurally
/// valid file that is semantically wrong.
/// </summary>
internal static class Mp4BoxWalker
{
    private static readonly Dictionary<string, string[]> Expected = new()
    {
        [""] = ["ftyp", "mdat", "moov"],
        ["moov"] = ["mvhd", "trak"],
        ["trak"] = ["tkhd", "mdia"],
        ["mdia"] = ["mdhd", "hdlr", "minf"],
        // A minf holds vmhd for video and smhd for audio, so both are legal on this level.
        ["minf"] = ["vmhd", "smhd", "dinf", "stbl"],
        ["dinf"] = ["dref"],
        ["stbl"] = ["stsd", "stts", "stsc", "stsz", "stco"],
        // Full boxes carrying a 4-byte entry count before their children. The video sample entry is
        // avc1 for H.264 and hvc1 for HEVC; they carry identical fields, so only the name differs.
        ["stsd"] = ["avc1", "hvc1", "mp4a"],
        ["dref"] = ["url "],
    };

    /// <summary>
    /// Sample entries are NOT plain containers: VisualSampleEntry carries 78 bytes of fixed fields and
    /// AudioSampleEntry 40 bytes (6 reserved + 2 dri + 4 reserved + 2 version + 2 revision +
    /// 12 pre_defined[3] + 2 channelcount + 2 samplesize + 2 pre_defined + 2 reserved + 4 samplerate)
    /// before their child boxes. Recursing at header+8 lands inside those fields, which the walker
    /// correctly reported as a child box with a garbage size.
    /// </summary>
    private static readonly Dictionary<string, int> SampleEntryChildOffset = new()
    {
        ["avc1"] = 8 + 78,
        // hvc1 is the same VisualSampleEntry field-for-field, so the same 78-byte prefix applies.
        ["hvc1"] = 8 + 78,
        // 8 header + 28 field bytes, so the child box starts at 36. Verified against a real
        // ffmpeg-produced m4a (mp4a box size 90 = 8 + 28 + esds 54).
        ["mp4a"] = 8 + 28,
    };

    public static string? Check(byte[] data)
    {
        return Walk(data, 0, data.Length, "");
    }

    private static string? Walk(byte[] data, int start, int end, string parent)
    {
        var offset = start;
        var first = true;

        while (offset + 8 <= end)
        {
            var size = (int)ReadUInt32(data, offset);
            var type = System.Text.Encoding.ASCII.GetString(data, offset + 4, 4);

            if (size == 0)
                return $"{Describe(parent, offset)}: box '{type}' declares size 0, which is never valid here";

            if (size == 1)
                return $"{Describe(parent, offset)}: box '{type}' uses the 64-bit size form, which this writer does not emit";

            if (size < 8)
                return $"{Describe(parent, offset)}: box '{type}' declares size {size}, smaller than its own 8-byte header";

            if (offset + (long)size > end)
                return $"{Describe(parent, offset)}: box '{type}' size {size} runs past the end of {Describe(parent, start)} " +
                       $"(parent payload ends at {end}, box would end at {offset + size})";

            if (Expected.TryGetValue(parent, out var allowed) && !allowed.Contains(type))
                return $"{Describe(parent, offset)}: unexpected box '{type}' at this level (allowed: {string.Join(", ", allowed)})";

            if (type == "esds")
            {
                // esds holds MPEG-4 descriptors, not ISO boxes, so its payload is NOT walked as boxes.
                // Skipped for now; the descriptor tree gets its own branch once the box sizes reconcile.
            }
            else if (type == "stsd" || type == "dref")
            {
                // Full boxes: 4 bytes of version/flags + 4 of entry count precede their children.
                var err = Walk(data, offset + 16, offset + size, type);
                if (err is not null)
                    return err;
            }
            else if (SampleEntryChildOffset.TryGetValue(type, out var childOffset))
            {
                var err = Walk(data, offset + childOffset, offset + size, type);
                if (err is not null)
                    return err;
            }
            else if (Expected.ContainsKey(type))
            {
                var err = Walk(data, offset + 8, offset + size, type);
                if (err is not null)
                    return err;
            }

            offset += size;
            first = false;
        }

        if (offset != end)
            return $"{Describe(parent, start)}: children end at {offset} but the parent payload ends at {end} " +
                   $"(a box size is {end - offset} bytes too long)";

        if (first)
            return $"{Describe(parent, start)}: contains no child boxes at all";

        return null;
    }

    private static string Describe(string parent, int offset) =>
        string.IsNullOrEmpty(parent) ? $"top level at byte {offset}" : $"inside '{parent}' at byte {offset}";

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
