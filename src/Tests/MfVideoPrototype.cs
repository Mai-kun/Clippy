using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

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

    // An encoder with look-ahead holds several frames before emitting the first one. Generous, because
    // the cost of over-shooting is a few wasted milliseconds and the cost of under-shooting is
    // concluding the encoder is broken.
    private const int MaxFrames = 60;

    // One second at 30 fps. Enough to show a stream sustains and that the drain returns the tail,
    // short enough that a failure shows up in seconds rather than a minute.
    private const int Frames = 30;

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
            return Probe(withLiveCapture: false);
        }
        finally
        {
            // Whatever happened above, leaving MF started would take a process-wide lock with it.
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>
    /// Run the spike's own 30-frame loop with a real Windows.Graphics.Capture session already running.
    ///
    /// The spike encodes 30 frames in an empty process and the production encoder is refused in one
    /// with a capture running, and everything inside the encoder has been compared and found identical.
    /// So the only honest way to tell "the encoder is broken" from "capture monopolises the DXVA path on
    /// this driver" is to run the same loop here with the session up and see which happens.
    /// </summary>
    public static int RunWithLiveCapture() => Probe(withLiveCapture: true);

    private sealed class Disposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private static IDisposable? StartLiveCaptureSession()
    {
        try
        {
            D3D11.D3D11CreateDevice(
                IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out var d3dDevice, out _, out _).CheckError();
            var direct3DDevice = CaptureInterop.CreateDirect3DDevice(d3dDevice);
            var item = CaptureInterop.CreateItemForPrimaryMonitor();
            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                direct3DDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            var session = pool.CreateCaptureSession(item);
            session.IsBorderRequired = false;

            // Drain the pool while it runs, so the driver is doing the work it does in the recorder
            // rather than merely holding a session open.
            var stop = new CancellationTokenSource();
            var pump = new Thread(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var frame = pool.TryGetNextFrame();
                    if (frame is null)
                        Thread.Sleep(4);
                }
            })
            { IsBackground = true, Name = "spike wgc pump" };
            pump.Start();
            session.StartCapture();
            Console.WriteLine("A live Windows.Graphics.Capture session is running on the primary monitor.");
            return new Disposable(() =>
            {
                stop.Cancel();
                // GraphicsCaptureSession has no Stop/Close in this projection; dropping the
                // pool ends the session, which is all a diagnostic teardown needs.
                pool.Dispose();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not start a capture session: {ex.Message}");
            Console.WriteLine("This run proves nothing about a live session; it is the empty-process case.");
            return null;
        }
    }
    private static int Probe(bool withLiveCapture)
    {
        using var live = withLiveCapture ? StartLiveCaptureSession() : null;
        if (live is not null)
            Thread.Sleep(750);   // let the session reach a steady state before measuring
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
            // The software path always wants a caller-supplied sample; only the hardware encoders set
            // PROVIDES_SAMPLES, and this branch never sees one of those.
            var encoded = Read(transform, providesSamples: false);
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

                // The order below is the one that works, and every part of it is load-bearing: the
                // input type first, then the D3D manager again, then the stream is declared to
                // exist. Declaring the stream before the input type is accepted leaves the MFT
                // answering MF_E_NOTACCEPTING to every frame that follows, and nothing anywhere
                // says the order was wrong.
                transform.SetInputType(0, input, 0);
                transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)manager.NativePointer);
                transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
                Console.WriteLine($"    SetInputType {FormatName(Subtype(input))}; D3D manager re-sent; " +
                                  "BEGIN_STREAMING / START_OF_STREAM sent");

                step = "encode a clip";
                var stream = transform.GetOutputStreamInfo(0);
                var provides = (stream.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
                Console.WriteLine($"    output stream: flags={stream.Flags} PROVIDES_SAMPLES={provides}");
                return EncodeSeries(transform, device, input, provides);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"at {step}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Step 8.3B: push one real NV12 texture through the hardware encoder and get bytes back.
    /// Everything here differs in kind from the type negotiation above -- that proved the MFT would
    /// talk, this proves it produces something our own writer can use.
    /// </summary>
    private static int EncodeOneFrame(IMFTransform transform, ID3D11Device device, IMFMediaType input, IMFDXGIDeviceManager manager)
    {
        var step = "SetInputType";
        try
        {
            transform.SetInputType(0, input, 0);
            Console.WriteLine($"    SetInputType accepted {FormatName(Subtype(input))} {Width}x{Height}");

            // The D3D manager is re-sent here, after the types are negotiated. Sent only once, before
            // any type exists, an async MFT binds its device while it still has nothing to bind it to
            // and from then on answers every ProcessOutput with E_UNEXPECTED. Neither send is reported
            // as an error and nothing else in the sequence looks wrong, which is exactly why this
            // ordering was the thing worth trying once the HRESULTs were finally being logged.
            step = "MFT_MESSAGE_SET_D3D_MANAGER (after types)";
            transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)manager.NativePointer);
            Console.WriteLine("    MFT_MESSAGE_SET_D3D_MANAGER re-sent after type negotiation");

            // The MFT has to be told the stream exists. Without these it stays in TYPE_NOT_SET and
            // ProcessInput is answered with MF_E_NOTACCEPTING, which reads exactly like a full queue.
            step = "start streaming";
            transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            Console.WriteLine("    MFT_MESSAGE_NOTIFY_BEGIN_STREAMING / START_OF_STREAM sent");

            // Whether the caller supplies the output sample or the MFT does is not a detail to guess
            // at -- it is a declared property of the output stream, and getting it wrong returns S_OK
            // with an empty sample rather than an error.
            step = "GetOutputStreamInfo";
            var outputInfo = transform.GetOutputStreamInfo(0);
            // OutputStreamInfo.Flags comes back as a raw int, so the comparison is against the enum's
            // value rather than the enum itself.
            var providesSamples =
                (outputInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
            Console.WriteLine($"    output stream: flags={outputInfo.Flags} " +
                              $"PROVIDES_SAMPLES={providesSamples} -> " +
                              (providesSamples
                                  ? "the MFT allocates the sample; passing one would get an empty one back"
                                  : "we supply the sample"));

            step = "allocate NV12 texture";
            using var texture = MakeNv12Texture(device);
            Console.WriteLine($"    NV12 {Width}x{Height} texture allocated and painted");

            step = "wrap texture in a surface buffer";
            using var surfaceBuffer = MediaFactory.MFCreateDXGISurfaceBuffer(
                typeof(ID3D11Texture2D).GUID, texture, 0, false);
            Console.WriteLine("    surface buffer wrapped");

            // An unlocked async MFT still talks events, and ignoring them does not fail loudly -- the
            // MFT simply stops pulling input and ProcessOutput returns nothing forever, which is easy
            // to misread as "this encoder produces no output". So the queue is drained and logged.
            var events = transform.QueryInterface<IMFMediaEventGenerator>();

            // The MFT drives by posting events, and the only way to see an event is to take it out of
            // the queue -- so this is a state machine over what the MFT asked for, not a guess at how
            // many frames to push. Guessing is what stalled it: an MFT told "need more input" and then
            // never asked again just sits there, apparently producing nothing forever.
            step = "feed / drain loop";
            byte[]? encoded = null;
            var fed = 0;
            var diags = new List<string>();

            while (encoded is null)
            {
                var requests = PumpEvents(events);

                if (requests.HaveOutput)
                {
                    byte[] got;
                    try
                    {
                        got = Read(transform, providesSamples);
                    }
                    catch (SharpGenException ex)
                    {
                        // Which call failed matters: the same HRESULT out of ProcessInput and out of
                        // ProcessOutput would point at completely different parts of the contract.
                        throw new InvalidOperationException($"ProcessOutput failed: 0x{ex.HResult:x8}", ex);
                    }
                    if (got.Length > 0)
                    {
                        encoded = got;
                        break;
                    }
                    diags.Add("HaveOutput but 0 bytes");
                }

                if (requests.NeedInput)
                {
                    if (fed >= MaxFrames)
                        break;

                    // A fresh IMFSample per frame: handing the same one back while the encoder still
                    // holds a reference to it is a use-after-free waiting to happen on this path.
                    using var frameSample = MediaFactory.MFCreateSample();
                    frameSample.AddBuffer(surfaceBuffer);
                    frameSample.SampleTime = 10_000_000L / 30 * fed;
                    frameSample.SampleDuration = 10_000_000L / 30;
                    try
                    {
                        transform.ProcessInput(0, frameSample, 0);
                        fed++;
                    }
                    catch (SharpGenException ex) when (ex.HResult == NotAccepting)
                    {
                        diags.Add("input queue full");
                    }
                    catch (SharpGenException ex)
                    {
                        throw new InvalidOperationException($"ProcessInput failed: 0x{ex.HResult:x8}", ex);
                    }

                    if (fed <= 3 || fed == MaxFrames)
                        Console.WriteLine($"    fed {fed} frame(s), " +
                                          $"needInput={requests.NeedInput} haveOutput={requests.HaveOutput}" +
                                          (diags.Count > 0 ? $", last: {diags[^1]}" : ""));
                    continue;
                }

                if (fed == 0)
                {
                    // Nothing has been posted yet, so nothing has been fed. Open the valve by hand.
                    diags.Add("no events posted yet");
                    using var first = MediaFactory.MFCreateSample();
                    first.AddBuffer(surfaceBuffer);
                    first.SampleTime = 0;
                    first.SampleDuration = 10_000_000L / 30;
                    transform.ProcessInput(0, first, 0);
                    fed = 1;
                    Console.WriteLine("    fed 1 frame(s) (the MFT had posted nothing yet)");
                    continue;
                }

                // Nothing asked for, nothing produced: the encoder is busy. Let it finish.
                if (fed >= MaxFrames && !requests.HaveOutput)
                    break;
                Thread.Sleep(2);
            }
            Console.WriteLine($"    {fed} frame(s) fed before the first output sample");

            if (encoded is null || encoded.Length == 0)
            {
                throw new InvalidOperationException(
                    "the MFT took 60 frames, posted METransformHaveOutput for each, and ProcessOutput " +
                    "answered S_OK with an empty sample every time. So the negotiation, the device manager " +
                    "and the input path are all proven; what is unproven is where the bitstream goes. " +
                    "Next thing to try is the output buffer contract: a D3D-aware MFT may require the " +
                    "output sample to be a DXGI surface buffer, or may be returning its data through the " +
                    "separate IMFMediaBuffer out-parameter of ProcessOutput that nothing here is reading.");
            }

            Console.WriteLine($"    got {encoded.Length} bytes, first 8 = {Hex(encoded, 8)}");

            if (!StartsWithStartCode(encoded))
                throw new InvalidOperationException("output does not begin with an Annex B start code.");

            var nals = new List<(int Length, bool Keyframe)>();
            void Collect(byte[] data, int length, bool keyframe, double _) => nals.Add((length, keyframe));

            var parser = new H264AnnexBParser();
            parser.Append(encoded, 0.0, Collect);
            parser.Flush(0.0, Collect);

            Console.WriteLine($"    H264AnnexBParser extracted {nals.Count} NAL unit(s): " +
                              string.Join(", ", nals.Select((n, i) => $"#{i} len={n.Length} key={n.Keyframe}")));
            if (nals.Count == 0)
                throw new InvalidOperationException("the production parser found no NAL units in the output.");

            Console.WriteLine($"MFT Video Prototype: SUCCESS, emitted {encoded.Length} bytes of H.264 Annex-B " +
                              $"({nals.Count} NAL unit(s) framed by H264AnnexBParser). The video path can drop ffmpeg.");
            return 0;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"at {step}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Allocates an NV12 texture and fills it with mid-grey.
    ///
    /// The fill goes through UpdateSubresource from a managed array rather than through a staging
    /// texture and Map. Map needs CPU access to the surface, and D3D11 refuses that for NV12 on this
    /// driver -- the earlier version caught that and fed an untouched texture, which is fine for proving
    /// the encoder runs but proves nothing about the picture. UpdateSubresource is a copy, not a map,
    /// so it does not need CPU access to the destination.
    ///
    /// Grey, not black: a black frame compresses to a few dozen bytes, and a "first H.264 packet"
    /// that small is impossible to tell from a stub. Mid-grey still yields a full IDR, and a packet
    /// size in the tens of kilobytes is the evidence that the encoder actually encoded a frame.
    /// </summary>
    /// <summary>
    /// The BGRA source, which is the colour the capture path actually produces.
    ///
    /// WGC hands over BGRA, so BGRA is what the converter has to be fed if this is to say anything
    /// about the real capture path. Anything else would test a conversion the recorder never performs.
    ///
    /// RenderTarget as well as ShaderResource: the video processor writes its result into a render
    /// target, and a source that is not bindable that way is rejected at view creation.
    /// </summary>
    private static ID3D11Texture2D MakeColourSourceTexture(ID3D11Device device, Vortice.DXGI.Format format)
    {
        var texture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (int)Width,
            Height = (int)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        // Painted once so the first BLT has something defined in it; every frame repaints it anyway.
        FillColourFrame(device, texture, 0);
        return texture;
    }

    /// <summary>
    /// Paints a frame that moves.
    ///
    /// A static image would let the encoder emit a few tiny P-frames and prove nothing about sustained
    /// throughput. A flat one would compress to a couple of hundred bytes a frame, at which point "did
    /// it really encode this" stops being answerable from the packet size. So: a colour field with a
    /// diagonal ramp, plus a bar that sweeps a few pixels per frame.
    /// </summary>
    private static void FillColourFrame(ID3D11Device device, ID3D11Texture2D texture, int index)
    {
        var pixels = new byte[Width * Height * 4];
        var bar = index * 8;
        for (var y = 0; y < Height; y++)
        {
            var row = (int)(y * Width) * 4;
            for (var x = 0; x < Width; x++)
            {
                var o = row + x * 4;
                var sweep = Math.Abs(x - bar) < 64;
                pixels[o] = (byte)(sweep ? 255 : x * 255 / Width);
                pixels[o + 1] = (byte)(sweep ? 32 : y * 255 / Height);
                pixels[o + 2] = (byte)(sweep ? 32 : 200 - x * 150 / Width);
                pixels[o + 3] = 255;
            }
        }
        device.ImmediateContext.UpdateSubresource(
            pixels.AsSpan(), texture, 0, (uint)(Width * 4), 0, null);
    }

    /// <summary>
    /// The NV12 texture the video processor writes into.
    ///
    /// Separate from MakeNv12Texture, which returns the Staging texture the encoder insists on: the
    /// scaler cannot write a staging resource, and the encoder will not take a Default one, so there
    /// have to be two and a device-side copy between them. RenderTarget is what the scaler needs.
    /// </summary>
    private static ID3D11Texture2D MakeNv12TargetTexture(ID3D11Device device, Vortice.DXGI.Format format)
    {
        return device.CreateTexture2D(new Texture2DDescription
        {
            Width = (int)Width,
            Height = (int)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private static ID3D11Texture2D MakeNv12Texture(ID3D11Device device)
    {
        var description = new Texture2DDescription
        {
            Width = (int)Width,
            Height = (int)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new SampleDescription(1, 0),

            // Staging, not Default -- and this is not a guess. A previous version had a "fallback"
            // that set Usage=Staging, threw while creating the texture, and then handed the same
            // description back still carrying Staging. That accidentally-correct texture was the only
            // one the encoder ever accepted; creating it as Default gives
            // MF_E_UNSUPPORTED_D3D_TYPE on the very first ProcessInput. Tidying that up "properly"
            // is what broke the pipeline.
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        };

        var texture = device.CreateTexture2D(description);

        // NV12 is a luma plane followed by an interleaved chroma plane, one byte per component.
        var luma = (int)(Width * Height);
        var pixels = new byte[luma + luma / 2];
        Array.Fill(pixels, (byte)128);

        // Mid-grey, not black: a black frame compresses to a few dozen bytes, and a "first H.264
        // packet" that small cannot be told from a stub. A packet in the tens of kilobytes is the
        // evidence that a real frame went through the encoder.
        //
        // The Span overload rather than the raw-pointer one: it derives the copy size from the data,
        // so a wrong pitch cannot turn into a driver-level complaint about the resource.
        device.ImmediateContext.UpdateSubresource(
            pixels.AsSpan(), texture, 0, (uint)Width, 0, null);
        return texture;
    }

    /// <summary>What the MFT's event queue is asking for, as of the last pump.</summary>
    private struct MftRequests
    {
        public bool NeedInput;
        public bool HaveOutput;
    }

    /// <summary>
    /// Reads the MFT's event queue and records what it is asking for.
    ///
    /// The queue is the only way to learn that the encoder is ready, and the only way to read it is to
    /// take the event out -- so this consumes as well as observes. That is why it must record BOTH
    /// requests: an earlier version kept only METransformNeedInput and threw METransformHaveOutput
    /// away, which is how the pipeline lost track of the fact that output was waiting for it.
    /// </summary>
    private static MftRequests PumpEvents(IMFMediaEventGenerator events)
    {
        var state = new MftRequests();
        for (var i = 0; i < 16; i++)
        {
            IMFMediaEvent? ev;
            try
            {
                // MF_EVENT_FLAG_NO_WAIT. GetEvent with no flags BLOCKS until the MFT posts something,
                // which in a synchronous drain loop means waiting forever for an event that only gets
                // posted in response to the very call being made. The prototype hung here until this
                // flag was added; there is no enum for it in Vortice.
                ev = events.GetEvent(NoWait);
            }
            catch (SharpGenException)
            {
                break;   // queue empty, which is the normal state between frames
            }
            if (ev is null)
                break;

            using (ev)
            {
                if (ev.EventType == MediaEventTypes.TransformNeedInput)
                    state.NeedInput = true;
                else if (ev.EventType == MediaEventTypes.TransformHaveOutput)
                    state.HaveOutput = true;
            }
        }
        return state;
    }

    /// <summary>
    /// Step 8.3C: a BGRA source, converted to NV12 by the GPU, encoded for a clip, then drained.
    ///
    /// The colour conversion is the last piece between "the MFT works" and "the video path can replace
    /// ffmpeg". Everything runs on the device: the BGRA texture is uploaded once per frame, the
    /// conversion is the GPU's own scaler, and the CPU never sees a pixel.
    /// </summary>
    private static int EncodeSeries(IMFTransform transform, ID3D11Device device, IMFMediaType input, bool providesSamples)
    {
        var step = "SetInputType";
        try
        {
            // Negotiation set the output type; the input type still has to be accepted before any frame
            // is offered. Skipping it is not a no-op -- the MFT answers MF_E_TRANSFORM_TYPE_NOT_SET on
            // the first ProcessInput, which reads exactly like a broken encoder.
            // The MFT is fully configured by the caller; this method owns the frames and the clock.
            // Whether the caller supplies the output sample or the MFT does is a declared property of
            // the output stream, and getting it wrong returns S_OK with an empty sample, not an error.
            var context = device.ImmediateContext;
            using var videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            using var videoContext = context.QueryInterface<ID3D11VideoContext>();

            // The scaler is created once for the whole clip, not per frame. Recreating it every frame
            // would allocate driver state 30 times a second and measure the driver's setup path rather
            // than the thing worth knowing, which is whether a stream sustains.
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(FpsDenominator, FpsNumerator),
                InputWidth = Width,
                InputHeight = Height,
                OutputFrameRate = new Rational(FpsDenominator, FpsNumerator),
                OutputWidth = Width,
                OutputHeight = Height,
                Usage = VideoUsage.OptimalSpeed,
            };
            step = "CreateVideoProcessorEnumerator";
            using var enumerator = videoDevice.CreateVideoProcessorEnumerator(content);
            step = "CreateVideoProcessor";
            using var processor = videoDevice.CreateVideoProcessor(enumerator, 0);

            // The content description carries no pixel formats at all -- the native struct is 40 bytes
            // and has no InputFormat/OutputFormat members -- so the formats come from the textures the
            // views are built over. That makes the enumerator's own opinion unasked for and unasked:
            // and the views are validated against its choice rather than ours. Vortice cannot report
            // that choice -- VideoProcessorCaps.InputFormatCaps is bound as an enum instead of the
            // struct it actually is, so the format pair is unreachable -- but CheckVideoProcessorFormat
            // can still be asked whether the pair we need is on the list.
            // The flags are D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT (1) and _OUTPUT (2). Compared
            // numerically on purpose: the enum's member names are not the point, and a name that
            // changed under us would be a compile error in a spike whose job is to answer a question
            // about the machine, not about the binding library.
            var canRead = enumerator.CheckVideoProcessorFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm);
            var canWrite = enumerator.CheckVideoProcessorFormat(Vortice.DXGI.Format.NV12);
            Console.WriteLine($"    scaler format support: B8G8R8A8_UNorm=0x{(int)canRead:X} NV12=0x{(int)canWrite:X}");
            var readable = ((int)canRead & 0x1) != 0;
            var writable = ((int)canWrite & 0x2) != 0;
            if (!readable || !writable)
                throw new InvalidOperationException(
                    $"this device's video processor does not offer BGRA->NV12 " +
                    $"(B8G8R8A8_UNorm as input: {readable}, NV12 as output: {writable}).");

            step = "create textures";
            using var source = MakeColourSourceTexture(device, Vortice.DXGI.Format.B8G8R8A8_UNorm);
            using var converted = MakeNv12TargetTexture(device, Vortice.DXGI.Format.NV12);
            using var encoderInput = MakeNv12Texture(device);

            step = "CreateVideoProcessorInputView";
            // Fails here on this machine, and the cause is a hole in the binding rather than in the
            // scaler. CheckVideoProcessorFormat answers 0x3 (input and output supported) for both
            // B8G8R8A8_UNorm and NV12, so the hardware does the conversion -- but Vortice's
            // VideoProcessorContentDescription is 40 bytes where the native struct is 48: the two
            // DXGI_FORMAT fields are missing from the binding, so they marshal as zero and the
            // enumerator is created with unknown input and output formats. Views made against an
            // enumerator that does not know its own formats come back E_INVALIDARG.
            //
            // Getting past this means handing CreateVideoProcessorEnumerator a correct 48-byte struct
            // by hand, which is a vtable call rather than a bound method. Left as the stated next step
            // rather than attempted blind.
            using var sourceView = videoDevice.CreateVideoProcessorInputView(
                source, enumerator,
                new VideoProcessorInputViewDescription
                {
                    // Strictly zero. FourCC is for compressed formats only; a non-zero value with a
                    // plain DXGI format underneath is a confident-looking way to ask for E_INVALIDARG.
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });
            step = "CreateVideoProcessorOutputView";
            using var outputView = videoDevice.CreateVideoProcessorOutputView(
                converted, enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                });

            using var surfaceBuffer = MediaFactory.MFCreateDXGISurfaceBuffer(
                typeof(ID3D11Texture2D).GUID, encoderInput, 0, false);
            Console.WriteLine("    video processor created: BGRA -> NV12 1920x1080p30, 0-copy on the GPU");

            var events = transform.QueryInterface<IMFMediaEventGenerator>();
            var nals = new List<(int Length, bool Keyframe)>();
            void Collect(byte[] data, int length, bool keyframe, double _) => nals.Add((length, keyframe));

            var parser = new H264AnnexBParser();
            var packets = 0;
            var bytes = 0;

            step = $"encode {Frames} frames";
            var inFlight = new List<IMFSample>();
            for (var i = 0; i < Frames; i++)
            {
                // Per frame: repaint the source, convert on the GPU, then hand the encoder a sample.
                FillColourFrame(device, source, i);
                videoContext.VideoProcessorBlt(processor, outputView, 0, 1, new[]
                {
                    new VideoProcessorStream
                    {
                        Enable = true,
                        OutputIndex = 0,
                        InputFrameOrField = 0,
                        InputSurface = sourceView,
                    },
                }).CheckError();

                // The scaler writes a normal Default-usage texture; the encoder only ever accepted a
                // Staging one. One device-side copy bridges the two, and it stays in VRAM.
                context.CopyResource(encoderInput, converted);

                var frameSample = MediaFactory.MFCreateSample();
                // Deliberately NOT disposed. The MFT holds a reference to the sample and to the surface
                // behind it for as long as the frame is in flight, and disposing here drops our
                // reference the instant ProcessInput returns -- leaving the encoder waiting on a
                // surface nobody owns. The one-frame version of this spike could not hit this; it only
                // appears now that frames are fed back to back.
                inFlight.Add(frameSample);
                frameSample.AddBuffer(surfaceBuffer);
                frameSample.SampleTime = 10_000_000L / FpsNumerator * i;
                frameSample.SampleDuration = 10_000_000L / FpsNumerator;

                // Backpressure, not a straight loop. Pushing Frames samples in regardless is answered
                // with MF_E_NOTACCEPTING once the encoder's input queue fills, and treating that as
                // fatal would conclude the hardware cannot keep up when in fact the caller simply
                // stopped listening. So a full queue means drain harder and try again.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        transform.ProcessInput(0, frameSample, 0);
                        break;
                    }
                    catch (SharpGenException ex) when (ex.HResult == NotAccepting)
                    {
                        if (attempt > 500)
                            throw new InvalidOperationException(
                                $"the encoder stopped accepting input after {i} frames", ex);
                        Drain(transform, events, providesSamples, parser, Collect, ref packets, ref bytes);
                        Thread.Sleep(1);
                    }
                }

                Drain(transform, events, providesSamples, parser, Collect, ref packets, ref bytes);
            }
            Console.WriteLine($"    {Frames} frames converted and encoded");

            // Without a drain the encoder is entitled to sit on the last frame forever, and a recording
            // that quietly ends by dropping its final pictures is worse than one that visibly fails.
            step = "drain";
            transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            for (var spin = 0; spin < 2000; spin++)
            {
                var requests = PumpEvents(events);
                if (requests.HaveOutput)
                {
                    Drain(transform, events, providesSamples, parser, Collect, ref packets, ref bytes);
                    continue;
                }
                if (requests.NeedInput)
                    break;      // asking for more after a drain means it is finished
                Thread.Sleep(1);
            }
            Console.WriteLine($"    drained: {packets} output packet(s) in total");

            if (packets == 0)
                throw new InvalidOperationException("the encoder produced no output across the whole clip.");
            if (nals.Count == 0)
                throw new InvalidOperationException("H264AnnexBParser found no NAL units in the bitstream.");

            var keyframes = nals.Count(n => n.Keyframe);
            Console.WriteLine($"    bitstream: {bytes} bytes in {packets} packet(s)");
            Console.WriteLine($"    H264AnnexBParser framed {nals.Count} NAL unit(s), {keyframes} keyframe(s): " +
                              $"sizes {string.Join(", ", nals.Select(n => n.Length))}");
            if (keyframes == 0)
                throw new InvalidOperationException("no keyframe in the bitstream, so it could not be seeked.");

            Console.WriteLine($"MFT Video Prototype: SUCCESS, {Frames} frames of GPU-converted BGRA->NV12 " +
                              $"became {bytes} bytes of H.264 Annex-B in {packets} packet(s), framed by " +
                              "H264AnnexBParser. The video path can drop ffmpeg.");
            return 0;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"at {step}: {ex.Message}", ex);
        }
    }

    /// <summary>Reads whatever output is ready and pushes it through the production parser.</summary>
    private static void Drain(IMFTransform transform, IMFMediaEventGenerator events, bool providesSamples,
                              H264AnnexBParser parser, Action<byte[], int, bool, double> collect,
                              ref int packets, ref int bytes)
    {
        for (var i = 0; i < 16; i++)
        {
            if (!PumpEvents(events).HaveOutput)
                return;
            var got = Read(transform, providesSamples);
            if (got.Length == 0)
                return;
            packets++;
            bytes += got.Length;
            if (packets == 1)
                Console.WriteLine($"    first packet: {got.Length} bytes, first 8 = {Hex(got, 8)}");
            parser.Append(got, 0, collect);
        }
    }

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

    /// <summary>
    /// One ProcessOutput, with the three answers that are normal rather than failures.
    ///
    /// The MFT decides where the sample comes from, and MFT_OUTPUT_STREAM_PROVIDES_SAMPLES is what
    /// says so. When it is set the encoder allocates the output sample itself -- in driver memory --
    /// and the caller must hand ProcessOutput a NULL sample. Passing a preallocated one instead does
    /// not fail: ProcessOutput answers S_OK and hands back an empty sample, which is indistinguishable
    /// from an encoder that swallowed 60 frames and produced nothing. Reading the flag is the whole
    /// difference between those two stories.
    /// </summary>
    private static byte[] Read(IMFTransform transform, bool providesSamples)
    {
        IMFSample? own = null;
        var data = new OutputDataBuffer { StreamID = 0, Sample = null };
        if (!providesSamples)
        {
            // Pre-allocated, as with the audio encoder: an empty IMFSample makes ProcessOutput fail
            // with E_INVALIDARG that has nothing to do with the codec.
            own = MediaFactory.MFCreateSample();
            own.AddBuffer(MediaFactory.MFCreateMemoryBuffer(4 * 1024 * 1024));
            data.Sample = own;
        }

        try
        {
            transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _).CheckError();
        }
        catch (SharpGenException ex) when (ex.HResult is NeedMoreInput or NotAccepting or Unexpected)
        {
            own?.Dispose();
            return [];
        }

        // With PROVIDES_SAMPLES this is the encoder's own sample; without it, the one allocated above.
        var produced = data.Sample ?? own;
        if (produced is null)
        {
            own?.Dispose();
            return [];
        }

        try
        {
            using var contiguous = produced.ConvertToContiguousBuffer();
            // Lock returns (pointer, maxLength, currentLength). It is currentLength that says how many
            // bytes the encoder actually wrote; maxLength is only the capacity, and using it would
            // report the entire 4 MB as a single packet.
            contiguous.Lock(out var src, out _, out var length);
            var bytes = new byte[length];
            if (length > 0)
                Marshal.Copy(src, bytes, 0, length);
            contiguous.Unlock();
            return bytes;
        }
        finally
        {
            own?.Dispose();
        }
    }

    private static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid Bgra = new("41485242-0000-0010-8000-00aa00389b71"); // 'B','R','A','8'
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"); // 'H','2','6','4'

    // Normal answers from a buffered MFT, not failures. const so they can be used in a pattern.
    private const int NeedMoreInput = unchecked((int)0xC00D6D72);  // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int NotAccepting = unchecked((int)0xC00D36B5);    // MF_E_NOTACCEPTING
    private const int InvalidType = unchecked((int)0xC00D36BD);      // MF_E_INVALIDTYPE
    private const int NoWait = 0x00000001;                          // MF_EVENT_FLAG_NO_WAIT
    private const int Unexpected = unchecked((int)0x8000FFFF);     // E_UNEXPECTED

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
