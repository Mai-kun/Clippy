#if LINUX
using System.Diagnostics;
using System.IO.Compression;

namespace Clippy;

/// <summary>
/// Desktop integration: a .desktop file and an icon under ~/.local/share, so the portal's screen
/// picker says "Clippy" instead of "an unidentifiable application", and the tray icon theme finds
/// IconName=clippy. The icon itself is rendered procedurally -- a paperclip, this program's namesake
/// -- because the Windows app.ico is a build artifact the portable target does not ship.
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
            var needsIcon = !File.Exists(iconPath);

            if (!needsWrite && !needsIcon)
                return;

            if (needsIcon)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                File.WriteAllBytes(iconPath, EncodePng(256, RenderPaperclip(256)));
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

    /// <summary>
    /// The 32x32 tray icon in SNI's IconPixmap format: ARGB32 with every pixel in network byte
    /// order (A, R, G, B from low byte to high), exactly what org.kde.StatusNotifierItem wants.
    /// </summary>
    public static byte[] TrayIconArgb(int size) => RenderPaperclip(size);

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

    // --- The icon renderer ---

    /// <summary>
    /// Renders the paperclip as ARGB (little-endian-friendly) bytes, row-major, top-down: one byte
    /// per channel in A, R, G, B order. <see cref="TrayIconArgb"/> is the same layout.
    /// </summary>
    private static byte[] RenderPaperclip(int size)
    {
        var pixels = new byte[size * size * 4];

        // The clip: a 2.5-turn spiral stroked with anti-aliased lines. Distances are measured in
        // pixels so the stroke width and its one-pixel anti-alias falloff stay fixed at any size.
        const double strokeHalfWidth = 0.030;                     // of the icon's side
        var halfWidth = strokeHalfWidth * size;
        var falloff = 1.5;
        const double metalR = 158, metalG = 178, metalB = 202;    // steel blue
        var segments = BuildClipPath();

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // Distance from this pixel's centre to the path, in pixels.
                double px = x + 0.5, py = y + 0.5;
                var distance = double.MaxValue;
                foreach (var (ax, ay, bx, by) in segments)
                {
                    var dax = ax * size;
                    var day = ay * size;
                    var dbx = bx * size;
                    var dby = by * size;
                    var dx = dbx - dax;
                    var dy = dby - day;
                    var lengthSquared = dx * dx + dy * dy;
                    var t = lengthSquared > 0
                        ? Math.Clamp(((px - dax) * dx + (py - day) * dy) / lengthSquared, 0.0, 1.0)
                        : 0.0;
                    var ex = px - (dax + t * dx);
                    var ey = py - (day + t * dy);
                    distance = Math.Min(distance, Math.Sqrt(ex * ex + ey * ey));
                }

                // Anti-aliased edge: a 1.5-pixel ramp around the stroke.
                var coverage = Math.Clamp((halfWidth + falloff / 2 - distance) / falloff, 0.0, 1.0);
                if (coverage <= 0)
                    continue;

                var offset = (y * size + x) * 4;
                pixels[offset] = (byte)(255 * coverage);        // A
                pixels[offset + 1] = (byte)(metalR * coverage); // R -- premultiplied, which is
                pixels[offset + 2] = (byte)(metalG * coverage); // what ARGB32 wants here anyway
                pixels[offset + 3] = (byte)(metalB * coverage); // B
            }
        }

        return pixels;
    }

    /// <summary>
    /// The clip's centre line: right side down, bottom U-turn, left side up, top U-turn, inner
    /// right side down to just past the middle. Recognisably a paperclip, vertical.
    /// </summary>
    private static List<(double Ax, double Ay, double Bx, double By)> BuildClipPath()
    {
        var segments = new List<(double, double, double, double)>();
        const double rightOuter = 0.62, leftOuter = 0.38, rightInner = 0.62;
        const double top = 0.16, bottom = 0.84, midTop = 0.30, midBottom = 0.62;
        const double radius = 0.12;
        const int arcSteps = 12;

        void Line(double ax, double ay, double bx, double by) => segments.Add((ax, ay, bx, by));
        void Arc(double cx, double cy, double from, double to)
        {
            for (var i = 0; i < arcSteps; i++)
            {
                var a0 = from + (to - from) * i / arcSteps;
                var a1 = from + (to - from) * (i + 1) / arcSteps;
                segments.Add((cx + radius * Math.Cos(a0), cy + radius * Math.Sin(a0),
                              cx + radius * Math.Cos(a1), cy + radius * Math.Sin(a1)));
            }
        }

        Line(rightOuter, top, rightOuter, bottom - radius);
        Arc(0.50, bottom - radius, 0, Math.PI);                    // bottom U-turn
        Line(leftOuter, bottom - radius, leftOuter, midTop + radius);
        Arc(0.50, midTop + radius, Math.PI, 2 * Math.PI);          // top U-turn
        Line(rightInner, midTop + radius, rightInner, midBottom);

        return segments;
    }

    // --- Minimal PNG encoder: signature, IHDR, one zlib-compressed IDAT (filter 0), IEND. ---

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes a 256-entry clip icon as an RGBA8 PNG for the hicolor theme.</summary>
    private static byte[] EncodePng(int size, byte[] argb)
    {
        using var output = new MemoryStream();
        output.Write(PngSignature);

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, size);
        WriteBigEndian(ihdr, 4, size);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type: RGBA
        WriteChunk(output, "IHDR", ihdr);

        // Every row gets a filter byte; filter 0 (none) keeps the encoder honest and simple.
        var raw = new byte[size * (1 + size * 4)];
        for (var y = 0; y < size; y++)
        {
            raw[y * (1 + size * 4)] = 0;
            // Source is ARGB; PNG wants RGBA per pixel.
            for (var x = 0; x < size; x++)
            {
                var src = (y * size + x) * 4;
                var dst = y * (1 + size * 4) + 1 + x * 4;
                raw[dst] = argb[src + 1];
                raw[dst + 1] = argb[src + 2];
                raw[dst + 2] = argb[src + 3];
                raw[dst + 3] = argb[src];
            }
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal))
            zlib.Write(raw);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = BitConverter.GetBytes(data.Length);
        output.Write(length);

        var typeAndData = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
            typeAndData[i] = (byte)type[i];
        data.CopyTo(typeAndData, 4);
        output.Write(typeAndData);

        output.Write(BitConverter.GetBytes(Crc32(typeAndData)));
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    /// <summary>The zlib CRC-32, table-driven, as PNG chunks require.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint[] table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
#endif
