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
        failures += Check("Tray menu: last clip is the newest one", LastClip);
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
