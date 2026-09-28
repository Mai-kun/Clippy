namespace Clippy;

/// <summary>Result of aligning the two tracks onto one shared zero.</summary>
/// <param name="Zero">The absolute capture time that becomes t=0 of the clip.</param>
/// <param name="VideoTimes">Video capture times relative to <paramref name="Zero"/>, all >= 0.</param>
/// <param name="AudioTimes">Audio capture times relative to <paramref name="Zero"/>, all >= 0.</param>
/// <param name="DroppedVideo">How much video was cut from the head.</param>
/// <param name="DroppedAudio">How much audio was cut from the head.</param>
public readonly record struct TrackAlignment(
    double Zero,
    IReadOnlyList<double> VideoTimes,
    IReadOnlyList<double> AudioTimes,
    double DroppedVideo,
    double DroppedAudio);

/// <summary>
/// Puts both tracks on ONE shared zero, so a video frame and an audio sample that came from the same
/// physical moment keep that relationship in the exported file.
/// </summary>
/// <remarks>
/// Both cases are deliberate, and neither is privileged:
/// video later than audio -> the early audio is dropped, because before that moment there is no
/// picture to pair it with; audio later than video -> the early video is dropped, symmetrically.
/// Whichever track starts later defines the zero, and the earlier one loses its head.
/// Without this, each track was written relative to its own first sample and the origins differed
/// by the start-up gap between WASAPI loopback and the first video frame.
/// </remarks>
public static class TrackAligner
{
    public static TrackAlignment Align(
        IReadOnlyList<double> videoCaptureTimes,
        IReadOnlyList<double> audioCaptureTimes,
        bool align = true)
    {
        if (videoCaptureTimes.Count == 0 && audioCaptureTimes.Count == 0)
        {
            return new TrackAlignment(0, [], [], 0, 0);
        }

        // With alignment off, every track keeps its own origin -- the bug this exists to prevent.
        if (!align)
        {
            var vZero = videoCaptureTimes.Count > 0 ? videoCaptureTimes[0] : 0;
            var aZero = audioCaptureTimes.Count > 0 ? audioCaptureTimes[0] : 0;
            return new TrackAlignment(
                vZero,
                videoCaptureTimes.Select(t => t - vZero).ToList(),
                audioCaptureTimes.Select(t => t - aZero).ToList(),
                0,
                0);
        }

        var zero = Math.Max(
            videoCaptureTimes.Count > 0 ? videoCaptureTimes[0] : double.MinValue,
            audioCaptureTimes.Count > 0 ? audioCaptureTimes[0] : double.MinValue);

        var v = videoCaptureTimes.Where(t => t >= zero).Select(t => t - zero).ToList();
        var a = audioCaptureTimes.Where(t => t >= zero).Select(t => t - zero).ToList();
        var droppedVideo = videoCaptureTimes.Count > 0 ? zero - videoCaptureTimes[0] : 0;
        var droppedAudio = audioCaptureTimes.Count > 0 ? zero - audioCaptureTimes[0] : 0;
        return new TrackAlignment(zero, v, a, droppedVideo, droppedAudio);
    }
}

