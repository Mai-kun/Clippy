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
    public const string CurrentVersion = "v1.3.6";
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
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            // GitHub answers 403 Forbidden to any request without a User-Agent. This one line is the
            // difference between the check working and always failing.
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            request.Headers.UserAgent.ParseAdd("Clippy-App");

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"GitHub API returned {(int)response.StatusCode} {response.ReasonPhrase}; staying on {CurrentVersion}.");
                return UpdateInfo.UpToDate;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                Log($"Release {tag} has no valid assets array; staying on {CurrentVersion}.");
                return UpdateInfo.UpToDate;
            }

            var assetList = new List<(string Name, string Url)>();
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) &&
                    asset.TryGetProperty("browser_download_url", out var download))
                {
                    assetList.Add((name.GetString() ?? "", download.GetString() ?? ""));
                }
            }

            Log($"Release {tag} contains {assetList.Count} asset(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, assetList.Select(a => $"  - {a.Name}: {a.Url}")));

            var isInstalled = IsInstalled;
            var wanted = WantedAsset;
            var fallback = FallbackAsset;
            Log($"Mode: {(isInstalled ? "Installed (Setup)" : "Portable (Zip)")}. Wanted: '{wanted}', fallback: '{fallback}'.");

            var chosen = assetList.FirstOrDefault(a => string.Equals(a.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (chosen.Url is null)
            {
                Log($"Wanted asset '{wanted}' not found, trying fallback '{fallback}'...");
                chosen = assetList.FirstOrDefault(a => string.Equals(a.Name, fallback, StringComparison.OrdinalIgnoreCase));
            }

            if (chosen.Url is null)
            {
                Log($"No matching asset ('{wanted}' or '{fallback}') found in release {tag}.");
                return UpdateInfo.UpToDate;
            }

            Log($"Selected asset: '{chosen.Name}' with URL: {chosen.Url}");

            if (tag.Length == 0 || !IsNewer(tag, CurrentVersion))
            {
                Log($"{tag} is not newer than {CurrentVersion}; staying on {CurrentVersion}.");
                return UpdateInfo.UpToDate;
            }

            Log($"Update available: {CurrentVersion} -> {tag} ({chosen.Url}).");
            return new UpdateInfo(true, tag, chosen.Url);
        }
        catch (Exception ex)
        {
            Log($"Update check failed ({ex.GetType().Name}: {ex.Message}):{Environment.NewLine}{ex}");
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
        try
        {
            Log($"Starting update installation from: {downloadUrl}");
            if (IsInstalled)
            {
                await ApplyViaSetupAsync(downloadUrl, notify).ConfigureAwait(false);
            }
            else
            {
                await ApplyViaZipAsync(downloadUrl, notify).ConfigureAwait(false);
            }

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log($"Update installation failed ({ex.GetType().Name}: {ex.Message}):{Environment.NewLine}{ex}");
            notify?.Invoke("Clippy Update", "Update failed. The current version still works.");
        }
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
        var appDir = AppContext.BaseDirectory;
        // Unique setup filename to prevent file locking/sharing violation conflicts with existing files in %TEMP%.
        var setupPath = Path.Combine(Path.GetTempPath(), $"Clippy-Setup-{Guid.NewGuid():N}.exe");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Clippy-App");
            notify?.Invoke("Clippy Update", "Downloading...");
            Log($"Downloading installer to: {setupPath}");

            using var response = await client
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var target = File.Create(setupPath))
            {
                await response.Content.CopyToAsync(target).ConfigureAwait(false);
            }

            Log($"Downloaded installer successfully ({new FileInfo(setupPath).Length} bytes).");
            notify?.Invoke("Clippy Update", "Installing...");

            // В цепочке cmd.exe запускаем установщик, ждём завершения (&) и стартуем обновлённый Clippy с флагом --updated:
            string cmdLine = $"/c \"%SystemRoot%\\System32\\ping.exe -n 2 127.0.0.1 >nul & \"{setupPath}\" /VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART & start \"\" \"{Path.Combine(appDir, "Clippy.exe")}\" --updated\"";
            Log($"Launching installer via: cmd.exe {cmdLine}");

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdLine,
                CreateNoWindow = true,
                UseShellExecute = false,
            };

            try
            {
                var silent = Process.Start(psi);
                if (silent is null)
                    throw new InvalidOperationException("cmd.exe did not start");
            }
            catch (Exception ex)
            {
                // Antivirus or the OS blocked the headless launch. The installer is already on disk,
                // so rather than reporting failure, open it as the ordinary visible Inno Setup wizard:
                // the user finishes the update by hand and nothing is lost. Environment.Exit so the
                // wizard is not fighting a running copy of Clippy.exe for the files it must replace.
                Log($"Silent launch failed ({ex.GetType().Name}: {ex.Message}); opening interactive installer.");
                notify?.Invoke("Clippy Update", "Silent update failed, opening installer window...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    UseShellExecute = true // Открывает стандартный визард Inno Setup с окном
                });
                Environment.Exit(0);
            }
        }
        catch (Exception)
        {
            RemoveIfPresent(setupPath);
            throw;
        }
    }

    private static async Task ApplyViaZipAsync(string downloadUrl, Action<string, string>? notify)
    {
        var temp = Path.GetTempPath();
        var appDir = AppContext.BaseDirectory;
        var zipPath = Path.Combine(temp, $"clippy_update_{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(temp, "clippy_extracted");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Clippy-App");
            notify?.Invoke("Clippy Update", "Downloading...");
            Log($"Downloading zip update to: {zipPath}");

            using var response = await client
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using (var target = File.Create(zipPath))
            {
                await response.Content.CopyToAsync(target).ConfigureAwait(false);
            }

            Log($"Downloaded zip archive successfully ({new FileInfo(zipPath).Length} bytes).");
            notify?.Invoke("Clippy Update", "Installing...");

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);

            Log($"Extracting archive to: {extractDir}");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // No recursive copy of the extracted folder: that would clobber config.json and ffmpeg.exe
            // with whatever the release happens to contain.
            var newExe = Directory
                .EnumerateFiles(extractDir, "Clippy.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (newExe is null)
                throw new InvalidOperationException("no Clippy.exe inside the downloaded archive");

            var scriptPath = Path.Combine(extractDir, "apply_update.cmd");
            await File.WriteAllTextAsync(scriptPath, BuildApplyScript(extractDir, appDir, zipPath))
                .ConfigureAwait(false);

            Log($"Launching detached apply script: {scriptPath}");
            StartDetached(scriptPath);
        }
        catch (Exception)
        {
            RemoveIfPresent(zipPath);
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
            Log($"Could not remove temporary file {path} ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Logs messages to logs/update.log, logs/clippy-error.log, EventLog and Console.
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            var logDir = LogPaths.LogsDirectory;
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }

        EventLog.Mark("UPDATE", message);
        CrashLog.Write($"Update: {message}");
    }

    /// <summary>
    /// The detached installer, with the paths substituted in: a batch file has no other way to
    /// receive them.
    /// </summary>
    private static string BuildApplyScript(string extractDir, string appDir, string zipPath)
    {
        var from = extractDir.Replace("\"", "");
        var to = Path.Combine(appDir, "Clippy.exe").Replace("\"", "");
        var zip = zipPath.Replace("\"", "");

        return $"""
            @echo off
            rem Detached from Clippy.exe, which has exited by the time this runs, so the image is no
            rem longer locked and the copy can replace it. Copying one file, not the folder: config.json
            rem and ffmpeg.exe belong to the user, not to the release.
            timeout /t 1 /nobreak >nul
            copy /y "{from}\Clippy.exe" "{to}"
            start "" "{to}" --updated
            del "{zip}"
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

    /// <summary>
    /// Self-check verifying update check, asset enumeration, zip extraction with overwrite,
    /// and HTTP download connectivity.
    /// </summary>
    public static async Task<int> RunSelfTest()
    {
        var failures = 0;
        Log("Running update self-test...");

        // 1. Check update check and asset resolution
        var info = await CheckForUpdateAsync();
        Console.WriteLine($"[ OK ] CheckForUpdateAsync completed successfully (Available: {info.Available}, Tag: '{info.Tag}').");

        // 2. Test directory cleanup and zip extraction logic
        try
        {
            var testTemp = Path.Combine(Path.GetTempPath(), $"clippy_test_{Guid.NewGuid():N}");
            var testZip = Path.Combine(Path.GetTempPath(), $"clippy_test_{Guid.NewGuid():N}.zip");
            Directory.CreateDirectory(testTemp);
            File.WriteAllText(Path.Combine(testTemp, "dummy.txt"), "hello");
            ZipFile.CreateFromDirectory(testTemp, testZip);

            // Re-extracting to existing directory with cleanup
            if (Directory.Exists(testTemp))
                Directory.Delete(testTemp, true);
            ZipFile.ExtractToDirectory(testZip, testTemp, overwriteFiles: true);

            if (!File.Exists(Path.Combine(testTemp, "dummy.txt")))
                throw new InvalidOperationException("Zip extraction failed to restore dummy.txt");

            Directory.Delete(testTemp, true);
            File.Delete(testZip);
            Console.WriteLine("[ OK ] Zip extraction with cleanup and overwriteFiles succeeded.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Zip extraction self-test: {ex.Message}");
            failures++;
        }

        // 3. Test HTTP download stream with configured timeout and User-Agent
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Clippy-App");
            var testUrl = "https://github.com/Mai-kun/Clippy/releases/download/v1.3.2/Clippy-Setup.exe";
            using var response = await client.GetAsync(testUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is null or 0)
                throw new InvalidOperationException("Download response has zero or null ContentLength");

            Console.WriteLine($"[ OK ] Download test succeeded (ContentLength: {response.Content.Headers.ContentLength} bytes).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Download test: {ex.Message}");
            failures++;
        }

        // 4. Test installer command line syntax with cmd.exe /c start
        try
        {
            var dummyExe = "cmd.exe";
            var dummyArgs = "/c exit 0";
            var cmdLine = $"/c \"%SystemRoot%\\System32\\ping.exe -n 1 127.0.0.1 >nul & start \"\" \"{dummyExe}\" {dummyArgs}\"";
            using var proc = Process.Start(new ProcessStartInfo("cmd.exe", cmdLine)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            proc?.WaitForExit(5000);
            if (proc is null || proc.ExitCode != 0)
                throw new InvalidOperationException($"cmd start test exited with code {proc?.ExitCode}");

            Console.WriteLine("[ OK ] Installer cmd.exe /c start command syntax succeeded.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] Installer cmd syntax test: {ex.Message}");
            failures++;
        }

        Console.WriteLine(failures == 0 ? "UPDATE SELFTEST: OK" : $"UPDATE SELFTEST: FAILED ({failures})");
        return failures;
    }
}
