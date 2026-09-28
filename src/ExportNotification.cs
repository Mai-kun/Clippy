namespace Clippy;

/// <summary>
/// Audible confirmation that a clip reached disk.
///
/// The whole point is that the player is in a game and cannot see the console, so the feedback has
/// to arrive through the same headphones they are listening to. MessageBeep plays through the
/// default audio device's system-sound path, which needs no window, no UI thread and no extra
/// package -- SystemSounds would drag in a WinForms dependency this console app does not have.
/// </summary>
internal static partial class ExportNotification
{
    private const uint MB_ICONASTERISK = 0x00000040;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "MessageBeep")]
    private static partial uint MessageBeep(uint type);

    /// <summary>
    /// Plays a short system sound. Never throws and never blocks: a failure to beep is not worth
    /// interrupting a recording, and MessageBeep is asynchronous anyway.
    /// </summary>
    public static void PlayExportSaved()
    {
        try
        {
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
