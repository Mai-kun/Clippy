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
}
