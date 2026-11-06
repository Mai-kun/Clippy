#if LINUX
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// The clip triggers Linux has instead of a global Windows keyboard hook. Every source funnels
/// into one debounced dispatcher, so an F8 that arrives from two backends at once still saves
/// exactly one clip:
///
/// 1. a Unix-domain IPC socket (/tmp/clippy-ipc.sock), so "clippy --save-short" -- bound to any
///    compositor shortcut on KDE Plasma or Niri -- saves a clip and prints its path;
/// 2. SIGUSR1 / SIGUSR2, for the same job from a shell or a script;
/// 3. the XDG Desktop Portal org.freedesktop.portal.GlobalShortcuts (CreateSession, BindShortcuts
///    with preferred_trigger, the Activated signal): the one capture that is sanctioned on
///    Wayland, and on KDE Plasma it registers the keys in kglobalaccel for the user to see;
/// 4. X11 XGrabKey through libX11.so.6 (XWayland on Plasma, a plain X server elsewhere): works
///    right after "./Clippy" with no installer and no group membership -- the compositor delivers
///    the grabbed key to this process on the root window;
/// 5. raw evdev (/dev/input/event*), when the process can read the input group's devices, as a
///    belt to the above two.
///
/// Wayland deliberately has no global keyboard capture, so there is no single mechanism; these
/// layered ones cover every setup without root, without install.sh and without manual setup.
/// </summary>
internal sealed partial class LinuxHotkeyService : IDisposable
{
    public const string SocketPath = "/tmp/clippy-ipc.sock";

    /// <summary>A clip request that arrived while the exporter is busy is answered with ERR busy, never queued.</summary>
    public enum Trigger { Short, Long, Stop }

    /// <summary>
    /// One physical F8 can arrive from several backends within milliseconds (evdev and the X11
    /// grab both see it, the portal fires alongside them). A second dispatch of the same trigger
    /// inside this window is dropped -- which also eats keyboard auto-repeat.
    /// </summary>
    private const int DebounceMs = 300;

    private readonly Func<Trigger, string> handle; // returns "OK <path>" / "ERR <reason>"
    private readonly ClippyConfig config;
    private Socket? listener;
    private Thread? acceptThread;
    private volatile bool stopped;
    private readonly List<int> openedEvdevFds = [];

    // Per-trigger debounce bookkeeping: lastDispatchTicks[i] is when trigger i was last dispatched.
    private readonly long[] lastDispatchTicks = new long[3];
    private readonly object dispatchGate = new();

    public LinuxHotkeyService(Func<Trigger, string> handle, ClippyConfig config)
    {
        this.handle = handle;
        this.config = config;
    }

    // The sigaction trampoline: one static entry point, since a signal can arrive on any thread and
    // the kernel needs an unmanaged address. The registry is plain static state; a recorder is a
    // one-per-process object, which is also the assumption the Windows hotkey service makes.
    private static LinuxHotkeyService? signalTarget;

    [UnmanagedCallersOnly]
    private static void SignalTrampoline(int signalNumber)
    {
        // The handler body does nothing but enqueue a request; an export happens on a worker.
        try
        {
            signalTarget?.handle(signalNumber == SigUsr1 ? Trigger.Short : Trigger.Long);
        }
        catch (Exception)
        {
            // A signal handler that throws is a dead process; nothing here is allowed to.
        }
    }

    private const int SigUsr1 = 10;
    private const int SigUsr2 = 12;
    private const int SaRestart = 0x10000000;

    public void Start()
    {
        StartIpc();
        StartSignals();
        StartEvdev();
        StartX11();
        StartPortal();
    }

    /// <summary>
    /// The debounced funnel every capture source goes through. The debounce window is short on
    /// purpose -- it eats double delivery and auto-repeat, not a deliberate second press -- and it
    /// does not span the export itself: handle() runs on the calling thread, the way the IPC path
    /// needs its return value and the other threads simply drop it.
    /// </summary>
    private void Dispatch(Trigger trigger, string source)
    {
        lock (dispatchGate)
        {
            var now = Environment.TickCount64;
            var since = now - lastDispatchTicks[(int)trigger];
            if (since < DebounceMs)
            {
                Console.WriteLine($"Hotkeys: {trigger} from {source} dropped -- the same trigger fired {since} ms ago (debounce {DebounceMs} ms).");
                return;
            }
            lastDispatchTicks[(int)trigger] = now;
        }

        var answer = handle(trigger);
        Console.WriteLine($"Hotkeys: {trigger} ({source}) -> {answer}");
    }

    /// <summary>
    /// Rebinds both hotkeys live: every backend picks the new keys up without a restart. The
    /// evdev loops read the code fields each event, the X11 thread regenerates its grabs on its
    /// next idle pass, the portal thread re-issues BindShortcuts with the new preferred triggers.
    /// Callers may keep passing the config around; the fields here are kept in step so the next
    /// Save writes exactly what is in force.
    /// </summary>
    public void Rebind(string shortKey, string longKey)
    {
        config.ShortClipHotkey = shortKey;
        config.LongClipHotkey = longKey;
        Console.WriteLine($"Hotkeys: rebinding short clip to {shortKey}, long clip to {longKey}.");
        RecomputeEvdevCodes();
        RequestX11Regrab();
        RequestPortalRebind();
    }

    /// <summary>
    /// The IPC listener. A stale socket file from a crashed instance is removed at startup -- bind
    /// would otherwise refuse the address -- and one accept thread serves one command per
    /// connection, the way a CLI flag needs it.
    /// </summary>
    private void StartIpc()
    {
        try
        {
            if (File.Exists(SocketPath))
                File.Delete(SocketPath);

            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            // Only the owner may talk to the recorder; /tmp is world-writable otherwise.
            try { if (OperatingSystem.IsLinux()) File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Hotkeys: could not restrict {SocketPath} to the owner ({ex.Message}).");
            }
            listener.Listen(2);

            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "clippy-ipc" };
            acceptThread.Start();
            Console.WriteLine($"Hotkeys: IPC listening on {SocketPath} (--save-short / --save-long / --stop).");
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Hotkeys: could not listen on {SocketPath} ({ex.Message}); " +
                "use the hotkeys or SIGUSR1/SIGUSR2 instead.");
        }
    }

    private void AcceptLoop()
    {
        while (!stopped)
        {
            Socket connection;
            try
            {
                connection = listener!.Accept();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or ThreadInterruptedException)
            {
                if (!stopped)
                    Thread.Sleep(200);
                continue;
            }

            using (connection)
            {
                try
                {
                    connection.ReceiveTimeout = 3000;
                    connection.SendTimeout = 3000;
                    var command = ReadLine(connection);
                    var answer = command switch
                    {
                        "SHORT" => handle(Trigger.Short),
                        "LONG" => handle(Trigger.Long),
                        "STOP" => handle(Trigger.Stop),
                        _ => "ERR unknown command",
                    };
                    var response = System.Text.Encoding.UTF8.GetBytes(answer + "\n");
                    var sent = 0;
                    while (sent < response.Length)
                        sent += connection.Send(response, sent, response.Length - sent, SocketFlags.None);
                }
                catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
                {
                    // The client hung up mid-command; nothing on the recorder's side to roll back.
                }
            }
        }
    }

    private static string ReadLine(Socket connection)
    {
        var buffer = new byte[1];
        var bytes = new List<byte>(8);
        while (bytes.Count < 32)
        {
            var received = connection.Receive(buffer);
            if (received == 0)
                break;
            if (buffer[0] == (byte)'\n')
                break;
            bytes.Add(buffer[0]);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray()).Trim();
    }

    /// <summary>The client half, used by the --save-short / --save-long / --stop flags.</summary>
    public static string? SendCommand(string command)
    {
        if (!File.Exists(SocketPath))
        {
            Console.WriteLine($"IPC: no recorder is listening on {SocketPath}. Start Clippy first (it prints this socket path on startup).");
            return null;
        }

        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.ReceiveTimeout = 60_000; // an export can take seconds; the reply is worth waiting for
            client.SendTimeout = 5000;
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            var request = System.Text.Encoding.UTF8.GetBytes(command + "\n");
            var sent = 0;
            while (sent < request.Length)
                sent += client.Send(request, sent, request.Length - sent, SocketFlags.None);

            var response = new List<byte>(128);
            var buffer = new byte[256];
            while (response.Count < 4096)
            {
                var received = client.Receive(buffer);
                if (received == 0)
                    break;
                response.AddRange(buffer.Take(received));
                if (response[^1] == (byte)'\n')
                    break;
            }

            var answer = System.Text.Encoding.UTF8.GetString(response.ToArray()).Trim();
            if (answer.StartsWith("OK ", StringComparison.Ordinal))
                return answer[3..];
            Console.WriteLine($"IPC: {answer}");
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            Console.WriteLine($"IPC: could not reach the recorder on {SocketPath} ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// SIGUSR1 = short clip, SIGUSR2 = long clip, via raw sigaction: the runtime's
    /// PosixSignalRegistration covers only the signals it manages itself, and USR1/USR2 are not
    /// among them. The handler is an unmanaged trampoline so the address survives Native AOT.
    /// </summary>
    private unsafe void StartSignals()
    {
        signalTarget = this;
        Span<int> signals = [SigUsr1, SigUsr2];
        foreach (var signal in signals)
        {
            var action = new SigAction
            {
                Handler = (nint)(delegate* unmanaged<int, void>)&SignalTrampoline,
                Flags = SaRestart, // interrupted syscalls restart; the recorder must not lose reads
            };
            if (sigaction(signal, ref action, 0) != 0)
                Console.WriteLine($"Hotkeys: could not register signal {signal} (errno {Marshal.GetLastWin32Error()}).");
        }
        Console.WriteLine("Hotkeys: SIGUSR1 saves the short clip, SIGUSR2 the long one.");
    }

    /// <summary>glibc's struct sigaction layout: handler, mask (128 bytes), flags, restorer.</summary>
    private unsafe struct SigAction
    {
        public nint Handler;
        public fixed byte Mask[128];
        public int Flags;
        public nint Restorer;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int sigaction(int signum, ref SigAction action, nint oldAction);

    /// <summary>
    /// Raw evdev listening, one thread per readable device so a silent device's blocking read never
    /// starves the others. F-keys come from the config, mapped to their Linux keycodes (KEY_F1=59
    /// ..KEY_F10=68, KEY_F11=87, KEY_F12=88, KEY_F13..=183..), the keypad from its own block
    /// (KEY_KP7..KP0 = 71..73, 75..77, 79..82). Only keyboards emit those codes, so no device-type
    /// filtering is needed. Nothing readable => the compositor-shortcut hint.
    /// </summary>
    private void StartEvdev()
    {
        RecomputeEvdevCodes();
        if (evdevShortCode == 0 && evdevLongCode == 0)
            return;

        var opened = 0;
        for (var i = 0; i < 32; i++)
        {
            var path = $"/dev/input/event{i}";
            if (!File.Exists(path))
                break;
            try
            {
                var fd = open(path, OReadOnly);
                if (fd < 0)
                    continue;
                if (!IsKeyboard(fd))
                {
                    close(fd);
                    continue; // mice, audio jacks, power buttons, sensors: not our business
                }
                openedEvdevFds.Add(fd);
                opened++;
                var thread = new Thread(() => EvdevLoop(fd))
                {
                    IsBackground = true,
                    Name = $"clippy-evdev-{i}",
                };
                thread.Start();
            }
            catch (Exception)
            {
                // Not readable: the usual no-input-group case, handled by the count below.
            }
        }

        if (opened == 0)
        {
            Console.WriteLine("Hotkeys: /dev/input is not readable; falling back to the portal and X11 captures " +
                "(they work without any group membership). A compositor shortcut bound to 'clippy --save-short' " +
                "or SIGUSR1/SIGUSR2 also always works.");
        }
        else
        {
            Console.WriteLine($"Hotkeys: evdev listening on {opened} device(s); " +
                $"{config.ShortClipHotkey} saves {config.ShortClipSeconds:F0} s, {config.LongClipHotkey} saves {config.LongClipSeconds:F0} s.");
        }
    }

    /// <summary>
    /// True when the event device is a real keyboard: EVIOCGBIT(0) must list EV_KEY, and the
    /// EVIOCGBIT(EV_KEY) bitmap must contain ordinary typing keys (A / 1 / space). Without this
    /// filter the service opens one thread per every /dev/input/event* node -- on a typical box
    /// that is 25 threads for mice, audio jacks, power buttons and lid sensors.
    /// </summary>
    private static bool IsKeyboard(int fd)
    {
        // EVIOCGBIT(ev, len) = _IOC(_IOC_READ=2, 'E', 0x20 + ev, len)
        //   = (2 << 30) | (len << 16) | ('E' << 8) | (0x20 + ev)
        const uint eviocgbitType = 0x80084520u;   // event types, 8 bytes
        const uint eviocgbitKey = 0x80064521u;    // key bitmap, 96 bytes (KEY_MAX/8 + 1)

        Span<byte> types = stackalloc byte[8];
        if (ioctl(fd, eviocgbitType, types) < 0)
        {
            Console.WriteLine($"Evdev: EVIOCGBIT(0) failed on a device (errno {Marshal.GetLastWin32Error()}).");
            return false;
        }
        if ((types[0] & (1 << 1)) == 0) // EV_KEY
            return false;

        Span<byte> keys = stackalloc byte[96];
        if (ioctl(fd, eviocgbitKey, keys) < 0)
            return false;
        // KEY_A (30), KEY_1 (2) or KEY_SPACE (57): ordinary typing keys a mouse never sends.
        return (keys[30 >> 3] & (1 << (30 & 7))) != 0
            || (keys[2 >> 3] & (1 << (2 & 7))) != 0
            || (keys[57 >> 3] & (1 << (57 & 7))) != 0;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int ioctl(int fd, uint request, nint argument);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int ioctl(int fd, uint request, nuint argument);

    private static unsafe int ioctl(int fd, uint request, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return ioctl(fd, request, (nint)p);
    }

    /// <summary>
    /// The evdev key codes now in force, volatile so a Rebind from the tray thread is visible to
    /// every device loop on its very next event without any lock.
    /// </summary>
    private volatile ushort evdevShortCode;
    private volatile ushort evdevLongCode;

    private void RecomputeEvdevCodes()
    {
        evdevShortCode = HotkeyCode(config.ShortClipHotkey);
        evdevLongCode = HotkeyCode(config.LongClipHotkey);
    }

    private void EvdevLoop(int fd)
    {
        // input_event on linux-x64: struct timeval (2 x i64 = 16 bytes), u16 type, u16 code,
        // i32 value, 4 pad = 24 bytes. The type/code/value offsets MUST be counted from after
        // the 64-bit timeval -- reading them at the 32-bit-timeval offsets (8/10/12) parses the
        // timestamp bytes instead, and every real event is silently dropped.
        var record = new byte[24];
        while (!stopped)
        {
            // Blocking read, one thread per device, so a silent keyboard never starves the others.
            var bytes = evdev_read(fd, record, (nuint)record.Length);
            if (bytes < 24)
            {
                if (bytes == 0 || (bytes < 0 && Marshal.GetLastWin32Error() != ErrnoAgain))
                    return; // the device went away; the other triggers still work
                continue;
            }

            var type = BitConverter.ToUInt16(record, 16);
            var code = BitConverter.ToUInt16(record, 18);
            var value = BitConverter.ToInt32(record, 20);
            if (type != 1 /* EV_KEY */ || value != 1 /* key down, not autorepeat */)
                continue;

            // The codes are read per event, so a tray rebind is live on the next keypress.
            var shortCode = evdevShortCode;
            var longCode = evdevLongCode;
            if (code == shortCode && shortCode != 0)
                Dispatch(Trigger.Short, "evdev");
            else if (code == longCode && longCode != 0)
                Dispatch(Trigger.Long, "evdev");
        }
    }

    /// <summary>"F8" -> 66, "NumPad5" -> 76. Returns 0 for names that are not bindable keys.</summary>
    internal static ushort HotkeyCode(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;
        var key = name.Trim().ToUpperInvariant();

        // The keypad has its own evdev block, laid out as it sits on the keyboard:
        // KP7-KP9 = 71-73, KP4-KP6 = 75-77, KP1-KP3 = 79-81, KP0 = 82.
        if (key.StartsWith("NUMPAD", StringComparison.Ordinal)
            && key.Length == "NUMPAD0".Length
            && key[^1] is >= '0' and <= '9')
        {
            return key[^1] switch
            {
                '0' => 82,
                '1' => 79,
                '2' => 80,
                '3' => 81,
                '4' => 75,
                '5' => 76,
                '6' => 77,
                '7' => 71,
                '8' => 72,
                _ => 73,
            };
        }

        if (!key.StartsWith('F') || !int.TryParse(key[1..], out var number))
            return 0;
        return number switch
        {
            >= 1 and <= 10 => (ushort)(58 + number), // KEY_F1 = 59
            11 => 87,
            12 => 88,
            >= 13 and <= 24 => (ushort)(170 + number), // KEY_F13 = 183
            _ => 0,
        };
    }

    // --- X11 global grabs (libX11.so.6): XGrabKey on the root window ---

    /// <summary>
    /// On a plain X11 session XGrabKey IS the global-shortcut mechanism; through XWayland on KDE
    /// Plasma, KWin honours grabs from XWayland clients (it implements
    /// zwp_xwayland_keyboard_grab_v1), so the same code works there. Either way it needs no
    /// privileges: any process with a DISPLAY connection can grab a free key, which is what makes
    /// the hotkeys work right after "./Clippy" with no install script and no group membership.
    ///
    /// Every Xlib call happens on the ONE X11 thread -- no XInitThreads dance: the event loop
    /// waits in poll() with a timeout, so a regrab flag set by Rebind is picked up within ~200 ms.
    /// </summary>
    private nint xDisplay;
    private Thread? x11Thread;
    private volatile bool x11RegrabPending;
    private int xShortKeycode, xLongKeycode;     // written only by the X11 thread

    // Modifier combos to grab alongside the bare key: LockMask = Caps Lock, Mod2Mask = Num Lock
    // (a numpad binding needs it), Mod5Mask = Scroll Lock. All eight combinations, so the key
    // fires no matter which locking modifiers the user has on.
    private static readonly uint[] GrabModifierCombos =
    [
        0,
        LockMask, Mod2Mask, LockMask | Mod2Mask,
        Mod5Mask, LockMask | Mod5Mask, Mod2Mask | Mod5Mask, LockMask | Mod2Mask | Mod5Mask,
    ];

    private const uint LockMask = 0x02;
    private const uint Mod2Mask = 0x10;
    private const uint Mod5Mask = 0x80;
    private const int KeyPressType = 2;
    private const int GrabModeAsync = 0;
    private const int PollIn = 0x001;

    /// <summary>Count of X errors since the last grab install (a conflicting grab is the common one).</summary>
    private static int xErrorCount;

    private void StartX11()
    {
        try
        {
            xDisplay = XOpenDisplay(null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine("Hotkeys: libX11 is not available; the X11 grab capture is off (the portal and evdev still work).");
            xDisplay = 0;
        }
        if (xDisplay == 0)
        {
            if (x11Thread is null)
                Console.WriteLine("Hotkeys: no X display; the X11 grab capture is off (the portal and evdev still work).");
            return;
        }

        x11Thread = new Thread(X11Loop) { IsBackground = true, Name = "clippy-x11-keys" };
        x11Thread.Start();
    }

    private unsafe void X11Loop()
    {
        var disp = xDisplay;
        var root = XDefaultRootWindow(disp);
        // The default Xlib error handler EXITS the process on the first protocol error. A key
        // already grabbed by another client (a screenshot tool on F8, say) is a routine situation,
        // not a fatal one: the handler below just counts, and the grabber logs.
        XSetErrorHandler(&XErrorTrampoline);

        InstallGrabs(disp, root);
        Console.WriteLine($"Hotkeys: X11 grabs active on the root window ({config.ShortClipHotkey}, {config.LongClipHotkey}).");

        var fd = XConnectionNumber(disp);
        var ev = new XEvent();
        while (!stopped)
        {
            if (x11RegrabPending)
            {
                x11RegrabPending = false;
                RemoveGrabs(disp, root);
                InstallGrabs(disp, root);
                Console.WriteLine("Hotkeys: X11 grabs updated for the new key bindings.");
            }

            if (!WaitReadable(fd, 200))
                continue;

            while (!stopped && XPending(disp) > 0)
            {
                XNextEvent(disp, ref ev);
                if (ev.Type != KeyPressType)
                    continue;

                // Keycode comparison, not keysym lookup: the keycode is what the grab was made
                // for, and a numpad key keeps its keycode whether or not Num Lock is on.
                if (xShortKeycode != 0 && ev.KeyCode == xShortKeycode)
                    Dispatch(Trigger.Short, "X11");
                else if (xLongKeycode != 0 && ev.KeyCode == xLongKeycode)
                    Dispatch(Trigger.Long, "X11");
            }
        }

        XCloseDisplay(disp);
        xDisplay = 0;
    }

    /// <summary>Ungrabs whatever is in force, then grabs the configured keys in all modifier combos.</summary>
    private unsafe void InstallGrabs(nint disp, nint root)
    {
        RemoveGrabs(disp, root);
        xShortKeycode = 0;
        xLongKeycode = 0;
        Interlocked.Exchange(ref xErrorCount, 0);

        xShortKeycode = GrabKey(disp, root, config.ShortClipHotkey);
        xLongKeycode = GrabKey(disp, root, config.LongClipHotkey);
        XFlush(disp);

        var errors = Interlocked.Exchange(ref xErrorCount, 0);
        if (errors > 0)
            Console.WriteLine($"Hotkeys: {errors} X11 grab(s) were refused -- those keys are already taken by another client; " +
                "the portal and evdev captures still cover them.");
    }

    private unsafe int GrabKey(nint disp, nint root, string keyName)
    {
        var keysym = XStringToKeysym(XKeysymName(keyName));
        if (keysym == 0)
        {
            Console.WriteLine($"Hotkeys: '{keyName}' has no X11 keysym; it is not grabbed there.");
            return 0;
        }
        var keycode = XKeysymToKeycode(disp, keysym);
        if (keycode == 0)
        {
            Console.WriteLine($"Hotkeys: '{keyName}' has no key on this keyboard; it is not grabbed there.");
            return 0;
        }

        foreach (var modifiers in GrabModifierCombos)
            XGrabKey(disp, keycode, modifiers, root, 0 /* owner events: false */, GrabModeAsync, GrabModeAsync);
        return keycode;
    }

    private unsafe void RemoveGrabs(nint disp, nint root)
    {
        foreach (var keycode in new[] { xShortKeycode, xLongKeycode })
        {
            if (keycode == 0)
                continue;
            foreach (var modifiers in GrabModifierCombos)
                XUngrabKey(disp, keycode, modifiers, root);
        }
        XFlush(disp);
    }

    /// <summary>"F8" and "NumPad5" as they are called in the config, in X11 keysym naming.</summary>
    private static string XKeysymName(string name)
    {
        var key = name.Trim();
        return key.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && key.Length == 7 && char.IsAsciiDigit(key[6])
            ? $"KP_{key[6]}"
            : key.ToUpperInvariant();
    }

    private void RequestX11Regrab()
    {
        if (xDisplay != 0)
            x11RegrabPending = true;
    }

    private static unsafe bool WaitReadable(int fd, int timeoutMs)
    {
        var pfd = new PollFd { fd = fd, events = PollIn };
        return poll(&pfd, 1, timeoutMs) > 0 && (pfd.revents & PollIn) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    /// <summary>The XEvent union is 192 bytes; XKeyEvent.keycode sits at offset 84 of it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct XEvent
    {
        public fixed byte Raw[192];

        public readonly int Type
        {
            get
            {
                fixed (byte* p = Raw)
                    return *(int*)p;
            }
        }

        public readonly int KeyCode
        {
            get
            {
                fixed (byte* p = Raw)
                    return *(int*)(p + 84);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static int XErrorTrampoline(nint display, nint errorEvent)
    {
        Interlocked.Increment(ref xErrorCount);
        return 0; // handled: the protocol continues, the failed grab simply is not ours
    }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8, SetLastError = false)]
    private static partial nint XOpenDisplay(string? name);

    [LibraryImport("libX11.so.6")]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial ulong XStringToKeysym(string keysymName);

    [LibraryImport("libX11.so.6")]
    private static partial int XKeysymToKeycode(nint display, ulong keysym);

    [LibraryImport("libX11.so.6")]
    private static unsafe partial void XGrabKey(nint display, int keycode, uint modifiers, nint window,
        int ownerEvents, int pointerMode, int keyboardMode);

    [LibraryImport("libX11.so.6")]
    private static unsafe partial void XUngrabKey(nint display, int keycode, uint modifiers, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial void XNextEvent(nint display, ref XEvent eventReturn);

    [LibraryImport("libX11.so.6")]
    private static partial int XPending(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial void XFlush(nint display);

    [LibraryImport("libX11.so.6")]
    private static unsafe partial void XSetErrorHandler(delegate* unmanaged<nint, nint, int> handler);

    [LibraryImport("libX11.so.6")]
    private static partial int XConnectionNumber(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint count, int timeout);

    // --- XDG Desktop Portal org.freedesktop.portal.GlobalShortcuts: the sanctioned Wayland capture ---

    private const string PortalDestination = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string GlobalShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    /// <summary>The shortcut ids this app binds; the Activated signal echoes them back.</summary>
    private const string PortalShortId = "clippy_save_short";
    private const string PortalLongId = "clippy_save_long";

    private LinuxDbus? portalBus;
    private string portalSender = "";
    private volatile bool portalRebindPending;
    private Thread? portalThread;

    private void StartPortal()
    {
        portalThread = new Thread(PortalLoop) { IsBackground = true, Name = "clippy-portal-keys" };
        portalThread.Start();
    }

    private void PortalLoop()
    {
        try
        {
            portalBus = LinuxDbus.Connect();
            portalSender = portalBus.Hello();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Console.WriteLine($"Hotkeys: no session bus for the GlobalShortcuts portal ({ex.Message}); X11 and evdev still work.");
            portalBus = null;
            return;
        }

        if (!TryCreateAndBind())
            return; // the session bus connection dies with the thread; X11 and evdev remain active

        var sessionPath = portalSessionPath;
        Console.WriteLine($"Hotkeys: GlobalShortcuts portal session bound ({config.ShortClipHotkey}, {config.LongClipHotkey}). " +
            "Plasma will show these keys under the app's Global Shortcuts.");

        while (!stopped)
        {
            // A rebind re-issues BindShortcuts on the SAME session: on KDE the triggers are
            // registered in kglobalaccel, so changed keys get a fresh (one-click) approval.
            if (portalRebindPending)
            {
                portalRebindPending = false;
                TryBindShortcuts(sessionPath, "rebind");
            }

            // A local capture: Dispose can null the field while this loop waits below.
            var bus = portalBus;
            if (bus is null)
                return;

            try
            {
                var (_, body) = bus.WaitForSignal(GlobalShortcutsInterface, sessionPath, 500);
                var reader = new DbusReader(body);
                reader.ReadObjectPath(); // the session handle, equal to the path we filtered on
                var shortcutId = reader.ReadString();
                var trigger = shortcutId == PortalShortId ? Trigger.Short
                    : shortcutId == PortalLongId ? Trigger.Long
                    : (Trigger?)null;
                if (trigger is { } t)
                    Dispatch(t, "portal");
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (IOException ex)
            {
                if (!stopped)
                    Console.WriteLine($"Hotkeys: the portal session closed ({ex.Message}); X11 and evdev keep working.");
                return;
            }
        }
    }

    private string portalSessionPath = "";

    private bool TryCreateAndBind()
    {
        var bus = portalBus;
        if (bus is null)
            return false;
        var token = NewPortalToken();
        var requestPath = RequestPathFor(token);
        try
        {
            // The Response can beat the method reply out of the daemon; the match rule has to be
            // in place BEFORE the call, exactly as the ScreenCast portal flow does it.
            bus.AddMatch($"type='signal',sender='{PortalDestination}',path='{requestPath}'," +
                         $"interface='{RequestInterface}',member='Response'");

            var reply = bus.Call(PortalDestination, PortalPath, GlobalShortcutsInterface, "CreateSession", "a{sv}",
                w => w.WriteOptionsDict(PortalOptions(token, withSessionToken: true)), 10_000);
            var replyPath = new DbusReader(reply.Body).ReadObjectPath();

            // The reply names the REQUEST handle; the session itself is announced by the Response
            // signal's results, under "session_handle" -- the same two-step the ScreenCast portal
            // needs, for the same reason. A portal that answers CreateSession directly never
            // sends that Response, in which case the reply path IS the session handle.
            try
            {
                var (code, body) = bus.WaitForSignal(RequestInterface, requestPath, 15_000);
                var results = new DbusReader(body).ReadResponseResults();
                portalSessionPath = code == 0 && results.TryGetValue("session_handle", out var handle)
                    ? handle.String
                    : replyPath;
                if (code != 0)
                {
                    Console.WriteLine($"Hotkeys: the portal CreateSession answered with response code {code}.");
                    return false;
                }
            }
            catch (TimeoutException)
            {
                portalSessionPath = replyPath;
            }
        }
        catch (IOException ex)
        {
            Console.WriteLine("Hotkeys: the GlobalShortcuts portal refused a session (" + ex.Message + "). " +
                "A portal needs to identify the running app; launching through the .desktop entry (autostart, app menu) " +
                "gives it an identity, while a bare terminal launch may not. The X11 grab and evdev captures are active.");
            return false;
        }

        // From here on, Activated deliveries ride the session path.
        bus.AddMatch($"type='signal',sender='{PortalDestination}',path='{portalSessionPath}'," +
                     $"interface='{GlobalShortcutsInterface}',member='Activated'");
        return TryBindShortcuts(portalSessionPath, "bind");
    }

    /// <summary>
    /// BindShortcuts with the configured triggers. The portal may answer only after the user
    /// works through the approval dialog (KDE shows one for keys it has not seen before), so the
    /// response timeout is generous and a timeout is logged, not fatal.
    /// </summary>
    private bool TryBindShortcuts(string sessionPath, string what)
    {
        var bus = portalBus;
        if (bus is null)
            return false;
        var token = NewPortalToken();
        var requestPath = RequestPathFor(token);
        try
        {
            // The Response can beat the method reply out of the daemon; the match rule has to be
            // in place BEFORE the call, exactly as the ScreenCast portal flow does it.
            bus.AddMatch($"type='signal',sender='{PortalDestination}',path='{requestPath}'," +
                         $"interface='{RequestInterface}',member='Response'");

            bus.Call(PortalDestination, PortalPath, GlobalShortcutsInterface, "BindShortcuts", "oa(sa{sv})sa{sv}",
                w =>
                {
                    w.ObjectPath(sessionPath);
                    WritePortalShortcuts(w);
                    w.String(""); // parent window: none
                    w.WriteOptionsDict(PortalOptions(token, withSessionToken: false));
                }, 15_000);

            var (code, body) = bus.WaitForSignal(RequestInterface, requestPath, 120_000);
            var results = new DbusReader(body).ReadResponseResults();
            if (code == 1)
            {
                Console.WriteLine($"Hotkeys: the portal shortcut {what} dialog was dismissed; the X11 and evdev captures stay active.");
                return false;
            }
            if (code != 0)
            {
                Console.WriteLine($"Hotkeys: the portal shortcut {what} failed with response code {code}.");
                return false;
            }

            // The response names the triggers actually in force (the dialog may have changed them).
            if (results.TryGetValue("shortcuts", out var value) && value.Shortcuts.Count > 0)
                foreach (var (id, props) in value.Shortcuts)
                    Console.WriteLine($"Hotkeys: portal shortcut '{id}' is bound to '{props.GetValueOrDefault("trigger")?.String ?? "?"}'.");
            return true;
        }
        catch (TimeoutException)
        {
            Console.WriteLine($"Hotkeys: nobody answered the portal shortcut {what} dialog; the X11 and evdev captures stay active.");
            return false;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Hotkeys: the portal shortcut {what} failed ({ex.Message}).");
            return false;
        }
    }

    /// <summary>The two shortcuts, with the current config as the preferred trigger.</summary>
    private void WritePortalShortcuts(DbusWriter w)
    {
        var (lengthPosition, contentStart) = w.BeginArray(8);
        void Shortcut(string id, string description, string trigger)
        {
            w.Align(8); // the (sa{sv}) struct
            w.String(id);
            w.WriteOptionsDict(new Dictionary<string, (string, Action<DbusWriter>)>
            {
                ["description"] = ("s", x => x.WriteString(description)),
                ["preferred_trigger"] = ("s", x => x.WriteString(trigger)),
            });
        }
        Shortcut(PortalShortId, "Save the short clip from the instant replay", config.ShortClipHotkey);
        Shortcut(PortalLongId, "Save the long clip from the instant replay", config.LongClipHotkey);
        w.EndArray(lengthPosition, contentStart);
    }

    private static Dictionary<string, (string, Action<DbusWriter>)> PortalOptions(string token, bool withSessionToken) =>
        withSessionToken
            ? new()
            {
                ["handle_token"] = ("s", w => w.WriteString(token)),
                ["session_handle_token"] = ("s", w => w.WriteString(token)),
            }
            : new() { ["handle_token"] = ("s", w => w.WriteString(token)) };

    private string RequestPathFor(string token)
    {
        var senderEscaped = portalSender.TrimStart(':').Replace('.', '_');
        return $"/org/freedesktop/portal/desktop/request/{senderEscaped}/{token}";
    }

    private void RequestPortalRebind()
    {
        if (portalBus is not null)
            portalRebindPending = true;
    }

    /// <summary>The token names the request handle, so it must be unique per request.</summary>
    private static string NewPortalToken() =>
        $"clippy_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Environment.ProcessId}";

    private const int OReadOnly = 0;      // O_RDONLY, blocking: one thread per device waits politely
    private const int ErrnoAgain = 11;

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint evdev_read(int fd, byte[] buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    public void Dispose()
    {
        stopped = true;
        try { listener?.Close(); } catch (Exception) { /* already closed */ }
        try { File.Delete(SocketPath); } catch (Exception) { /* never bound, or taken over */ }
        signalTarget = null;
        foreach (var fd in openedEvdevFds)
            close(fd);
        openedEvdevFds.Clear();

        // The X11 thread owns its display end to end: it polls the stopped flag every 200 ms,
        // exits the loop and closes the display itself -- closing it here would race the thread
        // into a double close. The portal thread's blocking wait throws the moment its bus fd is
        // closed below. Both threads are background, so even a straggler cannot hold the process.
        try { portalBus?.Dispose(); } catch (Exception) { /* already gone */ }
        portalBus = null;
    }
}
#endif
