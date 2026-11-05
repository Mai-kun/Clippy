#if LINUX
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// The saved-clip feedback on Linux: the same confirmation sound the Windows ExportNotification
/// plays (including the embedded DefaultSound.wav resource, which the csproj ships on both
/// platforms), and notify-send in place of the overlay card. Both are fire-and-forget: a missing
/// helper binary or a headless notification daemon must never fail an export that already wrote
/// its bytes.
/// </summary>
internal static class LinuxNotifications
{
    /// <summary>
    /// Plays the confirmation sound through pw-play, asynchronously. The embedded wav is written to
    /// a temp file first, because pw-play plays files (and "-" for stdin, but a temp file survives
    /// an early exit of the caller where a pipe would be closed under it).
    /// </summary>
    public static void PlayExportSaved(string customSoundPath, string tempDirectory)
    {
        var soundPath = ResolveSoundFile(customSoundPath, tempDirectory);
        if (soundPath is null)
            return;

        // Fire and forget: the process outlives any immediate caller and cleans up after itself.
        _ = Task.Run(() =>
        {
            try
            {
                if (!TryPlay(soundPath, "pw-play") && !TryPlay(soundPath, "paplay") && !TryPlay(soundPath, "aplay"))
                    Console.WriteLine("Export: no sound player found (pw-play / paplay / aplay); confirmation is silent.");
            }
            finally
            {
                if (soundPath.StartsWith(tempDirectory, StringComparison.Ordinal))
                    try { File.Delete(soundPath); } catch (Exception) { /* temp, transient */ }
            }
        });
    }

    private static bool TryPlay(string path, string player)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = player,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo);
            if (process is null)
                return false;
            return process.WaitForExit(15_000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The custom sound when the config points at a real file (relative paths resolve against the
    /// executable, same rule as on Windows); otherwise the embedded DefaultSound.wav.
    /// </summary>
    private static string? ResolveSoundFile(string customSoundPath, string tempDirectory)
    {
        if (!string.IsNullOrWhiteSpace(customSoundPath))
        {
            var candidate = Path.IsPathRooted(customSoundPath)
                ? customSoundPath
                : Path.Combine(AppContext.BaseDirectory, customSoundPath);
            if (File.Exists(candidate))
                return candidate;
            Console.WriteLine($"Export: custom sound '{customSoundPath}' does not exist; using the embedded default.");
        }

        try
        {
            using var resource = typeof(LinuxNotifications).Assembly.GetManifestResourceStream("DefaultSound.wav");
            if (resource is null)
            {
                Console.WriteLine("Export: the embedded DefaultSound.wav is missing from the build.");
                return null;
            }

            Directory.CreateDirectory(tempDirectory);
            var path = Path.Combine(tempDirectory, $"clippy-sound-{Environment.ProcessId}.wav");
            using var file = File.Create(path);
            resource.CopyTo(file);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Export: could not materialise the notification sound ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// The visual announcement, via notify-send. "overlay" and "windows_toast" both land here --
    /// the desktop notification is what a Linux session has instead of either.
    /// </summary>
    public static void ShowClipSaved(ClippyConfig config, string title, string body)
    {
        if (string.Equals(config.NotificationType, "none", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "notify-send",
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-a", "Clippy", "-i", "video-x-generic", "-t", "3000", title, body })
                startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Headless session or no notification daemon: the sound already did the announcing.
            Console.WriteLine($"Export: notification daemon unreachable ({ex.GetType().Name}).");
        }
    }
}
#endif
