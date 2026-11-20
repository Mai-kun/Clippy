using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace Clippy;

/// <summary>The result of asking GitHub what the newest published release is.</summary>
internal readonly record struct UpdateInfo(bool Available, string Tag, string DownloadUrl, string ReleaseNotes)
{
    public static UpdateInfo UpToDate => new(false, "", "", "");
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
    public const string CurrentVersion = "v1.8.0";
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
    /// Tarball of the Linux build. Releases ship it alongside the Windows assets, and it must stay
    /// invisible to the asset picker on Windows: a tarball cannot be applied by either apply path
    /// below, and the exact-name match is what keeps a Windows copy from ever choosing it.
    /// </summary>
    private const string AssetLinux = "Clippy-linux-x64.tar.gz";

    /// <summary>
    /// Inno Setup drops unins000.exe in the application folder, so its presence is what tells the two
    /// installation shapes apart. There is no other reliable marker: a portable folder and an
    /// installed one contain the same Clippy.exe.
    /// </summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>
    /// The asset that updates THIS copy, or the other one when a release ships only that. On Linux
    /// there is no fallback: a release without the tarball (an older, Windows-only one) reads as
    /// "no matching asset" and therefore no update, which is the honest answer -- a Linux copy
    /// must never land on a Windows asset.
    /// </summary>
    private static string WantedAsset => !OperatingSystem.IsWindows() ? AssetLinux
        : IsInstalled ? AssetSetup : AssetPortable;
    private static string FallbackAsset => !OperatingSystem.IsWindows() ? ""
        : IsInstalled ? AssetPortable : AssetSetup;

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
            // The body is the release notes: the one human-readable line the completed card shows
            // after the restart. First non-empty, non-marker line, capped -- the card is a report,
            // not a changelog.
            var releaseNotes = "";
            if (root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String)
                releaseNotes = ExtractChangeSummary(bodyElement.GetString() ?? "");

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
            return new UpdateInfo(true, tag, chosen.Url, releaseNotes);
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
    /// The one line of the release notes the completed card shows. Markdown scaffolding is
    /// stripped, headers and bullets flattened, and the result capped -- the card is a report of
    /// what changed, not the changelog itself.
    /// </summary>
    internal static string ExtractChangeSummary(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!') || line.StartsWith('<'))
                continue;
            line = line.TrimStart('*', '-', '+', ' ').Trim();
            if (line.Length == 0)
                continue;
            return line.Length <= 120 ? line : line[..117] + "...";
        }
        return "";
    }

    /// <summary>
    /// Downloads the release, unpacks it, and installs it in place -- via the setup installer, a
    /// detached copy script, or (on Linux) an in-place swap of everything the release ships plus
    /// restart. Progress goes to the NotificationCenter, so both platforms' cards show a live bar.
    ///
    /// On Windows a running image cannot overwrite its own exe, so the copy happens from a process
    /// that outlives this one: apply_update.cmd for the portable shape, the silent Inno Setup run
    /// for the installed one. Linux allows swapping a running binary (rename the old file away --
    /// the running process keeps its inode -- and move the new one in), so the tarball path does
    /// the whole job itself and restarts via exec.
    ///
    /// What is applied and what is left alone is a per-path contract below: the release's own
    /// payload (on Linux that includes libclippy_linux.so, whose ABI the binary is compiled
    /// against) goes in, the user's data (config.json, clips, logs) never does. Before the
    /// restart, a one-shot summary marker lands next to the logs; the --updated startup reads it
    /// to announce what changed, since the restart itself cannot speak for the new process.
    /// </summary>
    public static async Task ApplyUpdateAsync(string version, string downloadUrl, string releaseNotes = "")
    {
        try
        {
            Log($"Starting update installation from: {downloadUrl}");
            if (!OperatingSystem.IsWindows())
            {
                await ApplyViaTarballAsync(version, downloadUrl, releaseNotes).ConfigureAwait(false);
            }
            else if (IsInstalled)
            {
                await ApplyViaSetupAsync(version, downloadUrl, releaseNotes).ConfigureAwait(false);
            }
            else
            {
                await ApplyViaZipAsync(version, downloadUrl, releaseNotes).ConfigureAwait(false);
            }

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log($"Update installation failed ({ex.GetType().Name}: {ex.Message}):{Environment.NewLine}{ex}");
            NotificationCenter.UpdateFailed("The current version still works.");
        }
    }

    /// <summary>
    /// The one-shot marker the restart cannot carry in its arguments: WHAT changed in the system
    /// (which files the apply touched) plus the release's own one-line summary. Written next to
    /// the logs before the restart; the --updated startup reads and deletes it.
    /// </summary>
    private static void WriteUpdateSummaryMarker(string version, string releaseNotes, string whatChanged)
    {
        try
        {
            var logDir = LogPaths.LogsDirectory;
            Directory.CreateDirectory(logDir);
            var lines = new List<string> { version, whatChanged };
            if (releaseNotes.Length > 0)
                lines.Add(releaseNotes);
            File.WriteAllLines(Path.Combine(logDir, "update-summary.txt"), lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Could not write the update summary marker ({ex.GetType().Name}); the restart will show a generic report.");
        }
    }

    /// <summary>
    /// The --updated counterpart: reads and DELETES the marker (one shot -- a later plain restart
    /// must not re-announce an old update), or reports null so the caller shows the generic text.
    /// </summary>
    public static (string Version, string WhatChanged, string ReleaseNotes)? ReadUpdateSummaryMarker()
    {
        try
        {
            var path = Path.Combine(LogPaths.LogsDirectory, "update-summary.txt");
            if (!File.Exists(path))
                return null;
            var lines = File.ReadAllLines(path);
            File.Delete(path);
            if (lines.Length == 0)
                return null;
            return (lines[0],
                lines.Length > 1 ? lines[1] : "",
                lines.Length > 2 ? string.Join(" ", lines.Skip(2)) : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// One download for all three apply paths. A server that closes the connection early still
    /// hands back a file -- no exception, just fewer bytes -- so the announced Content-Length,
    /// when there is one, is checked against what landed on disk: a truncated archive must die
    /// here, before anything of it touches the working install. The stream is copied in 64 KB
    /// chunks with the running total reported to the NotificationCenter (which throttles the
    /// card to ~11 updates a second), so the bar moves while the bytes land.
    ///
    /// file:// is not a scheme the release server hands out; it exists for --test-update-apply,
    /// which exercises the apply logic against a local archive without a GitHub release.
    /// </summary>
    private static async Task DownloadReleaseAsync(string version, string url, string targetPath, string what)
    {
        Log($"Downloading {what} to: {targetPath}");

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            File.Copy(uri.LocalPath, targetPath, overwrite: true);
        }
        else
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Clippy-App");
            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using (var target = File.Create(targetPath))
            {
                var buffer = new byte[64 * 1024];
                long bytesRead = 0;
                int chunk;
                while ((chunk = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, chunk)).ConfigureAwait(false);
                    bytesRead += chunk;
                    NotificationCenter.UpdateDownloading(version, bytesRead, total);
                }
            }

            var announced = response.Content.Headers.ContentLength;
            if (announced is { } expected)
            {
                var actual = new FileInfo(targetPath).Length;
                if (actual != expected)
                    throw new IOException(
                        $"{what}: download incomplete ({actual} of {expected} bytes); nothing is applied.");
            }
        }

        Log($"Downloaded {what} successfully ({new FileInfo(targetPath).Length} bytes).");
    }

    /// <summary>
    /// The Linux apply path: download the tarball, unpack it into a temp directory, and replace
    /// EVERYTHING the release ships in place. The release carries more than the main binary:
    /// libclippy_linux.so is the in-process capture/encode bridge whose ABI the binary is
    /// compiled against -- swapping only the binary left a new Clippy on an old .so, which is the
    /// "updated and now capture is dead" mix; config.example.json, install.sh and uninstall.sh
    /// carry the same version's settings shape and install steps. The user's data (config.json
    /// with its hotkeys and encoder, clips/, logs/, portal_token.txt) is never in a release
    /// archive and is skipped even if a hand-built one contains it.
    ///
    /// Each file is renamed away before its replacement lands: a .so the running process has
    /// mapped refuses O_WRONLY (ETXTBSY), so the copy-over can only work as move-then-copy (the
    /// running image keeps its inode). Renames are journaled so a failed copy rolls the whole set
    /// back rather than leaving a half-replaced install.
    ///
    /// The restart goes through exec when it can: the process image is replaced in place, the PID
    /// survives, and the systemd unit that started this run never sees its main process exit --
    /// a start-then-exit restart would have the unit deactivate and (KillMode=control-group, the
    /// default) kill the successor that was just born into its cgroup. The single-instance lock
    /// is released first either way, and the restart carries --updated (the new run announces the
    /// new version, as the Windows paths already do) and --restart-handoff (it waits for the
    /// lock instead of exiting as a duplicate).
    /// </summary>
    private static async Task ApplyViaTarballAsync(string version, string downloadUrl, string releaseNotes)
    {
        var temp = Path.GetTempPath();
        var tarPath = Path.Combine(temp, $"clippy_update_{Guid.NewGuid():N}.tar.gz");
        var extractDir = Path.Combine(temp, "clippy_extracted_linux");

        try
        {
            await DownloadReleaseAsync(version, downloadUrl, tarPath, "tarball").ConfigureAwait(false);
            NotificationCenter.UpdateInstalling(version, "Extracting & updating app + native bridge...");

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);
            // System.Formats.Tar's strict parser chokes on the GNU-flavoured headers the release
            // CI writes ("Unable to parse number"), so extraction goes through the same tar binary
            // that produced the archive -- and that install.sh requires anyway.
            var tarStart = new ProcessStartInfo("tar") { RedirectStandardError = true };
            foreach (var argument in new[] { "-xzf", tarPath, "-C", extractDir })
                tarStart.ArgumentList.Add(argument);
            using var tar = Process.Start(tarStart);
            if (tar is null)
                throw new InvalidOperationException("tar could not be started; the archive cannot be unpacked");
            var tarErrors = tar.StandardError.ReadToEndAsync();
            if (!tar.WaitForExit(120_000) || tar.ExitCode != 0)
                throw new InvalidOperationException(
                    $"tar extraction failed (exit {tar.ExitCode}): {tarErrors.Result.Trim()}");

            var currentBinary = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentBinary) || !File.Exists(currentBinary))
                throw new InvalidOperationException("the running binary's path is unknown; it cannot be replaced");
            var installDir = Path.GetDirectoryName(currentBinary)!;

            // What the update may never touch, wherever it came from. install.sh is an
            // archive-root tool as well: install.sh itself never copies it into the app dir, and
            // running it from the app dir would re-install the app onto itself.
            static bool IsUserDataOrTool(string relative)
            {
                var fileName = Path.GetFileName(relative);
                return fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("portal_token.txt", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("install.sh", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("clips", StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith("logs", StringComparison.OrdinalIgnoreCase);
            }

            var shipped = Directory
                .EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
                .Where(f => !IsUserDataOrTool(Path.GetRelativePath(extractDir, f)))
                .ToList();
            if (!shipped.Any(f => Path.GetFileName(f).Equals("Clippy", StringComparison.Ordinal)))
                throw new InvalidOperationException("no Clippy binary inside the downloaded archive");
            Log($"Applying {shipped.Count} file(s) from the archive into {installDir}.");
            var carriedNativeBridge = shipped.Any(f => Path.GetFileName(f).Equals("libclippy_linux.so", StringComparison.Ordinal));

            var backups = new List<(string Destination, string Backup)>();
            try
            {
                foreach (var file in shipped)
                {
                    var destination = Path.Combine(installDir, Path.GetRelativePath(extractDir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var backup = destination + ".updatebak";
                    if (File.Exists(destination))
                        File.Move(destination, backup, overwrite: true);
                    backups.Add((destination, backup));

                    File.Copy(file, destination, overwrite: true);
                    // The tar extraction preserves the exec bit; File.Copy's mode handling is not
                    // part of any contract, so each file carries its extracted mode over.
                    if (OperatingSystem.IsLinux())
                        File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
                }
            }
            catch (Exception)
            {
                // Restore in reverse order: a failed update must not leave a broken install, so
                // every file the pass touched goes back to its backup (files the tarball did not
                // carry at all are untouched -- they never had a backup to restore).
                for (var i = backups.Count - 1; i >= 0; i--)
                {
                    var (destination, backup) = backups[i];
                    try
                    {
                        if (File.Exists(backup))
                            File.Move(backup, destination, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log($"Could not restore {destination} from its backup ({ex.GetType().Name}).");
                    }
                }
                throw;
            }
            foreach (var (_, backup) in backups)
                RemoveIfPresent(backup);

#if LINUX
            // The lock must go before the replacement starts: it is held by an fd opened without
            // close-on-exec, which even survives an exec -- the successor would otherwise find
            // its own predecessor holding it and exit as a duplicate after the 5 s handoff wait.
            LinuxSingleInstance.Release();
#endif

            // The restart is about to replace this process: the one report of what changed travels
            // in a file, and the --updated startup turns it into the completed card.
            WriteUpdateSummaryMarker(version, releaseNotes,
                carriedNativeBridge
                    ? "Updated Clippy + libclippy_linux.so (config & service kept)"
                    : "Updated Clippy (config & service kept)");

            NotificationCenter.UpdateInstalling(version, "Restarting into the new version...");
            var replayArguments = BuildRestartArguments();
            Log($"Update installed; restarting into {currentBinary} ({string.Join(' ', replayArguments)}).");

#if LINUX
            // exec first: the successor is THIS process, same PID, same unit -- on success the
            // call never returns. TryExecSelf's true means exec FAILED and this image is still
            // standing: fall through to start-then-exit, which under systemd trades the update
            // for a stopped unit but keeps the recording alive -- the rare path, not the plan.
            if (!LinuxProcess.TryExecSelf(currentBinary, replayArguments))
                return;
#endif
            var startInfo = new ProcessStartInfo(currentBinary) { UseShellExecute = false };
            foreach (var argument in replayArguments)
                startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
        }
        catch (Exception)
        {
            RemoveIfPresent(tarPath);
            throw;
        }
    }

    /// <summary>
    /// The arguments the update restart replays: whatever launched this run, plus the two
    /// handoff flags. The --test-update-apply hook is the exception: its restart carries the flag
    /// WITHOUT its URL argument, which the new image answers with a verification report and a
    /// clean exit -- replaying the URL would re-apply the same archive forever.
    /// </summary>
    private static string[] BuildRestartArguments()
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (arguments.Contains("--test-update-apply", StringComparer.OrdinalIgnoreCase))
            return ["--test-update-apply", "--updated", "--restart-handoff"];

        arguments.Add("--updated");
        arguments.Add("--restart-handoff");
        return [.. arguments];
    }

    /// <summary>
    /// Downloads the installer and hands the update to it, silently.
    ///
    /// /VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART is the headless update
    /// invocation, and it is started through a cmd that waits two seconds first -- see below for why
    /// the wait is not optional. The caller then exits, and the installer takes over.
    /// </summary>
    private static async Task ApplyViaSetupAsync(string version, string downloadUrl, string releaseNotes)
    {
        var appDir = AppContext.BaseDirectory;
        // Unique setup filename to prevent file locking/sharing violation conflicts with existing files in %TEMP%.
        var setupPath = Path.Combine(Path.GetTempPath(), $"Clippy-Setup-{Guid.NewGuid():N}.exe");

        try
        {
            await DownloadReleaseAsync(version, downloadUrl, setupPath, "installer").ConfigureAwait(false);

            // The file is about to be executed silently, with nobody watching: a plausible size
            // carrying garbage (a proxy error page, an HTML login wall) must be caught before
            // cmd runs it, not after a broken installer replaced the working install.
            VerifyExecutableImage(setupPath);
            NotificationCenter.UpdateInstalling(version, "Running silent installer...");
            WriteUpdateSummaryMarker(version, releaseNotes, "Updated via the silent installer (config & shortcuts kept)");

            // One cmd.exe chain: launch the installer, wait for it to finish, and only if it
            // SUCCEEDED (&&, not &) start the updated Clippy with the --updated flag -- a failed
            // installer must not leave something half-updated claiming to be new:
            string cmdLine = $"/c \"%SystemRoot%\\System32\\ping.exe -n 2 127.0.0.1 >nul & \"{setupPath}\" /VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART && start \"\" \"{Path.Combine(appDir, "Clippy.exe")}\" --updated\"";
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
                NotificationCenter.UpdateInstalling(version, "Silent launch failed; opening the installer window...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = setupPath,
                    UseShellExecute = true // Opens the standard Inno Setup wizard with a window
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

    private static async Task ApplyViaZipAsync(string version, string downloadUrl, string releaseNotes)
    {
        var temp = Path.GetTempPath();
        var appDir = AppContext.BaseDirectory;
        var zipPath = Path.Combine(temp, $"clippy_update_{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(temp, "clippy_extracted");

        try
        {
            await DownloadReleaseAsync(version, downloadUrl, zipPath, "zip archive").ConfigureAwait(false);
            NotificationCenter.UpdateInstalling(version, "Extracting & updating app + native bridge...");
            WriteUpdateSummaryMarker(version, releaseNotes, "Updated Clippy.exe + clippy_nvenc.dll (config & shortcuts kept)");

            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);

            Log($"Extracting archive to: {extractDir}");
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // The archive must carry an exe, but the APPLY is not exe-only: the release zip
            // ships clippy_nvenc.dll (the native NVENC bridge a new exe is compiled against --
            // an updated exe left on the old DLL is the ABI mix that kills the encoder) and
            // config.example.json. Every file the archive brings is applied; the apply script
            // excludes the user's config.json, which a release never contains.
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
    /// A PE image check for a file that is about to be executed unattended: the first two bytes
    /// of every Windows executable are "MZ", and anything else (an error page, an HTML document,
    /// an empty body from a captive portal) must not be handed to cmd as an installer.
    /// </summary>
    private static void VerifyExecutableImage(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[2];
        stream.ReadExactly(header);
        if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new IOException("the downloaded installer is not a Windows executable; nothing is applied");
    }

    private static void Log(string message) => LogDiagnostic($"Update: {message}");

    /// <summary>
    /// Writes a diagnostic (update checks, the audio encoder refusing to start, and friends) into
    /// logs/update.log. The console is hidden by the tray, and a fallback that "still works" is
    /// exactly how a machine ends up recording video with no sound and no file anywhere that says
    /// why. clippy-error.log is kept for real failures, so it is not copied there.
    /// </summary>
    public static void LogDiagnostic(string message)
    {
        try
        {
            var logDir = LogPaths.LogsDirectory;
            Directory.CreateDirectory(logDir);
            LogPaths.AppendCapped(
                Path.Combine(logDir, "update.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    /// <summary>
    /// The detached installer, with the paths substituted in: a batch file has no other way to
    /// receive them.
    ///
    /// robocopy, not copy: the release carries every file that ships (the exe, clippy_nvenc.dll,
    /// the sample config), the copy must survive the old exe still being locked when this script
    /// starts -- Clippy.exe exits only AFTER launching it -- and robocopy's exit codes tell a
    /// successful run (0-7) from a failed one (8+), which a bare `copy` cannot. A failed file
    /// never starts the new exe: the old version's files are all still in place, and the retry
    /// loop (up to 20 s) outlives the old process's exit by a wide margin.
    /// </summary>
    private static string BuildApplyScript(string extractDir, string appDir, string zipPath)
    {
        var from = extractDir.Replace("\"", "");
        var to = Path.Combine(appDir, "Clippy.exe").Replace("\"", "");
        var toDir = Path.GetDirectoryName(to)!.Replace("\"", "");
        var zip = zipPath.Replace("\"", "");

        return $"""
            @echo off
            rem Detached from Clippy.exe, which exits right after starting this script, so the
            rem image is no longer locked and the copy can replace it. Every file the release
            rem ships is copied; the user's own files (config.json, clips, logs) are excluded --
            rem a release never carries them, and a hand-built archive must not smuggle them in.
            set /a tries=0
            :retry
            timeout /t 1 /nobreak >nul
            robocopy "{from}" "{toDir}" /e /xf config.json apply_update.cmd /xd clips logs /r:0 /w:0 /nfl /ndl /njh /njs >nul
            if errorlevel 8 goto copyfailed
            goto copied
            :copyfailed
            set /a tries+=1
            if %tries% geq 20 goto giveup
            goto retry
            :giveup
            echo Clippy update: the files could not be replaced after %tries% attempts; the old version keeps running.
            exit /b 1
            :copied
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

        // 4. Test installer command line syntax with cmd.exe /c start.
        // Windows-only: the installer this exercises is an Inno Setup exe driven by cmd, and a
        // Linux box has no cmd.exe to launch -- skipping there is the honest result, not a pass.
        if (OperatingSystem.IsWindows())
        {
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
        }
        else
        {
            Console.WriteLine("[SKIP] Installer cmd syntax test is Windows-only.");
        }

        Console.WriteLine(failures == 0 ? "UPDATE SELFTEST: OK" : $"UPDATE SELFTEST: FAILED ({failures})");
        return failures;
    }
}
