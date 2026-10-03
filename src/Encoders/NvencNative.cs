using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Clippy;

/// <summary>
/// Managed side of the native NVENC bridge, plus the spike that proves it works.
/// </summary>
/// <remarks>
/// Everything crossing this boundary is an opaque pointer or a flat scalar. None of NVIDIA's
/// structures are declared here, which is the whole reason the bridge exists: the API decides a
/// structure's layout from the version number carried inside it, so a C# transcription that drifts from
/// the header does not fail cleanly -- it silently misaligns. The compiler in <c>src/native</c> checks
/// the usage against the SDK's real header instead.
///
/// The C# side still has to own the D3D11 device, because the texture has to be a real device texture
/// for NVENC to read it in place. That is done with Vortice, which is already referenced.
/// </remarks>
internal static unsafe partial class NvencNative
{
    private const string Library = "clippy_nvenc.dll";

    [LibraryImport(Library)]
    private static partial int Nvenc_Open(void* device, int width, int height, int fps, int bitrate,
                                         int hevc, void** context);

    [LibraryImport(Library)]
    private static partial int Nvenc_EncodeTexture(void* context, void* texture, long timestamp100ns,
                                                   byte* output, int maxOutSize, int* isKeyframe);

    [LibraryImport(Library)]
    private static partial void Nvenc_Close(void* context);

    [LibraryImport(Library)]
    private static partial int Nvenc_GetSequenceParams(void* context, byte* outBuffer, int maxOut, int* outLength);

    [LibraryImport(Library)]
    private static partial byte* Nvenc_LastError();

    [LibraryImport(Library)]
    private static partial uint Nvenc_MaxVersion();

    private static string LastError()
    {
        var p = Nvenc_LastError();
        return p is null ? "(no message)" : Marshal.PtrToStringAnsi((nint)p) ?? "(no message)";
    }
    internal static uint MaxVersion() => Nvenc_MaxVersion();

    internal static string ErrorText => LastError();

    internal static int Open(void* device, int width, int height, int fps, int bitrate, bool hevc, void** context)
        => Nvenc_Open(device, width, height, fps, bitrate, hevc ? 1 : 0, context);

    internal static int Encode(void* context, void* texture, long timestamp100ns,
                               byte* output, int maxOutSize, int* isKeyframe)
        => Nvenc_EncodeTexture(context, texture, timestamp100ns, output, maxOutSize, isKeyframe);

    internal static void Close(void* context) => Nvenc_Close(context);

    /// <summary>
    /// The parameter sets, in Annex B, as the driver holds them: SPS and PPS for H.264, VPS+SPS+PPS
    /// for HEVC, which arrives as one block in both cases.
    /// </summary>
    /// <remarks>
    /// Returned separately because NVENC does not put them in the bitstream: the observed stream
    /// contains picture NALs only, and an elementary stream with no parameter sets cannot be muxed.
    /// Null when the driver declined to produce them.
    /// </remarks>
    internal static byte[]? GetSequenceParams(void* context)
    {
        var buffer = new byte[4096];
        int length = 0;
        fixed (byte* p = buffer)
        {
            if (Nvenc_GetSequenceParams(context, p, buffer.Length, &length) != 0 || length <= 0)
                return null;
        }
        return buffer.AsSpan(0, length).ToArray();
    }
}
/// <summary>The <c>--test-nvenc-native</c> spike.</summary>
internal static unsafe class NvencNativeSpike
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int Fps = 30;
    private const int Frames = 30;
    private const int Bitrate = 8_000_000;

    public static int Run()
    {
        Console.WriteLine($"clippy_nvenc.dll, built against NVENC API {NvencNative.MaxVersion():X}");
        Console.WriteLine($"target {Width}x{Height} @ {Fps}fps, {Bitrate / 1_000_000} Mbit/s, {Frames} frames");
        Console.WriteLine();

        D3D11.D3D11CreateDevice(
            IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out ID3D11Device device, out _, out ID3D11DeviceContext context).CheckError();
        Console.WriteLine("[ok] D3D11 device created");

        // CPU_ACCESS_WRITE so the pattern can be pushed straight into the texture. NVENC reads the
        // texture where it already sits, so nothing is uploaded twice and there is no staging copy --
        // that was the point of dropping the media-foundation path.
        var desc = new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
        };
        ID3D11Texture2D texture = device.CreateTexture2D(desc);
        Console.WriteLine("[ok] BGRA texture created");

        void* ctx = null;
        int rc = NvencNative.Open((void*)device.NativePointer, Width, Height, Fps, Bitrate, hevc: false, &ctx);
        if (rc != 0)
        {
            Console.WriteLine($"[FAIL] Nvenc_Open returned {rc}: {NvencNative.ErrorText}");
            return 1;
        }
        Console.WriteLine("[ok] NVENC session open (H.264, preset P4, low-latency tuning, CBR, 1 IDR per second)");

        var outBuf = new byte[8 * 1024 * 1024];
        int totalBytes = 0, framesOut = 0, keyframes = 0, emptyFrames = 0;

        // Reused across frames: a per-frame allocation here would put GC pauses in the middle of the
        // timing the encoder is being measured on.
        var parser = new AnnexBParser();
        int nalCount = 0, sliceNalCount = 0;
        int capturedBytes = 0;

        for (int i = 0; i < Frames; i++)
        {
            DrawFrame(context, texture, i);

            int isKey = 0;
            int written;
            fixed (byte* dst = outBuf)
            {
                long ts = (long)i * 10_000_000L / Fps;    // 100ns units
                written = NvencNative.Encode(ctx, (void*)texture.NativePointer, ts, dst, outBuf.Length, &isKey);
            }

            if (written < 0)
            {
                Console.WriteLine($"[FAIL] frame {i}: Nvenc_EncodeTexture returned {written}: {NvencNative.ErrorText}");
                NvencNative.Close(ctx);
                return 1;
            }
            if (written == 0) { emptyFrames++; continue; }

            framesOut++;
            totalBytes += written;
            if (isKey != 0) keyframes++;

            if (i == 0)
            {
                Console.WriteLine($"[ok] frame 0: {written:N0} bytes, first four = {outBuf[0]:X2} {outBuf[1]:X2} {outBuf[2]:X2} {outBuf[3]:X2}");
                bool annexB = outBuf[0] == 0 && outBuf[1] == 0 && outBuf[2] == 0 && outBuf[3] == 1;
                if (!annexB)
                {
                    Console.WriteLine("[FAIL] stream does not begin with a 4-byte Annex B start code");
                    NvencNative.Close(ctx);
                    return 1;
                }
            }

            // The real check. Bytes that merely look like H.264 are not the question; bytes the
            // project's own parser turns back into NAL units are.
            capturedBytes += written;
            parser.Append(outBuf.AsSpan(0, written), i / (double)Fps, (nal, len, isSlice, _) =>
            {
                nalCount++;
                if (isSlice) sliceNalCount++;
            });
        }

        parser.Flush((double)Frames / Fps, (_, _, _, _) => nalCount++);

        NvencNative.Close(ctx);
        Console.WriteLine("[ok] session closed");
        Console.WriteLine();
        Console.WriteLine($"frames in       {Frames}");
        Console.WriteLine($"frames out      {framesOut}   (empty: {emptyFrames})");
        Console.WriteLine($"keyframes       {keyframes}");
        Console.WriteLine($"bytes encoded   {totalBytes:N0}   (mean {totalBytes / Math.Max(1, framesOut):N0} per frame)");
        Console.WriteLine($"bitrate actual  {totalBytes * 8.0 / Frames:F0} bit/s");
        Console.WriteLine($"NAL units       {nalCount}   (VCL: {sliceNalCount})");

        Console.WriteLine();
        bool pass = framesOut == Frames && nalCount > 0 && sliceNalCount > 0 && keyframes >= 1;
        Console.WriteLine(pass
            ? $"[PASS] {framesOut}/{Frames} frames encoded, {nalCount} NAL units parsed by AnnexBParser"
            : "[FAIL] see the counts above");
        return pass ? 0 : 1;
    }

    /// <summary>
    /// Writes a moving pattern into the texture.
    /// </summary>
    /// <remarks>
    /// A flat colour would compress to almost nothing, which would leave the encode path untested --
    /// the interesting behaviour is what happens when there is real work for the encoder, and a
    /// near-empty frame also hides ordering bugs because every frame looks identical.
    /// </remarks>
    private static void DrawFrame(ID3D11DeviceContext context, ID3D11Texture2D texture, int frame)
    {
        // MapMode.Write, not WriteDiscard: WriteDiscard is only legal on a dynamic texture, and this
        // one has to be staging. A dynamic texture would live in system memory, which NVENC cannot
        // read as a D3D texture -- the whole point of this path is that the pixels never leave VRAM.
        var mapping = context.Map(texture, 0, MapMode.Write, MapFlags.None);
        int shift = frame * 4;
        for (int y = 0; y < Height; y++)
        {
            var row = (uint*)(mapping.DataPointer + (nint)y * mapping.RowPitch);
            for (int x = 0; x < Width; x++)
            {
                // B, G, R, A -- BGRA byte order.
                byte b = (byte)((x + shift) & 0xFF);
                byte g = (byte)((y * 2) & 0xFF);
                byte r = (byte)(((x ^ y) + shift) & 0xFF);
                row[x] = (uint)(b | (g << 8) | (r << 16) | (0xFFu << 24));
            }
        }
        context.Unmap(texture, 0);
    }
}
