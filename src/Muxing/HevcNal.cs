namespace Clippy;

/// <summary>
/// HEVC (H.265) NAL unit header, ITU-T H.265 §7.3.2.
///
/// H.264 puts <c>nal_unit_type</c> in the low 5 bits of a ONE-byte header. HEVC uses a TWO-byte
/// header with the type in bits 1-6 of the first byte. Reading an HEVC NAL the H.264 way therefore
/// shifts every type by one, which is silent and fatal at once: a VPS (32) reads as 16, an SPS (33)
/// as 17 -- both inside the IRAP range, so they are "keyframes", a parameter set is mistaken for a
/// picture, and the export starts mid-stream on bytes no decoder can start from.
/// <para>
/// This class exists so that one definition of "what type is this NAL" is shared by the parser, the
/// encoder that pulls VPS/SPS/PPS out of NVENC's block, and the muxer that files them into hvcC.
/// </para>
/// </summary>
internal static class HevcNal
{
    public const int Vps = 32;
    public const int Sps = 33;
    public const int Pps = 34;

    /// <summary>
    /// nal_unit_type from the two-byte header: bits 1-6 of the first byte.
    /// </summary>
    public static int Type(ReadOnlySpan<byte> nal) => (nal[0] >> 1) & 0x3F;

    /// <summary>
    /// True for the coded slice NALs (types 0..31), which are the only NALs that carry a picture.
    /// Everything at 32 and above is a parameter set, an access unit delimiter or SEI: those travel
    /// WITH a picture and must never consume a capture time of their own.
    /// </summary>
    public static bool IsVcl(int type) => type <= 31;

    /// <summary>
    /// True for the IRAP range, 16..21: BLA, IDR_W_RADL, IDR_N_LP and CRA. Those are the pictures a
    /// decoder can start a stream from, so they are what "keyframe" means here -- and what the ring
    /// buffer trims to, because a clip that begins on anything else decodes to garbage.
    /// </summary>
    public static bool IsKeyframe(int type) => type is >= 16 and <= 21;
}