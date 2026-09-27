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
