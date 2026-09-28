using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// Global hotkeys for saving a clip while recording (phase 6). F9 saves 30 s, F10 saves 3 min.
///
/// A low-level keyboard hook (WH_KEYBOARD_LL) calls back on the thread that installed it, and the
/// OS gives that callback roughly a second before it silently unhooks us. So the callback does the
/// least possible work -- map the key, write a field, signal an event -- and a DEDICATED worker
/// thread performs the export. Not a pool thread: an export takes seconds, and the pool is shared
/// with everything else in the process.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    // Key codes and clip lengths come from config.json, not from constants, so the F9/F10 bindings
    // and the 30 s / 3 min lengths can be changed without rebuilding. A key the user did not map
    // is simply absent from the map and is ignored.
    private readonly Dictionary<int, double> bindings;

    private readonly Action<double> export;
    private readonly AutoResetEvent work = new(false);

    // Milliseconds, as a long, so the hook can swap it atomically and the worker can drain it in
    // one interlocked op. A double field cannot be volatile, and the exchange must be atomic.
    private long pendingTicks;
    private Thread? worker;
    private Thread? pump;
    private readonly ManualResetEvent hookReady = new(false);
    private IntPtr hook = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point location;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    public HotkeyService(Action<double> export, ClippyConfig config)
    {
        this.export = export;
        bindings = [];

        if (ClippyConfig.TryParseHotkey(config.ShortClipHotkey, out var shortVk))
            bindings[shortVk] = config.ShortClipSeconds;
        if (ClippyConfig.TryParseHotkey(config.LongClipHotkey, out var longVk))
            bindings[longVk] = config.LongClipSeconds;

        // A single key mapped to two lengths would be ambiguous, and the later mapping would simply
        // win, so the second one is dropped with a warning rather than silently shadowing the first.
        if (bindings.Count == 1 && config.ShortClipHotkey == config.LongClipHotkey)
        {
            Console.WriteLine($"Hotkeys: {config.ShortClipHotkey} is bound to both clip lengths; " +
                "only the short clip will be saved.");
        }

        Console.WriteLine("Hotkeys: " + string.Join(", ", bindings.Select(b => $"{KeyName(b.Key)} = {b.Value:F0}s")));
    }

    private static string KeyName(int vk) => $"F{vk - 0x70 + 1}";

    public void Start()
    {
        worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "clippy-export",
        };
        worker.Start();

        // The hook MUST be installed on a thread that pumps messages. A low-level hook procedure is
        // invoked by posting a message to the installing thread; the capture thread is parked in
        // stopped.Wait() and pumps nothing, so the hook silently receives nothing and every key
        // looks dead. That is exactly what we saw: SendInput of F9 produced no export at all.
        pump = new Thread(HookThread)
        {
            IsBackground = true,
            Name = "clippy-hotkey",
        };
        pump.Start();
        hookReady.WaitOne(TimeSpan.FromSeconds(5));
    }

    private void HookThread()
    {
        hookReady.Reset();
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, HookProc, GetModuleHandle(null), 0);
        hookReady.Set();

        if (hook == IntPtr.Zero)
        {
            Console.WriteLine(
                $"SetWindowsHookEx failed for WH_KEYBOARD_LL (error {Marshal.GetLastWin32Error()}).");
            return;
        }

        // Standard message pump. Without it the hook callback is never invoked.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            work.WaitOne();

            // Read the pending value ONCE and clear it, so a second press during an export is not
            // lost -- it simply overwrites and runs as soon as this one finishes.
            var seconds = Interlocked.Exchange(ref pendingTicks, 0);
            if (seconds == 0)
            {
                continue;
            }

            // Breadcrumbs, because a failure here used to end the process with nothing on screen.
            CrashLog.Write($"hotkey: export of {seconds / 1000.0:F0}s starting");
            try
            {
                export(seconds / 1000.0);
                CrashLog.Write("hotkey: export returned normally");
            }
            catch (Exception ex)
            {
                CrashLog.Write($"hotkey: export threw {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN))
            {
                var vk = Marshal.ReadInt32(lParam);
                double? seconds = bindings.TryGetValue(vk, out var configured) ? configured : null;

                if (seconds is { } s)
                {
                    // Everything below is the hook callback, so it stays trivial.
                    Interlocked.Exchange(ref pendingTicks, (long)(s * 1000));
                    work.Set();
                }
            }
        }
        catch (Exception ex)
        {
            // This runs inside a native callback: anything thrown here unwinds straight through the
            // OS message pump and takes the process with it, so it is caught and logged instead.
            CrashLog.Write($"hotkey hook threw {ex.GetType().FullName}: {ex.Message}");
        }

        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }

        work.Dispose();
    }

    private delegate IntPtr HookProcDelegate(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProcDelegate lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
