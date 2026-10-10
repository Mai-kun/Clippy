using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using DataFlow = NAudio.CoreAudioApi.DataFlow;
using Role = NAudio.CoreAudioApi.Role;

namespace Clippy;

internal static class SmokeTest
{
    public static int Run()
    {
        int failures = 0;
        failures += Check("DXGI: adapters + outputs", Dxgi);
        failures += Check("D3D11: hardware device", D3D11Device);
        failures += Check("WASAPI: endpoints + loopback recorder", Wasapi);
        failures += Check("Media Foundation: startup/shutdown", MediaFoundation);
        failures += Check("Tray menu: bitrate options are consistent", BitrateOptions);
        failures += Check("Tray menu: hotkey choices are unique, bindable and do not clash", HotkeyChoices);
        failures += Check("Notification: supported styles and fallback", NotificationTypes);
        failures += Check("Tray menu: last clip is the newest one", LastClip);
        failures += Check("Export notification: custom sound path resolution", CustomSoundPath);
        failures += Check("Export notification: embedded default sound plays from memory", EmbeddedSound);
        failures += Check("Folder picker: runs on an STA thread", FolderPickerApartment);
        failures += Check("Clip naming: ISO stamp sorts chronologically and never collides", ClipNaming);
        failures += Check("Overlay: card lands on the target monitor and scales with its DPI", OverlayPlacement);
        failures += Check("Overlay: text block is centred and the border is a hairline", OverlayLayout);
        failures += Check("Overlay: rendered card has a neutral hairline and symmetric insets", OverlayPixels);
        failures += Check("Clip stamp: duration rounds to the nearest second", ClipDurationRounding);

        Console.WriteLine(failures == 0 ? "SMOKE: OK" : $"SMOKE: FAILED ({failures})");
        return failures;
    }

    private static int Check(string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"[ OK ] {name}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The hotkey table has three ways to be wrong that no compiler can see and that all of them
    /// only show up as a menu item that does nothing:
    ///
    ///   * two rows naming the same key, so one check mark wins and the other item is decoration;
    ///   * two different names that parse to the SAME virtual key (which is why the keypad rows are
    ///     checked against the parser rather than trusted), so the hook would bind one and ignore
    ///     the other;
    ///   * a command id that collides with another menu item -- the same failure the bitrate table
    ///     has, where one of the two entries becomes unselectable while the other keeps its mark.
    ///
    /// The two id ranges are also checked against each other: the short and long menus sit in
    /// separate blocks, and overlapping blocks would make "which clip is this key for?" a coin toss.
    /// </summary>
    private static void HotkeyChoices()
    {
        var choices = TrayIcon.HotkeyChoices;
        if (choices.Length == 0)
            throw new InvalidOperationException("a hotkey submenu would be empty");

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenVks = new HashSet<int>();
        var ids = new HashSet<uint>();
        var shortRange = TrayIcon.HotkeyChoices.Length;

        for (var i = 0; i < choices.Length; i++)
        {
            var (key, label) = choices[i];
            if (!seenKeys.Add(key))
                throw new InvalidOperationException($"'{key}' is offered twice in the hotkey menus");

            if (!ClippyConfig.TryParseHotkey(key, out var vk))
                throw new InvalidOperationException($"'{key}' is in the menu but the config cannot parse it");

            if (!seenVks.Add(vk))
                throw new InvalidOperationException($"'{key}' maps to virtual key 0x{vk:X2}, which another choice already uses");

            foreach (var id in new[] { TrayIcon.HotkeyShortBase + (uint)i, TrayIcon.HotkeyLongBase + (uint)i })
            {
                if (!ids.Add(id))
                    throw new InvalidOperationException($"menu id {id} is used twice");
                if (id >= TrayIcon.HotkeyShortBase && id < TrayIcon.HotkeyShortBase + choices.Length
                    && id >= TrayIcon.HotkeyLongBase && id < TrayIcon.HotkeyLongBase + choices.Length)
                    throw new InvalidOperationException($"menu id {id} falls into both hotkey ranges");
            }
        }

        // The ranges must also stay clear of every id the rest of the menu already uses, or a
        // hotkey row would quietly steal another menu's click.
        foreach (var option in TrayIcon.BitrateOptions)
        {
            foreach (var id in new[] { TrayIcon.HotkeyShortBase, TrayIcon.HotkeyLongBase })
            {
                if (id <= option.Id && option.Id < id + (uint)shortRange)
                    throw new InvalidOperationException($"hotkey range starting at {id} covers bitrate id {option.Id}");
            }
        }
    }

    /// <summary>
    /// The tray's bitrate table has to satisfy two things that only fail at runtime, and both fail
    /// silently: an id used twice makes one of the two rates unselectable while the other one still
    /// gets its check mark, and a value outside the config's clamp is written to config.json and
    /// then replaced with 6 on the next load, so the menu would offer a setting that does not stick.
    /// </summary>
    private static void BitrateOptions()
    {
        if (TrayIcon.BitrateOptions.Length == 0)
            throw new InvalidOperationException("the bitrate submenu would be empty");

        var ids = new HashSet<uint>();
        foreach (var option in TrayIcon.BitrateOptions)
        {
            if (!ids.Add(option.Id))
                throw new InvalidOperationException($"menu id {option.Id} is used twice");
            if (option.Mbps < ClippyConfig.MinVideoBitrateMbps || option.Mbps > ClippyConfig.MaxVideoBitrateMbps)
                throw new InvalidOperationException(
                    $"{option.Mbps} Mbps is outside the {ClippyConfig.MinVideoBitrateMbps}-{ClippyConfig.MaxVideoBitrateMbps} range the config accepts");
            if (option.Label.Length == 0 || !option.Label.StartsWith($"{option.Mbps} Mbps", StringComparison.Ordinal))
                throw new InvalidOperationException($"label '{option.Label}' does not name {option.Mbps} Mbps");
            Console.WriteLine($"       {option.Label}");
        }
    }

    /// <summary>
    /// The three notification styles are mutually exclusive and the default is "overlay". This is the
    /// one place that can catch a drift between the tray menu's three command ids, the config values
    /// they write and the helper properties the routing code branches on -- if any of those disagree
    /// the wrong (or no) card shows, which is invisible until a clip is saved.
    /// </summary>
    private static void NotificationTypes()
    {
        var expected = new[] { "overlay", "windows_toast", "none" };
        if (ClippyConfig.SupportedNotificationTypes.Length != expected.Length)
            throw new InvalidOperationException(
                $"expected {expected.Length} notification styles, config lists {ClippyConfig.SupportedNotificationTypes.Length}");

        foreach (var style in expected)
        {
            if (!ClippyConfig.SupportedNotificationTypes.Contains(style, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"'{style}' is offered in the tray menu but not accepted by the config");

            var config = new ClippyConfig { NotificationType = style };
            var branch = config.UseOverlayNotification ? "overlay"
                : config.UseWindowsToastNotification ? "windows_toast"
                : "none";
            if (!string.Equals(branch, style, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"NotificationType='{style}' routes to '{branch}'");

            Console.WriteLine($"{style} -> {branch}");
        }

        // The default must be the overlay, and exactly one of the two "show something" helpers may be
        // true for it -- otherwise the menu's check marks would disagree about what is selected.
        var def = new ClippyConfig();
        if (!def.UseOverlayNotification || def.UseWindowsToastNotification)
            throw new InvalidOperationException($"default NotificationType must be 'overlay' (got '{def.NotificationType}')");
    }

    /// <summary>
    /// "Open Last Clip" opens the newest file by write time, and every way of getting that wrong is
    /// invisible until a user watches the wrong moment open. Built in a temp folder rather than the
    /// real clips folder, so this never touches or reports someone's actual recordings.
    /// </summary>
    private static void LastClip()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"clippy-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            if (TrayIcon.FindLastClip(folder) is not null)
                throw new InvalidOperationException("an empty folder reported a clip");

            // Names are the reverse of the timestamps ON PURPOSE: an older file sorts first by name, so
            // sorting by name -- or taking the first file the directory hands back -- fails here
            // instead of accidentally agreeing with the right answer.
            var older = Write(folder, "clip-00000001.mp4", DateTime.UtcNow.AddHours(-2));
            var newer = Write(folder, "clip-99999999.mp4", DateTime.UtcNow);
            Write(folder, "notes.txt", DateTime.UtcNow.AddMinutes(1));

            var found = TrayIcon.FindLastClip(folder);
            if (found != newer)
                throw new InvalidOperationException($"expected {Path.GetFileName(newer)}, got {found ?? "null"}");
            Console.WriteLine($"       last clip = {Path.GetFileName(found)} (older: {Path.GetFileName(older)})");

            if (TrayIcon.FindLastClip(Path.Combine(folder, "does-not-exist")) is not null)
                throw new InvalidOperationException("a missing folder reported a clip");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
/// The folder picker hangs FOREVER on an MTA thread, which is what made the tray menu die: the call
/// sat inside the tray thread's message pump and the icon never came back. The failure is a hang, not
/// an exception, so nothing else in the build would ever report it. This asserts the apartment the
/// picker runs on WITHOUT opening a dialog, because a check that needs a human to click Cancel is
/// not a check.
/// </summary>
private static void FolderPickerApartment()
{
    ApartmentState seen = ApartmentState.Unknown;
    var ran = false;

    FolderPicker.RunOnStaThread(() =>
    {
        seen = Thread.CurrentThread.GetApartmentState();
        ran = true;
    });

    if (!ran)
        throw new InvalidOperationException("the picker thread never ran");
    if (seen != ApartmentState.STA)
        throw new InvalidOperationException($"the picker thread is {seen}; SHBrowseForFolder would hang");
    Console.WriteLine($"       picker apartment = {seen}");
}

    /// <summary>
    /// The clip name is the feature, not a detail: clip_yyyy-MM-dd_HH-mm-ss.mp4 exists so that a plain
    /// lexicographic sort -- Explorer's default, a shell glob, `sort` -- reproduces chronological
    /// order. The old HHmmssfff format broke that across midnight and across days (9:30:00 sorted
    /// after 10:15:00, and nothing in the name said which day it was), so both properties are
    /// asserted here rather than trusted.
    ///
    /// Two clips inside one second is the other failure: same name, and the second silently
    /// overwrote a recording the user had asked for.
    /// </summary>
    private static void ClipNaming()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"clippy-naming-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var a = Path.GetFileName(ScreenCapture.ClipPath(folder, new DateTime(2026, 10, 10, 21, 14, 33)));
            if (a != "clip_2026-10-10_21-14-33.mp4")
                throw new InvalidOperationException($"stamp is '{a}', expected 'clip_2026-10-10_21-14-33.mp4'");

            // Written out and re-generated at the SAME instant: the second clip must not take the
            // first one's name.
            var same = ScreenCapture.ClipPath(folder, new DateTime(2026, 10, 10, 21, 14, 33));
            File.WriteAllText(same, "first");
            var again = Path.GetFileName(ScreenCapture.ClipPath(folder, new DateTime(2026, 10, 10, 21, 14, 33)));
            if (again == a)
                throw new InvalidOperationException("a second clip in the same second overwrote the first");

            // The sort property, with the values the old format got wrong: 9:30 must precede 10:15,
            // and 23:59 must precede 00:01 of the next day. Declared deliberately out of order, and
            // compared against the chronological order the names are MEANT to sort into -- so this
            // fails if a lexicographic sort stops reproducing time, which is the whole point of the
            // zero-padded widest-field-first stamp.
            var names = new[]
            {
                Path.GetFileName(ScreenCapture.ClipPath(Path.Combine(folder, "n1"), new DateTime(2026, 10, 10, 10, 15, 0))),
                Path.GetFileName(ScreenCapture.ClipPath(Path.Combine(folder, "n2"), new DateTime(2026, 10, 10, 9, 30, 0))),
                Path.GetFileName(ScreenCapture.ClipPath(Path.Combine(folder, "n3"), new DateTime(2026, 10, 10, 23, 59, 0))),
                Path.GetFileName(ScreenCapture.ClipPath(Path.Combine(folder, "n4"), new DateTime(2026, 10, 11, 0, 1, 0))),
            };
            var chronological = new[]
            {
                "clip_2026-10-10_09-30-00.mp4",
                "clip_2026-10-10_10-15-00.mp4",
                "clip_2026-10-10_23-59-00.mp4",
                "clip_2026-10-11_00-01-00.mp4",
            };
            var sorted = (string[])names.Clone();
            Array.Sort(sorted, StringComparer.Ordinal);
            if (!sorted.SequenceEqual(chronological))
                throw new InvalidOperationException(
                    $"alphabetical order is not chronological: {string.Join(", ", sorted)}");

            Console.WriteLine($"       {a} (second in same second: {again}); sorted = {sorted[0]} .. {sorted[^1]}");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// The overlay is aimed at the monitor holding the foreground window, which on a multi-monitor
    /// desk means its rectangle has a non-zero origin (monitor 2 starts at x=1920, or wherever the
    /// user put it). Two things go wrong there and neither throws: subtracting from the primary
    /// display's width puts the card on the wrong screen, and reporting a 96 DPI scale on a 150%
    /// monitor leaves it at a third of its intended size.
    ///
    /// Only the arithmetic is checked, not the Win32 calls around it: a check that needs a real game
    /// window on a real second monitor cannot run unattended, and the geometry is where the bugs are.
    /// </summary>
    private static void OverlayPlacement()
    {
        // A 1920x1080 secondary monitor to the right of the primary one, and a taskbar taking the
        // bottom 40 px off it (which is why rcWork, not rcMonitor, is what positions the card).
        var second = new MonitorRect(1920, 0, 3840, 1040);

        using var overlay = new OverlayNotification();
        overlay.Layout(96);
        var (x100, y100) = OverlayNotification.CornerInWorkArea(
            second.Right, second.Top, overlay.Width, overlay.Margin);
        if (x100 + overlay.Width != second.Right - overlay.Margin || y100 != second.Top + overlay.Margin)
            throw new InvalidOperationException(
                $"100%: card at ({x100},{y100}) w={overlay.Width} is not inset from ({second.Right},{second.Top})");
        if (x100 < second.Left)
            throw new InvalidOperationException($"100%: card at x={x100} is off the left edge of monitor 2");

        var width100 = overlay.Width;

        // 150% then 200%: every metric must grow by the scale, or the card and its text shrink
        // relative to the game's own UI at that same scale.
        foreach (var (dpi, ratio) in new[] { (120u, 1.25f), (144u, 1.5f), (192u, 2f) })
        {
            overlay.Layout(dpi);
            var expected = (int)MathF.Round(width100 * ratio);
            if (overlay.Width != expected)
                throw new InvalidOperationException(
                    $"{dpi} DPI: width {overlay.Width}, expected {expected} ({ratio} of {width100})");
            if (overlay.TitleFontPx != (int)MathF.Round(OverlayNotification.BaseTitleFontPx * ratio))
                throw new InvalidOperationException(
                    $"{dpi} DPI: title font {overlay.TitleFontPx} px did not scale with the card");
            if (overlay.Height <= overlay.MessageY)
                throw new InvalidOperationException($"{dpi} DPI: the message line falls outside the card");
        }

        Console.WriteLine($"       monitor-2 card at ({x100},{y100}); 320 -> {width100} @96, " +
            string.Join(", ", new[] { 120u, 144u, 192u }.Select(d =>
            {
                overlay.Layout(d);
                return $"{overlay.Width}px @{d}";
            })));
    }

    /// <summary>
    /// The two failure modes of the card's typography, neither of which throws and neither of which
    /// shows up anywhere but on a real monitor next to a game:
    ///
    ///   * unequal insets. The icon column used to push the text 58 px in while the right edge sat
    ///     at 14, so the card read lopsided; the text inset is now the same number on both sides.
    ///   * a text block that is not centred as a whole. Centring each line independently, or leaving
    ///     the two at fixed offsets, drifts as the card and the fonts scale at different rounding
    ///     steps -- the "the text sits low in the box" look.
    ///
    /// Only the arithmetic, for the same reason OverlayPlacement only checks the arithmetic.
    /// </summary>
    private static void OverlayLayout()
    {
        foreach (var (dpi, ratio) in new[] { (96u, 1f), (120u, 1.25f), (144u, 1.5f), (192u, 2f) })
        {
            using var overlay = new OverlayNotification();
            overlay.Layout(dpi);

            // 1 px at 96 DPI and scaling with it from there, like every other dimension on the card:
            // a hairline that stayed 1 physical pixel would look thinner on a 150% display than on a
            // 100% one, and the point of the neutral border is that it reads the same everywhere.
            var expectedBorder = Math.Max(1, (int)MathF.Round(ratio));
            if (overlay.Border != expectedBorder)
                throw new InvalidOperationException(
                    $"{dpi} DPI: border is {overlay.Border} px, expected the {expectedBorder} px hairline");

            var expectedTextX = (int)MathF.Round(14 * ratio);
            if (overlay.TextX != expectedTextX)
                throw new InvalidOperationException(
                    $"{dpi} DPI: text inset {overlay.TextX}, expected {expectedTextX} (symmetric with the right inset)");

            // title + gap + subtitle is what TitleY centres on: the space above the block must equal
            // the space below it, to the pixel the odd/even split loses.
            var expectedTextHeight = (int)MathF.Round(16 * ratio) + (int)MathF.Round(4 * ratio)
                                   + (int)MathF.Round(14 * ratio);
            if (overlay.TextHeight != expectedTextHeight)
                throw new InvalidOperationException(
                    $"{dpi} DPI: text block is {overlay.TextHeight} px tall, expected {expectedTextHeight}");

            var above = overlay.TitleY;
            var below = overlay.Height - (overlay.TitleY + overlay.TextHeight);
            if (Math.Abs(above - below) > 1)
                throw new InvalidOperationException(
                    $"{dpi} DPI: text block is not vertically centred ({above} px above, {below} px below)");

            // The whole block has to stay inside the card, or the subtitle would be drawn over the
            // border. TextHeight already covers both lines plus the gap, so TitleY + TextHeight is
            // the block's bottom edge -- MessageY is inside it, not below it.
            if (overlay.TitleY < overlay.Border ||
                overlay.TitleY + overlay.TextHeight > overlay.Height - overlay.Border)
                throw new InvalidOperationException(
                    $"{dpi} DPI: text block overflows the card (y={overlay.TitleY}.." +
                    $"{overlay.TitleY + overlay.TextHeight} inside a {overlay.Height} px card)");
        }

        Console.WriteLine("       1 px border, symmetric 14 px inset, text block centred at every DPI");
    }

    /// <summary>
    /// The same card, but measured in the pixels GDI actually produced instead of the numbers
    /// Layout() computed. This is what catches a neutral border that is only neutral in the constant
    /// (a brush still holding the old red), an icon column that the layout no longer reserves room
    /// for but whose drawing call was left behind, and text that does not start on the left inset --
    /// none of which the arithmetic above can see, because it never opens a DC.
    ///
    /// It also prints the card as text, which is the only way to eyeball its balance on a machine
    /// with no display attached.
    /// </summary>
    private static void OverlayPixels()
    {
        const string title = "Clip Saved";
        const string message = "0:30 · 12.4 MB";

        using var overlay = new OverlayNotification();
        var pixels = overlay.RenderForTest(title, message, 96);
        var w = overlay.Width;

        // The hairline itself, sampled on the top edge away from the rounded corners: it must be
        // exactly #3D3D3D. Sampling the pixel inside it too is what proves the ring is one pixel
        // wide -- that one has to be the card's #1E1E1E, not the border colour a second time.
        var (b, g, r) = OverlayNotification.PixelAt(pixels, w, w / 2, 0);
        ExpectNeutral(0x3D, r, g, b, "border at top edge");
        (b, g, r) = OverlayNotification.PixelAt(pixels, w, w / 2, 1);
        if (r != 0x1E || g != 0x1E || b != 0x1E)
            throw new InvalidOperationException(
                $"Border is not 1 px: the pixel inside it is #{r:X2}{g:X2}{b:X2}, expected #1E1E1E");

        // The bottom edge must match the top one. A frame that is drawn on the top row but not the
        // last row reads as a card with a missing bottom border -- a GDI round-rect region stops one
        // row short of the rectangle it was given, so this is exactly the seam that can slip.
        (b, g, r) = OverlayNotification.PixelAt(pixels, w, w / 2, overlay.Height - 1);
        ExpectNeutral(0x3D, r, g, b, "border at bottom edge");
        (b, g, r) = OverlayNotification.PixelAt(pixels, w, w / 2, overlay.Height - 2);
        if (r != 0x1E || g != 0x1E || b != 0x1E)
            throw new InvalidOperationException(
                $"Bottom border is not 1 px: the pixel inside it is #{r:X2}{g:X2}{b:X2}, expected #1E1E1E");

        // Ink is measured strictly inside the frame, so the border ring itself is never mistaken for
        // text -- the rightmost column of the bitmap is frame, and counting it would report every
        // line as running off the card.
        var left = int.MaxValue;
        var right = 0;

        MeasureLine(overlay, pixels, title, overlay.TitleY,
            overlay.TitleY + overlay.TitleFontPx, ref left, ref right);
        MeasureLine(overlay, pixels, message, overlay.MessageY,
            overlay.MessageY + overlay.TitleFontPx, ref left, ref right);

        // A glyph's stem starts a hair inside its advance origin, so allow the outer pixel or two;
        // a column reserved for an icon shifts the first ink by tens of pixels, not two.
        if (left > overlay.TextX + 2)
            throw new InvalidOperationException(
                $"Text starts {left} px in, past the {overlay.TextX} px inset plus 2 px of bearing -- " +
                "something is still reserving space on the left");

        var rightLimit = w - overlay.Margin;
        if (right > rightLimit)
            throw new InvalidOperationException(
                $"Text runs to {right} px, past the {rightLimit} px right inset");

        // The card, as text: the balance that is the whole point of this change is visible here
        // rather than only asserted, and a regression shows up in a diff of this line.
        Console.WriteLine($"       {w}x{overlay.Height} px card, border #3D3D3D, text {left}..{right} " +
                          $"of insets {overlay.TextX}..{rightLimit}");
        DumpCard(overlay, pixels);
    }

    private static void ExpectNeutral(int expected, byte r, byte g, byte b, string label)
    {
        if (r != expected || g != expected || b != expected)
            throw new InvalidOperationException(
                $"{label}: got #{r:X2}{g:X2}{b:X2}, expected #{expected:X2}{expected:X2}{expected:X2}");
    }

    /// <summary>
    /// Widens the bounding box of one line's ink to cover both lines passed so far. Rows are scanned
    /// only inside the line's own band, so the other line's ascenders cannot contaminate it.
    /// </summary>
    private static void MeasureLine(
        OverlayNotification overlay, byte[] pixels, string text,
        int rowFrom, int rowTo, ref int left, ref int right)
    {
        var w = overlay.Width;
        var found = false;

        for (var y = rowFrom; y < rowTo; y++)
        {
            for (var x = overlay.Border; x <= w - overlay.Border - 1; x++)
            {
                var (b, g, r) = OverlayNotification.PixelAt(pixels, w, x, y);
                // Anything that is neither the card nor the frame colour is glyph ink.
                if ((r == 0x1E && g == 0x1E && b == 0x1E) || (r == 0x3D && g == 0x3D && b == 0x3D))
                    continue;

                found = true;
                if (x < left) left = x;
                if (x > right) right = x;
            }
        }

        if (!found)
            throw new InvalidOperationException(
                $"'{text}' was not drawn at all, so its placement cannot be measured");
    }

    /// <summary>
    /// Prints the rendered card as ASCII: frame, interior and glyph ink. One character per four
    /// pixels, which keeps a 320 px card inside a line of console output while still showing where
    /// the text sits.
    /// </summary>
    private static void DumpCard(OverlayNotification overlay, byte[] pixels)
    {
        var w = overlay.Width;
        for (var y = 0; y < overlay.Height; y += 2)
        {
            var line = new System.Text.StringBuilder();
            for (var x = 0; x < w; x += 2)
            {
                var (b, g, r) = OverlayNotification.PixelAt(pixels, w, x, y);
                line.Append((r, g, b) switch
                {
                    (0x3D, 0x3D, 0x3D) => '#',   // frame
                    (0x1E, 0x1E, 0x1E) => '.',   // card interior
                    _ => '@',                    // glyph ink
                });
            }

            Console.WriteLine($"       |{line}|");
        }
    }

    /// <summary>
    /// A clip the encoder closes at 29.96 s is a 30 s clip to the player. Truncating instead of
    /// rounding printed "0:29", which reads as a recorder that came up a second short -- the exact
    /// complaint this rounding was introduced for. AwayFromZero, so 179.96 s is 3:00 and not 2:59.
    /// </summary>
    private static void ClipDurationRounding()
    {
        Expect((int)Math.Round(29.96, MidpointRounding.AwayFromZero), 30, "29.96 s");
        Expect((int)Math.Round(179.96, MidpointRounding.AwayFromZero), 180, "179.96 s");
        Expect((int)Math.Round(0.4, MidpointRounding.AwayFromZero), 0, "0.4 s");
        Expect((int)Math.Round(0.5, MidpointRounding.AwayFromZero), 1, "0.5 s (ties go up)");

        Expect(ScreenCapture.ClipStamp(30), "0:30", "stamp of 30 s");
        Expect(ScreenCapture.ClipStamp(180), "3:00", "stamp of 180 s");
        Expect(ScreenCapture.ClipStamp(9), "0:09", "stamp of 9 s");
        Expect(ScreenCapture.ClipStamp(60), "1:00", "stamp of 60 s");

        Console.WriteLine("       29.96 s -> 0:30, 179.96 s -> 3:00");
    }

    private static void Expect(int actual, int expected, string label)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }

    /// <summary>Stand-in for GDI's RECT in the placement check; no Win32 call can take a monitor.</summary>
    private readonly record struct MonitorRect(int Left, int Top, int Right, int Bottom);

    /// <summary>
    /// The configured custom notification sound is resolved the way the config documents it: a
    /// relative path is anchored to the executable's folder -- never to the current directory, which
    /// for a tray app is wherever the shortcut happened to start -- an absolute path is used as it
    /// stands, and anything that does not name an existing file resolves to null so the exporter
    /// falls back to the system beep instead of playing silence.
    /// </summary>
    private static void CustomSoundPath()
    {
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "config.example.json"));

        Expect(ExportNotification.ResolveCustomSound(null), null, "null");
        Expect(ExportNotification.ResolveCustomSound(""), null, "empty");
        Expect(ExportNotification.ResolveCustomSound("   "), null, "whitespace");
        Expect(ExportNotification.ResolveCustomSound("no-such-sound.wav"), null, "missing relative path");
        Expect(ExportNotification.ResolveCustomSound(Path.Combine(AppContext.BaseDirectory, "no-such-sound.wav")),
            null, "missing absolute path");

        // config.example.json is copied next to the binary by the csproj, so both branches resolve
        // to a file that really exists without the test having to create one.
        Expect(ExportNotification.ResolveCustomSound("config.example.json"), expected, "existing relative path");
        Expect(ExportNotification.ResolveCustomSound(expected), expected, "existing absolute path");

        Console.WriteLine($"       custom sound resolves to {expected}");
    }

    /// <summary>
    /// The default notification sound is compiled into the exe (assets\sounds\sound.wav as the
    /// embedded resource DefaultSound.wav) and played through winmm from a pinned in-memory buffer,
    /// so a portable install has nothing extra to ship. Both halves of that can break without any
    /// compiler noticing: a wrong LogicalName in the csproj makes GetManifestResourceStream return
    /// null, and a bad WAV image or unpinned buffer makes PlaySoundW refuse to start -- which is
    /// why the assertion is on PlayExportSaved's return value (a wav really began playing) and not
    /// merely on "it did not throw".
    /// </summary>
    private static void EmbeddedSound()
    {
        if (!ExportNotification.HasEmbeddedSound)
            throw new InvalidOperationException(
                "embedded resource 'DefaultSound.wav' is missing from this build (check Clippy.csproj)");

        if (!ExportNotification.PlayExportSaved(null))
            throw new InvalidOperationException("winmm refused the in-memory default sound");

        Console.WriteLine("       embedded default sound plays from memory");
    }

    private static void Expect(string? actual, string? expected, string label)
    {
        if (actual != expected)
            throw new InvalidOperationException(
                $"{label}: expected {(expected is null ? "null" : $"'{expected}'")}, got {(actual is null ? "null" : $"'{actual}'")}");
    }

    private static string Write(string folder, string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static void Dxgi()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint i = 0; ; i++)
        {
            if (factory.EnumAdapters1(i, out var adapter).Failure)
                break;

            using (adapter)
            {
                var desc = adapter.Description1;
                Console.WriteLine($"       adapter {i}: {desc.Description} (ven {desc.VendorId:X4}:{desc.DeviceId:X4})");

                for (uint j = 0; ; j++)
                {
                    if (adapter.EnumOutputs(j, out var output).Failure)
                        break;

                    using (output)
                    {
                        var od = output.Description;
                        Console.WriteLine($"       output  {j}: {od.DeviceName}, " +
                            $"{od.DesktopCoordinates.Right - od.DesktopCoordinates.Left}x" +
                            $"{od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top}, attached={od.AttachedToDesktop}");
                    }
                }
            }
        }
    }

    private static void D3D11Device()
    {
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        var result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out var device,
            out var featureLevel,
            out var context);

        if (result.Failure)
            throw new InvalidOperationException($"D3D11CreateDevice failed: {result.Description}");

        using (device)
        using (context)
        {
            Console.WriteLine($"       feature level = {featureLevel}");
        }
    }

    private static void Wasapi()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        for (int i = 0; i < endpoints.Count; i++)
        {
            using var endpoint = endpoints[i];
            Console.WriteLine($"       render endpoint {i}: {endpoint.FriendlyName}");
        }

        using var recorder = new WasapiRecorderBuilder()
            .WithDevice(render)
            .WithLoopbackCapture()
            .Build();

        var format = recorder.WaveFormat;
        Console.WriteLine($"       loopback: {recorder.DeviceFriendlyName}, {format.SampleRate} Hz, " +
            $"{format.Channels} ch, {format.BitsPerSample} bit");

        recorder.StartRecording();
        Thread.Sleep(200);
        recorder.StopRecording();
        Console.WriteLine("       capture start/stop ok");
    }

    private static void MediaFoundation()
    {
        MediaFactory.MFStartup(false).CheckError();
        try
        {
            Console.WriteLine("       MFStartup ok");
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }
}
