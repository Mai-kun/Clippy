using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Clippy;

/// <summary>The result of asking GitHub what the newest published release is.</summary>
internal readonly record struct UpdateInfo(bool Available, string Tag, string DownloadUrl)
{
    public static UpdateInfo UpToDate => new(false, "", "");
}

/// <summary>
/// Checks GitHub Releases for a newer build and installs it in place.
///
/// ponytail: JsonDocument, not a deserialised type. Native AOT has no reflection-based JSON, and the
/// one field pair we need does not justify hand-writing a serialisation context for a response that
/// is somebody else's to change.
///
/// The version is a constant rather than the assembly version because a self-updating single file has
/// no way to learn its own release: the binary on disk is whatever the last update wrote, and its PE
/// metadata is not a reliable record of which tag produced it. Bumping it is a deliberate one-line
/// act at release time.
/// </summary>
internal static class UpdateService
{
    private const string CurrentVersion = "v1.3.0";
    private const string ApiUrl = "https://api.github.com/repos/Mai-kun/Clippy/releases/latest";

    /// <summary>Installer, used when the running copy was installed by the setup.</summary>
    private const string AssetSetup = "Clippy-Setup.exe";

    /// <summary>
    /// Zip, used by a copy the user just unpacked somewhere. The name is load-bearing: every release
    /// before v1.0.4 ships this exact asset, and renaming it would leave those builds looking for
    /// something no release contains, quietly ending updates for everyone already on v1.0.3.
    /// </summary>
    private const string AssetPortable = "Clippy-win-x64.zip";

    /// <summary>
    /// Inno Setup drops unins000.exe in the application folder, so its presence is what tells the two
    /// installation shapes apart. There is no other reliable marker: a portable folder and an
    /// installed one contain the same Clippy.exe.
    /// </summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>The asset that updates THIS copy, or the other one when a release ships only that.</summary>
    private static string WantedAsset => IsInstalled ? AssetSetup : AssetPortable;
    private static string FallbackAsset => IsInstalled ? AssetPortable : AssetSetup;

    public static string Version => CurrentVersion;

    /// <summary>
    /// Asks GitHub for the latest release. Every failure -- no network, DNS, rate limit, a changed
    /// response shape -- is absorbed and reported as "no update", because a recorder that refuses to
    /// start because a check failed is worse than one that quietly stays on its version.
    /// </summary>
    public static async Task<UpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            // GitHub answers 403 Forbidden to any request without a User-Agent. This one line is the
            // difference between the check working and always failing.
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.ParseAdd("Clippy-App");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Update: GitHub said {(int)response.StatusCode}; staying on {CurrentVersion}.");
                return UpdateInfo.UpToDate;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            if (tag.Length == 0 || !IsNewer(tag, CurrentVersion))
            {
                Console.WriteLine($"Update: {tag} is not newer than {CurrentVersion}.");
                return UpdateInfo.UpToDate;
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return UpdateInfo.UpToDate;

            var wanted = WantedAsset;
            string? url = null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) &&
                    asset.TryGetProperty("browser_download_url", out var download) &&
                    name.GetString() == wanted)
                {
                    url = download.GetString();
                    break;
                }
            }

            if (url is null)
            {
                // A release published before this build can ship only the other shape, and a user who
                // never installed anything should still be able to update. The setup would install to
                // a different folder, so it is only offered when nothing better exists.
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("name", out var name) &&
                        asset.TryGetProperty("browser_download_url", out var download) &&
                        name.GetString() == FallbackAsset)
                    {
                        url = download.GetString();
                        break;
                    }
                }
            }

            return url is null
                ? UpdateInfo.UpToDate
                : new UpdateInfo(true, tag, url);
        }
        catch (Exception ex)
        {
            // Offline, DNS failure, timeout, cancellation: all the same to a user who just wants to
            // record. Say what happened in the log and carry on.
            Console.WriteLine($"Update: check failed ({ex.GetType().Name}: {ex.Message}); " +
                              $"staying on {CurrentVersion}.");
            return UpdateInfo.UpToDate;
        }
    }

    /// <summary>
    /// Compares two tags as dotted numbers. Anything unparseable counts as "not newer", so a
    /// malformed tag can never make the app offer itself as an update to itself.
    /// </summary>
    private static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => v.TrimStart('v').Split('.')
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .ToArray();

        var a = Parts(candidate);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
                return x > y;
        }

        return false;
    }

    /// <summary>
    /// Downloads the release, unpacks it, and hands the replacement to a detached script.
    ///
    /// A running Windows image cannot overwrite its own exe. The copy therefore has to happen from a
    /// process that outlives this one, which is what apply_update.cmd is for.
    ///
    /// ONLY Clippy.exe is copied. config.json holds the user's hotkeys, encoder and bitrate, and
    /// ffmpeg.exe is 217 MB that has not changed; a blanket folder copy would silently reset both.
    /// </summary>
    public static async Task ApplyUpdateAsync(string downloadUrl, Action<string, string>? notify)
    {
        var temp = Path.GetTempPath();
        var appDir = AppContext.BaseDirectory;

        // An installed copy is updated by the installer, never by dropping an exe on top: the
        // uninstaller, the shortcut and the registry entries would then describe a program that no
        // longer exists, and the next real install would refuse to run. A portable folder has none of
        // that, so there the copy is still the right answer.
        if (IsInstalled)
        {
            await ApplyViaSetupAsync(downloadUrl, notify).ConfigureAwait(false);
            Environment.Exit(0);
            return;
        }

        var zipPath = Path.Combine(temp, "clippy_update.zip");
        var extractDir = Path.Combine(temp, "clippy_extracted");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            notify?.Invoke("Clippy Update", "Downloading...");

            using var response = await client
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var target = File.Create(zipPath))
            {
                await response.Content.CopyToAsync(target).ConfigureAwait(false);
            }

            notify?.Invoke("Clippy Update", "Installing...");

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // No recursive copy of the extracted folder: that would clobber config.json and ffmpeg.exe
            // with whatever the release happens to contain.
            var newExe = Directory
                .EnumerateFiles(extractDir, "Clippy.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (newExe is null)
                throw new InvalidOperationException("no Clippy.exe inside the downloaded archive");

            var scriptPath = Path.Combine(extractDir, "apply_update.cmd");
            await File.WriteAllTextAsync(scriptPath, BuildApplyScript(extractDir, appDir))
                .ConfigureAwait(false);

            StartDetached(scriptPath);
        }
        catch (Exception ex)
        {
            // Leave the old version running. A failed update must never cost the user a working app.
            Console.WriteLine($"Update: install failed ({ex.GetType().Name}: {ex.Message}).");
            notify?.Invoke("Clippy Update", "Update failed. The current version still works.");
            return;
        }

        Environment.Exit(0);
    }

    /// <summary>
    /// Downloads the installer and hands the update to it, silently.
    ///
    /// /VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART is the headless update
    /// invocation, and it is started through a cmd that waits two seconds first -- see below for why
    /// the wait is not optional. The caller then exits, and the installer takes over.
    /// </summary>
    private static async Task ApplyViaSetupAsync(string downloadUrl, Action<string, string>? notify)
    {
        var setupPath = Path.Combine(Path.GetTempPath(), "Clippy-Setup.exe");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            notify?.Invoke("Clippy Update", "Downloading...");

            using var response = await client
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var target = File.Create(setupPath))
            {
                await response.Content.CopyToAsync(target).ConfigureAwait(false);
            }

            notify?.Invoke("Clippy Update", "Installing...");

            // The installer cannot replace Clippy.exe while this process is still tearing itself
            // down: threads are exiting and handles are being closed, and Inno Setup answers that
            // with "the application is running". So the installer is launched by a hidden cmd that
            // first WAITS, and /FORCECLOSEAPPLICATIONS tells it to close whatever is still holding
            // the exe.
            //
            // The wait is `%SystemRoot%\System32\ping.exe -n 3`, not `timeout /t 2`, and both halves
            // of that were measured rather than assumed on this machine:
            //   * `timeout` is not on this PATH at all -- cmd answers "not recognized" and the delay
            //     silently evaporates, which is the exact bug being fixed;
            //   * even by absolute path, `timeout` refuses to wait when stdin is redirected ("Input
            //     redirection is not supported, exiting the process immediately"), and a recorder
            //     started from a script or a service is exactly that case.
            // ping has neither failure mode: it is addressed by absolute path, reads no stdin, and
            // three one-second pings make the two-second pause.
            var cmd = $"/c \"%SystemRoot%\\System32\\ping.exe -n 3 127.0.0.1 >nul & start \"\" \"{setupPath}\" " +
                      "/VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            // Leave the old version running. A failed update must never cost the user a working app.
            Console.WriteLine($"Update: installer path failed ({ex.GetType().Name}: {ex.Message}).");
            notify?.Invoke("Clippy Update", "Update failed. The current version still works.");
            RemoveIfPresent(setupPath);
            throw;
        }
    }

    private static void RemoveIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Update: could not remove {path} ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// The detached installer, with the paths substituted in: a batch file has no other way to
    /// receive them.
    /// </summary>
    private static string BuildApplyScript(string extractDir, string appDir)
    {
        var from = extractDir.Replace("\"", "");
        var to = Path.Combine(appDir, "Clippy.exe").Replace("\"", "");

        return $"""
            @echo off
            rem Detached from Clippy.exe, which has exited by the time this runs, so the image is no
            rem longer locked and the copy can replace it. Copying one file, not the folder: config.json
            rem and ffmpeg.exe belong to the user, not to the release.
            timeout /t 1 /nobreak >nul
            copy /y "{from}\Clippy.exe" "{to}"
            start "" "{to}"
            del "%TEMP%\clippy_update.zip"
            rd /s /q "{from}"
            del "%~f0"
            """;
    }

    /// <summary>
    /// Starts the script windowless. CreateNoWindow with UseShellExecute false is what yields a truly
    /// detached child; a visible cmd window would flash on screen at the worst possible moment.
    /// </summary>
    private static void StartDetached(string scriptPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(scriptPath);

        Process.Start(startInfo);
    }
}
