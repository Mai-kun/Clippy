using System.Globalization;
using System.Runtime.CompilerServices;
using Clippy;

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
    var videoEncoder = args.FirstOrDefault(a => a.StartsWith("--encoder=", StringComparison.Ordinal))?["--encoder=".Length..]
        ?? "h264_nvenc";
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
    return Clippy.AudioEncoder.SelfTest() ? 0 : 1;
}

if (args.FirstOrDefault() == "--capture")
{
    Console.Error.WriteLine("Usage: --capture [duration-seconds]");
    return 1;
}

Console.WriteLine("Hello");
Console.WriteLine($"NativeAOT: {!RuntimeFeature.IsDynamicCodeSupported}");
Console.WriteLine("Capture: --capture [duration-seconds]");
Console.WriteLine("Record:  --record [seconds] [--audio] [--encoder=h264_nvenc|h264_qsv|h264_amf] [--fps-mode=passthrough|vfr|cfr]");
return 0;
