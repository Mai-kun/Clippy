namespace Clippy;

/// <summary>
/// Where the ffmpeg executable lives.
///
/// A portable install is the point: dropping ffmpeg.exe next to Clippy.exe has to win over anything
/// on PATH, so the whole folder can be copied to another PC and work there. Falling back to the bare
/// name keeps the developer's installed ffmpeg working with no extra setup.
/// </summary>
internal static class FfmpegLocator
{
    private static string? resolved;

    /// <summary>
    /// The full path of a bundled ffmpeg.exe, or null when there is none next to the binary.
    /// </summary>
    public static string? BundledPath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    /// <summary>
    /// What to hand to ProcessStartInfo: the bundled executable's full path when it exists, else the
    /// bare "ffmpeg" for the system PATH. Resolved once, because the answer cannot change mid-run and
    /// File.Exists per capture would be a pointless syscall.
    /// </summary>
    public static string Executable
    {
        get
        {
            if (resolved is not null)
                return resolved;

            var bundled = BundledPath;
            if (File.Exists(bundled))
            {
                Console.WriteLine($"ffmpeg: using the bundled copy at {bundled}");
                resolved = bundled;
            }
            else
            {
                Console.WriteLine($"ffmpeg: none bundled at {bundled}, falling back to PATH");
                resolved = "ffmpeg";
            }

            return resolved;
        }
    }

    /// <summary>
    /// The full path of a bundled ffprobe.exe, or null when there is none next to the binary.
    /// </summary>
    public static string? BundledProbePath => Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

    /// <summary>
    /// The ffprobe to shell out to: the bundled copy when present, else the bare name for PATH.
    /// Same precedence rule as ffmpeg, so a portable folder carries both or neither.
    /// </summary>
    public static string ProbeExecutable
    {
        get
        {
            var bundled = BundledProbePath;
            return File.Exists(bundled) ? bundled : "ffprobe";
        }
    }

    /// <summary>
    /// Whether ffprobe can actually be launched, checked once.
    ///
    /// "Is the file there" and "does it run" are different questions: a bare "ffprobe" that PATH
    /// cannot resolve throws Win32Exception from Process.Start rather than returning null, and that
    /// exception was taking the whole selftest down with it. A Windows CI agent has neither ffmpeg
    /// nor ffprobe installed, so the self-test has to be able to say "skipped" instead.
    /// </summary>
    public static bool ProbeAvailable
    {
        get
        {
            if (probeAvailable is not null)
                return probeAvailable.Value;

            try
            {
                var probe = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ProbeExecutable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                probe.ArgumentList.Add("-version");

                using var process = System.Diagnostics.Process.Start(probe);
                if (process is null)
                {
                    probeAvailable = false;
                }
                else
                {
                    process.WaitForExit(10_000);
                    probeAvailable = true;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.WriteLine($"ffprobe: not runnable ({ex.GetType().Name}: {ex.Message})");
                probeAvailable = false;
            }

            return probeAvailable.Value;
        }
    }

    private static bool? probeAvailable;
}
