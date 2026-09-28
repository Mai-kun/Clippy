namespace Clippy;

/// <summary>
/// A crash breadcrumb file next to the executable.
///
/// A tray recorder that vanishes leaves nothing behind: the console is hidden by design, so the
/// one diagnostic that would explain the failure is exactly the thing the user cannot see. This
/// writes instead of relying on the console, and it never throws -- a logger that can crash the
/// process it is describing would be worse than no logger at all.
/// </summary>
internal static class CrashLog
{
    private static readonly object Gate = new();
    private static bool broken;

    public static string Path { get; } =
        System.IO.Path.Combine(AppContext.BaseDirectory, "clippy-error.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (broken)
                    return;

                File.AppendAllText(
                    Path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{Environment.NewLine}");
            }

            // Also on the console, which is free and covers the case where it is not hidden.
            Console.Error.WriteLine($"[clippy] {message}");
        }
        catch
        {
            // An unwritable log directory must not turn a recoverable failure into a fatal one.
            broken = true;
        }
    }
}