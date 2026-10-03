using System.Buffers;
using Vortice.Direct3D11;

namespace Clippy;

/// <summary>
/// The live video path: a Windows.Graphics.Capture texture goes straight into NVENC, and the H.264
/// comes back out of the silicon without an ffmpeg process and without the pixels ever reaching
/// system memory.
/// </summary>
/// <remarks/
/// This exists because the two obvious ways of encoding video both fail on this machine. ffmpeg spawns
/// a process and needs a CPU copy per frame, and the Media Foundation path produced no video at all
/// once a capture session was live. The native bridge in <c>src/native</c> takes a D3D11 texture
/// directly, which is only possible because the capture and the encoder are given the *same*
/// D3D11 device -- <c>ScreenCapture</c> builds one device and hands it to both.
/// <para>
/// Threading: every method here is called from the single Windows.Graphics.Capture callback thread
/// under that class's own lock. The Annex B parser keeps state between calls and is not thread-safe,
/// so that single caller is a requirement rather than an accident.
/// </para>
/// </remarks>
internal sealed unsafe class NvencDirectVideoEncoder : IVideoEncoder
{
    /// <summary>One encoded frame, in the worst case, for 4K at a high bitrate.</summary>
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    private void* context;
    private readonly bool hevc;
    private readonly AnnexBParser parser;
    private readonly Queue<double> captureTimes = new();
    private readonly object stateLock = new();

    private RingBuffer? ring;
    private byte[]? sps;
    private byte[]? pps;
    private byte[]? vps;
    private byte[]? parameterSets;          // SPS and PPS, prepended to the first keyframe
    private bool parametersPrepended;      // so they go in once, not on every IDR
    private int captureTimeCount;
    private int accessUnitCount;
    private bool disposed;

    // Reused across frames so that a GC pause never lands inside the encode the timing is measured
    // on, and so that a 4K frame does not allocate 16 MB sixty times a second.
    private readonly byte[] frameBytes = new byte[MaxFrameBytes];

    private NvencDirectVideoEncoder(void* ctx, bool hevc)
    {
        context = ctx;
        this.hevc = hevc;
        // The parser needs the codec up front: H.264 and HEVC frame the stream identically and
        // differ only in the NAL header, so one flag here decides how every later byte is read.
        parser = new AnnexBParser(hevc);
    }

    /// <summary>
    /// Opens an NVENC session on the given device, or returns null if there is no usable one.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception, because "this machine has no NVIDIA encoder" is a normal
    /// condition to fall back from and not a fault. The native side already covers the ordinary
    /// reasons with specific messages -- no driver, no DLL, driver too old, GPU cannot do H.264 --
    /// and the caller prints them and starts ffmpeg instead.
    /// <para>
    /// The device must be the same one the capture frames come from. Two different D3D11 devices
    /// cannot copy between each other, so a session opened on some other device would force the
    /// pixels back through system memory and give up the entire point of this class.
    /// </para>
    /// </remarks>
    /// <param name="hevc">True to encode H.265 instead of H.264.</param>
    public static NvencDirectVideoEncoder? TryCreate(
        ID3D11Device device, int width, int height, int fps, int bitrateBps, bool hevc = false)
    {
        if (width <= 0 || height <= 0 || fps <= 0 || bitrateBps <= 0)
        {
            Console.WriteLine($"[nvenc-direct] refusing to open with {width}x{height} @ {fps}fps.");
            return null;
        }

        void* ctx = null;
        int rc;
        try
        {
            rc = NvencNative.Open((void*)device.NativePointer, width, height, fps, bitrateBps, hevc, &ctx);
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("[nvenc-direct] clippy_nvenc.dll is not next to Clippy.exe.");
            return null;
        }
        catch (EntryPointNotFoundException ex)
        {
            Console.WriteLine($"[nvenc-direct] clippy_nvenc.dll is stale or incomplete: {ex.Message}");
            return null;
        }

        if (rc != 0 || ctx is null)
        {
            Console.WriteLine($"[nvenc-direct] NVENC unavailable -> {NvencNative.ErrorText}");
            return null;
        }

        var encoder = new NvencDirectVideoEncoder(ctx, hevc);
        encoder.ReadParameterSets();
        Console.WriteLine($"[nvenc-direct] NVENC session open at {width}x{height} @ {fps}fps, " +
                          $"{bitrateBps / 1_000_000} Mbit/s, codec {(hevc ? "HEVC (H.265)" : "H.264")}. " +
                          "No ffmpeg process will be started.");
        return encoder;
    }

    /// <summary>
    /// Fetches the parameter sets and splits them out for the muxer.
    /// </summary>
    /// <remarks>
    /// NVENC does not put the SPS or the PPS in the bitstream -- the observed stream carries picture
    /// NALs only -- so both have to be asked for. They are needed twice over: <see cref="Sps"/> and
    /// <see cref="Pps"/> build the avcC configuration record, and the same bytes are prepended to the
    /// first keyframe so the ring holds a self-contained elementary stream. A ring without them
    /// slices into a file no player will open.
    /// </remarks>
    private void ReadParameterSets()
    {
        var raw = NvencNative.GetSequenceParams(context);
        if (raw is null || raw.Length == 0)
        {
            Console.WriteLine($"[nvenc-direct] the driver produced no sequence parameters: " +
                              $"{NvencNative.ErrorText}");
            return;
        }

        parameterSets = raw;
        var parser = new AnnexBParser(hevc);
        parser.Append(raw, 0.0, CaptureParameterSet);
        // Flush, not just Append: the parser holds the final NAL back until something follows it or
        // the stream ends. The last parameter set is the final unit in this buffer, so without the
        // flush it is never emitted and the muxer gets an hvcC record with a missing array.
        parser.Flush(0.0, CaptureParameterSet);
        Console.WriteLine($"[params] NVENC sequence parameters: {raw.Length} bytes, " +
                          $"{(hevc ? $"VPS {(vps is null ? "missing" : vps.Length.ToString())} B, " : "")}" +
                          $"SPS {(sps is null ? "missing" : sps.Length.ToString())} B, " +
                          $"PPS {(pps is null ? "missing" : pps.Length.ToString())} B");
    }

    /// <summary>
    /// Files one parameter set, by NAL type, for whichever codec this session is running. H.264 uses
    /// 7 and 8; HEVC uses 32, 33 and 34, and all three are needed -- a container missing the VPS is
    /// one players reject.
    /// </summary>
    private void CaptureParameterSet(byte[] nal, int length, bool _, double __)
    {
        var type = NalType(nal);
        if (hevc)
        {
            if (type == HevcNal.Vps && vps is null)
                vps = nal;
            else if (type == HevcNal.Sps && sps is null)
                sps = nal;
            else if (type == HevcNal.Pps && pps is null)
                pps = nal;
        }
        else if (type == 7 && sps is null)
        {
            sps = nal;
        }
        else if (type == 8 && pps is null)
        {
            pps = nal;
        }
    }

    /// <summary>nal_unit_type for this session's codec, from the right header layout.</summary>
    private int NalType(ReadOnlySpan<byte> nal) =>
        hevc ? HevcNal.Type(nal) : nal[0] & 0x1F;

    /// <summary>True when this NAL is a picture: types 1-5 for H.264, 0-31 (VCL) for HEVC.</summary>
    private bool IsPicture(int type) => hevc ? HevcNal.IsVcl(type) : type is >= 1 and <= 5;

    /// <summary>
    /// Encodes one capture texture and pushes the resulting access unit into the ring.
    /// </summary>
    /// <remarks>
    /// The texture is handed to NVENC where it already lives. There is no staging copy and no read
    /// back: that is the whole reason this class exists, and the reason the device passed to
    /// <see cref="TryCreate"/> has to be the capture's own device.
    /// <para>
    /// The native call is synchronous -- it queues the frame and then waits for the bitstream before
    /// returning -- so the bytes are in hand here and no drain thread is needed. The frame's own
    /// capture time has already been queued by <see cref="EnqueueCaptureTime"/>.
    /// </para>
    /// </remarks>
    public void PushTexture(ID3D11Texture2D texture)
    {
        if (disposed || context is null)
            return;
        if (ring is null)
        {
            // Pushing before StartDrain would drop frames on the floor with nothing to show for it.
            // The capture code starts the drain before frames flow, so this only fires on a wiring bug.
            Console.WriteLine("[nvenc-direct] a frame arrived before StartDrain; dropping it.");
            return;
        }

        // The capture time is read, not consumed, here: the parser hands the same value to every NAL
        // of the access unit, and it is the VCL NAL on the way out that dequeues for the ring.
        double captureSeconds;
        lock (stateLock)
        {
            captureSeconds = captureTimes.Count > 0 ? captureTimes.Peek() : 0.0;
        }

        int isKey = 0;
        int written;
        fixed (byte* dst = frameBytes)
        {
            long ts = (long)(captureSeconds * 10_000_000.0);
            written = NvencNative.Encode(context, (void*)texture.NativePointer, ts,
                                         dst, frameBytes.Length, &isKey);
        }

        if (written < 0)
        {
            Console.WriteLine($"[nvenc-direct] encode failed: {NvencNative.ErrorText}");
            return;
        }
        if (written == 0)
        {
            Console.WriteLine("[nvenc-direct] the encoder produced no bytes for a frame.");
            return;
        }

        // The parameter sets go in ahead of the first keyframe so the ring holds a stream that can
        // stand on its own. Only once: repeating them before every IDR would grow the file for no
        // gain, and a decoder that has seen them once will not need them again.
        if (!parametersPrepended && parameterSets is not null)
        {
            parser.Append(parameterSets, captureSeconds, Push);
            parametersPrepended = true;
        }

        // The same parser the ffmpeg path uses, on the same callback signature. Reusing it rather
        // than splitting NAL units here is what keeps one definition of "what a VCL NAL is" in the
        // project, and that definition is what the A/V timeline is built on.
        parser.Append(frameBytes.AsSpan(0, written), captureSeconds, Push);
    }

    /// <summary>One NAL unit leaving the parser.</summary>
    private void Push(byte[] data, int length, bool isIdr, double parserTime)
    {
        var type = NalType(data);
        CaptureParameterSet(data, length, isIdr, parserTime);

        // Only a picture NAL is a picture, so only a picture NAL consumes a capture time. Parameter
        // sets and SEI travel with whatever picture follows them and must not shift the timeline --
        // the same rule the ffmpeg drain applies, and for the same reason. The test differs by codec:
        // H.264 slices are types 1-5, HEVC slices are every VCL type from 0 to 31.
        var isVcl = IsPicture(type);
        // A non-VCL NAL carries the time the parser saw it, exactly as the ffmpeg drain does. It must
        // not be stamped 0: RingBuffer.Slice selects by capture time, and a parameter set sitting
        // at zero falls outside every export window, so the clipped stream loses its own avcC data.
        var captureSeconds = parserTime;
        if (isVcl)
        {
            lock (stateLock)
            {
                if (captureTimes.Count > 0)
                    captureSeconds = captureTimes.Dequeue();
                var m = accessUnitCount++;
                if (m < 10)
                    Console.WriteLine($"[vq] out #{m} ts={captureSeconds:F4} remaining={captureTimes.Count}");
            }
        }

        var rented = ArrayPool<byte>.Shared.Rent(length);
        data.CopyTo(rented, 0);
        ring!.Push(rented, length, captureSeconds, isIdr);
    }

    public void EnqueueCaptureTime(double captureClockSeconds)
    {
        lock (stateLock)
        {
            var n = captureTimeCount++;
            if (n < 10)
                Console.WriteLine($"[vq] in  #{n} ts={captureClockSeconds:F4} depth={captureTimes.Count}");
            captureTimes.Enqueue(captureClockSeconds);
        }
    }

    public (int Captured, int AccessUnits) TimingCounts
    {
        get { lock (stateLock) { return (captureTimeCount, accessUnitCount); } }
    }

    public void StartDrain(RingBuffer target) => ring = target;

    public byte[]? Sps => sps;
    public byte[]? Pps => pps;

    /// <summary>HEVC only; null in an H.264 session, which is what makes the muxer write avc1.</summary>
    public byte[]? Vps => vps;

    /// <summary>
    /// Nothing to wait for.
    /// </summary>
    /// <remarks>
    /// The ffmpeg path has to block here because its input is probed by reading a frame, so it cannot
    /// report readiness before the first write. This path opens its session synchronously and encodes
    /// on the caller's thread, so it is ready the moment it is constructed.
    /// </remarks>
    public bool WaitForReady(TimeSpan timeout) => !disposed;

    /// <summary>
    /// Blocks until every frame handed in has been pushed to the ring.
    /// </summary>
    /// <remarks>
    /// A no-op beyond the disposed check, and deliberately so. Encoding happens inside
    /// <see cref="PushTexture"/> and the ring is written there too, so by the time capture stops
    /// there is no work in flight anywhere. Exporting reads the ring directly, and trimming it while
    /// an encode was still appending is the failure this call exists to prevent.
    /// </remarks>
    public void WaitForDrain(TimeSpan timeout)
    {
    }

    /// <summary>
    /// Present because the interface speaks in CPU pixels; never called on this path.
    /// </summary>
    /// <remarks>
    /// Throwing rather than copying. Silently accepting a <c>byte[]</c> here would mean the readback
    /// this class exists to remove had come back, and the recording would look fine while costing
    /// exactly what it did before.
    /// </remarks>
    public void Write(byte[] bgraPixels, double systemTimeMs) =>
        throw new NotSupportedException(
            "NvencDirectVideoEncoder encodes the capture texture in VRAM. " +
            "PushTexture must be used; Write would require a CPU readback.");

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        // The last NAL of the stream is still held by the parser, so it is flushed here. Losing it
        // costs the final picture of every recording, which is the one a user replays to check
        // that the export worked.
        if (ring is not null)
            parser.Flush(0.0, Push);
        if (context is not null)
        {
            NvencNative.Close(context);
            context = null;
        }
    }
}
