using System.Globalization;
using System.Runtime.CompilerServices;
using Clippy;

// Installed before anything else can fail. In tray mode the console is hidden, so a crash leaves the
// user with a vanished tray icon and no explanation at all -- and no way to report it. This writes
// the reason to a file next to the exe where it can actually be found, and puts the same line on
// screen for the case where the console happens to be visible.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    CrashLog.Write(
        $"UNHANDLED: {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}");
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    CrashLog.Write($"UNOBSERVED TASK: {e.Exception}");
    e.SetObserved();
};

// No arguments is the normal case and must not be a usage screen: the user double-clicked the exe
// because they want it recording, not because they want to read a help text. Everything the run
// needs comes from config.json, so the default path is the same one --record builds by hand.
if (args.Length == 0)
{
    var defaults = ClippyConfig.Load();
    return Clippy.ScreenCapture.RunVideo(
        // No timeout: this mode runs until the user quits it from the tray, which is what an instant
        // replay is for. A duration would silently stop the recorder at an arbitrary moment.
        duration: null,
        Path.Combine(AppContext.BaseDirectory, defaults.OutputFolder, $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.mp4"),
        defaults.VideoEncoder,
        "passthrough",
        withAudio: true,
        audioCaptureOnly: false,
        hotkeys: true,
        exportAt: null,
        exportDuration: null,
        config: defaults,
        // The tray owns the process lifetime. Without an icon there would be no way back to this
        // window and no way to quit except killing it from Task Manager.
        tray: true);
}

if (args.Contains("--self-update"))
{
    // Forces the whole download-and-install path without a tray icon, so the one branch that is
    // impossible to test by hand (it replaces the running exe and exits) can still be exercised
    // on demand. Also the thing to ask a user to run when an update misbehaves.
    var found = await UpdateService.CheckForUpdateAsync();
    Console.WriteLine(found.Available
        ? $"{UpdateService.Version} -> {found.Tag}: {found.DownloadUrl}"
        : $"{UpdateService.Version} is current; nothing to install.");
    if (!found.Available)
        return 0;

    await UpdateService.ApplyUpdateAsync(found.DownloadUrl, null);
    return 0;
}

if (args.Contains("--help") || args.Contains("-h") || args.Contains("--usage"))
{
    return PrintUsage();
}

  if (args.Contains("--test-mft-video-wgc"))
      return MfVideoPrototype.RunWithLiveCapture();

  if (args.Contains("--test-nvenc-native"))
      return NvencNativeSpike.Run();

  if (args.Contains("--test-mft-video"))
      return MfVideoPrototype.Run();

if (args.Contains("--test-mft-audio"))
{
    return MfAudioPrototype.Run();
}

if (args.Contains("--smoke"))
{
    return Clippy.SmokeTest.Run();
}

if (args.Contains("--audio-probe"))
{
    return Clippy.AudioCapture.RunAudioProbe();
}

if (args.Contains("--test-parsers"))
{
    return Clippy.ParserSelfTest.Run();
}

if (args.Contains("--test-ring"))
{
    return Clippy.RingBufferSelfTest.Run();
}

if (args.Contains("--test-mp4"))
{
    return Clippy.Mp4WriterSelfTest.Run();
}

if (args is ["--capture"])
{
    return Clippy.ScreenCapture.Run(null);
}

if (args is ["--capture", var durationText] &&
    double.TryParse(durationText, CultureInfo.InvariantCulture, out var durationSeconds) &&
    durationSeconds > 0)
{
    return Clippy.ScreenCapture.Run(TimeSpan.FromSeconds(durationSeconds));
}

if (args.FirstOrDefault() == "--record")
{
    // Loaded once here and passed down, so the output folder, the hotkey bindings and the sound
    // setting all come from the same file for the whole run.
    var config = ClippyConfig.Load();
    var videoSeconds = args.Length > 1 && double.TryParse(args[1], CultureInfo.InvariantCulture, out var parsed) && parsed > 0
        ? parsed
        : 60;
    var videoPath = Path.Combine(Environment.CurrentDirectory, config.OutputFolder, $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
    // The config file is the default; an explicit --encoder= on the command line overrides it for
    // one run, which is how you test a different GPU without editing anything.
    var videoEncoder = args.FirstOrDefault(a => a.StartsWith("--encoder=", StringComparison.Ordinal))?["--encoder=".Length..]
        ?? config.VideoEncoder;
    // passthrough is the default: vfr makes ffmpeg resample onto a 1/25 grid and drop frames
    // (measured: 658 captured -> 346 encoded). Pass --fps-mode=vfr to reproduce that for debugging.
    var fpsMode = args.FirstOrDefault(a => a.StartsWith("--fps-mode=", StringComparison.Ordinal))?["--fps-mode=".Length..]
        ?? "passthrough";
    var withAudio = args.Any(a => a.Equals("--audio", StringComparison.OrdinalIgnoreCase));
    // Bisection: WASAPI runs, audio ffmpeg does not exist. Isolates Clippy's threads from the
    // coexistence of two ffmpeg processes.
    var audioCaptureOnly = args.Any(a => a.Equals("--audio-capture-only", StringComparison.OrdinalIgnoreCase));
    // --export-at <seconds> exports while the recording is still running; --export-dur sets its
    // length in seconds. Phase 6 replaces this timer with a hotkey.
    TimeSpan? exportAt = null;
    if (args.FirstOrDefault(a => a.StartsWith("--export-at=", StringComparison.Ordinal)) is { } atArg
        && double.TryParse(atArg["--export-at=".Length..], CultureInfo.InvariantCulture, out var at) && at > 0)
    {
        exportAt = TimeSpan.FromSeconds(at);
    }

    double? exportDuration = null;
    if (args.FirstOrDefault(a => a.StartsWith("--export-dur=", StringComparison.Ordinal)) is { } durArg
        && double.TryParse(durArg["--export-dur=".Length..], CultureInfo.InvariantCulture, out var dur) && dur > 0)
    {
        exportDuration = dur;
    }
    // --tray / --minimized imply the tray icon even when the config does not ask for it, so the flag
    // on the command line wins over the file.
    var trayRequested = config.StartMinimizedToTray
        || args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase)
                      || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

    return Clippy.ScreenCapture.RunVideo(
        TimeSpan.FromSeconds(videoSeconds), videoPath, videoEncoder, fpsMode, withAudio || audioCaptureOnly, audioCaptureOnly,
        args.Any(a => a.Equals("--hotkeys", StringComparison.OrdinalIgnoreCase)), exportAt?.TotalSeconds, exportDuration, config,
        trayRequested);
}

if (args.FirstOrDefault() == "--test-audio-timeline")
{
    return Clippy.FfmpegAudioEncoder.SelfTest() ? 0 : 1;
}

if (args.FirstOrDefault() == "--capture")
{
    Console.Error.WriteLine("Usage: --capture [duration-seconds]");
    return 1;
}

return PrintUsage();

/// <summary>
/// The flag reference, reachable only on purpose. Plain "Hello" plus two lines used to be the
/// no-argument output, which is a worse answer than none: it looks like a crash and teaches the
/// user nothing about what to type next.
/// </summary>
static int PrintUsage()
{
    Console.WriteLine("Clippy -- instant replay recorder for Windows");
    Console.WriteLine();
    Console.WriteLine("  Clippy.exe                 start recording; no arguments needed.");
    Console.WriteLine("                              Screen + system audio, hotkeys, tray icon. Runs until");
    Console.WriteLine("                              you quit it from the tray. Reads config.json.");
    Console.WriteLine();
    Console.WriteLine("  --help, -h, --usage        this text");
    Console.WriteLine("  --self-update             check for and install an update, then exit");
    Console.WriteLine();
    Console.WriteLine("Modes:");
    Console.WriteLine("  --record [seconds]         record to a file for a fixed time (default 60s)");
    Console.WriteLine("  --capture [seconds]        capture without encoding, for diagnosing capture");
    Console.WriteLine();
    Console.WriteLine("Modifiers for --record:");
    Console.WriteLine("  --audio                    include system audio");
    Console.WriteLine("  --hotkeys                  enable F9 / F10 clip export while recording");
    Console.WriteLine("  --tray                     run in the tray (also --minimized)");
    Console.WriteLine("  --encoder=NAME             h264_nvenc | h264_qsv | h264_amf; falls back to libx264");
    Console.WriteLine("  --fps-mode=MODE            passthrough | vfr | cfr");
    Console.WriteLine("  --export-at=SECONDS        export a clip while still recording");
    Console.WriteLine("  --export-dur=SECONDS       its length in seconds");
    Console.WriteLine();
    Console.WriteLine("Diagnostics:");
    Console.WriteLine("  --smoke                    end-to-end smoke test");
    Console.WriteLine("  --audio-probe              list audio devices and capture from one");
    Console.WriteLine("  --test-parsers             H.264 / AAC parser self-test");
    Console.WriteLine("  --test-ring                ring buffer self-test");
    Console.WriteLine("  --test-mp4                 MP4 writer self-test");
    Console.WriteLine("  --test-nvenc-native       spike: can the native NVENC bridge encode a real BGRA texture?");
    Console.WriteLine("  --test-nvenc-native       spike: can the native NVENC bridge encode a BGRA texture?");
    Console.WriteLine("  --test-mft-audio          spike: can the system AAC encoder emit ADTS?");
    Console.WriteLine("  --test-mft-video          spike: can a system H.264 MFT be driven at all?");
    Console.WriteLine("  --test-mft-video-wgc      the same spike, with a live capture session running");
    Console.WriteLine();
    Console.WriteLine($"Config: {ClippyConfig.Path}");
    return 0;
}
