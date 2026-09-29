using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Clippy;

/// <summary>
/// Spike: can the system AAC encoder produce ADTS that our own parser accepts?
///
/// This exists to answer one question before any of it is wired into the recorder: does
/// CLSID_AACMFTEncoder exist on this machine, accept 16-bit PCM, and emit ADTS rather than raw AAC?
/// The whole idea of dropping the 200 MB ffmpeg rests on the third part. Everything here runs
/// outside the capture path on purpose -- a spike that can take down a recording is not a spike.
///
/// Vortice.MediaFoundation already declares every interface and factory entry point this needs
/// (MediaFactory.MFTEnumEx, MFCreateMediaType, MFCreateSample, IMFActivate.ActivateObject), so the
/// first version's hand-written P/Invoke layer was pure duplication. What was NOT a duplication bug
/// was the category GUID written from memory -- that one cost a whole debugging session.
/// </summary>
internal static class MfAudioPrototype
{
    // MFTEnumEx, rather than a hard-coded CLSID. FFmpeg does the same thing, and the reason is the
    // lesson of the first version of this file: a CLSID written down from memory was simply wrong,
    // and it failed as CLASS_E_CLASSNOTAVAILABLE -- indistinguishable, on the console, from Windows
    // not having an AAC encoder at all. Enumerating cannot get that wrong, and it also finds
    // whatever the machine actually offers instead of insisting on one particular class.
    //
    // MFT_CATEGORY_AUDIO_ENCODER, verbatim from mfapi.h. The wrong value that shipped first was a
    // well-formed but invented GUID, which is the worst kind: MFTEnumEx accepted it and returned zero
    // encoders, so the console said "Windows has no AAC encoder" while CLSID_AACMFTEncoder was sitting
    // right there. Zero is never proof of absence here -- it is a bug report about the query.
    private static readonly Guid CategoryAudioEncoder = TransformCategoryGuids.AudioEncoder;

    // MF_MT_MAJOR_TYPE = MFMediaType_Audio. PCM is the INPUT subtype; the OUTPUT subtype is AAC, and
    // the first version of this filter asked for PCM on both sides. Nothing can match PCM -> PCM, so
    // it reported zero encoders and looked like Windows had none. The system AAC encoder is right
    // there; the filter was asking for something that cannot exist.
    private static readonly RegisterTypeInfo PcmInput = new()
    {
        GuidMajorType = MediaTypeGuids.Audio,
        GuidSubtype = AudioFormatGuids.Pcm,
    };
    private static readonly RegisterTypeInfo AacOutput = new()
    {
        GuidMajorType = MediaTypeGuids.Audio,
        GuidSubtype = AudioFormatGuids.Aac,
    };

    // SYNCMFT | SORTANDFILTER: the system AAC encoder is synchronous, and sorting puts the built-in
    // one ahead of any third-party encoder that might also match. The extra bits in the originally
    // suggested 0x51 are MFT_ENUM_FLAG_HARDWARE/FIELDOFUSE, which would have HIDDEN the software
    // encoder this test is looking for -- the flags have to be the documented SYNCMFT|SORTANDFILTER.
    private const uint MftEnumFlagSyncMft = 0x00000008;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;

    // MF_MT_AAC_PAYLOAD_TYPE = 1 selects the ADTS form (a 7-byte header on every frame). 0 is the
    // headerless form, which AdtsFrameParser cannot frame at all -- so picking the wrong index of
    // GetOutputAvailableType is the difference between "works" and "silently unframeable audio".
    private const uint AacPayloadTypeAdts = 1;

    private const uint SampleRate = 44100;
    private const uint Channels = 2;
    private const uint Bitrate = 128_000;
    private const int SamplesPerFrame = 1024;
    private const int FrameDuration100ns = (int)(SamplesPerFrame * 10_000_000L / 44100);

    // Two HRESULTs that are part of the normal conversation with a buffered MFT, not failures.
    // Both mean "keep going", so both are named here rather than being scattered as magic numbers.
    private const int NotAccepting = unchecked((int)0xC00D36B5);       // MF_E_NOTACCEPTING
    private const int TransformNeedMoreInput = unchecked((int)0xC00D6D72); // MF_E_TRANSFORM_NEED_MORE_INPUT

    public static int Run()
    {
        // Whatever happened below, leaving MF started would take a process-wide lock with it.
        MediaFactory.MFStartup(false).CheckError();
        try
        {
            return Probe();
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static int Probe()
    {
        // Two queries, because "zero encoders" has two very different causes: a wrong category GUID
        // (Windows looks in a bucket that does not exist) and a type filter nothing can satisfy.
        using (var all = MediaFactory.MFTEnumEx(CategoryAudioEncoder, MftEnumFlagSortAndFilter, null, null))
        {
            Console.WriteLine($"  encoders in the audio-encoder category, no type filter: {Count(all)}");
        }

        using var candidates = MediaFactory.MFTEnumEx(
            CategoryAudioEncoder,
            MftEnumFlagSyncMft | MftEnumFlagSortAndFilter,
            PcmInput,
            AacOutput);

        Console.WriteLine($"  encoders accepting PCM and producing AAC: {Count(candidates)}");
        if (Count(candidates) == 0)
        {
            Console.WriteLine("MFT AAC Prototype: FAILED -- no system AAC encoder is reachable " +
                              "for PCM here, so the ffmpeg dependency cannot be dropped on this machine.");
            return 1;
        }

        // Enumeration only proves a class is REGISTERED. The question this spike exists to answer is
        // whether the activated transform really emits ADTS our own parser can frame, so candidates
        // are actually run instead of assuming the first one is good.
        foreach (var activate in candidates)
        {
            // The built-in encoder has no friendly name at all, so the report would otherwise say
            // "trying " and leave the reader unable to tell what actually answered. The CLSID from
            // MFT_ENUM_TRANSFORM_CLSID is the only identifier Windows reliably provides here.
            var clsid = activate.GetGUID(TransformAttributeKeys.MftTransformClsidAttribute);
            Console.WriteLine($"  trying CLSID {clsid}" +
                              (string.IsNullOrEmpty(activate.FriendlyName) ? string.Empty : $" ({activate.FriendlyName})"));

            try
            {
                return Encode(activate, clsid);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    rejected: {ex.Message}");
            }
        }

        Console.WriteLine("MFT AAC Prototype: FAILED -- encoders were found but none could encode ADTS.");
        return 1;
    }

    private static int Count(IMFActivateCollection activates)
    {
        var n = 0;
        foreach (var _ in activates)
            n++;
        return n;
    }

    /// <summary>
    /// Feeds PCM until the encoder produces something, then checks the bytes really are ADTS.
    ///
    /// Two things here are not obvious and both cost an hour:
    /// a media type BUILT BY HAND is rejected with MF_E_ATTRIBUTENOTFOUND even when every attribute
    /// looks right, so the type has to be one the encoder itself enumerated via GetOutputAvailableType;
    /// and the output sample must arrive with a pre-allocated buffer, or ProcessOutput fails with
    /// E_INVALIDARG that has nothing to do with the audio.
    /// </summary>
    private static int Encode(IMFActivate activate, Guid clsid)
    {
        using var transform = activate.ActivateObject<IMFTransform>();

        // payload type 1 == ADTS. Index 0 of the enumeration is the headerless form, which is the
        // exact thing this whole spike exists to rule out.
        var output = FindAvailable(
            transform,
            output: true,
            m => Read(m, MediaTypeAttributeKeys.AudioNumChannels) == Channels
                 && Read(m, MediaTypeAttributeKeys.AudioSamplesPerSecond) == SampleRate
                 && Read(m, MediaTypeAttributeKeys.AvgBitrate) == Bitrate
                 && Read(m, MediaTypeAttributeKeys.AacPayloadType) == AacPayloadTypeAdts);

        var input = FindAvailable(
            transform,
            output: false,
            m => Read(m, MediaTypeAttributeKeys.AudioNumChannels) == Channels
                 && Read(m, MediaTypeAttributeKeys.AudioSamplesPerSecond) == SampleRate);

        if (output is null || input is null)
            throw new InvalidOperationException(
                $"no enumerated type for {SampleRate} Hz / {Channels} ch / {Bitrate} bps ADTS " +
                $"(output found: {output is not null}, input found: {input is not null}).");

        transform.SetInputType(0, input, 0);
        transform.SetOutputType(0, output, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);

        var pcm = new byte[SamplesPerFrame * (int)Channels * 2];
        // A quiet 440 Hz sine, not silence: an all-zero frame is legal AAC input and compresses to
        // almost nothing, which would make a frame-length check prove nothing.
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var v = (short)(8000 * Math.Sin(2 * Math.PI * 440 * (i / 2) / SampleRate));
            pcm[i] = (byte)(v & 0xFF);
            pcm[i + 1] = (byte)((v >> 8) & 0xFF);
        }
        Console.WriteLine($"    input: {pcm.Length} bytes of PCM (16-bit, {SampleRate} Hz, {Channels} ch) per frame");

        // The encoder is a look-ahead pipeline: the first two frames come back as
        // MF_E_TRANSFORM_NEED_MORE_INPUT, and its input queue holds one frame, so ProcessInput after
        // that returns MF_E_NOTACCEPTING unless the output is drained first. Draining between inputs
        // is the whole loop; a fixed "one input, one output" assumption is what fails here.
        byte[]? encoded = null;
        var time = 0L;
        for (var frame = 0; frame < 8 && encoded is null; frame++)
        {
            var produced = TryOutput(transform);
            if (produced is not null)
            {
                encoded = produced;
                break;
            }

            try
            {
                transform.ProcessInput(0, PcmSample(pcm, time), 0);
                time += FrameDuration100ns;
            }
            catch (SharpGenException ex) when (ex.HResult == NotAccepting)
            {
                // Queue full: drain and retry the same frame rather than dropping audio on the floor.
            }
        }

        if (encoded is null)
            throw new InvalidOperationException("the encoder produced no output after 8 input frames.");

        Console.WriteLine($"    output: {encoded.Length} bytes, first 7 " +
                          string.Join(" ", encoded.AsSpan(0, 7).ToArray().Select(x => x.ToString("X2"))));

        if (encoded.Length < 7 || encoded[0] != 0xFF || (encoded[1] & 0xF0) != 0xF0)
        {
            Console.WriteLine("    rejected: no ADTS sync word -- raw AAC, which AdtsFrameParser cannot frame.");
            return 1;
        }

        // The byte pattern is not the contract; the parser is. Feeding real encoder output
        // through the production framer is what actually unblocks the recorder.
        var frames = 0;
        new AdtsFrameParser().Append(encoded, 0.0, (frame, _, _) =>
        {
            if (frames == 0 && (frame[0] != 0xFF || (frame[1] & 0xF0) != 0xF0))
                throw new InvalidOperationException("AdtsFrameParser returned a frame without a sync word.");
            frames++;
        });

        // protection_absent is set in the ADTS header when there is NO CRC. Reading it as "CRC
        // present" is backwards, and the bug shows up as a 7-byte header claiming 9.
        var crc = (encoded[1] & 0x01) != 0 ? "absent (7-byte header)" : "present (9-byte header)";
        Console.WriteLine($"    ADTS header: AAC profile {(encoded[2] >> 6) & 0x03} (1 = LC), " +
                          $"rate index 0x{(encoded[2] >> 2) & 0x0F} (4 = 44100), " +
                          $"channels {((encoded[2] & 0x01) << 2) | ((encoded[3] >> 6) & 0x03)}, " +
                          $"frame length {FrameLength(encoded)} bytes, CRC {crc}");

        if (frames != 1)
        {
            Console.WriteLine($"    rejected: one ADTS frame should parse as exactly one frame, got {frames}.");
            return 1;
        }

        Console.WriteLine($"MFT AAC Prototype: SUCCESS -- {clsid} emitted one {encoded.Length}-byte ADTS frame " +
                          "that AdtsFrameParser framed correctly. The ffmpeg audio dependency can go.");
        return 0;
    }

    /// <summary>
    /// One frame out, or null while the encoder still wants input. MF_E_TRANSFORM_NEED_MORE_INPUT is
    /// the expected answer for the first couple of calls, not an error, so it becomes null.
    /// </summary>
    private static byte[]? TryOutput(IMFTransform transform)
    {
        using var sample = MediaFactory.MFCreateSample();
        // The pre-allocated buffer is not optional. An empty sample makes this MFT fail
        // ProcessOutput with E_INVALIDARG, which reads like a codec problem and is not one.
        sample.AddBuffer(MediaFactory.MFCreateMemoryBuffer(8192));
        var data = new OutputDataBuffer { StreamID = 0, Sample = sample };

        try
        {
            transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _).CheckError();
        }
        catch (SharpGenException ex) when (ex.HResult == TransformNeedMoreInput)
        {
            return null;
        }

        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var src, out _, out var length);
        var bytes = new byte[length];
        Marshal.Copy(src, bytes, 0, length);
        contiguous.Unlock();
        return bytes;
    }

    private static IMFSample PcmSample(byte[] pcm, long time)
    {
        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(pcm.Length);
        buffer.Lock(out var dst, out _, out _);
        Marshal.Copy(pcm, 0, dst, pcm.Length);
        buffer.Unlock();
        buffer.CurrentLength = pcm.Length;
        sample.AddBuffer(buffer);
        sample.SampleTime = time;
        sample.SampleDuration = FrameDuration100ns;
        return sample;
    }

    /// <summary>
    /// Picks a media type the transform itself enumerated. Hand-built types look equivalent and are
    /// still refused with MF_E_ATTRIBUTENOTFOUND, because the encoder also wants attributes this
    /// spike never thought to set. Asking it what it accepts cannot get that wrong.
    /// </summary>
    private static IMFMediaType? FindAvailable(IMFTransform transform, bool output, Func<IMFMediaType, bool> accept)
    {
        for (var i = 0; i < 64; i++)
        {
            IMFMediaType type;
            try
            {
                type = output
                    ? transform.GetOutputAvailableType(0, i)
                    : transform.GetInputAvailableType(0, i);
            }
            catch (SharpGenException)
            {
                return null; // enumeration ended
            }

            if (accept(type))
                return type;
        }
        return null;
    }

    // GetUInt32 throws when the attribute is absent, and absence is a legitimate answer here: the
    // input types do not carry a bitrate, for instance. Reading it as a question, not a claim.
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

    // 13-bit frame length: 3 bits in byte 3, 8 in byte 4, 2 in byte 5 -- the same decode
    // AdtsFrameParser uses, repeated only so the report can print the number the parser derived.
    private static int FrameLength(byte[] a) => ((a[3] & 0x03) << 11) | (a[4] << 3) | (a[5] >> 5);
}
