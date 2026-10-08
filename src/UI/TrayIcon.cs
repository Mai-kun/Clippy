using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// A system-tray icon with a right-click menu, built on raw Win32.
///
/// ponytail: Windows Forms would have given all of this in twenty lines, but it drags the whole
/// WinForms stack into a NativeAOT build that currently has none. Shell_NotifyIcon and a
/// message-only window are a few hundred bytes of interop and keep the binary AOT-clean, which is
/// the point of doing it this way.
///
/// The window is created as HWND_MESSAGE (message-only, parent = -1), so it is never rendered and
/// never appears on the taskbar. It lives on its own thread with its own message pump, because the
/// capture thread is parked in a wait and pumps nothing.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    private const int WM_DESTROY = 0x0002;
    private const int WM_NULL = 0x0000;
    private const int WM_TRAY = 0x8000 + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;
    private const int NIIF_INFO = 0x00000001;

    /// <summary>Identifies our one icon. Shell_NotifyIcon matches on (hWnd, uID), so a NIM_MODIFY
    /// aimed at a notification has to repeat the same pair NIM_ADD used or it silently no-ops.</summary>
    private const uint IconId = 1;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_POPUP = 0x00000010;
    private const uint MF_CHECKED = 0x00000008;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint IDC_OPEN_FOLDER = 1001;
    private const uint IDC_OPEN_CONFIG = 1002;
    private const uint IDC_OPEN_LOGS = 1003;
    private const uint IDC_CHECK_UPDATES = 1005;
    private const uint IDC_EXIT = 1004;
    private const uint IDC_RESTART = 1006;
    private const uint IDC_OPEN_LAST_CLIP = 1007;

    // One id per selectable value across the settings submenus. They are grouped by hundreds so a
    // mis-numbered id lands in an obvious neighbourhood rather than silently colliding with an
    // unrelated action.
    private const uint IDC_SHORT_15 = 1101;
    private const uint IDC_SHORT_30 = 1102;
    private const uint IDC_SHORT_60 = 1103;
    private const uint IDC_LONG_60 = 1201;
    private const uint IDC_LONG_120 = 1202;
    private const uint IDC_LONG_180 = 1203;
    private const uint IDC_LONG_300 = 1204;
    private const uint IDC_RATE_4 = 1301;
    private const uint IDC_RATE_6 = 1302;
    private const uint IDC_RATE_8 = 1303;
    private const uint IDC_RATE_12 = 1304;
    private const uint IDC_RATE_2 = 1305;
    private const uint IDC_RATE_16 = 1306;
    private const uint IDC_RATE_20 = 1307;
    private const uint IDC_RATE_25 = 1308;
    private const uint IDC_RATE_30 = 1309;
    private const uint IDC_RATE_50 = 1310;
    private const uint IDC_ENCODER_DIRECT = 1403;
    private const uint IDC_PICK_FOLDER = 1601;
    private const uint IDC_QUOTA_0 = 1610;
    private const uint IDC_QUOTA_10 = 1611;
    private const uint IDC_QUOTA_20 = 1612;
    private const uint IDC_QUOTA_30 = 1613;
    private const uint IDC_QUOTA_50 = 1614;
    private const uint IDC_ENCODER_NVENC = 1401;
    private const uint IDC_ENCODER_X264 = 1402;
    private const uint IDC_CODEC_HEVC = 1404;
    private const uint IDC_CODEC_H264 = 1405;
    // Two contiguous command-id ranges, one per clip, and one table of choices. The id is the range's
    // base plus the index in the table, so adding a key is a new table row and nothing else -- there
    // is no per-key constant to declare, no case to add, and no way for the menu and the handler to
    // disagree about what a command means. The old fixed presets ("F8/F9", "F9/F10", "F11/F12") are
    // gone: they could only ever set the two bindings together, and the pairs people wanted (F9 for
    // the long clip with something else on the short one) were not among them.
    internal const uint HotkeyShortBase = 3000;
    internal const uint HotkeyLongBase = 4000;

    /// <summary>
    /// The keys both hotkey menus offer, in menu order: F1-F12, then the numeric keypad. The keypad
    /// is there because it is the one block of keys a game leaves free -- F-keys are usually taken,
    /// and on a laptop the numpad doubles as the arrow cluster, so a binding there is not always
    /// reachable either. That is the user's call, not ours, which is why both are offered.
    /// </summary>
    internal static readonly (string Key, string Label)[] HotkeyChoices =
    [
        ("F1", "F1"), ("F2", "F2"), ("F3", "F3"), ("F4", "F4"),
        ("F5", "F5"), ("F6", "F6"), ("F7", "F7"), ("F8", "F8"),
        ("F9", "F9"), ("F10", "F10"), ("F11", "F11"), ("F12", "F12"),
        ("NumPad0", "NumPad 0"), ("NumPad1", "NumPad 1"), ("NumPad2", "NumPad 2"),
        ("NumPad3", "NumPad 3"), ("NumPad4", "NumPad 4"), ("NumPad5", "NumPad 5"),
        ("NumPad6", "NumPad 6"), ("NumPad7", "NumPad 7"), ("NumPad8", "NumPad 8"),
        ("NumPad9", "NumPad 9"),
    ];

    /// <summary>Index of the first keypad row, and the place the separator goes.</summary>
    private const int HotkeySeparatorAfter = 12;

    /// <summary>
    /// Every selectable bitrate: menu id, value written to the config, and the menu label.
    ///
    /// One table so the menu and the click handler cannot drift apart. The RAM figure is the ring
    /// buffer's cost for the ~190 s it holds, extrapolated from the measured 12 Mbps -> ~450 MB
    /// point (~37 MB per Mbps); it is the reason to pick a lower number, so it belongs next to it.
    /// Every value has to sit inside the config's own 2-50 clamp, or the menu would write something
    /// the next load silently replaces. --smoke asserts that, so adding a row cannot break it quietly.
    /// </summary>
    internal static readonly (uint Id, int Mbps, string Label)[] BitrateOptions =
    [
        (IDC_RATE_2, 2, "2 Mbps  (~75 MB RAM)"),
        (IDC_RATE_4, 4, "4 Mbps  (~150 MB RAM)"),
        (IDC_RATE_6, 6, "6 Mbps  (~225 MB RAM)"),
        (IDC_RATE_8, 8, "8 Mbps  (~300 MB RAM)"),
        (IDC_RATE_12, 12, "12 Mbps (~450 MB RAM)"),
        (IDC_RATE_16, 16, "16 Mbps (~600 MB RAM)"),
        (IDC_RATE_20, 20, "20 Mbps (~750 MB RAM)"),
        (IDC_RATE_25, 25, "25 Mbps (~940 MB RAM)"),
        (IDC_RATE_30, 30, "30 Mbps (~1.1 GB RAM)"),
        (IDC_RATE_50, 50, "50 Mbps (~1.9 GB RAM)"),
    ];

    private const int IDI_APPLICATION = 32512;

    /// <summary>
    /// NOTIFYICONDATAW, blittable.
    ///
    /// The string fields MUST be fixed char buffers, not [MarshalAs(ByValTStr)] string. In managed
    /// memory a ByValTStr field is an 8-byte OBJECT REFERENCE, so handing Windows a raw pointer to
    /// such a struct makes it read managed references where it expects inline UTF-16, and
    /// Shell_NotifyIcon fails with an opaque E_FAIL. These fixed buffers make the struct genuinely
    /// blittable, so it can live on the stack with no GC involvement at all.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeoutOrVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public int cbSize;
        public int style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        // LPCWSTR, i.e. a pointer -- NOT an inline buffer. Declaring these as ByValTStr makes the
        // struct 400 bytes instead of 80, and RegisterClassEx then fails with ERROR_INVALID_PARAMETER.
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Msg
    {
        public nint hwnd;
        public int message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    // The struct arguments go across as raw pointers, not as `ref`. The source generator cannot
    // marshal a ByValTStr struct by reference (SYSLIB1051), but these structs are blittable
    // (fixed-size string buffers, no references), so a pointer to the pinned local is correct and
    // is what the marshalled signature would have produced anyway.
    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int Shell_NotifyIcon(int message, void* data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial ushort RegisterClassEx(void* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        int extendedStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindow(string className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hWnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hWnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg message, nint hWnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static partial void PostQuitMessage(int code);

    // The name is a resource id passed as a pointer, not a string: system icons are MAKEINTRESOURCE.
    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadIcon(nint instance, nint name);

    // Extracts the icon embedded in an executable, by path, handing back the 32x32 and the 16x16
    // in one call. This is how the tray picks up whatever ApplicationIcon put in the csproj, so the
    // taskbar, the Alt-Tab entry, the Explorer icon and the tray all show the same artwork instead
    // of drifting apart.
    //
    // ExtractIconEx rather than ExtractIconW because this icon is a size set, not a single image.
    // ExtractIconW can only ever return the largest one, and the tray draws about 16 logical pixels,
    // so the shell would scale 128x128 down on every single repaint and it looks soft.
    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExtractIconEx(string exePath, int iconIndex, out nint large, out nint small, uint count);

    // Only used to pick between the 16x16 and the 32x32 image, never for layout. GetDpiForSystem
    // arrived in Windows 10 1607, well below the 1803 floor that Windows Graphics Capture and the
    // NVENC bridge already impose, so there is no older machine to fall back on.
    [LibraryImport("user32.dll", EntryPoint = "GetDpiForSystem")]
    private static partial uint GetDpiForSystem();

    /// <summary>
    /// Fills the icon handle of the notification data, or falls back to the system icon.
    /// </summary>
    /// <remarks>
    /// NOTIFYICONDATA has one hIcon slot and no small-icon field -- hIconSm belongs to WNDCLASSEX,
    /// not here -- so the size has to be chosen before handing it over rather than left to the shell.
    /// The tray is roughly 16 logical pixels tall, which makes the 16x16 image the right one at
    /// 100% and 150%; only from 200% up does the tray have pixels for the 32x32 to earn.
    /// <para>
    /// The handle is deliberately never destroyed. The shell does not own icons returned by
    /// ExtractIconEx and never frees them, and it has to stay valid for as long as the tray icon
    /// exists; leaking two handles in a process that may run for days is cheaper than handing the
    /// shell a freed icon.
    /// </para>
    /// </remarks>
    private static void LoadApplicationIcons(ref NotifyIconData nid)
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) &&
            ExtractIconEx(path, 0, out var large, out var small, 1) && large != 0)
        {
            // 100% and 150% -> the 16x16, drawn verbatim. 200% and up -> the 32x32, which is the
            // only one of the two with enough pixels for the tray to draw at that size.
            nid.hIcon = small != 0 && GetDpiForSystem() <= 144 ? small : large;
            return;
        }

        Console.WriteLine($"Tray: no embedded icon found in {path ?? "(unknown path)"}, using the system one.");
        nid.hIcon = LoadIcon(0, new nint(IDI_APPLICATION));
    }

    [LibraryImport("user32.dll", EntryPoint = "CreatePopupMenu")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenu(nint menu, uint flags, nuint item, string text);

    // TrackPopupMenuEx is used instead of TrackPopupMenu so the "Ex" dismissal rules apply: with a
    // top-level owner window the menu closes on the next click anywhere, instead of hanging on screen.
    [LibraryImport("user32.dll", EntryPoint = "TrackPopupMenuEx", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int TrackPopupMenuEx(
        nint menu, uint flags, int x, int y, nint hWnd, nint rect);

    [LibraryImport("user32.dll", EntryPoint = "DestroyMenu")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, int message, nint wParam, nint lParam);

    // Kept in a static field for the lifetime of the process: if the GC collected the delegate while
    // the window still exists, the shell's clicks would land in freed memory.
    private static WndProcDelegate? wndProc;

    private readonly ClippyConfig config;
    private readonly Action onExit;
    private readonly string outputFolder;
    private Thread? thread;
    private nint window;
    private volatile bool disposed;

    /// <summary>True only once Shell_NotifyIcon(NIM_ADD) succeeded, so notifications are only
    /// sent when there is an icon to deliver them through.</summary>
    public bool iconAdded;

    // Start() returns as soon as the thread begins, but the icon only exists once NIM_ADD has
    // returned. Without this gate callers would read iconAdded before the tray thread set it.
    private readonly ManualResetEventSlim iconSettled = new(false);

    public TrayIcon(ClippyConfig config, string outputFolder, Action onExit)
    {
        this.config = config;
        this.outputFolder = outputFolder;
        this.onExit = onExit;
    }

    /// <summary>
    /// Creates the icon on its own thread and waits until the shell has accepted it (or refused).
    /// The wait is what makes iconAdded meaningful to the caller.
    /// </summary>
    public bool Start()
    {
        thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "clippy-tray",
        };
        thread.Start();
        iconSettled.Wait(TimeSpan.FromSeconds(5));
        return iconAdded;
    }

    /// <summary>
    /// Shows a native Windows notification balloon through the existing tray icon.
    ///
    /// This is the same mechanism the shell draws its own toasts with, so it costs nothing while the
    /// process is idle: one Shell_NotifyIcon call, no extra window, no message loop of our own.
    /// It targets the (hWnd, uID) pair that NIM_ADD established -- a notification aimed at any other
    /// pair is accepted and then silently dropped, which looks exactly like the feature being broken.
    ///
    /// Callers must tolerate this failing (it will, if there is no tray in this session), so it only
    /// ever logs.
    /// </summary>
    public unsafe void ShowNotification(string title, string message)
    {
        if (!iconAdded || window == 0)
            return;

        NotifyIconData nid = default;
        nid.cbSize = (uint)sizeof(NotifyIconData);
        nid.hWnd = window;
        nid.uID = IconId;
        // Only NIF_INFO: with the other flags set, NIM_MODIFY rewrites the tip and the icon as well,
        // which is not what this call is for.
        nid.uFlags = NIF_INFO;
        nid.dwInfoFlags = NIIF_INFO;

        // CopyTo stops at the NUL but throws if the text is longer than the fixed buffer, so the
        // length is clamped first. Windows truncates a balloon silently otherwise, and a notification
        // that silently loses its filename is worse than one that admits it was cut.
        var titleSpan = AsSpan(title, 64);
        var messageSpan = AsSpan(message, 256);
        titleSpan.CopyTo(MemoryMarshal.CreateSpan(ref nid.szInfoTitle[0], 64));
        messageSpan.CopyTo(MemoryMarshal.CreateSpan(ref nid.szInfo[0], 256));

        if (Shell_NotifyIcon(NIM_MODIFY, &nid) == 0)
        {
            Console.WriteLine($"Tray: notification failed (error {Marshal.GetLastWin32Error()}). " +
                "Windows may be suppressing notifications for this app.");
        }
    }

    /// <summary>Clamps to the fixed buffer's capacity, reserving room for the terminating NUL.</summary>
    private static ReadOnlySpan<char> AsSpan(string text, int capacity) =>
        text.AsSpan(0, Math.Min(text.Length, capacity - 1));

    private unsafe void Pump()
    {
        wndProc = WndProc;
        // A local is pinned for the duration of the call, so the pointer handed to Windows stays
        // valid until RegisterClassEx returns.
        var className = "ClippyTrayWindow";
        var windowClass = new WindowClassEx
        {
            cbSize = Marshal.SizeOf<WindowClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = Marshal.StringToHGlobalUni(className),
        };

        var registered = RegisterClassEx(Unsafe.AsPointer(ref windowClass));
        Marshal.FreeHGlobal(windowClass.lpszClassName);
        if (registered == 0)
        {
            Console.WriteLine($"Tray: RegisterClassEx failed (error {Marshal.GetLastWin32Error()}), " +
                "continuing without a tray icon.");
            iconSettled.Set();
            return;
        }

        // A plain TOP-LEVEL window, deliberately not HWND_MESSAGE. A message-only window cannot own a
        // tray icon properly: SetForegroundWindow does nothing for it, so TrackPopupMenuEx cannot
        // dismiss the menu and it sticks on screen until something else is clicked. hWndParent is
        // therefore IntPtr.Zero.
        //
        // WS_EX_TOOLWINDOW keeps it out of Alt+Tab, and dwStyle stays 0 (no WS_VISIBLE), so the
        // window is never drawn anywhere -- it exists only to own the icon and pump messages.
        const int WS_EX_TOOLWINDOW = 0x00000080;
        window = CreateWindowEx(
            WS_EX_TOOLWINDOW, className, "ClippyTrayWindow", 0,
            0, 0, 0, 0, 0, 0, windowClass.hInstance, 0);
        if (window == 0)
        {
            Console.WriteLine($"Tray: could not create the tray window (error " +
                $"{Marshal.GetLastWin32Error()}), continuing without an icon.");
            iconSettled.Set();
            return;
        }

        // The struct lives on the STACK. It is blittable, so there is no GC reference in it and
        // nothing to pin -- &nid is a real address that stays put for the duration of the call.
        NotifyIconData nid = default;
        nid.cbSize = (uint)sizeof(NotifyIconData);
        nid.hWnd = window;
        nid.uID = IconId;
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        nid.uCallbackMessage = WM_TRAY;

        // The icon embedded in this executable. Replaces IDI_APPLICATION, which put the generic window
        // glyph in the tray no matter what the exe carried. The size is picked here rather than left
        // to the shell, because a single hIcon scaled down from 128x128 to the tray's 16 pixels is
        // what makes a tray icon look soft.
        LoadApplicationIcons(ref nid);

        // Copies straight into the inline UTF-16 buffer. CopyFrom stops at the NUL, so the rest of
        // the array stays zeroed as the default initialisation left it.
        ReadOnlySpan<char> tip = "Clippy - recording";
        tip.CopyTo(MemoryMarshal.CreateSpan(ref nid.szTip[0], 128));

        // If the taskbar itself is missing, this process is on a window station with no shell and no
        // icon can ever appear. Saying so is far more useful than "E_FAIL".
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == 0)
        {
            Console.WriteLine("Tray: no 'Shell_TrayWnd' found -- this session has no taskbar, so no " +
                "tray icon can be created. The icon works when launched in a normal desktop session.");
            iconSettled.Set();
            return;
        }

        var added = Shell_NotifyIcon(NIM_ADD, &nid);
        if (added == 0)
        {
            Console.WriteLine($"Tray: Shell_NotifyIcon(NIM_ADD) failed (error {Marshal.GetLastWin32Error()}, " +
                $"hIcon={nid.hIcon}, sizeof={sizeof(NotifyIconData)}), continuing without an icon.");
            iconSettled.Set();
            return;
        }

        iconAdded = true;
        Console.WriteLine("Tray: icon added. Right-click for the menu, double-click to open the logs folder.");

        // Only now is the outcome known, so release the caller.
        iconSettled.Set();

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private nint WndProc(nint hWnd, int message, nint wParam, nint lParam)
    {
        if (message == WM_TRAY)
        {
            var mouseMessage = (int)(lParam & 0xFFFF);
            if (mouseMessage is WM_RBUTTONUP or WM_CONTEXTMENU)
                ShowContextMenu();
            else if (mouseMessage == WM_LBUTTONDBLCLK)
                OpenLogsFolder();

            return 0;
        }

        if (message == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            return;

        try
        {
            AppendMenu(menu, MF_STRING, IDC_OPEN_FOLDER, "Open Clips Folder");
            AppendMenu(menu, MF_STRING, IDC_OPEN_LAST_CLIP, "Open Last Clip");
            AppendMenu(menu, MF_STRING, IDC_PICK_FOLDER, "Select Clips Folder...");
            AppendMenu(menu, MF_STRING, IDC_OPEN_CONFIG, "Open Config");
            AppendMenu(menu, MF_SEPARATOR, 0, "");
            AppendSettingsMenus(menu);
            AppendMenu(menu, MF_SEPARATOR, 0, "");
            AppendMenu(menu, MF_STRING, IDC_OPEN_LOGS, "📄 Open Logs Folder");
            AppendMenu(menu, MF_STRING, IDC_CHECK_UPDATES, "Check for Updates");
            AppendMenu(menu, MF_STRING, IDC_RESTART, "Restart");
            AppendMenu(menu, MF_STRING, IDC_EXIT, "Exit");

            // The mandatory dismissal sequence. GetCursorPos, then SetForegroundWindow, then
            // TrackPopupMenuEx, then a WM_NULL post: without that last message the menu keeps
            // foreground ownership and refuses to close on the next click anywhere else.
            GetCursorPos(out var point);
            SetForegroundWindow(window);
            var id = (uint)TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.x, point.y, window, 0);
            PostMessage(window, WM_NULL, 0, 0);

            // Bitrates are matched from the table rather than one case per value: adding a rate is
            // then a single row above instead of a constant, a case and a menu line to keep in step.
            var bitrate = Array.Find(BitrateOptions, o => o.Id == id);
            if (bitrate.Mbps > 0)
            {
                ApplySetting("Bitrate", $"{bitrate.Mbps} Mbps",
                    c => c.VideoBitrateMbps = bitrate.Mbps, live: false);
                return;
            }

            // Hotkey ids are ranges rather than cases, so the whole menu is handled before the switch: two
            // contiguous blocks, one per clip, each the range's base plus a row index.
            if (id >= HotkeyShortBase && id - HotkeyShortBase < HotkeyChoices.Length)
            {
                ApplyHotkeyChoice((int)(id - HotkeyShortBase), isShort: true);
                return;
            }

            if (id >= HotkeyLongBase && id - HotkeyLongBase < HotkeyChoices.Length)
            {
                ApplyHotkeyChoice((int)(id - HotkeyLongBase), isShort: false);
                return;
            }

            switch (id)
            {
                case IDC_OPEN_FOLDER:
                    OpenOutputFolder();
                    break;
                case IDC_OPEN_LAST_CLIP:
                    OpenLastClip();
                    break;
                case IDC_OPEN_CONFIG:
                    OpenConfig();
                    break;
                case IDC_OPEN_LOGS:
                    OpenLogsFolder();
                    break;
                case IDC_CHECK_UPDATES:
                    CheckForUpdates();
                    break;
                case IDC_SHORT_15:
                    ApplySetting("Short clip", "15s", c => c.ShortClipSeconds = 15, live: true);
                    break;
                case IDC_SHORT_30:
                    ApplySetting("Short clip", "30s", c => c.ShortClipSeconds = 30, live: true);
                    break;
                case IDC_SHORT_60:
                    ApplySetting("Short clip", "60s", c => c.ShortClipSeconds = 60, live: true);
                    break;
                case IDC_LONG_60:
                    ApplySetting("Long clip", "1m", c => c.LongClipSeconds = 60, live: true);
                    break;
                case IDC_LONG_120:
                    ApplySetting("Long clip", "2m", c => c.LongClipSeconds = 120, live: true);
                    break;
                case IDC_LONG_180:
                    ApplySetting("Long clip", "3m", c => c.LongClipSeconds = 180, live: true);
                    break;
                case IDC_LONG_300:
                    ApplySetting("Long clip", "5m", c => c.LongClipSeconds = 300, live: true);
                    break;
                case IDC_ENCODER_DIRECT:
                    ApplySetting("Encoder", "nvenc_direct", c => c.VideoEncoder = "nvenc_direct", live: false);
                    break;
                case IDC_ENCODER_NVENC:
                    ApplySetting("Encoder", "h264_nvenc", c => c.VideoEncoder = "h264_nvenc", live: false);
                    break;
                case IDC_ENCODER_X264:
                    ApplySetting("Encoder", "libx264", c => c.VideoEncoder = "libx264", live: false);
                    break;
                case IDC_CODEC_HEVC:
                    // The codec reaches NVENC through the native bridge, which is opened once per
                    // session, so this takes effect on the next start for the same reason the encoder
                    // choice does.
                    ApplySetting("Codec", "HEVC (H.265)", c => c.VideoCodec = "hevc", live: false);
                    break;
                case IDC_CODEC_H264:
                    ApplySetting("Codec", "H.264", c => c.VideoCodec = "h264", live: false);
                    break;
                case IDC_PICK_FOLDER:
                    PickClipsFolder();
                    break;
                case IDC_QUOTA_0: ApplyQuota(0); break;
                case IDC_QUOTA_10: ApplyQuota(10); break;
                case IDC_QUOTA_20: ApplyQuota(20); break;
                case IDC_QUOTA_30: ApplyQuota(30); break;
                case IDC_QUOTA_50: ApplyQuota(50); break;
                case IDC_RESTART:
                    Restart();
                    break;
                case IDC_EXIT:
                    onExit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>
    /// Manual update check from the tray menu, and the one place a failed check is worth telling the
    /// user about: they asked, so silence would look like a broken menu item.
    /// </summary>
    private void CheckForUpdates()
    {
        _ = RunUpdateCheck(userInitiated: true);
    }

    /// <summary>
    /// Shared by the menu item and the startup check.
    ///
    /// The two differ in what they DO about a found update, and that difference is the whole reason
    /// userInitiated exists. Asking from the menu means "update me", so it downloads and installs --
    /// which replaces the running exe and ends the process, hence fire-and-forget and a very clear
    /// log line. The startup check only ever mentions it: replacing an exe under a user who did not
    /// ask for it, mid-game, is not a decision software should make on its own.
    /// </summary>
    internal async Task RunUpdateCheck(bool userInitiated)
    {
        var info = await UpdateService.CheckForUpdateAsync().ConfigureAwait(false);

        if (!info.Available)
        {
            Console.WriteLine($"Update: nothing newer than {UpdateService.Version}.");
            if (userInitiated)
                ShowNotification("Clippy Update", $"You are on the latest version ({UpdateService.Version}).");
            return;
        }

        Console.WriteLine($"Update: {info.Tag} is available.");
        if (!userInitiated)
        {
            ShowNotification(
                "Clippy Update",
                $"{info.Tag} is available. Right-click the tray icon and choose Check for Updates.");
            return;
        }

        ShowNotification("Clippy Update", $"Installing {info.Tag}...");
        await UpdateService.ApplyUpdateAsync(info.DownloadUrl, ShowNotification).ConfigureAwait(false);
    }

    /// <summary>
    /// The five settings submenus, each with a check mark on the value the config currently holds.
    ///
    /// Built fresh on every right-click, which is what makes them live: change the config and the
    /// next menu shows the new state with no cache to invalidate. MF_POPUP takes a real HMENU as its
    /// "string", which is why each submenu is created, filled and then handed over; DestroyMenu on
    /// the parent takes the children with it, so nothing leaks.
    /// </summary>
    private void AppendSettingsMenus(nint menu)
    {
        var shortClip = Submenu(menu, "Short Clip Duration",
            (IDC_SHORT_15, "15 seconds", config.ShortClipSeconds == 15),
            (IDC_SHORT_30, "30 seconds", config.ShortClipSeconds == 30),
            (IDC_SHORT_60, "60 seconds", config.ShortClipSeconds == 60));

        var longClip = Submenu(menu, "Long Clip Duration",
            (IDC_LONG_60, "1 minute", config.LongClipSeconds == 60),
            (IDC_LONG_120, "2 minutes", config.LongClipSeconds == 120),
            (IDC_LONG_180, "3 minutes", config.LongClipSeconds == 180),
            (IDC_LONG_300, "5 minutes", config.LongClipSeconds == 300));

        var bitrate = Submenu(menu, "Video Bitrate / RAM", [.. BitrateOptions
            .Select(o => (o.Id, o.Label, config.VideoBitrateMbps == o.Mbps))]);

        var encoder = Submenu(menu, "Video Encoder",
            (IDC_ENCODER_DIRECT, "Direct NVENC (0-copy VRAM, no ffmpeg)", config.VideoEncoder == "nvenc_direct"),
            (IDC_ENCODER_NVENC, "NVIDIA NVENC via ffmpeg (h264_nvenc)", config.VideoEncoder == "h264_nvenc"),
            (IDC_ENCODER_X264, "CPU x264 (libx264)", config.VideoEncoder == "libx264"));

        // The codec is a separate question from the encoder: NVENC speaks both, and only the direct
        // bridge can be told which one to open. HEVC carries about twice the detail per bit, so the
        // same picture survives roughly half the bitrate -- which is what halves the RAM head and the
        // clip size. The encoder submenu above says nothing about this, so the two stay separate.
        var codec = Submenu(menu, "Video Codec",
            (IDC_CODEC_HEVC, "HEVC / H.265  (sharper at the same bitrate)", config.UseHevc),
            (IDC_CODEC_H264, "H.264  (wider compatibility)", !config.UseHevc));

        // Two independent submenus instead of three presets. The binding currently in force is named in
        // the title and carries the check mark, so the answer to "what is F9 doing right now?" is on
        // screen without opening anything -- which the old preset list could not do, since it showed
        // which PAIR was picked rather than what either key was actually bound to.
        var shortKey = AppendHotkeySubmenu(menu, "Short Clip Hotkey", isShort: true);
        var longKey = AppendHotkeySubmenu(menu, "Long Clip Hotkey", isShort: false);

        var quota = Submenu(menu, "Clips Storage Limit",
            (IDC_QUOTA_0, "Unlimited", config.MaxClipsFolderSizeGB == 0),
            (IDC_QUOTA_10, "10 GB", config.MaxClipsFolderSizeGB == 10),
            (IDC_QUOTA_20, "20 GB", config.MaxClipsFolderSizeGB == 20),
            (IDC_QUOTA_30, "30 GB", config.MaxClipsFolderSizeGB == 30),
            (IDC_QUOTA_50, "50 GB", config.MaxClipsFolderSizeGB == 50));

        // Keep the handles alive until after the parent menu is shown; the OS reads them during
        // TrackPopupMenuEx, not during AppendMenu.
        _ = shortClip; _ = longClip; _ = bitrate; _ = encoder; _ = codec;
        _ = shortKey; _ = longKey; _ = quota;
    }

    /// <summary>
    /// One hotkey submenu: every key in <see cref="HotkeyChoices"/>, a separator before the keypad,
    /// and a check mark on the key currently bound. The title names that key too.
    ///
    /// Built here rather than through <see cref="Submenu"/> because that helper takes a flat list of
    /// items and cannot express the separator, and because the id of every row is derived from its
    /// index -- see <see cref="HotkeyShortBase"/>.
    /// </summary>
    private nint AppendHotkeySubmenu(nint parent, string title, bool isShort)
    {
        var current = isShort ? config.ShortClipHotkey : config.LongClipHotkey;
        var idBase = isShort ? HotkeyShortBase : HotkeyLongBase;

        var sub = CreatePopupMenu();
        for (var i = 0; i < HotkeyChoices.Length; i++)
        {
            if (i == HotkeySeparatorAfter)
                AppendMenu(sub, MF_SEPARATOR, 0, "");

            var (key, label) = HotkeyChoices[i];
            var isCurrent = string.Equals(key, current, StringComparison.OrdinalIgnoreCase);
            AppendMenu(sub, MF_STRING | (isCurrent ? MF_CHECKED : 0), idBase + (uint)i, label);
        }

        AppendMenu(parent, MF_POPUP, (nuint)sub, $"{title} (now {current})");
        return sub;
    }

    /// <summary>
    /// Applies one key chosen from either hotkey menu: reject a clash with the other binding, then
    /// save, rebind live and say so.
    ///
    /// The clash check is not politeness. The hook reads its map on every keypress, and one key can
    /// only mean one thing, so binding F9 to both clips would silently leave the short clip with no
    /// hotkey at all -- the user would press it and get a 3-minute clip, or nothing, with no error
    /// anywhere to notice.
    /// </summary>
    private void ApplyHotkeyChoice(int index, bool isShort)
    {
        if (index < 0 || index >= HotkeyChoices.Length)
            return;

        var (key, label) = HotkeyChoices[index];

        // Refuse anything the config cannot parse back: a binding written to the file that the
        // loader then rejects is a dead hotkey that looks configured in the menu.
        if (!ClippyConfig.TryParseHotkey(key, out _))
        {
            Console.WriteLine($"Settings: '{key}' is not a bindable hotkey; ignored.");
            ShowNotification("Clippy", $"'{key}' cannot be used as a hotkey");
            return;
        }

        var other = isShort ? config.LongClipHotkey : config.ShortClipHotkey;
        if (string.Equals(key, other, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Settings: {key} is already bound to the other clip; ignored.");
            ShowNotification("Clippy", "Hotkeys cannot be identical");
            return;
        }

        var action = isShort ? "Short clip" : "Long clip";
        ApplySetting(
            $"{action} hotkey",
            key,
            c =>
            {
                if (isShort)
                    c.ShortClipHotkey = key;
                else
                    c.LongClipHotkey = key;
            },
            live: true,
            notification: $"{action} hotkey set to {label}");
    }

    private nint Submenu(nint parent, string title, params (uint Id, string Text, bool Checked)[] items)
    {
        var sub = CreatePopupMenu();
        foreach (var (id, text, checked_) in items)
            AppendMenu(sub, MF_STRING | (checked_ ? MF_CHECKED : 0), id, text);

        AppendMenu(parent, MF_POPUP, (nuint)sub, title);
        return sub;
    }

    /// <summary>
    /// Applies a settings choice: write it to the config file, keep the in-memory copy in step so
    /// the check mark moves on the next right-click, and say what happened.
    ///
    /// Hotkeys are rebound live because the hook reads its map on every keypress. Encoder and
    /// bitrate cannot be: ffmpeg was started with those arguments and a running instance cannot
    /// change them, so they take effect on the next session. Pretending otherwise would leave a
    /// check mark next to a setting that is not in force.
    /// </summary>
    /// <summary>
    /// Opens the most recent clip in whatever the user has associated with .mp4.
    ///
    /// The alternative would be picking a player, and there is no right answer: the user may well
    /// have set VLC or DaVinci as the default, and overriding that to launch whichever one Clippy
    /// happened to find is exactly the sort of surprise software should not spring. Handing the
    /// shell the file and letting it resolve the association is one line, and always agrees with
    /// double-clicking the same clip in Explorer.
    /// </summary>
    private void OpenLastClip()
    {
        var clip = FindLastClip(outputFolder);
        if (clip is null)
        {
            // No clip yet is the ordinary state right after startup, not an error worth a balloon,
            // but silence would look like a menu item that does nothing.
            Console.WriteLine("Last clip: no *.mp4 in the clips folder yet.");
            ShowNotification("Clippy", "No clips yet. Press F8 to save one.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(clip) { UseShellExecute = true });
            Console.WriteLine($"Last clip: opened {Path.GetFileName(clip)}.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Console.WriteLine($"Last clip: could not open {clip} ({ex.Message}).");
            ShowNotification("Clippy", $"Could not open {Path.GetFileName(clip)}.");
        }
    }

    /// <summary>The newest clip in the folder, or null when there is none.</summary>
    /// <remarks>
    /// LastWriteTime rather than CreationTime, because a clip copied in from elsewhere keeps its
    /// original creation date and would otherwise sort ahead of the one the user just recorded.
    /// TopDirectoryOnly matches what ClipsQuota counts and deletes, so "last clip" can never name a
    /// file the quota would have removed.
    /// </remarks>
    internal static string? FindLastClip(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return null;

            return new DirectoryInfo(folder)
                .GetFiles("*.mp4", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder we cannot list is a folder whose clips we cannot name. Log it and report
            // "no clips": letting the exception escape through the Win32 menu callback would have
            // the OS swallow it and take the tray thread down with it.
            Console.WriteLine($"Last clip: could not list {folder} ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Asks for a clips folder and stores the answer.
    /// </summary>
    /// <remarks>
    /// The chosen path is absolute, which is what makes it survive a different working directory:
    /// ResolveOutputFolder returns a rooted folder unchanged, and a relative one would follow whatever
    /// the process happened to start in.
    /// <para>
    /// The quota runs immediately after, not at the next save. Moving the folder is the one moment a
    /// user is definitely paying attention, so it is the one moment to find out that the new location is
    /// already over the limit.
    /// </para>
    /// </remarks>
    private void PickClipsFolder()
    {
        string? picked;
        try
        {
            picked = FolderPicker.Pick("Choose where Clippy should save clips");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Folder picker failed ({ex.GetType().Name}: {ex.Message}).");
            ShowNotification("Clippy", "Could not open the folder picker.");
            return;
        }

        // A cancelled dialog is the most ordinary outcome there is and is not worth a notification.
        if (picked is null)
        {
            Console.WriteLine("Folder picker: cancelled.");
            return;
        }

        config.OutputFolder = picked;
        config.Save();
        Console.WriteLine($"Settings: OutputFolder = {picked}");
        ShowNotification("Clippy", $"Clips folder updated: {picked}");

        var deleted = ClipsQuota.Enforce(picked, config.MaxClipsFolderSizeGB);
        if (deleted > 0)
            ShowNotification("Clippy", $"Clips folder updated: {picked} (removed {deleted} old clip(s) over quota)");
    }

    /// <summary>Stores a new storage ceiling and enforces it against the current folder at once.</summary>
    private void ApplyQuota(int gigabytes)
    {
        ApplySetting("Clips Storage Limit", gigabytes == 0 ? "Unlimited" : $"{gigabytes} GB",
            c => c.MaxClipsFolderSizeGB = gigabytes, live: false);

        var deleted = ClipsQuota.Enforce(config.ResolveOutputFolder(), gigabytes);
        if (deleted > 0)
            Console.WriteLine($"Quota: {deleted} clip(s) removed immediately.");
    }
    private void ApplySetting(string name, string value, Action<ClippyConfig> mutate, bool live, string? notification = null)
    {
        mutate(config);
        config.Save();
        Console.WriteLine($"Settings: {name} = {value}{(live ? "" : " (applies next session)")}");
        ShowNotification("Clippy", notification ?? $"Settings updated: {name} = {value}");

        if (live)
            RebindHotkeys();
        else
            ShowNotification("Clippy", "Settings saved. Encoder settings will apply on next session.");
    }

    /// <summary>
    /// Set by RunVideo so a hotkey chosen in this menu can take effect without a restart. The tray
    /// has no business knowing how the hook is implemented, and ScreenCapture owns its lifetime.
    /// </summary>
    public Action<ClippyConfig>? rebindHotkeys;

    private void RebindHotkeys()
    {
        if (rebindHotkeys is null)
        {
            Console.WriteLine("Settings: no hotkey service to rebind; takes effect next session.");
            return;
        }

        rebindHotkeys(config);
    }

    public static void OpenFolder(string folderPath)
    {
        string fullPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
    }

    private void OpenOutputFolder()
    {
        try
        {
            OpenFolder(outputFolder);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Console.WriteLine($"Tray: could not open the clips folder: {ex.Message}");
        }
    }

    private void OpenConfig()
    {
        try
        {
            // Write the defaults out first, so notepad never opens an empty tab.
            if (!File.Exists(ClippyConfig.Path))
                config.Save();

            Process.Start(new ProcessStartInfo(ClippyConfig.Path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Console.WriteLine($"Tray: could not open the config: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts a fresh copy of this executable with the same arguments, then quits this one.
    ///
    /// This is what the "applies next session" settings actually need: the tray can write a new
    /// bitrate or encoder, but the running ffmpeg/NVENC pipeline cannot be told to change its own
    /// arguments, so without this button the user's only route is Task Manager and a double-click.
    ///
    /// The new process is started BEFORE this one stops, because the alternative -- exit first,
    /// launch second -- leaves a window where nothing is recording, and a crash between the two
    /// would leave nothing running at all. Both instances briefly hold the screen and the encoder,
    /// which is harmless: the ring buffer lives in each process's own memory and nothing is written
    /// to disk until a hotkey asks for it.
    ///
    /// The user's own arguments are replayed verbatim, so `--record --tray` restarts as a --record
    /// run and a bare launch restarts as a bare launch, reading StartMinimizedToTray from
    /// config.json exactly like a double-click in Explorer would.
    /// </summary>
    private void Restart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Console.WriteLine("Tray: restart needs a real executable path, which this run does not have.");
            ShowNotification("Clippy", "Could not restart: the running program path is unknown.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };

            // Element 0 is the executable path itself; ProcessStartInfo.FileName is that. The rest
            // are the user's own arguments, replayed through ArgumentList so a path with spaces
            // needs no quoting here.
            foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
            Console.WriteLine("Tray: a new instance was started; this one is shutting down.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            // Staying alive is the right answer here: quitting after a failed launch would take the
            // recorder away over a menu item that did not work.
            Console.WriteLine($"Tray: could not start a new instance ({ex.Message}); still running.");
            ShowNotification("Clippy", $"Could not restart: {ex.Message}");
            return;
        }

        onExit();
    }

    /// <summary>
    /// Opens the log folder using the system's default file manager.
    ///
    /// This replaces the old "Show / Hide Log" console toggle: the build is WinExe now, so there is
    /// no console window to show -- the log files on disk ARE the diagnostics, and the menu hands
    /// the user the folder itself.
    /// </summary>
    private void OpenLogsFolder()
    {
        try
        {
            OpenFolder(LogPaths.ResolveDirectory(config));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            Console.WriteLine($"Tray: could not open the logs folder: {ex.Message}");
        }
    }

    public unsafe void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        if (window != 0)
        {
            // NIM_DELETE first: destroying the window while the icon still points at it leaves an
            // orphan icon in the tray that only a hover would clear.
            if (iconAdded)
            {
                NotifyIconData nid = default;
                nid.cbSize = (uint)sizeof(NotifyIconData);
                nid.hWnd = window;
                nid.uID = IconId;
                Shell_NotifyIcon(NIM_DELETE, &nid);
            }

            PostMessage(window, WM_DESTROY, 0, 0);
            DestroyWindow(window);
            window = 0;
        }
    }
}
