#if LINUX
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// The three clip triggers Linux has instead of a global Windows keyboard hook:
///
/// 1. a Unix-domain IPC socket (/tmp/clippy-ipc.sock), so "clippy --save-short" -- bound to any
///    compositor shortcut on KDE Plasma or Niri -- saves a clip and prints its path;
/// 2. SIGUSR1 / SIGUSR2, for the same job from a shell or a script;
/// 3. raw evdev (/dev/input/event*), so the configured F-keys work with nothing else set up when
///    the process can read the input group's devices.
///
/// Wayland deliberately has no global keyboard capture, so there is no single mechanism; three
/// cheap ones cover every setup.
/// </summary>
internal sealed partial class LinuxHotkeyService : IDisposable
{
    public const string SocketPath = "/tmp/clippy-ipc.sock";

    /// <summary>A clip request that arrived while the exporter is busy is answered with ERR busy, never queued.</summary>
    public enum Trigger { Short, Long, Stop }

    private readonly Func<Trigger, string> handle; // returns "OK <path>" / "ERR <reason>"
    private readonly ClippyConfig config;
    private Socket? listener;
    private Thread? acceptThread;
    private volatile bool stopped;
    private readonly List<int> openedEvdevFds = [];

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
    /// ..KEY_F10=68, KEY_F11=87, KEY_F12=88, KEY_F13..=183..). Only keyboards emit those codes, so
    /// no device-type filtering is needed. Nothing readable => the compositor-shortcut hint.
    /// </summary>
    private void StartEvdev()
    {
        var shortCode = HotkeyCode(config.ShortClipHotkey);
        var longCode = HotkeyCode(config.LongClipHotkey);
        if (shortCode == 0 && longCode == 0)
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
                var thread = new Thread(() => EvdevLoop(fd, shortCode, longCode))
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
            Console.WriteLine("Hotkeys: /dev/input is not readable, so the F-key hotkeys cannot be captured directly. " +
                "Bind your compositor shortcuts (KDE: System Settings > Shortcuts; Niri: the config binds) to " +
                "'clippy --save-short' and 'clippy --save-long', or send SIGUSR1/SIGUSR2.");
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

    private void EvdevLoop(int fd, ushort shortCode, ushort longCode)
    {
        // input_event on linux-x64: timeval (2 x i64), u16 type, u16 code, i32 value, 4 pad = 24 bytes.
        var record = new byte[24];
        while (!stopped)
        {
            // Blocking read, one thread per device, so a silent keyboard never starves the others.
            var bytes = evdev_read(fd, record, (nuint)record.Length);
            if (bytes < 16)
            {
                if (bytes == 0 || (bytes < 0 && Marshal.GetLastWin32Error() != ErrnoAgain))
                    return; // the device went away; the other triggers still work
                continue;
            }

            var type = BitConverter.ToUInt16(record, 8);
            var code = BitConverter.ToUInt16(record, 10);
            var value = BitConverter.ToInt32(record, 12);
            if (type != 1 /* EV_KEY */ || value != 1 /* key down, not autorepeat */)
                continue;

            if (code == shortCode && shortCode != 0)
                handle(Trigger.Short);
            else if (code == longCode && longCode != 0)
                handle(Trigger.Long);
        }
    }

    /// <summary>"F8" -> 66. Returns 0 for names that are not plain function keys.</summary>
    internal static ushort HotkeyCode(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0;
        var key = name.Trim().ToUpperInvariant();
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
    }
}
#endif
