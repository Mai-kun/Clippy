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
    double DroppedAudio,
    double VideoStart,
    double AudioStart)
{
    /// <summary>How much silence the audio track must be padded with at its head, in seconds.</summary>
    public double AudioLeadIn => AudioStart > VideoStart ? AudioStart - VideoStart : 0.0;
}

/// <summary>
/// Puts both tracks on ONE shared zero, so a video frame and an audio sample that came from the same
/// physical moment keep that relationship in the exported file.
/// </summary>
/// <remarks>
/// t=0 is ALWAYS the video keyframe. H.264 cannot be decoded from anything earlier, so no clip can
/// begin before it. Audio recorded before that instant is reported as negative times and dropped by
/// the caller; when audio starts later, the resulting gap is a real one and is reported as
/// <see cref="AudioLeadIn"/> to be filled with silence. Video is never cut.
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
            return new TrackAlignment(0, [], [], 0, 0, 0, 0);
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
                0,
                vZero,
                aZero);
        }

        // The zero is ALWAYS the video keyframe when there is one. H.264 cannot be decoded from
        // anything earlier, so no clip can begin before it; the audio track has to be made to agree.
        //
        // An MP4 written without an edit list always plays both tracks from 0.000, so a start offset
        // that lives only in the timestamps is discarded by the container. Anything before the
        // keyframe has to be either dropped (the normal case: sound was already playing) or filled
        // with real silent frames (a clip that begins before audio exists).
        var videoStart = videoCaptureTimes.Count > 0 ? videoCaptureTimes[0] : double.NaN;
        var audioStart = audioCaptureTimes.Count > 0 ? audioCaptureTimes[0] : double.NaN;
        var zero = videoCaptureTimes.Count > 0 ? videoStart : audioStart;

        var v = videoCaptureTimes.Select(t => t - zero).ToList();
        var a = audioCaptureTimes.Select(t => t - zero).ToList();
        return new TrackAlignment(zero, v, a, 0, 0, videoStart, audioStart);
    }
}

