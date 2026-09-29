using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
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
    private const uint FpsDenominator = 1;

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
                //
                // Hardware first: the async encoders are the only ones actually alive here, and the
                // unlock + D3D manager dance below is the whole point of this spike. The software one
                // is still driven afterwards, to keep a record of why it cannot work.
                if (IsAsync(activate))
                {
                    Console.WriteLine();
                    Console.WriteLine($"--- unlocking async: {Describe(activate)}");
                    try
                    {
                        var result = DriveAsyncHardware(activate);

                        // 2 means "worked perfectly, but it is a different codec" -- a skip, not a
                        // failure. Counting it as driven would report the H.264 finding as a rejection.
                        if (result == 0)
                            return 0;
                        if (result != 2)
                            driven++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    rejected: {ex.Message}");
                        driven++;
                    }
                    continue;
                }

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
    /// <summary>
    /// Step 8.3A: bring an async hardware MFT to the point where it will talk about types at all.
    /// An async MFT refuses every call with MF_E_TRANSFORM_ASYNC_LOCKED until it is unlocked, and
    /// even unlocked it will not accept a D3D manager it was not told about -- without one it has
    /// no device to allocate its internal surfaces on, so type negotiation simply stalls.
    /// </summary>
    private static int DriveAsyncHardware(IMFActivate activate)
    {
        var step = "activate";
        try
        {
            using var transform = activate.ActivateObject<IMFTransform>();

            // 1. Unlock the async state machine.
            //
            // The instruction for this step gives the GUID as 2477308D-C502-4F75-8F67-1F0167C503E9.
            // That is wrong. Vortice's TransformAttributeKeys.TransformAsyncUnlock resolves to
            // E5666D6B-3422-4EB6-A421-DA7DB1F8E207, which is the real MF_TRANSFORM_ASYNC_UNLOCK;
            // the quoted value belongs to no attribute MF reads here, and setting it would have been
            // a silent no-op that still ends in MF_E_TRANSFORM_ASYNC_LOCKED. Using the named constant
            // rather than a literal also means a wrong GUID cannot be introduced later.
            step = "set MF_TRANSFORM_ASYNC_UNLOCK";
            transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
            Console.WriteLine($"    MF_TRANSFORM_ASYNC_UNLOCK = {TransformAttributeKeys.TransformAsyncUnlock}");

            // 2. A D3D11 device. Hardware video encoders never touch system memory: they read
            //    GPU surfaces and write GPU surfaces, so the device has to exist before the manager.
            step = "create D3D11 device";
            D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out var device,
                out _,
                out var context).CheckError();
            using (device)
            using (context)
            {
                Console.WriteLine("    D3D11 device created (BGRA support)");

                // 3. The manager wraps the device and is what the MFT is told to allocate on.
                //
                //    The instruction spells this MFCreateDXGIDeviceManager(out uint, out IMFDXGIDeviceManager).
                //    Vortice exposes it as a parameterless factory returning the manager, with the
                //    token exposed as a ResetToken property and the device handed to ResetDevice.
                //    Same COM API, different binding -- no P/Invoke shim needed for this.
                step = "create DXGI device manager";
                using var manager = MediaFactory.MFCreateDXGIDeviceManager();
                manager.ResetDevice(device).CheckError();
                Console.WriteLine($"    IMFDXGIDeviceManager created, reset token = {manager.ResetToken}");

                // 4. Hand the manager to the MFT. Without this the MFT has no device and every
                //    later call fails or returns nothing -- the single step that makes the rest work.
                step = "MFT_MESSAGE_SET_D3D_MANAGER";
                transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)manager.NativePointer);
                Console.WriteLine("    MFT_MESSAGE_SET_D3D_MANAGER accepted");

                // 5. The output type is now negotiable. This is the first moment the MFT could have
                //    been asked for input types, so the earlier TYPE_NOT_SET wall should be gone.
                step = "SetOutputType (H.264 1920x1080@30)";
                var output = DescribeWantedOutput();
                try
                {
                    transform.SetOutputType(0, output, 0);
                }
                catch (SharpGenException ex) when (ex.HResult == InvalidType)
                {
                    // The HEVC encoder is in the same enumeration and answers this perfectly
                    // correctly -- it is not an H.264 encoder. Reporting it as a rejected candidate
                    // would bury the real finding under a failure that is not one.
                    Console.WriteLine($"    (not an H.264 encoder -- it refused the H.264 output type, as expected)");
                    return 2;
                }
                Console.WriteLine("    SetOutputType accepted H.264 1920x1080@30 progressive");

                // 6. The actual question of the step: what does this encoder want to be fed?
                step = "enumerate input types";
                ReportInputs(transform);

                // Type negotiation is all this step promised, so a clean exit here is success --
                // but only once a 1080p input type actually exists to accept. Reporting success on a
                // list of unusable types would be the kind of green that means nothing.
                step = "pick a 1920x1080 input type";
                var input = Pick(transform, Width, Height);
                if (input is null)
                    throw new InvalidOperationException("negotiation succeeded but no 1920x1080 input type was offered.");
                Console.WriteLine($"    => async MFT unlocked; it wants {FormatName(Subtype(input))} " +
                                  $"{Width}x{Height}. Input is a D3D11 texture, not CPU memory.");
                Console.WriteLine("Step 8.3A result: the async H.264 MFT negotiates types once unlocked and " +
                                  "given a D3D manager. It has NOT yet emitted a byte -- feeding it a " +
                                  "texture and reading Annex B back is the next step, not this one.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"at {step}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds H.264 1920x1080@30 progressive, 6 Mbit/s by hand.
    ///
    /// The software MFT offered an output type that declared no frame size, and SetOutputType then
    /// failed with MF_E_ATTRIBUTENOTFOUND -- it wanted attributes the published type did not carry.
    /// The hardware path is the reverse case: the attributes have to be supplied, because there is
    /// no published type to copy them from. Hence every field is set explicitly.
    /// </summary>
    private static IMFMediaType DescribeWantedOutput()
    {
        // Not wrapped in `using`: the caller hands this straight to SetOutputType, and disposing it
        // here would drop the COM refcount that call depends on.
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, H264);

        // Frame size and frame rate are UINT64 attributes carrying a pair, high half first.
        // Writing them as a plain int would be silently truncated rather than rejected.
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)Height << 32) | Width);
        type.Set(MediaTypeAttributeKeys.FrameRate, ((ulong)FpsNumerator << 32) | FpsDenominator);

        type.Set(MediaTypeAttributeKeys.AvgBitrate, 6_000_000u);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // progressive
        type.Set(MediaTypeAttributeKeys.Compressed, true);
        return type;
    }

    /// <summary>
    /// Names a video subtype. Media Foundation prints raw GUIDs, but "3231564e-0000-0010-..." is a
    /// question the reader has to answer by hand, and the entire point of listing the input types is to
    /// read them at a glance.
    ///
    /// Two families share the first DWORD and nothing marks which is which. A FourCC subtype stores
    /// its four characters low byte first, so NV12 is 0x3231564E -- numerically indistinguishable from
    /// a DXGI format number, which is why the test is whether the bytes are printable characters and
    /// not whether the value is large. Get it wrong and every format looks exotic, or every format
    /// looks like a FourCC.
    /// </summary>
    private static string FormatName(Guid subtype)
    {
        var head = subtype.ToString("N")[..8];   // the first DWORD, as MF wrote it
        var value = uint.Parse(head, System.Globalization.NumberStyles.HexNumber);

        if (value == 0)
            return "none";

        var b = new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
        var printable = b.All(c => c is >= 0x20 and < 0x7f);
        if (!printable)
            return $"numeric 0x{value:X} (a DXGI format value, not a FourCC)";

        return $"{System.Text.Encoding.ASCII.GetString(b)} (0x{value:X8})";
    }

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
                // Walking off the end of the list is the normal way to end, not a failure.
                Console.WriteLine($"      in[{i}] (end of list: {ex.HResult:x8})");
                return;
            }

            using (type)
            {
                var size = FrameSize(type);
                Console.WriteLine($"      in[{i}] {FormatName(Subtype(type))} " +
                                  $"{size.Item1}x{size.Item2}@{FrameRate(type)}");
            }
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

    /// <summary>Frame rate of a type, or "?" when it does not declare one.</summary>
    private static string FrameRate(IMFMediaType type)
    {
        try
        {
            var v = type.GetUInt64(MediaTypeAttributeKeys.FrameRate);
            var num = v >> 32;
            var den = v & 0xffffffff;
            return den == 0 ? "?" : $"{num / den:0.##}";
        }
        catch
        {
            return "?";
        }
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

    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid Bgra = new("41485242-0000-0010-8000-00aa00389b71"); // 'B','R','A','8'
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"); // 'H','2','6','4'

    // Normal answers from a buffered MFT, not failures. const so they can be used in a pattern.
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);  // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int NotAccepting = unchecked((int)0xC00D36B5);    // MF_E_NOTACCEPTING
    private const int InvalidType = unchecked((int)0xC00D36BD);      // MF_E_INVALIDTYPE

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
