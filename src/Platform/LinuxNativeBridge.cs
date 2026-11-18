#if LINUX
namespace Clippy;

using System;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke bridge to libclippy_linux.so: provides direct PipeWire video + CUDA NVENC and
/// PipeWire audio + FDK-AAC encoding within the single Clippy process, replacing the external
/// gst-launch-1.0 and ffmpeg helper processes.
/// </summary>
internal static partial class LinuxNativeBridge
{
    private const string LibraryName = "clippy_linux";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void VideoPacketCallback(IntPtr userData, IntPtr data, uint len, int isKeyframe, double clockSec);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void AudioAdtsCallback(IntPtr userData, IntPtr adtsData, uint len, long timestampNs);

    [LibraryImport(LibraryName, EntryPoint = "clippy_is_supported")]
    public static partial int IsSupported(int isHevc);

    [LibraryImport(LibraryName, EntryPoint = "clippy_video_start")]
    public static partial int VideoStart(
        int pwFd,
        uint nodeId,
        int width,
        int height,
        int fps,
        int bitrateBps,
        int isHevc,
        VideoPacketCallback cb,
        IntPtr userData,
        out IntPtr outStream);

    [LibraryImport(LibraryName, EntryPoint = "clippy_video_get_sequence_params")]
    public static partial int VideoGetSequenceParams(
        IntPtr stream,
        out IntPtr outParams,
        out uint outSize);

    [LibraryImport(LibraryName, EntryPoint = "clippy_video_stop")]
    public static partial void VideoStop(IntPtr stream);

    [LibraryImport(LibraryName, EntryPoint = "clippy_audio_start", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int AudioStart(
        string? sinkName,
        int recordMic,
        string? micName,
        AudioAdtsCallback cb,
        IntPtr userData,
        out IntPtr outAudio);

    [LibraryImport(LibraryName, EntryPoint = "clippy_audio_stop")]
    public static partial void AudioStop(IntPtr audio);

    private static bool? isAvailable;
    private static readonly object lockObj = new();

    static LinuxNativeBridge()
    {
        NativeLibrary.SetDllImportResolver(typeof(LinuxNativeBridge).Assembly, (name, assembly, path) =>
        {
            if (name == LibraryName)
            {
                var candidate = Path.Combine(AppContext.BaseDirectory, "libclippy_linux.so");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;

                // Also check relative to native-linux release target
                var targetPath = Path.Combine(AppContext.BaseDirectory, "native-linux", "libclippy_linux.so");
                if (File.Exists(targetPath) && NativeLibrary.TryLoad(targetPath, out var handle2))
                    return handle2;
            }
            return IntPtr.Zero;
        });
    }

    public static bool IsAvailable
    {
        get
        {
            lock (lockObj)
            {
                if (isAvailable.HasValue) return isAvailable.Value;
                try
                {
                    var supported = IsSupported(1) != 0 || IsSupported(0) != 0;
                    isAvailable = supported;
                    return supported;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[libclippy_linux] Native bridge not loaded ({ex.GetType().Name}: {ex.Message}). Using gst-launch/ffmpeg fallback.");
                    isAvailable = false;
                    return false;
                }
            }
        }
    }
}
#endif
