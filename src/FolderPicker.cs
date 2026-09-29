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
    public static string? Pick(string title)
    {
        var info = new BrowseInfo
        {
            lpszTitle = title,
            ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI | BIF_NEWDIALOGSTYLE,
        };

        var pidl = SHBrowseForFolderW(ref info);
        if (pidl == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(MAX_PATH);
            return SHGetPathFromIDListW(pidl, buffer, buffer.Capacity) ? buffer.ToString() : null;
        }
        finally
        {
            // A leaked pidl is a small permanent leak in a process that may run for days, and the
            // shell does not reclaim it on its own.
            SHFree(pidl);
        }
    }
}