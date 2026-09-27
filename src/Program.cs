using System.Globalization;
using System.Runtime.CompilerServices;

if (args.Contains("--smoke"))
{
    return Clippy.SmokeTest.Run();
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
    var fpsMode = args.FirstOrDefault(a => a.StartsWith("--fps-mode=", StringComparison.Ordinal))?["--fps-mode=".Length..]
        ?? "vfr";
    return Clippy.ScreenCapture.RunVideo(TimeSpan.FromSeconds(videoSeconds), videoPath, videoEncoder, fpsMode);
}

if (args.FirstOrDefault() == "--capture")
{
    Console.Error.WriteLine("Usage: --capture [duration-seconds]");
    return 1;
}

Console.WriteLine("Hello");
Console.WriteLine($"NativeAOT: {!RuntimeFeature.IsDynamicCodeSupported}");
Console.WriteLine("Capture: --capture [duration-seconds]");
Console.WriteLine("Record:  --record [seconds] [--encoder=h264_nvenc|h264_qsv|h264_amf]");
return 0;
