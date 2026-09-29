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
/// ponytail: WASAPI delivers float32 and the MFT wants integer PCM, so there is a conversion here, and
/// there is no resampler -- the endpoint's 48 kHz is what the MFT is configured for. A device at
/// another rate is rejected loudly at startup rather than silently pitch-shifted. Upgrade path: a
/// resampler chosen by sample rate, if a non-48 kHz endpoint ever has to work.
/// </summary>
internal sealed class MfAudioEncoder : IAudioEncoder
{
    private const uint SampleRate = 48000;
    private const uint Channels = 2;
    private const int SamplesPerFrame = 1024;                              // AAC-LC frame size, fixed.
    private const int FrameBytes = SamplesPerFrame * (int)Channels * 2;    // 16-bit samples.
    private const long FrameDuration100ns = SamplesPerFrame * 10_000_000L / SampleRate;

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
    private readonly double masterZeroSeconds;

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
        double masterZeroSeconds)
    {
        this.transform = transform;
        this.sourceRate = sourceRate;
        this.sourceChannels = sourceChannels;
        this.sourceIsFloat = sourceIsFloat;
        this.masterZeroSeconds = masterZeroSeconds;
    }

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
            droppedSamples += samples.Length / (sourceChannels * 4);
            return;
        }

        queuedSamples += samples.Length / (sourceChannels * 4);
        if (!queue.TryAdd((samples, qpcPosition)))
        {
            droppedSamples += samples.Length / (sourceChannels * 4);
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
            // pitch-shifted by the encoder itself, and nobody would notice until playback.
            if (format.SampleRate != SampleRate)
                throw new NotSupportedException(
                    $"endpoint is {format.SampleRate} Hz and there is no resampler; the MFT path needs {SampleRate} Hz.");

            if (format.Channels != Channels)
                throw new NotSupportedException(
                    $"endpoint has {format.Channels} channels; the MFT path needs {Channels}.");

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

        var output = FindOutput(transform) ?? throw new InvalidOperationException(
            $"no {SampleRate} Hz / {Channels} ch ADTS output type offered.");
        var input = FindInput(transform) ?? throw new InvalidOperationException(
            $"no {SampleRate} Hz / {Channels} ch 16-bit PCM input type offered.");

        transform.SetInputType(0, input, 0);
        transform.SetOutputType(0, output, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);

        var bitrate = Read(output, MediaTypeAttributeKeys.AvgBitrate) ?? 0;
        Console.WriteLine($"[mft] audio encoder {clsid}: {SampleRate} Hz, {Channels} ch, " +
                          $"{bitrate / 1000} kbps AAC-LC, payload type {PayloadTypeAdts} (ADTS)");

        var encoder = new MfAudioEncoder(
            transform,
            format.SampleRate,
            format.Channels,
            format.IsFloat32,
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

    /// <summary>Converts one WASAPI buffer to 16-bit PCM, returning only whole 1024-sample frames.</summary>
    private byte[] ToInt16(byte[] source, byte[] carry, ref int carryLength, out int frames)
    {
        // float32 is 4 bytes per sample, 16-bit integer is 2. Loopback endpoints report float32 here,
        // but an integer endpoint must not be silently read as float.
        var bytesPerSample = sourceIsFloat ? 4 : 2;
        var sampleCount = source.Length / bytesPerSample;
        var target = new byte[carryLength + sampleCount * 2];
        Buffer.BlockCopy(carry, 0, target, 0, carryLength);

        if (sourceIsFloat)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                var value = BitConverter.ToSingle(source, i * 4);
                // Clamped, then scaled, never a bare cast: (short)3.5f is undefined, and loopback data
                // does overshoot 1.0 often enough that the wrap would be audible as a click.
                var scaled = Math.Clamp(value, -1f, 1f) * (value < 0 ? 32768f : 32767f);
                var s16 = (short)Math.Round(scaled);
                target[carryLength + i * 2] = (byte)(s16 & 0xFF);
                target[carryLength + i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
            }
        }
        else
        {
            Buffer.BlockCopy(source, 0, target, carryLength, sampleCount * 2);
        }

        // The tail that does not make a whole frame stays for the next call.
        frames = target.Length / FrameBytes;
        carryLength = target.Length - (frames * FrameBytes);
        if (carryLength > 0)
            Buffer.BlockCopy(target, frames * FrameBytes, carry, 0, carryLength);

        return target;
    }

    /// <summary>Hands one 1024-sample frame to the encoder. False means the pipeline has stopped.</summary>
    private bool Feed(ReadOnlySpan<byte> pcm)
    {
        using var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var dst, out _, out _);
        Marshal.Copy(pcm.ToArray(), 0, dst, pcm.Length);
        buffer.Unlock();
        buffer.CurrentLength = pcm.Length;
        sample.AddBuffer(buffer);

        // The MFT is fed on its own timeline, unrelated to the capture clock. The capture-clock time
        // is attached to the OUTPUT frame, where the ordinal arithmetic is exact; stamping the input
        // instead would put the residual packet fraction into every timestamp.
        var ordinal = framesFed * FrameDuration100ns;
        framesFed++;
        sample.SampleTime = ordinal;
        sample.SampleDuration = FrameDuration100ns;
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
        sample.AddBuffer(MediaFactory.MFCreateMemoryBuffer(8192));
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
    /// </summary>
    private static IMFMediaType? FindOutput(IMFTransform transform)
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

                if (Read(type, MediaTypeAttributeKeys.AudioSamplesPerSecond) == SampleRate &&
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

    private static IMFMediaType? FindInput(IMFTransform transform)
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

            if (Read(type, MediaTypeAttributeKeys.AudioSamplesPerSecond) == SampleRate &&
                Read(type, MediaTypeAttributeKeys.AudioNumChannels) == Channels)
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


