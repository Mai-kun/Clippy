using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Clippy;

internal sealed class ScreenCapture : IDisposable
{
    private const int FrameSaveInterval = 30;
    private const int FramePoolBufferCount = 2;

    private readonly object sync = new();
    private readonly ID3D11Device d3d11Device;
    private readonly ID3D11DeviceContext d3d11Context;
    private readonly IDirect3DDevice direct3DDevice;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;
    private readonly string outputDirectory;
    private readonly ManualResetEventSlim stopped = new(false);
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    // Video mode: ffmpeg is started lazily on the first frame, when the true capture size is known.
    // ffmpeg stamps frames on read, so no warm-up buffer and no frame-rate guess are needed.
    private string? videoPath;
    private string? videoEncoder;
    private string fpsMode = "passthrough";
    private bool withAudio;
    private AudioCapture? audio;
    private AudioEncoder? audioEncoder;
    private string? videoOnlyPath;
    private string? audioOnlyPath;
    private bool audioCaptureOnly;
    private int discardedAudioBuffers;
    private FfmpegEncoder? encoder;

    private TimeSpan lastFrameTime;
    private TimeSpan lastFpsLogTime;
    private int framesSinceFpsLog;
    private int frameCount;
    private bool disposed;

    private ScreenCapture(string outputDirectory)
    {
        this.outputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);

        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Windows.Graphics.Capture is unavailable on this system.");

        d3d11Device = CreateD3D11Device(out d3d11Context);
        direct3DDevice = CaptureInterop.CreateDirect3DDevice(d3d11Device);
        item = CaptureInterop.CreateItemForPrimaryMonitor();
        framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            FramePoolBufferCount,
            item.Size);
        session = framePool.CreateCaptureSession(item);

        framePool.FrameArrived += OnFrameArrived;
        item.Closed += OnItemClosed;
    }

    public static int Run(TimeSpan? duration)
    {
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "output");
        using var capture = new ScreenCapture(outputDirectory);
        return capture.Capture(duration);
    }

    /// <summary>Records a real video file: every captured frame is piped to ffmpeg.</summary>
    public static int RunVideo(TimeSpan duration, string outputPath, string encoderName, string fpsMode = "passthrough", bool withAudio = false, bool audioCaptureOnly = false)
    {
        using var capture = new ScreenCapture(Path.GetDirectoryName(outputPath)!)
        {
            videoPath = outputPath,
            videoEncoder = encoderName,
            fpsMode = fpsMode,
            withAudio = withAudio,
            audioCaptureOnly = audioCaptureOnly,
        };
        return capture.Capture(duration);
    }

    private int Capture(TimeSpan? duration)
    {
        Console.WriteLine($"Capture output: {outputDirectory}");
        Console.WriteLine("Waiting for frames from Windows.Graphics.Capture.");

        // One trace for every thread of this recording, all stamped from the same Stopwatch.
        EventLog.Clock = stopwatch;
        EventLog.Start(Path.Combine(outputDirectory, $"events-{DateTime.Now:yyyyMMdd-HHmmssfff}.csv"));

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Stop("Ctrl+C received");
        };
        Console.CancelKeyPress += cancelHandler;

        if (videoPath is not null)
        {
            Console.WriteLine($"Recording {videoPath} [{videoEncoder}]");
        }

        try
        {
            session.StartCapture();
            using var timer = duration is { } timeout
                ? new Timer(_ => Stop($"capture duration {timeout.TotalSeconds:F0}s elapsed"), null, timeout, Timeout.InfiniteTimeSpan)
                : null;

            stopped.Wait();
            Console.WriteLine($"Capture stopped after {frameCount} frames.");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;

            // Stop the callback first: once the session is disposed no new frame can be delivered, so
            // taking the lock afterwards is bounded instead of racing an in-flight Write.
            framePool.FrameArrived -= OnFrameArrived;

            // Tear down INSIDE the same lock OnFrameArrived uses. Doing it outside let Dispose race a
            // concurrent Write -- closing stdin while another thread writes to it -- which is exactly
            // the kind of timing-dependent fault that makes a run pass or hang at random.
            lock (sync)
            {
                disposed = true;

                // Audio first: it feeds the audio ffmpeg's stdin, which must close before it exits.
                audio?.Dispose();
                audio = null;
                audioEncoder?.Dispose();
                audioEncoder = null;

                // Close ffmpeg's stdin after the last frame so it finalises the mp4 container.
                encoder?.Dispose();
                encoder = null;
            }

            if (withAudio && videoOnlyPath is not null && audioOnlyPath is not null)
                MuxVideoAndAudio(videoOnlyPath, audioOnlyPath, videoPath!);
            else if (audioCaptureOnly)
                Console.WriteLine($"Bisection run: {discardedAudioBuffers} WASAPI buffers received and discarded; no mux.");
        }
    }

    /// <summary>
    /// Combines the two recordings into the final file.
    ///
    /// No -itsoffset: both ffmpeg processes stamped their packets with the same system clock
    /// (-use_wallclock_as_timestamps) starting at the same instant, so both files already share one
    /// zero. An offset here would paper over a real misalignment instead of measuring it, so the
    /// start_time of each input is compared instead and reported.
    /// </summary>
    private void MuxVideoAndAudio(string videoInput, string audioInput, string output)
    {
        if (!File.Exists(audioInput))
        {
            Console.WriteLine("Mux skipped: no audio file was produced.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", videoInput,
            "-i", audioInput,
            // -c copy: both streams are already encoded, and re-encoding here would add delay.
            // -shortest: without it a long silent tail would pad the file.
            "-map", "0:v:0", "-map", "1:a:0",
            "-c", "copy", "-shortest",
            "-movflags", "+faststart",
            output,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for muxing.");
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Mux ffmpeg did not exit within 120s and was killed.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Mux ffmpeg exited with {process.ExitCode}: {stderr.Trim()}");

        // Report the two start times so A/V alignment is a measured number, not an assumption.
        var starts = new[] { videoInput, audioInput }
            .Select(path => $"{(Path.GetFileNameWithoutExtension(path))}={ReadStartSeconds(path):F4}s")
            .ToArray();
        Console.WriteLine($"Muxed -> {output} (stream start times: {string.Join(", ", starts)})");
    }

    private static double ReadStartSeconds(string path)
    {
        var probe = new ProcessStartInfo
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "-v", "error",
            "-show_entries", "format=start_time",
            "-of", "csv=p=0",
            path,
        })
        {
            probe.ArgumentList.Add(argument);
        }

        using var process = Process.Start(probe)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return double.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
    {
        EventLog.Mark("FB_ENTER");
        lock (sync)
        {
            EventLog.Mark("FB_LOCK");
            if (disposed)
                return;

            using var frame = sender.TryGetNextFrame();
            if (frame is null)
                return;

            if (frame.ContentSize != item.Size)
            {
                sender.Recreate(
                    direct3DDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    FramePoolBufferCount,
                    frame.ContentSize);
                Console.WriteLine($"Capture size changed to {frame.ContentSize.Width}x{frame.ContentSize.Height}.");
                return;
            }

            var currentFrame = ++frameCount;
            LogFrameTiming();

            if (videoPath is not null)
            {
                var videoTexture = CaptureInterop.GetTexture(frame.Surface);
                var pixels = CopyTextureToCpu(videoTexture, out var videoWidth, out var videoHeight);
                videoTexture.Dispose();

                if (encoder is null)
                {
                    if (withAudio)
                    {
                        // Bisection mode: real WASAPI buffers arrive and the callback really runs,
                        // but no audio ffmpeg exists and nothing is written anywhere. Isolates
                        // "Clippy's own threads upset video capture" from "two ffmpeg processes".
                        // The video ffmpeg writes the video-only file in every audio mode, so the mux
                        // inputs exist regardless of whether audio is encoded or merely discarded.
                        videoOnlyPath = Path.ChangeExtension(videoPath, ".video.mp4");

                        if (audioCaptureOnly)
                        {
                            audio = new AudioCapture(stopwatch);
                            audio.Sink = (buffer, qpc) => Interlocked.Increment(ref discardedAudioBuffers);
                            audioEncoder = null;
                            audioOnlyPath = null;
                            Console.WriteLine($"Audio: capture-only [{audio.Format}], buffers will be discarded");
                        }
                        else
                        {
                        audioOnlyPath = Path.ChangeExtension(videoPath, ".audio.m4a");
                        audio = new AudioCapture(stopwatch);
                        audioEncoder = AudioEncoder.Start(audioOnlyPath, audio, stopwatch);
                        audio.Sink = (buffer, qpc) => audioEncoder!.Write(buffer, qpc);
                        // An f32le input probes without data, so audio reaches ready on its own.
                        audioEncoder.WaitForReady(TimeSpan.FromSeconds(15));
                        Console.WriteLine($"Audio: system loopback [{audio.Format}] -> {Path.GetFileName(audioOnlyPath)}");
                        }

                        encoder = FfmpegEncoder.Start(
                            videoOnlyPath,
                            videoWidth,
                            videoHeight,
                            videoEncoder ?? "h264_nvenc",
                            stopwatch,
                            fpsMode);
                        Console.WriteLine($"Encoding {videoWidth}x{videoHeight} -> {Path.GetFileName(videoOnlyPath)}");
                    }
                    else
                    {
                        encoder = FfmpegEncoder.Start(
                            videoPath,
                            videoWidth,
                            videoHeight,
                            videoEncoder ?? "h264_nvenc",
                            stopwatch,
                            fpsMode);
                        Console.WriteLine($"Encoding {videoWidth}x{videoHeight} -> {videoPath}");
                    }
                }

                // SystemRelativeTime is Windows' own capture timestamp: the same clock ffmpeg reads
                // -use_wallclock_as_timestamps against, so it is the reference for PTS verification.
                var isFirstFrame = currentFrame == 1;
                encoder.Write(pixels, frame.SystemRelativeTime.TotalMilliseconds);

                // The first write is what lets ffmpeg finish probing and open its input; only then is
                // it meaningful to wait for readiness. Both processes are confirmed up before any
                // further frames go in, so neither can miss early data.
                if (isFirstFrame)
                {
                    encoder.WaitForReady(TimeSpan.FromSeconds(15));
                    if (audio is not null)
                    {
                        audio.Start();
                        Console.WriteLine("Both encoders are reading; recording.");
                    }
                }

                return;
            }

            if (currentFrame % FrameSaveInterval != 0)
                return;

            using var texture = CaptureInterop.GetTexture(frame.Surface);
            var fileName = Path.Combine(outputDirectory, $"frame-{currentFrame:D6}-{DateTime.Now:yyyyMMdd-HHmmssfff}.png");
            SavePng(texture, fileName);
            Console.WriteLine($"Saved {Path.GetFileName(fileName)}.");
        }
    }

    private void OnItemClosed(GraphicsCaptureItem _, object __)
    {
        Stop("capture item closed");
    }

    private void LogFrameTiming()
    {
        var now = stopwatch.Elapsed;
        if (lastFrameTime != TimeSpan.Zero)
        {
            var interval = now - lastFrameTime;
            framesSinceFpsLog++;

            var elapsedSinceLog = now - lastFpsLogTime;
            if (elapsedSinceLog >= TimeSpan.FromSeconds(1))
            {
                Console.WriteLine($"Capture FPS: {framesSinceFpsLog / elapsedSinceLog.TotalSeconds:F1}; last interval: {interval.TotalMilliseconds:F1} ms.");
                framesSinceFpsLog = 0;
                lastFpsLogTime = now;
            }
        }

        lastFrameTime = now;
    }

    private void Stop(string reason)
    {
        if (stopped.IsSet)
            return;

        Console.WriteLine($"Stopping capture: {reason}.");
        stopped.Set();
    }

    /// <summary>Stages a GPU texture into system RAM as tightly packed BGRA rows.</summary>
    private byte[] CopyTextureToCpu(ID3D11Texture2D texture, out int width, out int height)
    {
        var description = texture.Description;
        var stagingDescription = description;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDescription.MiscFlags = ResourceOptionFlags.None;

        using var stagingTexture = d3d11Device.CreateTexture2D(stagingDescription);
        d3d11Context.CopyResource(stagingTexture, texture);

        var mapped = d3d11Context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            width = checked((int)description.Width);
            height = checked((int)description.Height);
            var rowLength = checked(width * 4);
            var pixels = new byte[checked(rowLength * height)];
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    IntPtr.Add(mapped.DataPointer, checked(row * (int)mapped.RowPitch)),
                    pixels,
                    row * rowLength,
                    rowLength);
            }

            return pixels;
        }
        finally
        {
            d3d11Context.Unmap(stagingTexture, 0);
        }
    }

    private void SavePng(ID3D11Texture2D texture, string path)
    {
        var pixels = CopyTextureToCpu(texture, out var width, out var height);
        PngWriter.Write(path, width, height, pixels);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;

            disposed = true;
            item.Closed -= OnItemClosed;
            framePool.FrameArrived -= OnFrameArrived;
            encoder?.Dispose();
            encoder = null;
            session.Dispose();
            framePool.Dispose();
            direct3DDevice.Dispose();
            d3d11Context.Dispose();
            d3d11Device.Dispose();
            stopped.Dispose();
        }
    }

    private static ID3D11Device CreateD3D11Device(out ID3D11DeviceContext context)
    {
        var result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out var device,
            out _,
            out context);

        if (result.Failure)
            throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Description}");

        return device;
    }
}

internal static partial class CaptureInterop
{
    private const uint MonitorDefaultToPrimary = 1;

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    public static GraphicsCaptureItem CreateItemForPrimaryMonitor()
    {
        var monitor = MonitorFromPoint(new Point { X = 0, Y = 0 }, MonitorDefaultToPrimary);
        if (monitor == 0)
            throw new InvalidOperationException("Primary HMONITOR was not found.");

        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();

        ThrowIfFailed(interop.CreateForMonitor(monitor, GraphicsCaptureItemIid, out var item));
        try
        {
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item)!;
        }
        finally
        {
            MarshalInspectable<GraphicsCaptureItem>.DisposeAbi(item);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3d11Device)
    {
        using var dxgiDevice = d3d11Device.QueryInterface<IDXGIDevice>();
        ThrowIfFailed(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var device));
        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(device)!;
        }
        finally
        {
            MarshalInspectable<IDirect3DDevice>.DisposeAbi(device);
        }
    }

    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        ThrowIfFailed(access.GetInterface(Texture2DIid, out var texture));
        return new ID3D11Texture2D(texture);
    }

    private static void ThrowIfFailed(int hresult)
    {
        if (hresult < 0)
            Marshal.ThrowExceptionForHR(hresult);
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint direct3DDevice);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [GeneratedComInterface]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [GeneratedComInterface]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal partial interface IDirect3DDxgiInterfaceAccess
    {
        [PreserveSig]
        int GetInterface(in Guid iid, out nint result);
    }
}

internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static void Write(string path, int width, int height, byte[] bgraPixels)
    {
        var scanlineLength = checked(width * 4 + 1);
        var scanlines = new byte[checked(scanlineLength * height)];

        for (var y = 0; y < height; y++)
        {
            var sourceOffset = y * width * 4;
            var destinationOffset = y * scanlineLength + 1;
            for (var x = 0; x < width; x++)
            {
                var source = sourceOffset + x * 4;
                var destination = destinationOffset + x * 4;
                scanlines[destination] = bgraPixels[source + 2];
                scanlines[destination + 1] = bgraPixels[source + 1];
                scanlines[destination + 2] = bgraPixels[source];
                scanlines[destination + 3] = bgraPixels[source + 3];
            }
        }

        using var output = File.Create(path);
        output.Write(Signature);
        WriteChunk(output, "IHDR", CreateHeader(width, height));

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(scanlines);

        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static byte[] CreateHeader(int width, int height)
    {
        var header = new byte[13];
        WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        WriteUInt32BigEndian(output, (uint)data.Length);
        output.Write(typeBytes);
        output.Write(data);

        var crc = CalculateCrc(typeBytes, data);
        WriteUInt32BigEndian(output, crc);
    }

    private static uint CalculateCrc(byte[] type, byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (var value in type)
            crc = UpdateCrc(crc, value);
        foreach (var value in data)
            crc = UpdateCrc(crc, value);
        return ~crc;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        return crc;
    }

    private static void WriteUInt32BigEndian(Stream output, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        WriteUInt32BigEndian(buffer, value);
        output.Write(buffer);
    }

    private static void WriteUInt32BigEndian(Span<byte> buffer, uint value)
    {
        buffer[0] = (byte)(value >> 24);
        buffer[1] = (byte)(value >> 16);
        buffer[2] = (byte)(value >> 8);
        buffer[3] = (byte)value;
    }
}
