using System.Globalization;
using System.Runtime.CompilerServices;

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
    var videoSeconds = args.Length > 1 && double.TryParse(args[1], CultureInfo.InvariantCulture, out var parsed) && parsed > 0
        ? parsed
        : 60;
    var videoPath = Path.Combine(Environment.CurrentDirectory, "output", $"clip-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
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
    return Clippy.ScreenCapture.RunVideo(
        TimeSpan.FromSeconds(videoSeconds), videoPath, videoEncoder, fpsMode, withAudio || audioCaptureOnly, audioCaptureOnly);
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
