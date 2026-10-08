namespace Clippy;

/// <summary>
/// Where the debug artifacts go, and how many of them to keep.
///
/// Clips are the product; everything else is evidence. Keeping them in one folder meant every export
/// left behind a pile of .timing.csv, .ffmpeg-debug.log and events-*.csv next to the user's videos,
/// so the logs get their own directory and the videos get a clean one.
/// </summary>
internal static class LogPaths
{
    /// <summary>How many log files to keep. Enough to compare a good run against a bad one.</summary>
    public const int KeepFiles = 10;

    /// <summary>
    /// The absolute log directory for a config: beside the exe, never the working directory.
    ///
    /// One definition, used by both the recorder and the tray's "Open Logs Folder": two separate
    /// computations of "where are the logs" would eventually disagree and open an empty folder.
    /// </summary>
    public static string ResolveDirectory(ClippyConfig config) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.LogsFolder));

    /// <summary>
    /// Places a session's log file, named after the media it belongs to so a video and its log still
    /// look like a pair. logDirectory null means "beside the media", which is what the one-shot
    /// diagnostic modes use.
    /// </summary>
    public static string Resolve(string? logDirectory, string mediaPath, string suffix)
    {
        var directory = logDirectory ?? Path.GetDirectoryName(mediaPath)!;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(mediaPath) + suffix);
    }

    /// <summary>
    /// Deletes all but the newest <see cref="KeepFiles"/> files in the log directory.
    ///
    /// Sorted by write time rather than name: the names carry a timestamp, but a file copied in from
    /// elsewhere does not, and a rotation that keeps the wrong ten files is worse than none.
    /// </summary>
    public static int Rotate(string logDirectory, int keep = KeepFiles)
    {
        if (!Directory.Exists(logDirectory))
            return 0;

        try
        {
            var files = new DirectoryInfo(logDirectory).GetFiles()
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open by a process that has not exited yet. Next startup will get it.
                    Console.WriteLine($"Logs: could not delete {file.Name} ({ex.GetType().Name}).");
                }
            }

            if (files.Count > 0)
                Console.WriteLine($"Logs: rotated {logDirectory}, kept the newest {keep} of " +
                                  $"{files.Count + keep} files.");

            return files.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Logs: rotation skipped ({ex.GetType().Name}: {ex.Message}).");
            return 0;
        }
    }
}