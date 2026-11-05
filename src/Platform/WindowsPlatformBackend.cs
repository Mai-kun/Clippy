using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// The Windows implementation. It is a shell over the existing code paths, not a replacement: every
/// method forwards to the same ScreenCapture / spike / probe entry points Program.cs used to call
/// inline, so the capture and encoder stack behaves exactly as before this indirection existed.
/// </summary>
internal sealed class WindowsPlatformBackend : IPlatformBackend
{
    /// <summary>
    /// The build is WinExe: on a normal start Windows creates no console window at all, which is the
    /// whole point -- under Windows 11 the default terminal is Windows Terminal, so the old
    /// ShowWindow(GetConsoleWindow(), SW_HIDE) trick only left its icon parked on the taskbar. Every
    /// Console.WriteLine below therefore goes nowhere unless a developer asks for a console with
    /// --console / --debug, and that request has to be honoured before the first such write.
    /// </summary>
    public void InitializeConsole(string[] args)
    {
        // Attach to the launching terminal when it has one -- "Clippy.exe --console --test-mp4" from
        // PowerShell should print there -- and allocate a fresh console only when there is none to
        // attach to (double-click with the flag).
        if (!args.Contains("--console", StringComparer.OrdinalIgnoreCase) &&
            !args.Contains("--debug", StringComparer.OrdinalIgnoreCase))
            return;

        if (!NativeConsole.AttachConsole(NativeConsole.AttachParentProcess))
            NativeConsole.AllocConsole();

        // Console.Out is a writer cached before the console existed, so both streams are re-pointed at
        // the handles the calls above just produced. One writer for both: same screen buffer, and two
        // separate ones would interleave out of order.
        var consoleOut = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(consoleOut);
        Console.SetError(consoleOut);
    }

    /// <summary>
    /// The whole UI assumes it gets real pixel coordinates from Windows, which it only does once this
    /// process declares itself DPI-aware. Unaware (the default for a WinExe with no manifest), Windows
    /// scales every coordinate by 96/monitor-DPI on the way out and back: rcWork comes back as a
    /// virtualized desktop, GetDpiForMonitor reports 96, and a card laid out at 320 px is stretched by
    /// the compositor instead of drawn at its intended size -- the blurry-toast problem in reverse.
    /// </summary>
    public void InitializeDpi()
    {
        // V2 (rather than V1) because it is the only mode that reports a DPI change for a window already
        // sitting on a monitor: WM_DPICHANGED arrives when the game is dragged to another screen, which
        // is what lets the overlay re-layout mid-run instead of waiting for the next process start.
        //
        // -4 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2. Failure is not fatal (Win7, or an app compat
        // setting): every measurement then just falls back to 96 DPI, which is exactly the old behaviour.
        var dpiAware = NativeDpi.SetProcessDpiAwarenessContext(new IntPtr(-4));
        Console.WriteLine(dpiAware
            ? "DPI: per-monitor-v2 awareness enabled."
            : $"DPI: awareness not set (error {Marshal.GetLastWin32Error()}); layout assumes 96 DPI.");
    }

    /// <summary>
    /// Forwards to the unchanged ScreenCapture.RunVideo. When the caller supplies no output path the
    /// default name is built through ScreenCapture.ClipPath, which owns the clip-name format -- no
    /// second implementation of the naming scheme may appear on this platform.
    /// </summary>
    public int RunVideo(PlatformRecordingOptions options)
    {
        var outputPath = options.OutputPath;
        if (string.IsNullOrEmpty(outputPath))
            outputPath = ScreenCapture.ClipPath(
                Path.Combine(AppContext.BaseDirectory, options.Config.OutputFolder), DateTime.Now);

        return ScreenCapture.RunVideo(
            options.Duration,
            outputPath,
            options.EncoderName,
            options.FpsMode,
            options.WithAudio,
            options.AudioCaptureOnly,
            options.Hotkeys,
            options.ExportAt,
            options.ExportDuration,
            options.Config,
            options.Tray,
            options.Updated);
    }

    /// <summary>Forwards to the unchanged ScreenCapture.Run capture-only diagnostic.</summary>
    public int RunCaptureOnly(TimeSpan? duration) => ScreenCapture.Run(duration);

    /// <summary>
    /// The Windows-only diagnostics, in the order they appeared in Program.cs. Each one forwards to
    /// the same entry point the inline flag check used to call.
    /// </summary>
    public int RunPlatformCommand(string[] args, out bool handled)
    {
        // The IPC clip triggers are a Linux mechanism (a Unix socket next to a running recorder);
        // on Windows the hotkeys come from the global keyboard hook instead.
        string? saveFlag = args.FirstOrDefault(a => a is "--save-short" or "--save" or "--save-long" or "--stop");
        if (saveFlag is not null)
        {
            handled = true;
            Console.WriteLine($"{saveFlag} needs a running Linux recorder listening on /tmp/clippy-ipc.sock; " +
                "on Windows use the hotkeys instead.");
            return 1;
        }

        if (args.Contains("--test-mft-video-wgc"))
        {
            handled = true;
            return MfVideoPrototype.RunWithLiveCapture();
        }

        if (args.Contains("--test-nvenc-native"))
        {
            handled = true;
            return NvencNativeSpike.Run();
        }

        if (args.Contains("--test-mft-video"))
        {
            handled = true;
            return MfVideoPrototype.Run();
        }

        if (args.Contains("--test-mft-audio"))
        {
            handled = true;
            return MfAudioPrototype.Run();
        }

        if (args.Contains("--smoke"))
        {
            handled = true;
            return SmokeTest.Run();
        }

        if (args.Contains("--audio-probe"))
        {
            handled = true;
            return AudioCapture.RunAudioProbe();
        }

        handled = false;
        return 0;
    }
}

/// <summary>
/// The user32 entry point behind the DPI-awareness call in InitializeDpi. It lives here because it
/// belongs to the Windows backend and nowhere else in the app may reach user32.
/// </summary>
internal static partial class NativeDpi
{
    [LibraryImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint value);
}

/// <summary>
/// The kernel32 entry points behind --console. A separate type because a LibraryImport has to hang
/// off a class of its own.
/// </summary>
internal static partial class NativeConsole
{
    /// <summary>ATTACH_PARENT_PROCESS: the console of whatever started us, when it has one.</summary>
    internal const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllocConsole();
}
