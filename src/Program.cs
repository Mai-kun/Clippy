using System.Globalization;
using Clippy;

// The backend owns every OS-specific startup ritual and run mode. It is created first so the
// console/DPI setup below can be delegated instead of inlined here: on Windows this is a WinExe
// where nothing prints unless a developer asked for a console, on Linux the terminal is simply
// already there.
var platform = PlatformFactory.Create();
platform.InitializeConsole(args);
platform.InitializeDpi();

// The platform's own client-side flags (--save-short, --stop on Linux) must run BEFORE the disk
// spool cleanup below: they are handled by an already-running recorder, and this process's
// startup cleanup would wipe that recorder's disk tail out from under it.
var platformResult = platform.RunPlatformCommand(args, out var platformHandled);
if (platformHandled)
    return platformResult;

if (args.Contains("--test-parsers"))
    return Clippy.ParserSelfTest.Run();

// The overlay's lifecycle, watched from the X server's side of the wire: five shows with quick
// in-place payload swaps, and after each one -- 3.5 s in, past hold plus both fades -- the card's
// window MUST be IsUnmapped per XGetWindowAttributes. "I scaled the bitmap to zero" is not the
// same as "the card left the screen"; this probe reads the server's map state, which is where a
// missing XFlush (and the fade-in-into-StickyHold bug that parked cards for minutes) hides.
// Does not touch the single-instance lock: it runs next to a live recorder.
if (args.Contains("--test-overlay-lifecycle"))
{
#if LINUX
    INotificationPresenter presenter = new LinuxNotificationPresenter(ClippyConfig.Load);
    NotificationCenter.SetPresenter(presenter);
    try
    {
        var failures = 0;
        for (var i = 1; i <= 5; i++)
        {
            Console.WriteLine($"[test] show {i}: ClipSaved card, in-place swap 300 ms later");
            NotificationCenter.ClipSaved(i * 10 + 29, 8.7);
            await Task.Delay(300);
            NotificationCenter.NotificationModePreview($"in-place swap {i}");
            await Task.Delay(3200); // hold (2.6 s) + both fades are over by now
            var state = LinuxOverlayProbe.GetMapState();
            Console.WriteLine($"[test] after 3.5 s: map_state={state} ({(state == LinuxOverlayProbe.IsUnmapped ? "IsUnmapped" : "STILL MAPPED")})");
            if (state != LinuxOverlayProbe.IsUnmapped)
                failures++;
        }

        // The sticky leg: a Checking card refreshed in place, then replaced by a non-sticky one --
        // the same hand-off the update flow makes, and the same trap the sticky bug hid in.
        Console.WriteLine("[test] sticky cycle: Checking -> in-place refresh -> UpToDate");
        NotificationCenter.CheckingForUpdates();
        await Task.Delay(2000);
        NotificationCenter.UpToDate(UpdateService.Version);
        await Task.Delay(3500);
        var stickyState = LinuxOverlayProbe.GetMapState();
        Console.WriteLine($"[test] after the sticky cycle: map_state={stickyState} ({(stickyState == LinuxOverlayProbe.IsUnmapped ? "IsUnmapped" : "STILL MAPPED")})");
        if (stickyState != LinuxOverlayProbe.IsUnmapped)
            failures++;

        Console.WriteLine(failures == 0 ? "OVERLAY LIFECYCLE: OK (all hidden)" : $"OVERLAY LIFECYCLE: FAILED ({failures} still mapped)");
        return failures == 0 ? 0 : 1;
    }
    finally
    {
        NotificationCenter.SetPresenter(null);
        presenter.Dispose();
    }
#else
    Console.WriteLine("Linux-only test.");
    return 0;
#endif
}

if (args.Contains("--test-ring"))
    return Clippy.RingBufferSelfTest.Run();

if (args.Contains("--test-mp4"))
    return Clippy.Mp4WriterSelfTest.Run();

if (args.FirstOrDefault() == "--test-audio-timeline")
    return Clippy.FfmpegAudioEncoder.SelfTest() ? 0 : 1;

if (args.Contains("--help") || args.Contains("-h") || args.Contains("--usage"))
    return PrintUsage();

// One recorder at a time. The client flags above were answered by the RUNNING recorder and
// already returned, so the only launches left here are recorder runs -- and a second one must
// not kill the first one's IPC socket or wipe its disk spool below. The lock lives on Linux
// (flock on /tmp/clippy-instance.lock); on Windows the single-instance story is the existing
// shortcut/installer behaviour and stays as it is.
//
// A handoff launch (--updated after an update, --restart-handoff from the tray's Restart) is a
// different second launch: its own predecessor started it and is STILL ALIVE, holding the lock
// on its way out. Without patience the replacement kills itself as a duplicate, the predecessor
// exits behind it, and nothing is left running -- which is exactly the dead-recorder-after-update
// report. A handoff therefore waits up to 5 s for the lock before believing "already running".
var handoff = args.Contains("--updated", StringComparer.OrdinalIgnoreCase)
    || args.Contains("--restart-handoff", StringComparer.OrdinalIgnoreCase);
#if LINUX
if (!LinuxSingleInstance.TryAcquireWithTimeout(handoff ? TimeSpan.FromSeconds(5) : TimeSpan.Zero))
{
    Console.WriteLine("Clippy is already running");
    // The shared pipeline routes it (a toast here -- the second process has no overlay of its
    // own); ForceVisual so a Sound Only user still learns why nothing started.
    NotificationCenter.SetPresenter(new LinuxNotificationPresenter(ClippyConfig.Load));
    NotificationCenter.AlreadyRunning();
    NotificationCenter.SetPresenter(null);
    return 0;
}
#endif

// Installed before anything else can fail. In tray mode there is no console at all, so a crash
// leaves the user with a vanished tray icon and no explanation on screen -- and no way to report
// it. This writes the reason to a file next to the exe where it can actually be found, and puts
// the same line on the console for the case where one was allocated.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    // The OS build number matters more here than for most apps: several of this app's crashes
    // were Windows-version-specific (the capture border on Windows 10, CreateForMonitor
    // E_INVALIDARG on some 24H2 builds), and without the build a user report cannot be told
    // apart from an ordinary bug.
    CrashLog.Write(
        $"UNHANDLED on {Environment.OSVersion.VersionString}: {ex?.GetType().FullName}: {ex?.Message}\n{ex?.StackTrace}");
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    CrashLog.Write($"UNOBSERVED TASK: {e.Exception}");
    e.SetObserved();
};

// Hybrid-buffer housekeeping. The disk spool under %TEMP%\Clippy\spool is a cache, never user data:
// startup clears whatever a previous crash left behind, and a normal exit clears this run's segments.
// Cleanup never throws, so neither path can take the process down. (Known ceiling: two instances
// share the folder, so a second startup wipes the first instance's tail -- one recorder at a time.
// The client-only flags above return before this point for exactly that reason.)
AppDomain.CurrentDomain.ProcessExit += (_, _) => DiskSpooler.Cleanup();
DiskSpooler.Cleanup();

var isUpdated = args.Contains("--updated", StringComparer.OrdinalIgnoreCase);

// No arguments is the normal case and must not be a usage screen: the user double-clicked the exe
// because they want it recording, not because they want to read a help text. Everything the run
// needs comes from config.json, so the default path is the same one --record builds by hand.
// If --updated was passed, start normally into the tray and show the update notification.
var nonOptionArgs = args.Where(a => !a.Equals("--updated", StringComparison.OrdinalIgnoreCase) &&
                                    !a.Equals("--restart-handoff", StringComparison.OrdinalIgnoreCase) &&
                                    !a.Equals("--console", StringComparison.OrdinalIgnoreCase) &&
                                    !a.Equals("--debug", StringComparison.OrdinalIgnoreCase)).ToArray();

if (nonOptionArgs.Length == 0)
{
    var defaults = ClippyConfig.Load();
    return platform.RunVideo(new PlatformRecordingOptions(
        // No timeout: this mode runs until the user quits it from the tray, which is what an instant
        // replay is for. A duration would silently stop the recorder at an arbitrary moment.
        Duration: null,
        // One helper owns the clip-name format, so the tray path and the hotkey path cannot drift
        // apart into two different naming schemes in the same folder.
        OutputPath: PlatformClipPath.ClipPath(
            Path.Combine(AppContext.BaseDirectory, defaults.OutputFolder), DateTime.Now),
        EncoderName: defaults.VideoEncoder,
        FpsMode: "passthrough",
        WithAudio: true,
        AudioCaptureOnly: false,
        Hotkeys: true,
        ExportAt: null,
        ExportDuration: null,
        Config: defaults,
        // The tray owns the process lifetime. Without an icon there would be no way back to this
        // window and no way to quit except killing it from Task Manager.
        Tray: true,
        Updated: isUpdated));
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

    await UpdateService.ApplyUpdateAsync(found.Tag, found.DownloadUrl, found.ReleaseNotes);
    return 0;
}

if (args.Contains("--test-update"))
{
    return await UpdateService.RunSelfTest();
}

// Applies the archive at <path-or-url> (a local file:// path or an HTTP URL) through the same
// apply path a real update takes, then restarts into it -- the one branch a hand test cannot
// reach, because CheckForUpdateAsync only ever offers the current GitHub release. The visuals run
// through the shared pipeline exactly as a real update's would: Checking parks the card, the
// download sweeps its bar in place, Installing completes it, and the restarted process shows the
// completed card from the marker the apply left. With no URL the flag becomes the VERIFICATION
// half: it is what the apply-restart replays, so the process that comes back (same PID, by exec)
// proves the restart worked, the lock was handed over, and the completed card showed.
if (args.Contains("--test-update-apply", StringComparer.OrdinalIgnoreCase))
{
    var source = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    Console.WriteLine($"[test] process {Environment.ProcessId} applying update from '{source ?? "<verify-only>"}'");

    // The visuals: a presenter of the running platform, so the card behaves exactly as it would
    // for a real update. The toast side stays silent -- this run has no tray to hang a balloon on.
#if LINUX
    INotificationPresenter presenter = new LinuxNotificationPresenter(ClippyConfig.Load);
#else
    INotificationPresenter presenter = new WindowsNotificationPresenter(ClippyConfig.Load, (_, _) => { });
#endif
    NotificationCenter.SetPresenter(presenter);
    try
    {
        if (source is null)
        {
            Console.WriteLine("[test] restart verified: the replacement image is running and holds the instance lock.");
            NotificationCenter.UpdateCompletedFromMarker(UpdateService.CurrentVersion);
            await Task.Delay(5500); // the completed card's hold, so a human actually sees it
            return 0;
        }

        NotificationCenter.CheckingForUpdates();

        // Map the native bridge before applying: a LIVE recorder holds libclippy_linux.so mapped
        // while an update replaces it, and the move-then-copy in the apply path must prove itself
        // against exactly that (a mapped file refuses open-for-write; the rename sidesteps it).
        try
        {
            _ = System.Runtime.InteropServices.NativeLibrary.Load(
                Path.Combine(AppContext.BaseDirectory, "libclippy_linux.so"));
            Console.WriteLine("[test] the native bridge is mapped for the duration of the apply.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[test] the native bridge did not load ({ex.GetType().Name}); the apply runs unmapped.");
        }

        // The version label is cosmetic here (it names the "Downloading {v}..." step); the marker
        // the apply writes uses the same string, and a REAL update's --updated restart overwrites
        // it with the real version and summary.
        await UpdateService.ApplyUpdateAsync("update", source);
        return 0;
    }
    finally
    {
        NotificationCenter.SetPresenter(null);
        presenter.Dispose();
    }
}

if (args.Contains("--help") || args.Contains("-h") || args.Contains("--usage"))
{
    return PrintUsage();
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

if (args.FirstOrDefault() == "--record")
{
    // Loaded once here and passed down, so the output folder, the hotkey bindings and the sound
    // setting all come from the same file for the whole run.
    var config = ClippyConfig.Load();
    var videoSeconds = args.Length > 1 && double.TryParse(args[1], CultureInfo.InvariantCulture, out var parsed) && parsed > 0
        ? parsed
        : 60;
    var videoPath = PlatformClipPath.ClipPath(
        Path.Combine(Environment.CurrentDirectory, config.OutputFolder), DateTime.Now);
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

    return platform.RunVideo(new PlatformRecordingOptions(
        TimeSpan.FromSeconds(videoSeconds), videoPath, videoEncoder, fpsMode, withAudio || audioCaptureOnly, audioCaptureOnly,
        args.Any(a => a.Equals("--hotkeys", StringComparison.OrdinalIgnoreCase)), exportAt?.TotalSeconds, exportDuration, config,
        trayRequested,
        Updated: isUpdated));
}

if (args.FirstOrDefault() == "--test-audio-timeline")
{
    return Clippy.FfmpegAudioEncoder.SelfTest() ? 0 : 1;
}

if (args is ["--capture"])
{
    return platform.RunCaptureOnly(null);
}

if (args is ["--capture", var durationText] &&
    double.TryParse(durationText, CultureInfo.InvariantCulture, out var durationSeconds) &&
    durationSeconds > 0)
{
    return platform.RunCaptureOnly(TimeSpan.FromSeconds(durationSeconds));
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
    Console.WriteLine("Clippy -- instant replay recorder");
    Console.WriteLine();
    Console.WriteLine("  Clippy.exe                 start recording; no arguments needed.");
    Console.WriteLine("                              Screen + system audio, hotkeys, tray icon. Runs until");
    Console.WriteLine("                              you quit it from the tray. Reads config.json.");
    Console.WriteLine();
    Console.WriteLine("  --help, -h, --usage        this text");
    Console.WriteLine("  --self-update             check for and install an update, then exit");
    Console.WriteLine("  --updated                 launched after update; shows update notification");
    Console.WriteLine("  --restart-handoff         internal: waits for the predecessor to release the");
    Console.WriteLine("                            single-instance lock instead of exiting as a duplicate");
    Console.WriteLine("  --console, --debug        allocate a console for this run (the Windows build is windowed)");
    Console.WriteLine();
    Console.WriteLine("Modes:");
    Console.WriteLine("  --record [seconds]         record to a file for a fixed time (default 60s)");
    Console.WriteLine("  --capture [seconds]        capture without encoding, for diagnosing capture");
    Console.WriteLine();
    Console.WriteLine("Modifiers for --record:");
    Console.WriteLine("  --audio                    include system audio");
    Console.WriteLine("  --hotkeys                  enable the clip hotkeys (F8 / F9 by default)");
    Console.WriteLine("  --tray                     run in the tray (also --minimized)");
    Console.WriteLine("  --encoder=NAME             h264_nvenc | h264_qsv | h264_amf; falls back to libx264");
    Console.WriteLine("  --fps-mode=MODE            passthrough | vfr | cfr");
    Console.WriteLine("  --export-at=SECONDS        export a clip while still recording");
    Console.WriteLine("  --export-dur=SECONDS       its length in seconds");
    Console.WriteLine();
    Console.WriteLine("Diagnostics:");
    Console.WriteLine("  --smoke                    end-to-end smoke test (Windows only)");
    Console.WriteLine("  --audio-probe              list audio devices and capture from one (Windows only)");
    Console.WriteLine("  --test-update              update self-test (checks, zip extraction, download)");
    Console.WriteLine("  --test-update-apply <url>  apply a local archive (file://) or URL through the real");
    Console.WriteLine("                             update path and restart into it; without a URL it only");
    Console.WriteLine("                             verifies the restarted process");
    Console.WriteLine("  --test-parsers             H.264 / AAC parser self-test");
    Console.WriteLine("  --test-ring                ring buffer self-test");
    Console.WriteLine("  --test-mp4                 MP4 writer self-test");
    Console.WriteLine("  --test-nvenc-native       spike: can the native NVENC bridge encode a BGRA texture? (Windows only)");
    Console.WriteLine("  --test-mft-audio          spike: can the system AAC encoder emit ADTS? (Windows only)");
    Console.WriteLine("  --test-mft-video          spike: can a system H.264 MFT be driven at all? (Windows only)");
    Console.WriteLine("  --test-mft-video-wgc      the same spike, with a live capture session running (Windows only)");
    Console.WriteLine();
    Console.WriteLine("Clip saving (Linux; bind to compositor shortcuts on KDE/Niri):");
    Console.WriteLine("  --save-short, --save      ask the running recorder to save the short clip");
    Console.WriteLine("  --save-long               save the long clip");
    Console.WriteLine("  --stop                    stop the recorder");
    Console.WriteLine();
    Console.WriteLine($"Config: {ClippyConfig.Path}");
    return 0;
}
