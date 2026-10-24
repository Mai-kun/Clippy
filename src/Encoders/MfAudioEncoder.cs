using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Clippy;

/// <summary>
/// Encodes system audio in-process with the Windows AAC MFT, pushing ADTS straight into the ring.
///
/// The point of this class is to delete a dependency, not to change behaviour. ffmpeg's audio path cost
/// a second process, a pipe, an ADTS stdout parser and about 245 ms of latency that had to be
/// subtracted out of the timeline. The MFT path removes the process and the pipe; the timestamps are
/// computed the same way as before, so the A/V alignment measured in phase 4 still holds.
///
/// Three things about this encoder are not obvious and all three cost real time:
///
/// 1. Media types must come from GetOutputAvailableType, never be built by hand. A hand-built type
///    with every attribute set correctly is still refused with MF_E_ATTRIBUTENOTFOUND, because the
///    encoder wants attributes nobody thought of.
/// 2. The output sample must be created WITH a pre-allocated buffer. An empty IMFSample makes
///    ProcessOutput fail with E_INVALIDARG, which reads like a codec fault and is not one.
/// 3. The encoder has look-ahead: the first ProcessOutput calls answer MF_E_TRANSFORM_NEED_MORE_INPUT
///    and the input queue holds a single frame, so the loop must drain before it feeds.
///    MF_E_NOTACCEPTING on ProcessInput is that queue, not an error.
///
/// ponytail: WASAPI delivers float32 and the MFT wants integer PCM, so there is a conversion here.
/// A studio-format endpoint (e.g. 24-bit / 48 kHz) delivers 24-bit integer instead of float32 --
/// packed into 3 bytes, or with 24 valid bits padded into a 32-bit container -- and ToInt16 takes
/// the top 16 bits of every sample in either layout. There is no resampler -- the endpoint's own rate
/// (48 kHz or 44.1 kHz, chosen per device at startup) is what the MFT is configured for. A device at
/// any other rate is rejected loudly rather than silently pitch-shifted. Multichannel endpoints
/// (quad/5.1/7.1 on gaming headsets) are downmixed to stereo inside the same conversion, because the
/// input type promises the MFT exactly 2 channels.
/// </summary>
internal sealed class MfAudioEncoder : IAudioEncoder
{
    // The two rates the system CLSID_AACEncoderMFT accepts natively. The endpoint's own rate is used
    // verbatim -- no resampler anywhere -- so a 44.1 kHz device is configured for 44.1 kHz instead of
    // being refused outright (the old behaviour, which showed up as "no sound" on those machines).
    private const int Rate48k = 48000;
    private const int Rate44k = 44100;

    // The MFT side is always stereo 16-bit: multichannel endpoints are downmixed in ToInt16, so the
    // input type's promise and the bytes fed to it can never disagree.
    private const uint Channels = 2;
    private const int SamplesPerFrame = 1024;                              // AAC-LC frame size, fixed.
    private const int FrameBytes = SamplesPerFrame * (int)Channels * 2;    // 16-bit samples.
    private const int OutputBytesPerFrame = (int)Channels * 2;             // stereo s16, one sample frame.

    // Standard ITU/SMPTE downmix attenuation for centre/LFE/surround channels: summed at full scale
    // they would clip the stereo pair on loud material.
    private const float SurroundGain = 0.707f;

    // MFTEnumEx flags. SYNCMFT because this encoder is synchronous; SORTANDFILTER so the built-in
    // encoder sorts ahead of any third-party one. The other bits of the suggested 0x51 (HARDWARE,
    // FIELDOFUSE) would have HIDDEN the software encoder this whole class is built around.
    private const uint EnumFlags = 0x00000008 | 0x00000040;

    // Preferred output bitrates, best first. 192k is what the ffmpeg path asked for, so swapping
    // encoders does not silently change quality; 128k is the fallback when 192k is not offered.
    private static readonly uint[] PreferredBitrates = [192_000, 128_000];

    // MF_MT_AAC_PAYLOAD_TYPE = 1 selects ADTS. Zero is the headerless form, which AdtsFrameParser
    // cannot frame at all, so this one attribute is what the whole audio path stands on.
    private const uint PayloadTypeAdts = 1;

    // Normal conversation with a buffered MFT, not failures. Named so the catch sites read as
    // "needs another frame" instead of as magic hex numbers.
    private const int NotAccepting = unchecked((int)0xC00D36B5);            // MF_E_NOTACCEPTING
    private const int TransformNeedMoreInput = unchecked((int)0xC00D6D72);  // MF_E_TRANSFORM_NEED_MORE_INPUT

    // ~50 s of audio at 100 ms packets. Overflow is counted, never silent: a dropped buffer is a hole
    // in the recording and pretending otherwise hides it in the sample accounting.
    private const int QueueLimit = 512;

    private readonly IMFTransform transform;
    private readonly int sourceRate;
    private readonly int sourceChannels;
    private readonly bool sourceIsFloat;
    // Layout of one source sample, fixed once from the endpoint's real format. Loopback is float32 on
    // most machines, but a studio-format endpoint delivers 24-bit integer -- packed in 3 bytes, or 24
    // valid bits padded into a 32-bit container. Reading either as 16-bit integer shifts every sample
    // and the encoder rejects the garbage: that was the "no sound" report on those machines.
    private readonly int sourceValidBits;      // 16 or 24 for integer sources; 0 for float32.
    private readonly int sourceBytesPerSample; // 2 (int16), 3 (packed 24), 4 (float32 or 24-in-32).
    private readonly double masterZeroSeconds;

    // Derived from the endpoint's real rate, not a constant: 1024 samples last 213333.3.. hundred-ns
    // at 48 kHz and 232199.5.. at 44.1 kHz, and the MFT's own timeline must agree with whichever the
    // device delivers.
    private readonly long frameDuration100ns;

    private readonly BlockingCollection<(byte[] Pcm, long Qpc)> queue =
        new(new ConcurrentQueue<(byte[], long)>());
    private readonly object gate = new();
    private Thread? worker;
    private volatile bool running;
    private bool disposed;

    private RingBuffer? ring;

    // Audio timeline anchor, identical in method to FfmpegAudioEncoder: the shared-clock time of the
    // first sample, after which every frame's time comes from its ordinal. Never from when bytes came
    // back out of an encoder, which is what put a constant offset into every timestamp before.
    private double anchorSeconds;
    private long framesFed;
    private int frameOrdinal;
    private int anchorSet;

    private long queuedSamples;
    private long fedSamples;
    private long droppedSamples;
    private int buffersWritten;

    private MfAudioEncoder(
        IMFTransform transform,
        int sourceRate,
        int sourceChannels,
        bool sourceIsFloat,
        int sourceValidBits,
        int sourceBytesPerSample,
        double masterZeroSeconds)
    {
        this.transform = transform;
        this.sourceRate = sourceRate;
        this.sourceChannels = sourceChannels;
        this.sourceIsFloat = sourceIsFloat;
        this.sourceValidBits = sourceValidBits;
        this.sourceBytesPerSample = sourceBytesPerSample;
        this.masterZeroSeconds = masterZeroSeconds;
        frameDuration100ns = SamplesPerFrame * 10_000_000L / sourceRate;
    }

    /// <summary>
    /// Bytes per source sample: 4 for float32, 2 for 16-bit integer, 3 for packed 24-bit, 4 for
    /// 24 valid bits padded into a 32-bit container.
    /// </summary>
    private int SourceBytesPerSample => sourceBytesPerSample;

    /// <summary>The ring is where encoded frames go; nothing is encoded before this is set.</summary>
    public void StartDrain(RingBuffer target)
    {
        lock (gate)
        {
            ring = target;
        }
    }

    /// <summary>
    /// In-process and already configured, so "ready" is true immediately. The ffmpeg path needed a
    /// real wait because a process had to boot; waiting anyway would only add dead time to startup.
    /// </summary>
    public bool WaitForReady(TimeSpan timeout) => true;

    /// <summary>Queues one buffer. Never blocks: the WASAPI callback must not stall on a codec.</summary>
    public void Write(byte[] samples, long qpcPosition)
    {
        if (disposed)
            return;

        // Anchor the audio timeline to the first real buffer, on the SAME shared clock the video uses.
        // Both tracks subtract the same hardware zero, taken before any capture existed; deriving the
        // zero here instead put it hundreds of milliseconds late and that delay landed in every
        // audio timestamp.
        if (Interlocked.CompareExchange(ref anchorSet, 1, 0) == 0)
        {
            anchorSeconds = (qpcPosition / (double)Stopwatch.Frequency) - masterZeroSeconds;
            Console.WriteLine($"[sync] MFT audio anchored at {anchorSeconds:F3}s from the first packet's " +
                              $"qpcPosition minus masterZero={masterZeroSeconds:F3}, {sourceRate} Hz");
        }

        if (queue.Count >= QueueLimit)
        {
            droppedSamples += samples.Length / (sourceChannels * SourceBytesPerSample);
            return;
        }

        queuedSamples += samples.Length / (sourceChannels * SourceBytesPerSample);
        if (!queue.TryAdd((samples, qpcPosition)))
        {
            droppedSamples += samples.Length / (sourceChannels * SourceBytesPerSample);
            return;
        }

        buffersWritten++;
    }

    /// <summary>
    /// Finds, configures and returns a ready encoder. Throws with a specific reason when this machine
    /// cannot do it, because the caller's response is to fall back to ffmpeg and a vague failure makes
    /// that fallback look like a bug rather than a decision.
    /// </summary>
    public static MfAudioEncoder Start(AudioCapture format, double masterZeroSeconds)
    {
        // MFStartup's only argument is "use the light-weight version of the platform", NOT an MF
        // version number: MF_VERSION is chosen by the library, and passing one here does not compile.
        MediaFactory.MFStartup(useLightVersion: false).CheckError();

        try
        {
            // Refused rather than resampled: a rate the MFT is not configured for would be
            // pitch-shifted by the encoder itself, and nobody would notice until playback. Both rates
            // the system AAC encoder accepts natively are supported; the endpoint keeps its own.
            if (format.SampleRate != Rate48k && format.SampleRate != Rate44k)
                throw new NotSupportedException(
                    $"endpoint is {format.SampleRate} Hz; the MFT path supports {Rate48k} and {Rate44k} Hz only.");

            // The sample layout decides which conversion ToInt16 applies. Everything outside this list
            // used to be read as 16-bit integer silently -- samples shifted by a byte or two, the
            // encoder fed garbage, the clip recorded with no sound. Refused outright instead; the
            // caller falls back to ffmpeg and logs this reason.
            if (format.IsFloat32)
            {
                if (format.BitsPerSample != 32)
                    throw new NotSupportedException(
                        $"endpoint is {format.BitsPerSample}-bit float; the MFT path supports 32-bit float only.");
            }
            else if (format.BitsPerSample is not (16 or 24)
                     && !(format.BitsPerSample == 32 && format.ValidBitsPerSample == 24))
            {
                var valid = format.ValidBitsPerSample;
                throw new NotSupportedException(
                    $"endpoint is {format.BitsPerSample}-bit integer" +
                    (valid == format.BitsPerSample ? "" : $" with {valid} valid bits") +
                    "; the MFT path supports 16-bit, 24-bit packed, and 24-bit in a 32-bit container.");
            }

            // Channel count is NOT a reason to refuse a device: anything above stereo is downmixed to
            // stereo in ToInt16, and mono is duplicated to both sides, so the MFT always receives the
            // 2-channel stream its input type promises. A 5.1/7.1 gaming headset used to end in
            // silence because of the check that used to live here.

            var candidates = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.AudioEncoder,
                EnumFlags,
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Pcm },
                new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Audio, GuidSubtype = AudioFormatGuids.Aac });

            var count = 0;
            foreach (var _ in candidates)
                count++;
            if (count == 0)
                throw new InvalidOperationException("no registered AAC encoder accepts PCM on this machine.");

            // Enumeration only proves a class is REGISTERED. Activating and configuring it is what
            // proves it works, so each candidate is tried for real rather than the first trusted.
            Exception? lastProblem = null;
            foreach (var activate in candidates)
            {
                try
                {
                    return Configure(activate, format, masterZeroSeconds);
                }
                catch (Exception ex)
                {
                    lastProblem = ex;
                }
            }

            throw new InvalidOperationException(
                $"every AAC encoder candidate failed; the last said: {lastProblem?.Message}", lastProblem);
        }
        catch
        {
            // MFStartup succeeded but nothing was built, so the process-wide lock has to go back.
            MediaFactory.MFShutdown();
            throw;
        }
    }

    private static MfAudioEncoder Configure(
        IMFActivate activate,
        AudioCapture format,
        double masterZeroSeconds)
    {
        // The built-in encoder has no friendly name at all, so the CLSID is the only identifier
        // Windows reliably provides here.
        var clsid = activate.GetGUID(TransformAttributeKeys.MftTransformClsidAttribute);
        var transform = activate.ActivateObject<IMFTransform>();

        var output = FindOutput(transform, format.SampleRate) ?? throw new InvalidOperationException(
            $"no {format.SampleRate} Hz / {Channels} ch ADTS output type offered.");
        var input = FindInput(transform, format.SampleRate) ?? throw new InvalidOperationException(
            $"no {format.SampleRate} Hz / {Channels} ch 16-bit PCM input type offered.");

        transform.SetInputType(0, input, 0);
        transform.SetOutputType(0, output, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);

        var bitrate = Read(output, MediaTypeAttributeKeys.AvgBitrate) ?? 0;
        var downmix = format.Channels > Channels ? $", downmixed from {format.Channels} ch" : "";
        Console.WriteLine($"[mft] audio encoder {clsid}: {format.SampleRate} Hz, {Channels} ch{downmix}, " +
                          $"{bitrate / 1000} kbps AAC-LC, payload type {PayloadTypeAdts} (ADTS)");

        // Layout of the source stream, per the check in Start: float32, 16-bit int, packed 24-bit, or
        // 24 valid bits in a 32-bit container. The bytes-per-sample figure is the container width, so
        // a packed 24-bit endpoint steps 3 bytes per sample (6 per stereo frame) through ToInt16.
        var isFloat = format.IsFloat32;
        var encoder = new MfAudioEncoder(
            transform,
            format.SampleRate,
            format.Channels,
            isFloat,
            sourceValidBits: isFloat ? 0 : Math.Min(format.ValidBitsPerSample, 24),
            sourceBytesPerSample: format.BitsPerSample / 8,
            masterZeroSeconds);

        encoder.running = true;
        encoder.worker = new Thread(encoder.Pump) { IsBackground = true, Name = "clippy-mft-audio" };
        encoder.worker.Start();
        return encoder;
    }

    /// <summary>
    /// The whole encoder loop: pull queued PCM, convert to 16-bit, feed whole 1024-sample frames, and
    /// push whatever ADTS comes back into the ring stamped from the frame ordinal.
    ///
    /// The leftover carries between packets because WASAPI packets do not divide 1024 (480 samples per
    /// packet at 48 kHz, so 2.13 packets per frame). Feeding only whole frames is what keeps the
    /// ordinal arithmetic exact -- and therefore the A/V alignment exact.
    /// </summary>
    private void Pump()
    {
        var carry = new byte[FrameBytes];
        var carryLength = 0;

        while (running || !queue.IsCompleted)
        {
            if (!queue.TryTake(out var item, running ? 200 : 0))
                continue;

            var pcm = ToInt16(item.Pcm, carry, ref carryLength, out var frames);

            for (var f = 0; f < frames; f++)
            {
                if (!Feed(pcm.AsSpan(f * FrameBytes, FrameBytes)))
                {
                    Console.WriteLine("[mft] encoder stopped accepting input; abandoning the rest of the queue");
                    running = false;
                    break;
                }

                DrainOutput();
            }
        }

        // Whatever is left is real audio that arrived before shutdown. It is encoded, not discarded:
        // stopping early here would silently cut the tail of every recording.
        while (queue.TryTake(out var item))
        {
            var pcm = ToInt16(item.Pcm, carry, ref carryLength, out var frames);
            for (var f = 0; f < frames && Feed(pcm.AsSpan(f * FrameBytes, FrameBytes)); f++)
                DrainOutput();
        }

        // Drain the encoder's own look-ahead so its last frames are not stranded inside it.
        for (var i = 0; i < 8 && DrainOutput(); i++)
        {
        }
    }

    /// <summary>
    /// Converts one WASAPI buffer to 2-channel 16-bit PCM, returning only whole 1024-sample frames.
    ///
    /// Multichannel devices (quad, 5.1, 7.1 on gaming headsets) are downmixed to stereo here, so the
    /// MFT always sees the exact 2-channel stream its input type was configured with. Feeding the
    /// device's raw channel count would not encode "surround" -- it would encode garbage against a
    /// stereo input contract, which is exactly the silence those machines used to record.
    ///
    /// Stereo integer sources need no mix: 16-bit is copied bit-exact, and 24-bit sources (packed in
    /// 3 bytes, or 24 valid bits in a 32-bit container) keep the top 16 bits of every sample. The
    /// step is one whole source sample, so both channels of a frame land in the right place.
    ///
    /// The leftover carries between packets because WASAPI packets do not divide 1024 (480 samples per
    /// packet at 48 kHz, so 2.13 packets per frame). Feeding only whole frames is what keeps the
    /// ordinal arithmetic exact -- and therefore the A/V alignment exact.
    /// </summary>
    private byte[] ToInt16(byte[] source, byte[] carry, ref int carryLength, out int frames)
    {
        // float32 is 4 bytes per sample, 16-bit integer is 2, packed 24-bit is 3 and 24-in-32 is 4.
        var bytesPerSample = SourceBytesPerSample;
        var inputFrames = source.Length / (bytesPerSample * sourceChannels);
        var target = new byte[carryLength + inputFrames * OutputBytesPerFrame];
        Buffer.BlockCopy(carry, 0, target, 0, carryLength);

        if (!sourceIsFloat && sourceChannels == Channels)
        {
            if (sourceValidBits == 16)
            {
                // Stereo 16-bit integer already IS the target format; copying it bit-exact beats a
                // round-trip through float, which would shave an LSB off positive full-scale samples.
                Buffer.BlockCopy(source, 0, target, carryLength, inputFrames * OutputBytesPerFrame);
            }
            else
            {
                // Stereo 24-bit (packed or padded into 32 bits): take the top 16 bits of every sample.
                for (var f = 0; f < inputFrames; f++)
                {
                    var src = f * bytesPerSample * sourceChannels;
                    var dst = carryLength + f * OutputBytesPerFrame;
                    WriteS16(target, dst, ReadTop16(source, src));
                    WriteS16(target, dst + 2, ReadTop16(source, src + bytesPerSample));
                }
            }
        }
        else
        {
            for (var f = 0; f < inputFrames; f++)
            {
                MixFrameToStereo(source, f * bytesPerSample * sourceChannels, bytesPerSample,
                    out var left, out var right);
                var offset = carryLength + f * OutputBytesPerFrame;
                WriteS16(target, offset, left);
                WriteS16(target, offset + 2, right);
            }
        }

        // The tail that does not make a whole frame stays for the next call.
        frames = target.Length / FrameBytes;
        carryLength = target.Length - (frames * FrameBytes);
        if (carryLength > 0)
            Buffer.BlockCopy(target, frames * FrameBytes, carry, 0, carryLength);

        return target;
    }

    /// <summary>
    /// One source sample frame -> one stereo pair, downmixed in WAVEFORMATEXTENSIBLE channel order
    /// (FL, FR, C, LFE, BL, BR, SL, SR, ...). Front left/right pass through at unity; centre and LFE
    /// go to both sides and the surrounds to their own side, all at the standard 0.707 (-3 dB)
    /// ITU/SMPTE downmix attenuation so full-scale summing cannot clip the pair. Mono is duplicated
    /// rather than attenuated: a single channel is meant to be heard on both sides at its own level.
    /// </summary>
    private void MixFrameToStereo(byte[] source, int offset, int bytesPerSample,
        out float left, out float right)
    {
        if (sourceChannels == 1)
        {
            var mono = ReadNormalized(source, offset);
            left = mono;
            right = mono;
            return;
        }

        left = 0f;
        right = 0f;
        for (var ch = 0; ch < sourceChannels; ch++)
        {
            var value = ReadNormalized(source, offset + ch * bytesPerSample);

            switch (ch)
            {
                case 0:  // front left
                    left += value;
                    break;
                case 1:  // front right
                    right += value;
                    break;
                case 2:  // centre
                case 3:  // LFE
                    left += SurroundGain * value;
                    right += SurroundGain * value;
                    break;
                default: // BL/SL (even index) -> left, BR/SR (odd index) -> right
                    if ((ch & 1) == 0)
                        left += SurroundGain * value;
                    else
                        right += SurroundGain * value;
                    break;
            }
        }
    }

    /// <summary>
    /// One source sample's top 16 bits as a signed short. 16-bit is a straight read; packed 24-bit is
    /// bytes 1 (low) and 2 (high, holding the sign bit); 24 valid bits in a 32-bit container live in
    /// bytes 2 and 3, because WAVEFORMATEXTENSIBLE aligns the valid bits to the most significant end.
    /// </summary>
    private short ReadTop16(byte[] source, int offset)
    {
        if (sourceValidBits == 16)
            return BitConverter.ToInt16(source, offset);
        if (sourceBytesPerSample == 3)
            return (short)(source[offset + 1] | (source[offset + 2] << 8));
        return (short)(source[offset + 2] | (source[offset + 3] << 8));
    }

    /// <summary>One source sample as [-1, 1), for the downmix sum.</summary>
    private float ReadNormalized(byte[] source, int offset)
    {
        if (sourceIsFloat)
            return BitConverter.ToSingle(source, offset);
        if (sourceValidBits == 16)
            return BitConverter.ToInt16(source, offset) / 32768f;

        int value;
        if (sourceBytesPerSample == 3)
        {
            value = source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16);
            if ((value & 0x00800000) != 0) // sign-extend bit 23 into the int32
                value |= unchecked((int)0xFF000000);
        }
        else
        {
            // 24 valid bits, MSB-aligned in the 32-bit container: bytes 1..3, sign bit already at 31.
            // Shifting down 8 puts them on the same 2^23 scale as the packed branch -- without it the
            // value is 256x too large and a full-scale sample would come out as -256.0f.
            value = ((source[offset + 1] << 8) | (source[offset + 2] << 16) | (source[offset + 3] << 24)) >> 8;
        }
        return value / 8388608f;
    }

    /// <summary>
    /// Clamped, then scaled, never a bare cast: (short)3.5f is undefined, and loopback data does
    /// overshoot 1.0 often enough that the wrap would be audible as a click.
    /// </summary>
    private static short ToS16(float value) =>
        (short)Math.Round(Math.Clamp(value, -1f, 1f) * (value < 0 ? 32768f : 32767f));

    private static void WriteS16(byte[] target, int offset, float value)
    {
        var s16 = ToS16(value);
        target[offset] = (byte)(s16 & 0xFF);
        target[offset + 1] = (byte)((s16 >> 8) & 0xFF);
    }

    /// <summary>Little-endian store of an already-16-bit sample (the 24-bit conversion's output path).</summary>
    private static void WriteS16(byte[] target, int offset, short value)
    {
        target[offset] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>Hands one 1024-sample frame to the encoder. False means the pipeline has stopped.</summary>
    private bool Feed(ReadOnlySpan<byte> pcm)
    {
        // Both wrappers are disposed: AddBuffer takes the sample's OWN reference, so ours must still
        // be released. Leaving the buffer undisposed leaked ~12 KB of native memory per AAC frame
        // (with the output buffer below) -- ~2 GB of commit per hour, never touched again.
        using var sample = MediaFactory.MFCreateSample();
        using var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var dst, out _, out _);
        Marshal.Copy(pcm.ToArray(), 0, dst, pcm.Length);
        buffer.Unlock();
        buffer.CurrentLength = pcm.Length;
        sample.AddBuffer(buffer);

        // The MFT is fed on its own timeline, unrelated to the capture clock. The capture-clock time
        // is attached to the OUTPUT frame, where the ordinal arithmetic is exact; stamping the input
        // instead would put the residual packet fraction into every timestamp.
        var ordinal = framesFed * frameDuration100ns;
        framesFed++;
        sample.SampleTime = ordinal;
        sample.SampleDuration = frameDuration100ns;
        fedSamples += SamplesPerFrame;

        try
        {
            transform.ProcessInput(0, sample, 0);
            return true;
        }
        catch (SharpGenException ex) when (ex.HResult == NotAccepting)
        {
            // Input queue full: drain it and retry. The frame is NOT dropped, because losing it would
            // put a permanent gap in the audio track.
            return DrainOutput();
        }
    }

    /// <summary>Takes whatever the encoder has ready. False when it is still short of input.</summary>
    private bool DrainOutput()
    {
        using var sample = MediaFactory.MFCreateSample();
        // The pre-allocated buffer is not optional: without it this encoder answers E_INVALIDARG.
        using var outputBuffer = MediaFactory.MFCreateMemoryBuffer(8192);
        sample.AddBuffer(outputBuffer);
        var data = new OutputDataBuffer { StreamID = 0, Sample = sample };

        try
        {
            transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _).CheckError();
        }
        catch (SharpGenException ex) when (ex.HResult == TransformNeedMoreInput)
        {
            return false;
        }

        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var src, out _, out var length);
        var adts = new byte[length];
        Marshal.Copy(src, adts, 0, length);
        contiguous.Unlock();

        var target = ring;
        if (target is not null)
        {
            var rented = ArrayPool<byte>.Shared.Rent(length);
            adts.CopyTo(rented, 0);
            target.Push(rented, length, NextFrameTime(), isKeyframe: true);
        }

        return true;
    }

    /// <summary>
    /// Shared-clock time of the next AAC frame, from its ordinal. Never the time the bytes appeared,
    /// which would carry the whole encoder and queue latency into every timestamp.
    /// </summary>
    private double NextFrameTime()
    {
        var k = frameOrdinal++;
        return anchorSeconds + (k * (double)SamplesPerFrame / sourceRate);
    }

    /// <summary>
    /// Picks an output type the encoder itself enumerated. Hand-built types look equivalent and are
    /// still refused with MF_E_ATTRIBUTENOTFOUND, so asking what it accepts cannot get that wrong.
    /// The rate must match the endpoint exactly: the type that is configured is the type the ADTS
    /// frames will declare, and a mismatch is a file that plays at the wrong pitch.
    /// </summary>
    private static IMFMediaType? FindOutput(IMFTransform transform, int sampleRate)
    {
        foreach (var bitrate in PreferredBitrates)
        {
            for (var i = 0; i < 128; i++)
            {
                IMFMediaType type;
                try
                {
                    type = transform.GetOutputAvailableType(0, i);
                }
                catch (SharpGenException)
                {
                    break; // enumeration ended
                }

                if (Read(type, MediaTypeAttributeKeys.AudioSamplesPerSecond) == (uint)sampleRate &&
                    Read(type, MediaTypeAttributeKeys.AudioNumChannels) == Channels &&
                    Read(type, MediaTypeAttributeKeys.AvgBitrate) == bitrate &&
                    Read(type, MediaTypeAttributeKeys.AacPayloadType) == PayloadTypeAdts)
                {
                    return type;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The 16-bit PCM input type at the endpoint's own rate, stereo because everything above is
    /// downmixed before it gets here. The bits check reads "absent or 16": a type that does not state
    /// a width is the same one this encoder has always accepted, while one that states 24 or 32 is
    /// not what the conversion emits -- sources of any width are reduced to 16-bit before feeding.
    /// </summary>
    private static IMFMediaType? FindInput(IMFTransform transform, int sampleRate)
    {
        for (var i = 0; i < 128; i++)
        {
            IMFMediaType type;
            try
            {
                type = transform.GetInputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                return null;
            }

            var bits = Read(type, MediaTypeAttributeKeys.AudioBitsPerSample);
            if (Read(type, MediaTypeAttributeKeys.AudioSamplesPerSecond) == (uint)sampleRate &&
                Read(type, MediaTypeAttributeKeys.AudioNumChannels) == Channels &&
                bits is null or 16)
            {
                return type;
            }
        }
        return null;
    }

    // GetUInt32 throws when an attribute is absent, and absence is a legitimate answer: input types
    // carry no bitrate. Read it as a question, not a claim.
    private static uint? Read(IMFMediaType type, Guid key)
    {
        try
        {
            return type.GetUInt32(key);
        }
        catch (SharpGenException)
        {
            return null;
        }
    }

    public void PrintSampleAccounting()
    {
        var expectedFrames = (double)fedSamples / SamplesPerFrame;
        Console.WriteLine($"[audioacct] buffers={buffersWritten} queued={queuedSamples} fed={fedSamples} " +
                          $"dropped={droppedSamples} adtsFramesOut={frameOrdinal} " +
                          $"fed/1024={expectedFrames:F2} " +
                          $"diff={frameOrdinal - expectedFrames:F2} " +
                          $"(one AAC frame is {SamplesPerFrame} samples)");
    }

    /// <summary>Blocks until the worker has finished encoding everything queued.</summary>
    public void WaitForDrain(TimeSpan timeout) => worker?.Join(timeout);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        running = false;
        queue.CompleteAdding();
        worker?.Join(TimeSpan.FromSeconds(10));


        // The worker has already drained the look-ahead; this is the documented shutdown handshake.
        // MFShutdown while an MFT is still live is undefined, so the order here is not negotiable.
        try
        {
            transform.ProcessMessage(TMessageType.MessageCommandDrain, 0);
        }
        catch (SharpGenException)
        {
            // Already torn down by the OS; nothing left to release.
        }

        transform.Dispose();
        MediaFactory.MFShutdown();
    }
}


