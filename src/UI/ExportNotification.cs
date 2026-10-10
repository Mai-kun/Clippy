using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// Audible confirmation that a clip reached disk.
///
/// The whole point is that the player is in a game and cannot see the console, so the feedback has
/// to arrive through the same headphones they are listening to. MessageBeep plays through the
/// default audio device's system-sound path, which needs no window, no UI thread and no extra
/// package -- SystemSounds would drag in a WinForms dependency this console app does not have.
///
/// A config-supplied .wav wins over that beep: PlaySoundW from winmm.dll plays the file
/// asynchronously through the same audio path, again with no window and no package, and still no
/// extra thread. Whatever fails -- no path, no file, a headless session -- degrades to the beep.
/// </summary>
internal static partial class ExportNotification
{
    private const uint MB_ICONASTERISK = 0x00000040;

    // PlaySound flags: SND_ASYNC returns immediately instead of blocking the export, SND_FILENAME
    // treats the first argument as a file path rather than an alias in the registry, and
    // SND_NODEFAULT stops a missing/invalid file from silently substituting the default system
    // sound -- the fallback to MessageBeep below is the one and only default path.
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "MessageBeep")]
    private static partial uint MessageBeep(uint type);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>
    /// The configured custom sound as a full path to a file that exists, or null when there is
    /// nothing to play: unset, blank, or pointing at a file that is not on disk. Null is what tells
    /// <see cref="PlayExportSaved"/> to fall back to the system beep, so a typo in the config
    /// yields a beep rather than silence.
    ///
    /// A relative path is resolved against the executable's folder and not against the current
    /// directory: a tray app's working directory is wherever its shortcut started it, and a sound
    /// configured next to Clippy.exe must be found from anywhere.
    /// </summary>
    internal static string? ResolveCustomSound(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        var full = Path.GetFullPath(
            Path.IsPathRooted(configured)
                ? configured
                : Path.Combine(AppContext.BaseDirectory, configured));

        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Plays a short system sound. Never throws and never blocks: a failure to beep is not worth
    /// interrupting a recording, and MessageBeep is asynchronous anyway.
    /// </summary>
    /// <param name="customSoundPath">
    /// Optional <c>CustomSoundPath</c> from the config; see <see cref="ResolveCustomSound"/> for how
    /// it is interpreted. Empty, unset or missing falls through to the system beep.
    /// </param>
    public static void PlayExportSaved(string? customSoundPath = null)
    {
        try
        {
            if (ResolveCustomSound(customSoundPath) is { } custom)
            {
                // SND_ASYNC: returns before the file finishes playing, so the export thread is not
                // held for the length of the sound. A false return means winmm would not play it
                // (corrupt header, unsupported format) and -- with SND_NODEFAULT -- would stay
                // silent, which is exactly when the user most needs to hear the fallback beep.
                if (PlaySoundW(custom, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT))
                    return;
            }

            MessageBeep(MB_ICONASTERISK);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or DllNotFoundException
            or EntryPointNotFoundException)
        {
            // No system sounds available (a headless session, or a stripped-down Windows install).
        }
    }
}
