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
