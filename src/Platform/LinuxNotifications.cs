#if LINUX
using System.Diagnostics;

namespace Clippy;

/// <summary>
/// The saved-clip feedback on Linux: the same confirmation sound the Windows ExportNotification
/// plays (including the embedded DefaultSound.wav resource, which the csproj ships on both
/// platforms), and a visual side routed by config.NotificationType -- the custom Medal-style X11
/// overlay card (LinuxOverlayNotification), the session's own desktop toast (D-Bus
/// org.freedesktop.Notifications, notify-send as fallback), or nothing at all for Sound Only.
/// Everything here is fire-and-forget: a missing helper binary or a headless notification daemon
/// must never fail an export that already wrote its bytes.
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
    /// The visual announcement of a SAVED CLIP, routed by config.NotificationType -- exactly the
    /// branches the Windows ScreenCapture walks. Sound Only shows nothing.
    /// </summary>
    public static void ShowClipSaved(ClippyConfig config, string title, string body) =>
        ShowStatus(config, title, body);

    /// <summary>
    /// A status announcement -- update checks, notification previews, update progress. Same
    /// routing as the clip card with two differences:
    ///
    /// <list type="bullet">
    /// <item><paramref name="forceVisual"/> makes Sound Only still show the visual: the result of
    /// "Check for Updates" must reach the user even when clip notifications are off -- in the
    /// overlay card when it can come up, in the desktop toast otherwise.</item>
    /// <item>the overlay's failure ALWAYS falls back to the desktop toast here: an update status
    /// that vanishes because X11 was unavailable is worse than no status at all.</item>
    /// </list>
    /// Every path is fire-and-forget: a missing daemon must never fail the caller.
    /// </summary>
    public static void ShowStatus(ClippyConfig config, string title, string message, bool forceVisual = false)
    {
        var type = config.NotificationType;
        var isOverlay = string.Equals(type, "overlay", StringComparison.OrdinalIgnoreCase);
        var isNone = string.Equals(type, "none", StringComparison.OrdinalIgnoreCase);

        if (isNone && !forceVisual)
            return; // Sound Only: nothing visual unless the caller insists.

        if (isOverlay || (isNone && forceVisual))
        {
            var overlay = AcquireOverlay();
            if (overlay is not null && overlay.Show(title, message))
                return;
            // The card could not come up (no X11, no Xft): the desktop toast takes over.
        }

        SendDesktopNotification(title, message);
    }

    // The overlay is process-wide: one X11 connection and window, reused by every export.
    private static readonly object overlayGate = new();
    private static LinuxOverlayNotification? overlay;
    private static bool overlayFailed;

    private static LinuxOverlayNotification? AcquireOverlay()
    {
        lock (overlayGate)
        {
            if (overlayFailed)
                return null;
            if (overlay is null)
            {
                var candidate = new LinuxOverlayNotification();
                candidate.Start();
                if (!candidate.IsReady)
                {
                    // Headless or Xft-less session: remember the failure once, never retry it per
                    // export -- the fallback below is the only path from here on.
                    candidate.Dispose();
                    overlay = null;
                    overlayFailed = true;
                    return null;
                }
                overlay = candidate;
                Console.WriteLine("Export: the game overlay card is up (Medal style).");
            }
            return overlay;
        }
    }

    /// <summary>The system toast: D-Bus Notify first, notify-send as the portable fallback.</summary>
    private static void SendDesktopNotification(string title, string body)
    {
        _ = Task.Run(() =>
        {
            if (TryNotifyViaDbus(title, body))
                return;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "notify-send",
                    UseShellExecute = false,
                };
                foreach (var argument in new[] { "-a", "Clippy", "-i", "clippy", "-t", "3000", title, body })
                    startInfo.ArgumentList.Add(argument);
                using var process = Process.Start(startInfo);
                process?.WaitForExit(5000);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Headless session or no notification daemon: the sound already did the announcing.
                Console.WriteLine($"Export: notification daemon unreachable ({ex.GetType().Name}).");
            }
        });
    }

    /// <summary>
    /// A org.freedesktop.Notifications.Notify call over this program's own D-Bus wire layer, on a
    /// short-lived connection of its own: the tray's connection serves its menu on a dedicated
    /// dispatch thread and must not share it. The desktop-entry hint is what makes KDE label the
    /// toast "Clippy".
    /// </summary>
    private static bool TryNotifyViaDbus(string title, string body)
    {
        try
        {
            using var bus = LinuxDbus.Connect();
            bus.Hello();
            bus.Call("org.freedesktop.Notifications", "/org/freedesktop/Notifications",
                "org.freedesktop.Notifications", "Notify",
                "susssasa{sv}i",
                w =>
                {
                    w.String("Clippy");   // app_name
                    w.UInt32(0);          // replaces_id: every clip is its own toast
                    w.String("clippy");   // app_icon: the hicolor PNG the desktop integration installs
                    w.String(title);      // summary
                    w.String(body);
                    w.WriteStringArray([]); // actions: none
                    w.WriteOptionsDict(new Dictionary<string, (string, Action<DbusWriter>)>
                    {
                        ["desktop-entry"] = ("s", x => x.String("clippy")),
                    });
                    w.Int32(3500);        // expire_timeout, ms
                }, 5000);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"Export: the notification daemon refused the D-Bus call ({ex.GetType().Name}: {ex.Message}).");
            return false;
        }
    }
}
#endif
