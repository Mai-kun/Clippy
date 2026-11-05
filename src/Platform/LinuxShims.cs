#if LINUX
namespace Clippy;

/// <summary>
/// Linux-only shim for Capture\AudioCapture.cs, which is a WASAPI class and does not compile here.
/// FfmpegAudioEncoder has to stay in the Linux build -- --test-audio-timeline is a cross-platform
/// flag that lives in it -- yet its Start() declares one parameter of that missing type and touches
/// exactly three members: FormatArguments, SampleRate and BlockAlign. Those three are all the shim
/// supplies, so the encoder compiles unchanged and its self-test runs on Linux. If the Windows code
/// ever starts using more of the real class, the Linux build fails loudly right here instead of
/// letting the two definitions drift apart silently.
/// </summary>
internal sealed class AudioCapture
{
    /// <summary>Real class returns the ffmpeg demuxer options for the captured format.</summary>
    public IEnumerable<string> FormatArguments => [];

    /// <summary>Real class reports the endpoint's mix format rate.</summary>
    public int SampleRate => 48_000;

    /// <summary>Bytes per sample frame (channels * bytes/sample) on the real endpoint.</summary>
    public int BlockAlign => 8;
}
#endif
