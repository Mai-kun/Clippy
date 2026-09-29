using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Clippy;

/// <summary>
/// Spike: what can Media Foundation actually do for video on this machine, and can its H.264 output
/// be fed through our own Annex B parser?
///
/// The audio prototype found a working encoder immediately. Video is the harder question and the
/// answer is not known until it is run, so this exists to answer it BEFORE the capture path is
/// rewired. The decision it feeds: keep ffmpeg for video, or replace it.
///
/// Three things about video MFTs are not obvious, and all three are why enumeration alone proves
/// nothing here:
///
/// 1. There are two families and only one of them is real. SYNCMFT|SORTANDFILTER (0x48) returns the
///    software encoders plus Microsoft's "H264 Encoder MFT"; HARDWARE|SORTANDFILTER (0x44) returns
///    only the NVIDIA ones. Nothing from Intel on this box.
/// 2. Registered is not the same as usable. The Microsoft H.264 encoder advertises an H.264 OUTPUT
///    type and then answers MF_E_TRANSFORM_TYPE_NOT_SET when asked for its input types, with no
///    frame size on the type it does offer. The class is present; the pipeline is not.
/// 3. Hardware encoders are asynchronous and refuse GetInputStreamInfo with
///    MF_E_TRANSFORM_ASYNC_LOCKED until a DXGI device manager is set. That means the D3D11 device the
///    capture already creates could be handed straight to the encoder -- zero-copy -- but it also
///    means the input would be a texture, not CPU memory, which decides the whole integration shape.
///
/// Everything here runs outside the capture path. A spike that can take down a recording is not a spike.
/// </summary>
internal static class MfVideoPrototype
{
    private const uint Width = 1920;
    private const uint Height = 1080;
    private const uint FpsNumerator = 30;

    // MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER: the synchronous, software encoders.
    private const uint SyncAndSorted = 0x00000008 | 0x00000040;

    // MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER: the GPU encoders. Deliberately not 0x51:
    // the extra bit that was in the original suggestion restricts the search to encoders that
    // declare a hardware vendor id, and asking for hardware AND a vendor id at once is how a
    // search silently returns nothing.
    private const uint HardwareAndSorted = 0x00000004 | 0x00000040;

    public static int Run()
    {
        // MFStartup's only argument is "use the light-weight version of the platform", not a version
        // number: MF_VERSION is chosen by the library.
        MediaFactory.MFStartup(useLightVersion: false).CheckError();
        try
        {
            return Probe();
        }
        finally
        {
            // Whatever happened above, leaving MF started would take a process-wide lock with it.
            MediaFactory.MFShutdown();
        }
    }

    private static int Probe()
    {
        Console.WriteLine("MFT video encoders on this machine");
        Console.WriteLine("=================================");
        var driven = 0;
        var sawH264 = false;

        // The collection is enumerated and the candidates driven INSIDE the using, never collected
        // into a list first. Disposing an IMFActivateCollection releases the IMFActivate objects it
        // produced, so a list kept past the block would hold dead COM pointers and every Drive would
        // fail on an exception that looks like "the encoder is broken" rather than "we freed it".
        foreach (var flags in new[] { SyncAndSorted, HardwareAndSorted })
        {
            var kind = flags == SyncAndSorted
                ? "software (0x48 SYNCMFT|SORTANDFILTER)"
                : "hardware (0x44 HARDWARE|SORTANDFILTER)";

            using var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, flags, null, null);
            var n = 0;
            foreach (var activate in found)
            {
                n++;
                Console.WriteLine($"  [{kind}] {Describe(activate)}");

                // The useful question is narrower than "does an encoder exist": can one take this
                // machine's input format and emit Annex B. Only H.264 claims are worth driving.
                if (!ClaimsH264(activate))
                    continue;

                sawH264 = true;
                Console.WriteLine();
                Console.WriteLine($"--- driving {Describe(activate)}");
                try
                {
                    var result = Drive(activate);
                    if (result == 0)
                        return 0;
                    driven++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    rejected: {ex.Message}");
                    driven++;
                }
            }
            Console.WriteLine($"  [{kind}] {n} total");
        }

        Console.WriteLine();
        if (driven == 0 && !sawH264)
            Console.WriteLine("MFT Video Prototype: FAILED -- no H.264 encoder is registered at all.");
        else
            Console.WriteLine("MFT Video Prototype: FAILED -- " +
                              $"{driven} H.264 encoder(s) were found and none could be driven. " +
                              "The audio path can drop ffmpeg; the video path cannot yet.");
        return 1;
    }

    /// <summary>
    /// Configures 1920x1080@30 and pushes one synthetic frame through, then checks the bytes are
    /// Annex B and that the production parser frames them.
    /// </summary>
    private static int Drive(IMFActivate activate)
    {
        using var transform = activate.ActivateObject<IMFTransform>();

        // The order is forced by the MFT, not chosen: this encoder answers
        // MF_E_TRANSFORM_TYPE_NOT_SET when asked for input types before an output type exists.
        // Discovering that by trial makes "no input types supported" look like the finding.
        var step = "read output type";
        try
        {
            var output = First(transform, output: true)
                ?? throw new InvalidOperationException("no H.264 output type is offered.");
            Console.WriteLine($"    output type offered by the MFT: {Describe(output)}");

            step = "SetOutputType";
            transform.SetOutputType(0, output, 0);
            Console.WriteLine("    SetOutputType accepted it");

            step = "survey input types";
            var input = Pick(transform, Width, Height);
            if (input is null)
            {
                Console.WriteLine("    no usable 1920x1080 input type. Full walk:");
                ReportInputs(transform);
                throw new InvalidOperationException("no usable 1920x1080 input type.");
            }

            Console.WriteLine($"    input type chosen: {Describe(input)}");
            step = "SetInputType";
            transform.SetInputType(0, input, 0);

            step = "GetInputStreamInfo";
            var info = transform.GetInputStreamInfo(0);
            var size = Math.Max(info.Size, (int)(Width * Height * 3 / 2));
            Console.WriteLine($"    input stream: flags={info.Flags} size={info.Size} (using {size} bytes)");

            // A moving mid-grey pattern, not black. Black compresses to almost nothing and would let
            // a broken encoder look like a working one.
            step = "build sample";
            var frame = new byte[size];
            for (var i = 0; i + 1 < frame.Length; i += 2)
            {
                var v = (byte)(((i / 8) + (i / 800)) % 2 == 0 ? 128 : 96);
                frame[i] = v;
                frame[i + 1] = v;
            }

            using var sample = MediaFactory.MFCreateSample();
            var buffer = MediaFactory.MFCreateMemoryBuffer(size);
            buffer.Lock(out var dst, out _, out _);
            Marshal.Copy(frame, 0, dst, size);
            buffer.Unlock();
            buffer.CurrentLength = size;
            sample.AddBuffer(buffer);
            sample.SampleTime = 0;
            sample.SampleDuration = 10_000_000L / FpsNumerator;

            step = "ProcessInput";
            transform.ProcessInput(0, sample, 0);
            Console.WriteLine("    ProcessInput accepted one frame");

            step = "ProcessOutput";
            var encoded = Read(transform);
            if (encoded.Length == 0)
                throw new InvalidOperationException("ProcessOutput produced no bytes (look-ahead or empty queue).");
            Console.WriteLine($"    ProcessOutput: {encoded.Length} bytes, first 8 = {Hex(encoded, 8)}");

            // Checking only the three-byte form would pass on a stream that begins with a prefix and
            // no real delimiter, so both Annex B forms are accepted explicitly.
            step = "start code";
            if (!StartsWithStartCode(encoded))
            {
                Console.WriteLine("    rejected: output does not begin with an Annex B start code.");
                return 1;
            }

            // A byte pattern is not the contract; the parser is.
            step = "H264AnnexBParser";
            var nals = new List<(int Length, bool Keyframe)>();
            void Collect(byte[] data, int length, bool keyframe, double _) => nals.Add((length, keyframe));

            var parser = new H264AnnexBParser();
            parser.Append(encoded, 0.0, Collect);
            parser.Flush(0.0, Collect);

            Console.WriteLine($"    H264AnnexBParser extracted {nals.Count} NAL unit(s): " +
                              string.Join(", ", nals.Select((n, i) => $"#{i} len={n.Length} key={n.Keyframe}")));
            if (nals.Count == 0)
            {
                Console.WriteLine("    rejected: the production parser found no NAL units in the output.");
                return 1;
            }

            Console.WriteLine("MFT Video Prototype: SUCCESS -- the system H.264 encoder emitted Annex B that " +
                              "H264AnnexBParser framed without help. The video path can drop ffmpeg too.");
            return 0;
        }
        catch (Exception ex)
        {
            // The step matters. A bare HRESULT here is unreadable: "this encoder is broken" and "this
            // encoder is fine but the type you handed it was incomplete" produce the same message.
            throw new InvalidOperationException($"at {step}: {ex.Message}", ex);
        }
    }

    /// <summary>Reports every input type, including the HRESULT that stops the walk.</summary>
    private static void ReportInputs(IMFTransform transform)
    {
        for (var i = 0; i < 32; i++)
        {
            IMFMediaType type;
            try
            {
                type = transform.GetInputAvailableType(0, i);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      in[{i}] -> {ex.Message}");
                return;
            }

            Console.WriteLine($"      in[{i}] {Describe(type)}");
        }
    }

    /// <summary>
    /// The first input type at the requested size, preferring NV12 where it is offered. NV12 matters:
    /// a BGRA capture has to be converted to whatever this returns, and converting BGRA is both slower
    /// and higher-bandwidth than converting to NV12. If BGRA were accepted directly that would be a
    /// different and much cheaper integration, which is why it is looked for first by name.
    /// </summary>
    private static IMFMediaType? Pick(IMFTransform transform, uint width, uint height)
    {
        IMFMediaType? atSize = null;
        for (var i = 0; i < 32; i++)
        {
            IMFMediaType type;
            try
            {
                type = transform.GetInputAvailableType(0, i);
            }
            catch
            {
                break;
            }

            if (FrameSize(type) != (width, height))
                continue;

            if (Subtype(type) == Nv12)
                return type;
            if (Subtype(type) == Bgra)
                Console.WriteLine("    note: this encoder takes BGRA/ARGB32 directly, no conversion needed.");

            atSize ??= type;
        }
        return atSize;
    }

    private static IMFMediaType? First(IMFTransform transform, bool output)
    {
        try
        {
            return output ? transform.GetOutputAvailableType(0, 0) : transform.GetInputAvailableType(0, 0);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One ProcessOutput, with the two answers that are normal rather than failures.</summary>
    private static byte[] Read(IMFTransform transform)
    {
        using var sample = MediaFactory.MFCreateSample();
        // Pre-allocated, as with the audio encoder: an empty IMFSample makes ProcessOutput fail with
        // E_INVALIDARG that has nothing to do with the codec.
        sample.AddBuffer(MediaFactory.MFCreateMemoryBuffer(4 * 1024 * 1024));
        var data = new OutputDataBuffer { StreamID = 0, Sample = sample };

        try
        {
            transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _).CheckError();
        }
        catch (SharpGenException ex) when (ex.HResult is NeedMoreInput or NotAccepting)
        {
            // Both mean "keep going", not "broken": look-ahead and a full input queue respectively.
            return [];
        }

        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var src, out _, out var length);
        var bytes = new byte[length];
        Marshal.Copy(src, bytes, 0, length);
        contiguous.Unlock();
        return bytes;
    }

    private static readonly Guid Nv12 = new("30313256-0000-0010-8000-00aa00389b71");
    private static readonly Guid Bgra = new("30315841-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");

    // Normal answers from a buffered MFT, not failures. const so they can be used in a pattern.
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);  // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int NotAccepting = unchecked((int)0xC00D36B5);    // MF_E_NOTACCEPTING

    private static bool ClaimsH264(IMFActivate activate)
    {
        try
        {
            using var transform = activate.ActivateObject<IMFTransform>();
            return Subtype(transform.GetOutputAvailableType(0, 0)) == H264;
        }
        catch
        {
            // Expected for the hardware encoders: an async MFT will not answer type questions before a
            // DXGI device manager is set, so "not H.264" and "not answerable yet" are different
            // findings -- and the second is the interesting one. Say so rather than dropping the
            // candidate silently, which would read as "NVIDIA cannot do H.264".
            if (IsAsync(activate))
            {
                Console.WriteLine("      (not driven: async MFT -- it refuses type questions until an " +
                                  "IMFDXGIDeviceManager is set, so its input is a GPU texture, not CPU memory)");
            }
            return false;
        }
    }

    private static bool IsAsync(IMFActivate activate)
    {
        try { return activate.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; }
        catch { return false; }
    }

    private static Guid Subtype(IMFMediaType type)
    {
        try { return type.GetGUID(MediaTypeAttributeKeys.Subtype); }
        catch { return Guid.Empty; }
    }

    private static (uint Width, uint Height) FrameSize(IMFMediaType type)
    {
        try
        {
            // MF_MT_FRAME_SIZE is a UINT64 packing width in the low dword and height in the high one.
            var packed = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            return ((uint)(packed & 0xFFFFFFFF), (uint)(packed >> 32));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string Describe(IMFMediaType type)
    {
        var sub = Subtype(type);
        var name = sub == Nv12 ? "NV12"
            : sub == Bgra ? "BGRA/ARGB32"
            : sub == H264 ? "H264"
            : sub.ToString();

        var (w, h) = FrameSize(type);
        // A type with no frame size is not a detail. It is the single clearest sign that a class is
        // registered without being able to do the job, so it is stated rather than printed as 0x0.
        return $"{name} {(w == 0 && h == 0 ? "(no frame size declared)" : w.ToString() + "x" + h)}";
    }

    private static string Describe(IMFActivate activate)
    {
        var clsid = "?";
        var name = "(no friendly name)";
        var isAsync = "0";
        try { clsid = activate.GetGUID(TransformAttributeKeys.MftTransformClsidAttribute).ToString(); } catch { }
        try
        {
            var s = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute);
            if (!string.IsNullOrEmpty(s)) name = s;
        }
        catch { }
        try { isAsync = activate.GetUInt32(TransformAttributeKeys.TransformAsync).ToString(); } catch { }
        return $"{clsid}  {name}  (async={isAsync})";
    }

    private static string Hex(byte[] data, int count)
    {
        var take = Math.Min(count, data.Length);
        return string.Join(" ", data.AsSpan(0, take).ToArray().Select(b => b.ToString("X2")));
    }

    /// <summary>True for both the three- and four-byte Annex B start codes.</summary>
    private static bool StartsWithStartCode(byte[] data) =>
        (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1) ||
        (data.Length >= 3 && data[0] == 0 && data[1] == 0 && data[2] == 1);
}
