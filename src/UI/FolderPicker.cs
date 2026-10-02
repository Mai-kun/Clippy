using System.Runtime.InteropServices;
using System.Text;

namespace Clippy;

/// <summary>
/// Opens the native Windows folder chooser and returns what the user picked.
/// </summary>
/// <remarks>
/// SHBrowseForFolder rather than an IFileOpenDialog COM object. Both open the real Explorer dialog, but
/// the COM route needs an STA message pump plus an interop declaration per interface, and this tray
/// already owns a message loop; introducing a second one for a single dialog is how a hang gets
/// introduced. The shell call is one P/Invoke and hands back a pidl that this class must free.
/// <para>
/// Returns null when the user cancels. That is not an error and must not be logged as one: cancelling
/// a dialog is the most ordinary thing anyone does with it.
/// </para>
/// <para>
/// MUST be called from an STA thread. Verified on this machine, not assumed: the new-style browse
/// dialog hangs FOREVER on an MTA thread -- SHBrowseForFolderW never returns and never even creates
/// the window -- which is exactly what the tray did when this was called straight from the tray
/// thread's WndProc: the call never returned, so the pump stopped dispatching and the icon went
/// dead for every later click too, with nothing logged and nothing on screen.
/// </para>
/// </remarks>
internal static class FolderPicker
{
    private const uint BIF_RETURNONLYFSDIRS = 0x00000001;
    private const uint BIF_USENEWUI = 0x00000040;
    private const uint BIF_NEWDIALOGSTYLE = 0x00000040;
    private const int MAX_PATH = 260;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo lpbif);

    [DllImport("shell32.dll", EntryPoint = "SHGetPathFromIDListW", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path, int maxPath);

    [DllImport("shell32.dll", EntryPoint = "SHFree", PreserveSig = false)]
    private static extern void SHFree(IntPtr pidl);

    /// <summary>The chosen folder, or null when the dialog was cancelled.</summary>
    /// <remarks>
    /// Runs the shell call on a throwaway STA thread and waits for it, because the caller's thread is
    /// very likely an MTA one -- the tray's is. The caller still waits for the answer, so the tray pump
    /// is blocked while the dialog is up; that costs nothing, because a modal folder dialog is already
    /// taking every click. What it does buy is that the blocking happens on a thread whose apartment
    /// is ours to choose, rather than on whatever the caller happened to be.
    /// <para>
    /// The thread is a background thread, so a wedged dialog cannot keep the process alive: a hang here
    /// costs the user the dialog, not the recorder. SetApartmentState throws if the thread has already
    /// started, hence the order here.
    /// </para>
    /// </remarks>
    public static string? Pick(string title)
    {
        string? picked = null;

        RunOnStaThread(() =>
        {
            var info = new BrowseInfo
            {
                lpszTitle = title,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI | BIF_NEWDIALOGSTYLE,
            };

            var pidl = SHBrowseForFolderW(ref info);
            if (pidl == IntPtr.Zero)
                return;

            try
            {
                var buffer = new StringBuilder(MAX_PATH);
                picked = SHGetPathFromIDListW(pidl, buffer, buffer.Capacity) ? buffer.ToString() : null;
            }
            finally
            {
                // A leaked pidl is a small permanent leak in a process that may run for days, and the
                // shell does not reclaim it on its own.
                SHFree(pidl);
            }
        });

        return picked;
    }

    /// <summary>
    /// Runs body on a background STA thread and waits for it to finish.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so the smoke test can assert the apartment without opening a
    /// dialog: a regression here is invisible in every other way, because the failure mode is a hang
    /// rather than an exception.
    /// </remarks>
    internal static void RunOnStaThread(Action body)
    {
        var thread = new Thread(new ThreadStart(body))
        {
            IsBackground = true,
            Name = "clippy-folder-picker",
        };
        thread.SetApartmentState(ApartmentState.STA);

        // No timeout: a folder dialog has no deadline, and the user may be thinking about it for a
        // minute. What must not happen is the caller proceeding while the dialog is still up, so the
        // join is unconditional.
        thread.Start();
        thread.Join();
    }
}