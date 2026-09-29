namespace Clippy;

/// <summary>
/// A video encoder, whatever is under it.
/// </summary>
/// <remarks>
/// The point of this is narrow and deliberate: everything downstream of the encoder -- the ring
/// buffer, the capture-clock timestamps, TrackAligner, Mp4Writer -- must not be able to tell which
/// encoder produced the bytes. The A/V alignment in this project was measured and tuned to the
/// millisecond, and that alignment is entirely downstream of this interface. Swapping what is above
/// it is therefore safe; changing anything below it is not.
///
/// The interface describes the units the pipeline actually speaks, not the units an encoder would
/// like: raw BGRA in, Annex B out, and the capture clock carried alongside. An implementation that
/// needs different units has to convert before returning, not ask the pipeline to adapt.
/// </remarks>
internal interface IVideoEncoder : IDisposable
{
    /// <summary>SPS NAL, once the encoder has produced one. Null until then.</summary>
    byte[]? Sps { get; }

    /// <summary>PPS NAL, once the encoder has produced one. Null until then.</summary>
    byte[]? Pps { get; }

    /// <summary>
    /// Frames handed in versus access units that came out. A gap larger than the pipeline's
    /// in-flight tolerance means the encoder lost or reordered frames and every timestamp in a clip
    /// cut from that stretch is untrustworthy.
    /// </summary>
    (int Captured, int AccessUnits) TimingCounts { get; }

    /// <summary>
    /// Records the capture-clock time of the next <see cref="Write"/>. It is a separate call because
    /// the pixel copy and the time it belongs to are measured at different instants, and the
    /// difference between them is exactly the pipeline delay being calibrated.
    /// </summary>
    void EnqueueCaptureTime(double captureClockSeconds);

    /// <summary>One BGRA frame, raw, top-down, no stride padding.</summary>
    void Write(byte[] bgraPixels, double systemTimeMs);

    /// <summary>Starts the reader that moves encoded bytes into the ring.</summary>
    void StartDrain(RingBuffer target);

    /// <summary>Blocks until the encoder has produced its first bytes, or the timeout expires.</summary>
    bool WaitForReady(TimeSpan timeout);

    /// <summary>
    /// Blocks until whatever the encoder queued has been drained into the ring, or the timeout
    /// expires. Shutdown only: it is what keeps an export from slicing a ring that is still filling.
    /// </summary>
    void WaitForDrain(TimeSpan timeout);
}

/// <summary>
/// An audio encoder, whatever is under it.
/// </summary>
/// <remarks>
/// The unit is ADTS, not raw AAC, and that is a load-bearing detail rather than an accident:
/// <see cref="AdtsFrameParser"/> reads ADTS headers to find frame boundaries and measure duration,
/// and <c>TrackAligner</c> derives audio time from the frame count. An encoder that emits raw AAC
/// access units has to add the ADTS headers itself before handing anything here.
/// </remarks>
internal interface IAudioEncoder : IDisposable
{
    /// <summary>PCM samples for the moment identified by <paramref name="qpcPosition"/>.</summary>
    void Write(byte[] samples, long qpcPosition);

    void StartDrain(RingBuffer target);

    void WaitForDrain(TimeSpan timeout);

    /// <summary>Blocks until the encoder is consuming, or the timeout expires.</summary>
    bool WaitForReady(TimeSpan timeout);

    /// <summary>Prints how many buffers arrived and how many frames were encoded. Diagnostic only.</summary>
    void PrintSampleAccounting();
}