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
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private const uint IDC_OPEN_FOLDER = 1001;
    private const uint IDC_OPEN_CONFIG = 1002;
    private const uint IDC_TOGGLE_CONSOLE = 1003;
    private const uint IDC_EXIT = 1004;

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

    // GetConsoleWindow is exported by kernel32, not user32. Getting that wrong throws
    // EntryPointNotFoundException at the first call, which is what hid the console toggle.
    [LibraryImport("kernel32.dll", EntryPoint = "GetConsoleWindow")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

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

    /// <summary>True only once Shell_NotifyIcon(NIM_ADD) succeeded, so the console is hidden only
    /// when there is an icon to bring it back from.</summary>
    public bool iconAdded;

    // Start() returns as soon as the thread begins, but the icon only exists once NIM_ADD has
    // returned. Without this gate HideConsole() would read iconAdded before the tray thread set it,
    // and the console would stay visible even though there was an icon to restore it from.
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

        // IDI_APPLICATION is a MAKEINTRESOURCE id, i.e. the bare number as a pointer. Passing it as a
        // string made LoadIconW look for a named icon called "32512" and return 0.
        nid.hIcon = LoadIcon(0, new nint(IDI_APPLICATION));

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
        Console.WriteLine("Tray: icon added. Right-click for the menu, double-click to toggle the log.");

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
                ToggleConsole();

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
            AppendMenu(menu, MF_STRING, IDC_OPEN_CONFIG, "Open Config");
            AppendMenu(menu, MF_SEPARATOR, 0, "");
            AppendMenu(menu, MF_STRING, IDC_TOGGLE_CONSOLE, "Show / Hide Log");
            AppendMenu(menu, MF_STRING, IDC_EXIT, "Exit");

            // The mandatory dismissal sequence. GetCursorPos, then SetForegroundWindow, then
            // TrackPopupMenuEx, then a WM_NULL post: without that last message the menu keeps
            // foreground ownership and refuses to close on the next click anywhere else.
            GetCursorPos(out var point);
            SetForegroundWindow(window);
            var id = (uint)TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, point.x, point.y, window, 0);
            PostMessage(window, WM_NULL, 0, 0);
            switch (id)
            {
                case IDC_OPEN_FOLDER:
                    OpenOutputFolder();
                    break;
                case IDC_OPEN_CONFIG:
                    OpenConfig();
                    break;
                case IDC_TOGGLE_CONSOLE:
                    ToggleConsole();
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

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetFullPath(outputFolder)}\"")
            {
                UseShellExecute = true,
            });
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

    /// <summary>Shows or hides the console window, if this process has one.</summary>
    public void ToggleConsole()
    {
        var console = GetConsoleWindow();
        if (console == 0)
        {
            // A GUI-subsystem build has no console at all; say so rather than pretend it worked.
            Console.WriteLine("Tray: this build has no console window to show or hide.");
            return;
        }

        if (IsWindowVisible(console))
        {
            ShowWindow(console, SW_HIDE);
            Console.WriteLine("Tray: log hidden.");
        }
        else
        {
            ShowWindow(console, SW_RESTORE);
            ShowWindow(console, SW_SHOW);
            SetForegroundWindow(console);
            Console.WriteLine("Tray: log shown.");
        }
    }

    /// <summary>
    /// Hides the console at startup, for --tray / StartMinimizedToTray. Does nothing unless the icon
    /// actually appeared: hiding the only window when there is no tray icon to restore it from would
    /// leave the process running invisibly with no way back.
    /// </summary>
    public void HideConsole()
    {
        if (!iconAdded)
        {
            Console.WriteLine("Tray: no icon was added, so the log stays visible.");
            return;
        }

        var console = GetConsoleWindow();
        if (console != 0)
            ShowWindow(console, SW_HIDE);
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
