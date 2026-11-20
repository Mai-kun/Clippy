#if LINUX
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// Opening the X11 display in a session that may not have finished setting one up yet.
///
/// XOpenDisplay(null) reads DISPLAY from THIS process's environment, and a recorder started by
/// systemd --user at boot loses that race twice: the X server (XWayland) may not be listening
/// yet, and even once it is, DISPLAY reaches the process only through an environment import the
/// session manager may never have delivered to a service that started earlier. Both failures
/// used to be permanent verdicts for the X11 grab path and the overlay card.
///
/// The probe order below answers both: DISPLAY first (the common case, and the only one a
/// multi-display user really wants), then the well-known X11 sockets that actually exist --
/// /tmp/.X11-unix/XN and $XDG_RUNTIME_DIR/.X11-unix/XN, which Xlib itself connects to for a
/// display named ":N".
/// </summary>
internal static partial class LinuxX11
{
    /// <summary>
    /// Tries DISPLAY, then every display socket that exists. Zero when none answers, or when
    /// libX11 is missing entirely (the caller distinguishes by catching DllNotFoundException,
    /// which propagates out of here).
    /// </summary>
    public static nint TryOpenDisplay()
    {
        var display = XOpenDisplay(null);
        if (display != 0)
            return display;

        foreach (var directory in X11SocketDirectories())
        {
            string[] sockets;
            try
            {
                sockets = Directory.GetFiles(directory, "X?");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                continue; // a missing or unreadable socket directory is just no display there
            }

            foreach (var socket in sockets)
            {
                // "X0" through "X9" by the glob; the trailing digit is the display number.
                var number = Path.GetFileName(socket)["X".Length..];
                if (number.Length != 1 || number[0] is < '0' or > '9')
                    continue;
                display = XOpenDisplay($":{number}");
                if (display != 0)
                    return display;
            }
        }

        return 0;
    }

    private static IEnumerable<string> X11SocketDirectories()
    {
        yield return "/tmp/.X11-unix";
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime))
            yield return Path.Combine(runtime, ".X11-unix");
    }

    [LibraryImport("libX11.so.6", SetLastError = false, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XOpenDisplay(string? name);
}
#endif
