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
/// Preference order in <see cref="PlayExportSaved"/>:
///   1. a config-supplied .wav file (PlaySoundW + SND_FILENAME),
///   2. the default sound compiled INTO this exe (assets\sounds\sound.wav, see Clippy.csproj;
///      PlaySoundW + SND_MEMORY over a pinned buffer -- no file on disk to lose, which matters for
///      a portable install),
///   3. MessageBeep, when neither of the above could be started.
/// Everything goes through winmm's async path: no window, no package, no extra thread.
/// </summary>
internal static partial class ExportNotification
{
    private const uint MB_ICONASTERISK = 0x00000040;

    // PlaySound flags: SND_ASYNC returns immediately instead of blocking the export, SND_FILENAME
    // treats the first argument as a file path rather than an alias in the registry, SND_MEMORY
    // treats it as a pointer to a WAV image in memory, and SND_NODEFAULT stops a missing/invalid
    // sound from silently substituting the default system sound -- the fallback to MessageBeep
    // below is the one and only default path.
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const uint SND_FILENAME = 0x00020000;

    // The embedded default sound, loaded once and pinned for the life of the process. The pin is
    // never released on purpose: SND_ASYNC returns while winmm is still reading the buffer on
    // another thread, so an unpinned array could be relocated mid-playback and the sound would
    // cut out. One ~172 KB array living forever is a cheaper trade than tracking playback end.
    private static readonly byte[]? s_defaultSoundBytes = LoadEmbeddedSound();
    private static readonly IntPtr s_pinnedSoundPtr = InitPinnedPointer();

    /// <summary>
    /// True when this build actually carries the default sound (LogicalName DefaultSound.wav) and
    /// it could be loaded; false means <see cref="PlayExportSaved"/> must fall back to the beep.
    /// Exposed for the smoke test, which is the only thing that notices a botched csproj entry.
    /// </summary>
    internal static bool HasEmbeddedSound => s_pinnedSoundPtr != IntPtr.Zero;

    private static byte[]? LoadEmbeddedSound()
    {
        using var stream = typeof(ExportNotification).Assembly.GetManifestResourceStream("DefaultSound.wav");
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static IntPtr InitPinnedPointer()
    {
        if (s_defaultSoundBytes == null || s_defaultSoundBytes.Length == 0) return IntPtr.Zero;
        var handle = GCHandle.Alloc(s_defaultSoundBytes, GCHandleType.Pinned);
        return handle.AddrOfPinnedObject();
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "MessageBeep")]
    private static partial uint MessageBeep(uint type);

    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundW(string? pszSound, IntPtr hmod, uint fdwSound);

    // Same winmm entry point, but the first argument is a pointer to a WAV image in memory rather
    // than a path; a separate method name because LibraryImport's generated stub takes its name
    // from the C# method, so the two overloads cannot both be called PlaySoundW.
    [System.Runtime.InteropServices.LibraryImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PlaySoundWFromMemory(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    /// <summary>
    /// The configured custom sound as a full path to a file that exists, or null when there is
    /// nothing to play: unset, blank, or pointing at a file that is not on disk. Null is what tells
    /// <see cref="PlayExportSaved"/> to use the embedded default sound instead, so a typo in the
    /// config yields the built-in sound rather than silence.
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
    /// Plays the export confirmation sound. Never throws and never blocks: a failure to play is not
    /// worth interrupting a recording, and everything here is asynchronous anyway.
    /// </summary>
    /// <param name="customSoundPath">
    /// Optional <c>CustomSoundPath</c> from the config; see <see cref="ResolveCustomSound"/> for how
    /// it is interpreted. Empty, unset or missing falls through to the embedded default sound.
    /// </param>
    /// <returns>
    /// True when a .wav actually started playing (custom file or embedded default), false when the
    /// sound had to degrade to -- or ended at -- the system beep. The export path ignores it; the
    /// smoke test asserts on it, because "did not throw" would pass even with a broken buffer.
    /// </returns>
    public static bool PlayExportSaved(string? customSoundPath = null)
    {
        try
        {
            // SND_ASYNC: returns before the file finishes playing, so the export thread is not held
            // for the length of the sound. A false return means winmm would not play it (corrupt
            // header, unsupported format) and -- with SND_NODEFAULT -- would stay silent, which is
            // exactly when we must keep going down the fallback chain instead of staying quiet.
            if (ResolveCustomSound(customSoundPath) is { } custom
                && PlaySoundW(custom, IntPtr.Zero, SND_ASYNC | SND_FILENAME | SND_NODEFAULT))
                return true;

            // No usable custom file: play the sound that lives inside this binary.
            if (s_pinnedSoundPtr != IntPtr.Zero
                && PlaySoundWFromMemory(s_pinnedSoundPtr, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT))
                return true;

            // Neither the config file nor the embedded copy could be started.
            MessageBeep(MB_ICONASTERISK);
            return false;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or DllNotFoundException
            or EntryPointNotFoundException)
        {
            // No system sounds available (a headless session, or a stripped-down Windows install).
            return false;
        }
    }
}
