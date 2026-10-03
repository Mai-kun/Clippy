using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Clippy;

/// <summary>
/// H.264 encoding through the system's own Media Foundation encoder, with no ffmpeg process anywhere.
///
/// The video counterpart of MfAudioEncoder, and it exists because the last remaining ffmpeg dependency
/// was the one the capture path could least afford: ffmpeg had to receive every frame as raw BGRA over
/// a pipe, which means a GPU-to-CPU copy of every frame plus a process hop. The capture path already
/// holds the frame as a D3D11 texture and the encoder wants a D3D11 texture, so that round trip through
/// system memory is avoidable.
///
/// Pipeline: WGC BGRA texture -> video processor converts to NV12 inside VRAM -> hardware MFT encodes
/// -> Annex B bytes -> AnnexBParser -> the ring. The CPU touches the encoded bytes and nothing else.
///
/// Every non-obvious detail here was established by the --test-mft-video spike rather than assumed, and
/// the three most expensive to learn all fail silently when wrong: the output sample is allocated by
/// the MFT, the encoder input texture must be D3D11_USAGE_STAGING, and the MFT setup order is
/// load-bearing. Each is commented where it happens.
/// </summary>
internal sealed class MfVideoEncoder : IVideoEncoder
{
    private const int Fps = 30;
    private const int Bitrate = 6_000_000;
    private const int NeedsMoreInput = unchecked((int)0xC00D6D72);   // MF_E_TRANSFORM_NEED_MORE_INPUT
    private const int NotAccepting = unchecked((int)0xC00D36B5);     // MF_E_NOTACCEPTING
    private const int Unexpected = unchecked((int)0x8000FFFF);       // E_UNEXPECTED
    private const int NoWait = 0x00000001;                          // MF_EVENT_FLAG_NO_WAIT

    private readonly IMFTransform transform;
    private readonly IMFMediaEventGenerator events;
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
    private readonly ID3D11VideoProcessor videoProcessor;
    private readonly ID3D11VideoProcessorInputView sourceView;
    private readonly ID3D11VideoProcessorOutputView targetView;
    private readonly ID3D11Texture2D cpuSource;      // filled by Write() for the CPU-pixel path
    private readonly ID3D11Texture2D nv12Target;     // the video processor writes here
    private readonly ID3D11Texture2D nv12Staging;    // the encoder only accepts a staging surface
    private readonly IMFMediaBuffer surfaceBuffer;
    private readonly bool providesSamples;
    private readonly int width;
    private readonly int height;

    private readonly ConcurrentQueue<double> captureTimes = new();
    private readonly List<IMFSample> inFlight = [];   // the MFT holds each one until it is encoded
    private readonly ConcurrentQueue<IMFSample> inputQueue = new();

    private readonly AnnexBParser parser = new();
    private readonly object parserGate = new();
    private readonly ManualResetEventSlim ready = new(false);
    private readonly ManualResetEventSlim drained = new(false);

    private Thread? drainThread;
    private RingBuffer? target;
    private volatile bool stopping;
    private double currentAccessUnitTime = -1;
    private long captured;
    private int accessUnits;
    private long inFlightFrames;
    private int producedFrames;
    private byte[]? sps;
    private byte[]? pps;
    private MfVideoEncoder(
        IMFTransform transform, ID3D11Device device, ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice, ID3D11VideoContext videoContext,
        ID3D11VideoProcessor videoProcessor,
        ID3D11VideoProcessorInputView sourceView, ID3D11VideoProcessorOutputView targetView,
        ID3D11Texture2D cpuSource, ID3D11Texture2D nv12Target, ID3D11Texture2D nv12Staging,
        IMFMediaBuffer surfaceBuffer, int width, int height)
    {
        this.transform = transform;
        this.device = device;
        this.context = context;
        this.videoDevice = videoDevice;
        this.videoContext = videoContext;
        this.videoProcessor = videoProcessor;
        this.sourceView = sourceView;
        this.targetView = targetView;
        this.cpuSource = cpuSource;
        this.nv12Target = nv12Target;
        this.nv12Staging = nv12Staging;
        this.surfaceBuffer = surfaceBuffer;
        this.width = width;
        this.height = height;

        var info = transform.GetOutputStreamInfo(0);
        providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        events = transform.QueryInterface<IMFMediaEventGenerator>();
    }

    public byte[]? Sps => sps;
    public byte[]? Pps => pps;
    public (int Captured, int AccessUnits) TimingCounts => ((int)captured, accessUnits);

    public void EnqueueCaptureTime(double captureClockSeconds) => captureTimes.Enqueue(captureClockSeconds);

    /// <summary>One BGRA frame in system memory, per IVideoEncoder.</summary>
    /// <remarks>
    /// The interface path, and the slow one: the pixels cross the bus into a texture and then through
    /// the scaler. PushTexture is the same pipeline with the first hop removed, and ScreenCapture calls
    /// it whenever this class is the active encoder.
    /// </remarks>
    public void Write(byte[] bgraPixels, double systemTimeMs)
    {
        if (bgraPixels.Length < width * height * 4)
            throw new ArgumentException(
                $"expected {width * height * 4} BGRA bytes, got {bgraPixels.Length}", nameof(bgraPixels));

        context.UpdateSubresource(bgraPixels.AsSpan(), cpuSource, 0, (uint)(width * 4), 0, null);
        SubmitFrame();
    }

    /// <summary>A frame that is still on the GPU, which is the only kind the capture path has.</summary>
    /// <remarks>
    /// The source texture belongs to the Windows.Graphics.Capture frame pool and is recycled the moment
    /// this callback returns, so it cannot be handed to an encoder that works asynchronously. What is
    /// safe is to convert it here and now, synchronously, and hand over the copy: the video processor
    /// reads the capture texture and writes NV12, both device-side, and the CPU never sees a pixel.
    /// That is where the zero-copy claim comes from -- not from the MFT consuming the capture surface
    /// directly, which the pool ownership rules rule out.
    /// </remarks>
    public void PushTexture(ID3D11Texture2D source)
    {
        // MF_E_UNSUPPORTED_D3D_TYPE (0xC00D36D5) is a Media Foundation code; DirectX cannot raise it.
        // So it comes from one of exactly two places -- the surface buffer, or ProcessInput -- and
        // every step here is logged with its own HRESULT to find out which. Each step is also named,
        // because an exception that arrives with a step label instead of a bare code is the only
        // difference between a five-minute diagnosis and an afternoon.
        var step = "describe WGC texture";
        try
        {
            var d = source.Description;
            Console.WriteLine($"[mf-video] wgc desc: {d.Width}x{d.Height} format={d.Format} " +
                              $"usage={d.Usage} bind={d.BindFlags} cpu={d.CPUAccessFlags} " +
                              $"mips={d.MipLevels} array={d.ArraySize}");

            step = "CopyResource(wgc -> cpuSource)";
            context.CopyResource(cpuSource, source);

            step = "VideoProcessorBlt";
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                InputSurface = sourceView,
            };
            videoContext.VideoProcessorBlt(videoProcessor, targetView, 0, 1, new[] { stream }).CheckError();

            step = "CopyResource(nv12Target -> nv12Staging)";
            context.CopyResource(nv12Staging, nv12Target);

            step = "ProcessInput";
            SubmitFrame();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"mf-video step '{step}' failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Queues one frame for the encoder. It does not call ProcessInput itself.
    ///
    /// An asynchronous MFT takes input when it asks for it, and calling ProcessInput at some other
    /// moment is what MF_E_UNSUPPORTED_D3D_TYPE and MF_E_NOTACCEPTING both come from. So the capture
    /// thread only prepares -- a sample wrapping the staging surface -- and the drain thread decides
    /// when the frame is actually handed over, which is where METransformNeedInput arrives. This is
    /// the arrangement the spike used, and it is the one that worked.
    /// </summary>
    private void SubmitFrame()
    {
        if (stopping)
            return;

        var sample = MediaFactory.MFCreateSample();
        // Not disposed here: the drain thread hands it to ProcessInput, and the MFT keeps its own
        // reference to it and to the surface behind it until the frame is encoded.
        sample.AddBuffer(surfaceBuffer);
        sample.SampleTime = 10_000_000L / Fps * Interlocked.Read(ref queuedFrames);
        sample.SampleDuration = 10_000_000L / Fps;
        inputQueue.Enqueue(sample);
        Interlocked.Increment(ref queuedFrames);
    }

    private long queuedFrames;

    // Pipeline telemetry. Every stage of the frame's journey is counted separately, because "no clip
    // came out" is not a diagnosis: knowing that the queue filled and nothing was ever fed, or that
    // frames were fed and no output arrived, points at completely different places.
    private long needInputCount;
    private long haveOutputCount;
    private long fedCount;
    private long refusedCount;
    private long drainedCount;
    private long lastStatsTick;
    public void StartDrain(RingBuffer targetBuffer)
    {
        target = targetBuffer;
        drainThread = new Thread(DrainLoop)
        {
            // Above normal: this thread is the only thing moving encoded bytes into the ring, and it
            // competes with the capture thread for the same cores. Being starved here shows up as ring
            // lag, which then shows up as a clip that starts later than the user asked for.
            Priority = ThreadPriority.AboveNormal,
            IsBackground = true,
            Name = "MfVideoEncoder drain",
        };
        drainThread.Start();
    }

    public bool WaitForReady(TimeSpan timeout) => ready.Wait(timeout);

    public void WaitForDrain(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // Empty is not enough on its own: the encoder may still hold frames it was given and not
            // yet emitted, and an export that slices now takes a ring that is about to grow.
            if (captureTimes.IsEmpty && Interlocked.Read(ref inFlightFrames) <= 0 && producedFrames > 0)
            {
                drained.Set();
                return;
            }
            Thread.Sleep(5);
        }
    }

    private void DrainLoop()
    {
        try
        {
            while (!stopping)
            {
                PumpEvents(out var needInput, out var haveOutput);

                // Input first. METransformNeedInput is the MFT saying it has room, and it is the only
                // moment a frame may be handed over; feeding at any other time is exactly what
                // MF_E_UNSUPPORTED_D3D_TYPE and MF_E_NOTACCEPTING come from.
                if (needInput)
                    FeedOneQueuedSample();
                else if (Interlocked.Read(ref fedCount) == 0)
                    // KEEP THIS, and the telemetry is why. Measured with it removed: the MFT posted
                    // METransformNeedInput zero times across 500 queued frames, so the queue only grew
                    // -- queued=441, needInput=0, fed=0, refused=0. The encoder cannot ask for input
                    // until it has been given some, because it has no frame count to go on. So the
                    // first frame is offered, and from then on the events drive the loop.
                    FeedOneQueuedSample();
                else if (Interlocked.Read(ref captured) == 0)
                    // An unlocked asynchronous MFT may not post METransformNeedInput until it has
                    // been given something, so waiting for the request before the very first frame
                    // waits forever. The spike opened the valve by hand for exactly this reason, and
                    // dropping it is why the queue sat full while the recorder produced nothing.
                    FeedOneQueuedSample();

                if (haveOutput)
                    DrainOnce();

                if (!needInput && !haveOutput)
                    Thread.Sleep(1);

                // Telemetry every two seconds. "No clip came out" is not a diagnosis; whether the
                // queue filled and nothing was fed, or frames were fed and nothing came back, points
                // at opposite ends of the pipeline and looks identical in the log without this.
                if (Environment.TickCount64 - Interlocked.Read(ref lastStatsTick) >= 2000)
                {
                    Interlocked.Exchange(ref lastStatsTick, Environment.TickCount64);
                    Console.WriteLine(
                        $"[mf-stats] queued={inputQueue.Count} " +
                        $"needInput={Interlocked.Read(ref needInputCount)} " +
                        $"fed={Interlocked.Read(ref fedCount)} " +
                        $"haveOutput={Interlocked.Read(ref haveOutputCount)} " +
                        $"drained={Interlocked.Read(ref drainedCount)} " +
                        $"refused={Interlocked.Read(ref refusedCount)}");
                }
            }

            // Shutdown: hand over whatever is still queued, ask for the rest, then take it. Without
            // this the last GOP is dropped silently, and a recording that loses its final second looks
            // exactly like a clean stop.
            try
            {
                while (inputQueue.TryDequeue(out var pending))
                {
                    try { transform.ProcessInput(0, pending, 0); }
                    catch (SharpGenException) { /* the MFT is finished asking; the tail is lost */ }
                }
                transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
                for (var spin = 0; spin < 2000; spin++)
                {
                    PumpEvents(out _, out var more);
                    if (!more)
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    DrainOnce();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[mf-video] drain on shutdown failed: {ex.Message}");
            }

            lock (parserGate)
                parser.Flush(0, Emit);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mf-video] drain thread stopped: {ex.Message}");
        }
    }

    private void PumpEvents(out bool needInput, out bool haveOutput)
    {
        needInput = false;
        haveOutput = false;
        for (var i = 0; i < 16; i++)
        {
            IMFMediaEvent? ev;
            try
            {
                // MF_EVENT_FLAG_NO_WAIT. GetEvent with no flags blocks until the MFT posts something,
                // which in a polling loop means waiting forever for an event that only gets posted in
                // response to the call being made. There is no enum for this flag in Vortice.
                ev = events.GetEvent(NoWait);
            }
            catch (SharpGenException)
            {
                break;   // queue empty, the normal state between frames
            }
            if (ev is null)
                break;

            using (ev)
            {
                if (ev.EventType == MediaEventTypes.TransformNeedInput)
                    needInput = true;
                else if (ev.EventType == MediaEventTypes.TransformHaveOutput)
                    haveOutput = true;
            }
        }
    }

    /// <summary>
    /// Hands the encoder one prepared frame, if there is one waiting.
    ///
    /// The queue is drained only in response to METransformNeedInput, never on a timer. That is the
    /// whole contract with an asynchronous MFT: it asks for a frame when it has room, and offering one
    /// at any other moment is answered with MF_E_NOTACCEPTING or, on this driver, the more alarming
    /// MF_E_UNSUPPORTED_D3D_TYPE that reads like a format problem and is not one.
    /// </summary>
    private void FeedOneQueuedSample()
    {
        if (!inputQueue.TryDequeue(out var sample))
            return;

        try
        {
            transform.ProcessInput(0, sample, 0);
            Interlocked.Increment(ref captured);
        }
        catch (SharpGenException ex) when (ex.HResult == NotAccepting)
        {
            // It changed its mind between the event and this call. Put the frame back at the head of
            // the queue rather than dropping a frame and shifting every later timestamp with it.
            inputQueue.Enqueue(sample);
        }
        catch (SharpGenException ex)
        {
            Interlocked.Increment(ref refusedCount);
            Console.WriteLine($"[mf-video] ProcessInput refused a frame: 0x{ex.HResult:x8} {ex.Message}");
        }
    }

    private void DrainOnce()
    {
        var sample = MediaFactory.MFCreateSample();
        var data = new OutputDataBuffer { StreamID = 0, Sample = providesSamples ? null : sample };
        if (!providesSamples)
            sample.AddBuffer(MediaFactory.MFCreateMemoryBuffer(4 * 1024 * 1024));

        try
        {
            transform.ProcessOutput(ProcessOutputFlags.None, 1, ref data, out _).CheckError();
        }
        catch (SharpGenException ex) when (ex.HResult is NeedsMoreInput or NotAccepting or Unexpected)
        {
            // All three mean "nothing yet", not "broken". E_UNEXPECTED in particular is what an async
            // MFT answers before it has output, and treating it as fatal ends the stream on frame one.
            return;
        }

        var produced = data.Sample ?? sample;
        if (produced is null)
            return;

        byte[] bytes;
        using (var contiguous = produced.ConvertToContiguousBuffer())
        {
            // currentLength is what the encoder wrote; maxLength is only the buffer capacity, and using
            // that would report the whole 4 MB as one packet.
            contiguous.Lock(out var src, out _, out var length);
            bytes = new byte[length];
            if (length > 0)
                Marshal.Copy(src, bytes, 0, length);
            contiguous.Unlock();
        }

        if (bytes.Length == 0)
            return;

        Interlocked.Increment(ref producedFrames);
        Interlocked.Increment(ref drainedCount);
        Interlocked.Decrement(ref inFlightFrames);
        ready.Set();

        lock (parserGate)
            parser.Append(bytes, 0, Emit);
    }

    private void Emit(byte[] data, int length, bool isIdr, double _)
    {
        // nal_unit_type is the low 5 bits of the first payload byte. SPS (7) and PPS (8) are kept
        // because the muxer needs them to describe the track; AUD (9) and SEI (6) are dropped, because
        // the mp4 writer reconstructs framing from lengths and would otherwise count them as frames.
        var type = data[0] & 0x1F;
        switch (type)
        {
            case 7: sps = data; return;
            case 8: pps = data; return;
            case 9:
            case 6: return;
        }

        var ring = target;
        if (ring is null)
            return;

        // The capture time belongs to the frame, and one frame makes one access unit. Stamping a
        // timestamp per NAL would give a P-frame slice a different clock from its own; taking it when
        // the access unit begins is the only assignment that matches what the ffmpeg path did.
        if (currentAccessUnitTime < 0 && captureTimes.TryDequeue(out var t))
            currentAccessUnitTime = t;
        if (currentAccessUnitTime < 0)
            return;

        // Copied, not handed over: the parser array is reused on the next append, and the ring holds
        // packets for minutes.
        var packet = new byte[Math.Max(length, 16)];
        Buffer.BlockCopy(data, 0, packet, 0, length);
        ring.Push(packet, length, currentAccessUnitTime, isIdr);

        if (isIdr)
        {
            currentAccessUnitTime = -1;
            Interlocked.Increment(ref accessUnits);
        }
    }

    public void Dispose()
    {
        if (stopping)
            return;
        stopping = true;

        try
        {
            drainThread?.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mf-video] drain thread did not stop cleanly: {ex.Message}");
        }

        lock (inFlight)
            inFlight.Clear();

        cpuSource.Dispose();
        nv12Target.Dispose();
        nv12Staging.Dispose();
        surfaceBuffer.Dispose();
        sourceView.Dispose();
        targetView.Dispose();
        videoContext.Dispose();
        videoDevice.Dispose();
        // device and context belong to ScreenCapture and outlive this encoder; releasing them here
        // would leave the capture path on a dead device.
        transform.Dispose();
        ready.Dispose();
        drained.Dispose();
    }
    /// <summary>
    /// Builds an encoder, or throws with a message a user can act on.
    ///
    /// The ordering below is not a style choice. Every step of it was found by the --test-mft-video
    /// spike, and each one fails silently when moved: type negotiation, then the D3D manager, then the
    /// input type, then the stream is declared to exist. Declaring the stream before the input type is
    /// accepted leaves the MFT answering MF_E_NOTACCEPTING to every frame forever, with nothing
    /// anywhere saying the order was the problem.
    /// </summary>
    public static MfVideoEncoder Create(int width, int height)
    {
        var h264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
        var nv12 = new Guid("3231564e-0000-0010-8000-00aa00389b71");
        // This creates its own device, and why is still open.
        //
        // The capture device is definitely unusable: a live Windows.Graphics.Capture session on it
        // makes every ProcessInput fail with MF_E_UNSUPPORTED_D3D_TYPE, while the same encoder on a
        // device created here in the same process accepts the identical synthetic frame. And an
        // encoder cannot be given the capture texture directly, because Direct3D 11 will not copy a
        // resource between devices. So the frame has to arrive as BGRA in system memory, which is
        // what ScreenCapture already did for the ffmpeg path.
        //
        // What is NOT yet explained: with its own device and no probe running, this encoder still
        // fails the self-test in the live process, while the same code succeeds in the spike and
        // succeeds as the first instance built during a live session. A second instance also fails.
        // The remaining variable is therefore not the device, the formats or the call order, all of
        // which have been compared against the working spike; it is whatever the capture session has
        // already done to the process. Do not treat the comment above as a conclusion.
        D3D11.D3D11CreateDevice(
            IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out var device, out _, out var context).CheckError();
        // MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER. Enumerating software first would find
        // Microsoft H.264 encoder, which advertises the right output type and then cannot be driven.
        using var found = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder, 0x00000004 | 0x00000040, null, null);

        var reasons = new List<string>();
        Console.WriteLine($"[mf-video] capture device feature level {device.FeatureLevel}");
        // ProbeFreshDevice is OFF. It is not a harmless diagnostic: it builds a complete second
        // hardware encoder, and this machine allows only one NVENC instance at a time. While the
        // probe held it, the real encoder below was created second and every one of its
        // ProcessInput calls was refused with MF_E_UNSUPPORTED_D3D_TYPE -- which reads exactly like a
        // device or format problem and is neither. The self-test below is enough; the probe can be
        // re-enabled deliberately, never by default.
        foreach (var activate in found)
        {
            MfVideoEncoder? candidate = null;
            try
            {
                candidate = Build(activate, h264, nv12, width, height, device, context);
                return candidate;
            }
            catch (Exception ex)
            {
                candidate?.Dispose();
                reasons.Add($"candidate: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "no usable H.264 Media Foundation encoder on this machine. Media Foundation needs a " +
            "registered hardware encoder; the software one that ships with Windows is present but " +
            "does not work. Tried: " + string.Join("; ", reasons));
    }

    /// <summary>
    /// Runs the same self-test on a device made here, in the same process, and prints the result.
    ///
    /// ScreenCapture's device is not a plain D3D11CreateDevice: it is wrapped out of a DXGI device with
    /// CreateDirect3D11DeviceFromDXGIDevice, which is what Windows.Graphics.Capture requires, and a
    /// device obtained that way is not guaranteed to have the same feature set as one created
    /// directly. The self-test on the capture device already failed, so this is the one comparison
    /// left that settles it: same code, same process, same moment, two devices. If this one passes,
    /// the encoder is fine and the capture device is what the MFT objects to.
    /// </summary>
    private static void ProbeFreshDevice(Guid h264, Guid nv12, int width, int height)
    {
        try
        {
            D3D11.D3D11CreateDevice(
                IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out var fresh, out _, out _).CheckError();
            using (fresh)
            {
                Console.WriteLine($"[mf-video] fresh device feature level {fresh.FeatureLevel}");
                var n = 0;
                foreach (var activate in MediaFactory.MFTEnumEx(
                    TransformCategoryGuids.VideoEncoder, 0x00000004 | 0x00000040, null, null))
                {
                    n++;
                    try
                    {
                        Build(activate, h264, nv12, width, height, fresh, fresh.ImmediateContext).Dispose();
                        Console.WriteLine($"[mf-video] FRESH-DEVICE PROBE PASSED on candidate #{n}: a synthetic " +
                                          "frame went through on a device created here, in this process. " +
                                          "The capture device is therefore what the MFT objects to.");
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Every candidate is reported, unfiltered. An earlier version tried to skip
                        // MF_E_INVALIDTYPE as "this is the HEVC encoder" and returned on anything else,
                        // which meant the probe could print a verdict after testing exactly one
                        // candidate -- and did, quietly, while looking conclusive.
                        Console.WriteLine($"[mf-video] fresh-device probe candidate #{n}: " +
                                          $"{ex.GetType().Name} {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[mf-video] fresh-device probe could not run: {ex.Message}");
        }
    }

    private static MfVideoEncoder Build(
        IMFActivate activate, Guid h264, Guid nv12, int width, int height,
        ID3D11Device device, ID3D11DeviceContext deviceContext)
    {
        var transform = activate.ActivateObject<IMFTransform>();

        // An async MFT refuses every call with MF_E_TRANSFORM_ASYNC_LOCKED until it is told to behave
        // synchronously. Without this nothing below works, and the error names nothing helpful.
        transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);

        // The caller's context, fetched a second time here would be an AddRef that Dispose balances by
        // releasing something the capture path still owns.
        var videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        var videoContext = deviceContext.QueryInterface<ID3D11VideoContext>();

        // A hardware encoder never touches system memory, so it needs a device and a manager before it
        // will negotiate anything.
        var manager = MediaFactory.MFCreateDXGIDeviceManager();
        manager.ResetDevice(device).CheckError();
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)manager.NativePointer);

        transform.SetOutputType(0, BuildType(h264, width, height), 0);
        var input = PickInput(transform, nv12, width, height)
            ?? throw new InvalidOperationException("no usable NV12 input type was offered.");

        transform.SetInputType(0, input, 0);
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)manager.NativePointer);
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        var enumerator = videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational(1, Fps),
            InputWidth = (uint)width,
            InputHeight = (uint)height,
            OutputFrameRate = new Rational(1, Fps),
            OutputWidth = (uint)width,
            OutputHeight = (uint)height,
            Usage = VideoUsage.OptimalSpeed,
        });
        var processor = videoDevice.CreateVideoProcessor(enumerator, 0);

        var cpuSource = device.CreateTexture2D(BgraDescription(width, height));
        var nv12Target = device.CreateTexture2D(Nv12Description(width, height, BindFlags.RenderTarget));
        // Staging, and not a detail: a Default-usage texture is rejected with
        // MF_E_UNSUPPORTED_D3D_TYPE on the very first ProcessInput, while a staging one is accepted.
        var nv12Staging = device.CreateTexture2D(
            Nv12Description(width, height, BindFlags.None, staging: true));

        // The spike filled this texture before the first frame and this class did not, which was the
        // one remaining step of the working sequence that had never been reproduced. Filled once
        // The spike filled this texture before the first frame and this class did not. Filled once
        // here, because an uninitialised staging surface is the one state in which
        // MF_E_UNSUPPORTED_D3D_TYPE is the documented answer.
        //
        // Not wrapped in a catch that prints and continues: a swallowed failure here would leave exactly
        // that uninitialised surface feeding the encoder, turning a loud and obviously-wrong surface
        // into a quiet stream of rejected frames.
        var grey = new byte[(width * height * 3) / 2];
        Array.Fill(grey, (byte)128);
        deviceContext.UpdateSubresource(grey.AsSpan(), nv12Staging, 0, (uint)width, 0, null);
        var sourceView = videoDevice.CreateVideoProcessorInputView(
            cpuSource, enumerator,
            new VideoProcessorInputViewDescription
            {
                // Zero, always, for an uncompressed DXGI format. FourCC is for compressed formats, and
                // putting the format numeric value here is a confident way to earn E_INVALIDARG.
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
        var targetView = videoDevice.CreateVideoProcessorOutputView(
            nv12Target, enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        // The manager was told about `device`, and the surface below is wrapped out of a texture on
        // `device`, so they must be the same object. If they ever are not, the MFT is handed a
        // surface belonging to a device it was never given, and it answers with
        // MF_E_UNSUPPORTED_D3D_TYPE -- which is indistinguishable, from the log, from a format
        // problem. Cheap to check, so it is checked.
        if (!ReferenceEquals(nv12Staging.Device, device) &&
            nv12Staging.Device.NativePointer != device.NativePointer)
            throw new InvalidOperationException(
                "the NV12 surface and the DXGI device manager are on different devices.");

        var surfaceBuffer = MediaFactory.MFCreateDXGISurfaceBuffer(
            typeof(ID3D11Texture2D).GUID, nv12Staging, 0, false);
        Console.WriteLine($"[mf-video] MFCreateDXGISurfaceBuffer accepted; encoding {width}x{height}");


        return new MfVideoEncoder(
            transform, device, deviceContext, videoDevice, videoContext, processor,
            sourceView, targetView, cpuSource, nv12Target, nv12Staging, surfaceBuffer, width, height);
    }


    private static IMFMediaType BuildType(Guid subtype, int width, int height)
    {
        // Not wrapped in using: SetOutputType needs the COM reference to still be alive afterwards.
        var type = MediaFactory.MFCreateMediaType();
        type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        type.Set(MediaTypeAttributeKeys.Subtype, subtype);
        // Frame size and frame rate are UINT64 attributes carrying a pair, high half first. Written as
        // plain ints they truncate silently rather than being rejected.
        type.Set(MediaTypeAttributeKeys.FrameSize, ((ulong)(uint)height << 32) | (uint)width);
        type.Set(MediaTypeAttributeKeys.FrameRate, (ulong)Fps << 32 | 1);
        type.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Bitrate);
        type.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);   // progressive
        type.Set(MediaTypeAttributeKeys.Compressed, true);
        return type;
    }

    private static IMFMediaType? PickInput(IMFTransform transform, Guid nv12, int width, int height)
    {
        for (var i = 0; i < 32; i++)
        {
            IMFMediaType type;
            try
            {
                type = transform.GetInputAvailableType(0, i);
            }
            catch
            {
                return null;   // off the end of the list, which is how it ends
            }

            if (FrameSize(type) == (width, height) && Subtype(type) == nv12)
                return type;
        }
        return null;
    }

    private static (int Width, int Height) FrameSize(IMFMediaType type)
    {
        try
        {
            var v = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            return ((int)(v & 0xffffffff), (int)(v >> 32));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static Guid Subtype(IMFMediaType type)
    {
        try { return type.GetGUID(MediaTypeAttributeKeys.Subtype); }
        catch { return Guid.Empty; }
    }

    private static Texture2DDescription BgraDescription(int width, int height) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    };

    private static Texture2DDescription Nv12Description(int width, int height, BindFlags bind, bool staging = false) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.NV12,
        SampleDescription = new SampleDescription(1, 0),
        Usage = staging ? ResourceUsage.Staging : ResourceUsage.Default,
        BindFlags = bind,
        CPUAccessFlags = staging ? CpuAccessFlags.Write : CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    };
}
