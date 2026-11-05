using System.Globalization;

namespace Clippy;

/// <summary>
/// The clip-file naming, shared by both platforms. Windows owns the original in
/// ScreenCapture.ClipPath, which cannot be touched or reached from Program.cs any more (the class
/// is Windows-only), so the format lives here for the startup path -- the two must stay identical:
/// an ISO-shaped stamp, zero-padded and widest-field-first, so a plain lexicographic sort IS
/// chronological order, plus a -N suffix when two clips land inside the same second.
/// </summary>
internal static class PlatformClipPath
{
    internal static string ClipPath(string directory, DateTime when)
    {
        var stamp = when.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"clip_{stamp}.mp4");

        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"clip_{stamp}-{n}.mp4");

        return path;
    }
}
