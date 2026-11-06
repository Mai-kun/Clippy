#if LINUX
using System.Diagnostics;
using System.IO.Compression;

namespace Clippy;

/// <summary>
/// Desktop integration: a .desktop file and an icon under ~/.local/share, so the portal's screen
/// picker says "Clippy" instead of "an unidentifiable application", and the tray icon theme finds
/// IconName=clippy. The icon is the largest frame of the embedded app.ico (LinuxIconAssets) -- the
/// same file the Windows build uses for its executable -- so every surface shows the real artwork.
/// The .desktop entry also gives the process a D-Bus identity: the GlobalShortcuts portal refuses
/// an app it cannot name, and it names apps by their desktop entry.
/// </summary>
internal static class LinuxDesktopIntegration
{
    private static bool ensured;

    /// <summary>
    /// Writes ~/.local/share/applications/clippy.desktop (with the current executable's path) and
    /// the icon into the hicolor theme, but only when something actually changed: the file is
    /// rewritten when the binary moved, which is the portable-install case. Never throws -- a
    /// read-only home degrades to the portal's "unidentifiable application" wording, not to a
    /// failed recording.
    /// </summary>
    public static void EnsureDesktopFile()
    {
        if (ensured)
            return;
        ensured = true;

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                return;

            var applicationsDir = Path.Combine(home, ".local", "share", "applications");
            var desktopPath = Path.Combine(applicationsDir, "clippy.desktop");
            var exePath = Environment.ProcessPath ?? AppContext.BaseDirectory;

            // Re-write when the Exec line no longer points at this binary: the exe moved, or an
            // older install left a stale path behind.
            var needsWrite = true;
            if (File.Exists(desktopPath))
            {
                foreach (var line in File.ReadAllLines(desktopPath))
                {
                    if (line.StartsWith("Exec=", StringComparison.Ordinal) &&
                        line[5..].Trim().Trim('"') == exePath)
                    {
                        needsWrite = false;
                        break;
                    }
                }
            }

            var iconPath = Path.Combine(home, ".local", "share", "icons", "hicolor", "256x256", "apps", "clippy.png");

            // The icon bytes come from this build; the file is reinstalled whenever its bytes
            // differ, not only when it is missing -- an icon left behind by an older build (the
            // pre-app.ico procedural paperclip) would otherwise outlive this binary forever.
            var iconBytes = LinuxIconAssets.LargestPngBytes();
            if (iconBytes is null)
            {
                Console.WriteLine("Desktop: no usable icon frame in the embedded app.ico.");
                return;
            }
            var needsIcon = !File.Exists(iconPath)
                || !File.ReadAllBytes(iconPath).AsSpan().SequenceEqual(iconBytes);

            if (!needsWrite && !needsIcon)
                return;

            if (needsIcon)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                File.WriteAllBytes(iconPath, iconBytes);
                Console.WriteLine($"Desktop: icon written to {iconPath}.");
            }

            if (needsWrite)
            {
                Directory.CreateDirectory(applicationsDir);
                // Quoting Exec is required by the desktop-entry spec for paths with spaces.
                File.WriteAllLines(desktopPath,
                [
                    "[Desktop Entry]",
                    "Type=Application",
                    "Name=Clippy",
                    "Comment=Instant replay recorder",
                    $"Exec=\"{exePath}\"",
                    "Icon=clippy",
                    "Terminal=false",
                    "Categories=Utility;AudioVideo;Recorder;",
                    "NoDisplay=true",
                ]);
                Console.WriteLine($"Desktop: {desktopPath} registered for {exePath} -- the portal can identify this app now.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Desktop: could not install the .desktop entry ({ex.Message}); the portal picker may show an unidentifiable application.");
        }
    }

    // --- Autostart toggle (the tray's "Start with System" checkbox) ---
    //
    // Two backends, checked in install order: a systemd --user unit when the session runs one
    // (KDE Plasma, most Wayland compositors), else an XDG autostart entry. install.sh creates
    // the same shapes; these methods let the tray flip it without the user opening a terminal.

    private static string AutostartDesktopPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "autostart", "clippy.desktop");

    private static string ServiceUnitPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "systemd", "user", "clippy.service");

    /// <summary>True when the unit is enabled or the XDG autostart entry exists.</summary>
    public static bool IsAutostartEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add("is-enabled");
            psi.ArgumentList.Add("clippy.service");
            using var probe = Process.Start(psi);
            if (probe is not null)
            {
                probe.StandardOutput.ReadToEnd();
                probe.WaitForExit(3000);
                if (probe.ExitCode == 0)
                    return true;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No systemctl on PATH: fall through to the XDG check.
        }
        return File.Exists(AutostartDesktopPath);
    }

    /// <summary>
    /// Flips autostart for the current binary. Enabling writes the user unit and enables it for
    /// future sessions (starting it here would launch a second recorder next to the running one);
    /// disabling stops nothing, it just removes the boot hook.
    /// </summary>
    public static void SetAutostart(bool enabled)
    {
        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        if (enabled)
        {
            EnsureDesktopFile(); // the menu entry and the portal identification, same install
            if (SystemdUserAvailable())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ServiceUnitPath)!);
                File.WriteAllText(ServiceUnitPath, ServiceUnitText(exe));
                RunSystemctl("daemon-reload");
                RunSystemctl("enable clippy.service");
                Console.WriteLine("Autostart: systemd --user clippy.service enabled (starts on the next login).");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AutostartDesktopPath)!);
                File.WriteAllText(AutostartDesktopPath, AutostartDesktopText(exe));
                Console.WriteLine("Autostart: XDG autostart entry created (systemd --user unavailable).");
            }
        }
        else
        {
            RunSystemctl("disable clippy.service");
            try { if (File.Exists(ServiceUnitPath)) File.Delete(ServiceUnitPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            RunSystemctl("daemon-reload");
            try { if (File.Exists(AutostartDesktopPath)) File.Delete(AutostartDesktopPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best effort */ }
            Console.WriteLine("Autostart: disabled.");
        }
    }

    /// <summary>True when the session actually runs a systemd user instance.</summary>
    private static bool SystemdUserAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add("is-system-running");
            using var probe = Process.Start(psi);
            if (probe is null)
                return false;
            var state = probe.StandardOutput.ReadToEnd().Trim();
            probe.WaitForExit(3000);
            // "running" and "degraded" both mean a live user instance; "offline"/"starting" do not.
            return state is "running" or "degraded";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static void RunSystemctl(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--user");
            foreach (var part in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(part);
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The toggle state is the file system; a failed systemctl call only means the unit
            // stays as-is and the next toggle reports the real state.
        }
    }

    /// <summary>The unit text, matching installer/linux/install.sh byte for byte in shape.</summary>
    private static string ServiceUnitText(string exePath)
    {
        var appDir = Path.GetDirectoryName(exePath);
        return $@"
[Unit]
Description=Clippy Instant Replay Recorder
After=graphical-session.target pipewire.service pipewire-pulse.service
PartOf=graphical-session.target

[Service]
Type=simple
WorkingDirectory={appDir}
ExecStart={exePath}
Restart=on-failure
RestartSec=3

[Install]
WantedBy=graphical-session.target default.target
";
    }

    private static string AutostartDesktopText(string exePath) =>
        $@"
[Desktop Entry]
Type=Application
Name=Clippy
Exec={exePath}
Icon=clippy
Terminal=false
X-GNOME-Autostart-enabled=true
";

}
#endif
