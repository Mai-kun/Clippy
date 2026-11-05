namespace Clippy;

/// <summary>
/// Everything Program.cs needs from the operating system in order to run. The Windows side
/// (Graphics Capture, Media Foundation, WASAPI) keeps working exactly as before behind
/// WindowsPlatformBackend; Linux starts as a placeholder shell behind LinuxPlatformBackend and
/// gains PipeWire/Wayland capture later. Program.cs only ever sees this interface and the factory,
/// so a platform must never leak a capture, encoder or UI type into the shared startup path.
/// </summary>
internal interface IPlatformBackend
{
    /// <summary>
    /// Console setup, called before the first Console.WriteLine. On Windows the build is a WinExe:
    /// no console exists unless the user asked for one with --console / --debug, and this is what
    /// attaches to the launching terminal or allocates one. On Linux the process always has the
    /// terminal it was started from, so there is nothing to do.
    /// </summary>
    void InitializeConsole(string[] args);

    /// <summary>
    /// Declares how the process wants to see coordinates. Windows needs the per-monitor-v2 call
    /// or the whole UI measures against a virtualized, scaled desktop; Wayland compositors scale
    /// on their own terms, so Linux is a no-op.
    /// </summary>
    void InitializeDpi();

    /// <summary>
    /// The recording path behind both the no-arguments launch (instant replay, runs until quit)
    /// and --record. The options record carries exactly what ScreenCapture.RunVideo takes today,
    /// decided in Program.cs from config.json plus the command line.
    /// </summary>
    int RunVideo(PlatformRecordingOptions options);

    /// <summary>
    /// Capture without encoding (--capture), the diagnostic that separates "the frames are bad"
    /// from "the encode is bad".
    /// </summary>
    int RunCaptureOnly(TimeSpan? duration);

    /// <summary>
    /// Runs a platform-specific diagnostic flag (--smoke, --test-nvenc-native, --test-mft-*, --audio-probe).
    /// Returns its exit code with handled = true. When none of the platform flags is present, sets
    /// handled = false so Program.cs can keep parsing the cross-platform flags; a backend that
    /// cannot run a given flag reports that instead of pretending the flag does not exist.
    /// </summary>
    int RunPlatformCommand(string[] args, out bool handled);
}

/// <summary>
/// The command-line decision, made once in Program.cs and passed down. The field order mirrors
/// the ScreenCapture.RunVideo parameter list it replaces, and OutputPath is already resolved by
/// the caller (via <see cref="PlatformClipPath.ClipPath(string, System.DateTime)"/>), so the
/// backends never invent file names themselves -- one naming scheme for the whole app.
/// </summary>
internal sealed record PlatformRecordingOptions(
    TimeSpan? Duration,
    string OutputPath,
    string EncoderName,
    string FpsMode,
    bool WithAudio,
    bool AudioCaptureOnly,
    bool Hotkeys,
    double? ExportAt,
    double? ExportDuration,
    ClippyConfig Config,
    bool Tray,
    bool Updated);
