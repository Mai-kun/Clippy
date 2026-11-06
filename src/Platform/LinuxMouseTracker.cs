#if LINUX
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// Where the cursor REALLY is, on Wayland: a lightweight evdev pointer tracker.
///
/// The reason it exists is an XWayland limitation hit in live testing on KDE Plasma: the XWayland
/// server refreshes XQueryPointer's coordinates only while the cursor travels over X11/XWayland
/// windows. Over a native Wayland surface (the Plasma desktop, a Wayland-native app) the pointer
/// moves, but XQueryPointer keeps answering with the coordinates of the last XWayland contact --
/// so on a dual-head setup the overlay card kept appearing on the monitor the cursor had last
/// touched an X11 window on, never the one it is actually on.
///
/// The fix rides the same /dev/input/event* access the hotkey service already uses: pointer
/// devices (EV_REL with REL_X+REL_Y, or EV_ABS with ABS_X+ABS_Y) are read raw, and every REL_X /
/// REL_Y delta is summed onto a seeded position, clamped to the union of the Xinerama monitors.
/// The seed comes from one XQueryPointer at startup -- stale or not, it is exact for a cursor that
/// has not moved since; from then on the physical deltas are the truth, over Wayland surfaces
/// included. Absolute devices (tablets, keyboard-embedded touch panels) are mapped by delta too,
/// scaled axis-range-to-screen, with a re-touch guard so lifting the pen does not teleport the
/// estimate. One thing raw evdev cannot reproduce is libinput's pointer acceleration: the sum is
/// close enough to pick the right monitor, which is the only question asked of it.
///
/// Nothing here throws: a tracker that cannot start (no /dev/input permission, no X11) just makes
/// the overlay fall back to XQueryPointer, the pre-tracker behaviour.
/// </summary>
internal static partial class LinuxMouseTracker
{
    private static readonly object gate = new();
    private static bool started;
    private static int readers;                 // live per-device threads
    private static nint display;                // own X connection: geometry and the seed only
    private static int screen;
    private static double cursorX, cursorY;     // the running estimate, in X root coordinates
    private static bool positioned;             // the estimate was seeded at least once

    private static int boundsMinX, boundsMinY, boundsMaxX, boundsMaxY;
    private static long lastGeometryTicks;
    private static readonly bool[] watched = new bool[MaxDevices]; // per eventN: a reader owns it

    private const int MaxDevices = 32;
    private const int GeometryRefreshMs = 5_000; // hotplugged monitors show up on the next ask

    /// <summary>
    /// Scans /dev/input/event* for pointer devices once and starts one reader thread per device.
    /// Safe to call repeatedly (the overlay's SetUp is the only caller today); without an X
    /// connection there is neither a seed nor a screen union to clamp to, so it stands down.
    /// </summary>
    public static void Start()
    {
        lock (gate)
        {
            if (started)
                return;
            started = true;
        }

        try
        {
            display = XOpenDisplay(null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.WriteLine($"MouseTracker: libX11 unavailable ({ex.GetType().Name}); cursor tracking is off.");
            display = 0;
        }
        if (display == 0)
            return; // no X11 at all: the overlay is off too, nobody will ask for a position

        screen = XDefaultScreen(display);
        // Seed BEFORE any reader starts: deltas landing on an unseeded position would be thrown
        // away by the seed, and the seed is exactly right for a cursor that has not moved yet.
        if (XQueryPointer(display, XDefaultRootWindow(display),
                out _, out _, out var seedX, out var seedY, out _, out _, out _) != 0)
        {
            cursorX = seedX;
            cursorY = seedY;
            positioned = true;
            Console.WriteLine($"MouseTracker: seeded the cursor estimate at ({seedX}, {seedY}).");
        }

        RefreshGeometry();
        var opened = WatchNewDevices();
        readers = opened;
        if (opened == 0)
        {
            Console.WriteLine("MouseTracker: no readable pointer device; the overlay falls back to XQueryPointer " +
                "(add the user to the 'input' group for exact multi-monitor tracking on Wayland).");
        }
        else
        {
            Console.WriteLine($"MouseTracker: tracking the cursor through {opened} evdev device(s).");
        }
    }

    /// <summary>
    /// Opens every /dev/input/event* node that is a pointer device and not yet watched, one reader
    /// thread per node. Re-run on the periodic geometry refresh, so a mouse plugged in after
    /// startup (or a virtual input device) joins the sum without a restart. Returns how many
    /// devices are now being tracked in total.
    /// </summary>
    private static int WatchNewDevices()
    {
        var opened = 0;
        lock (gate)
        {
            for (var i = 0; i < MaxDevices; i++)
            {
                var path = $"/dev/input/event{i}";
                // No break on a missing node: numbering has holes once devices are unplugged, and
                // the tracker wants every pointer behind them.
                if (!File.Exists(path))
                    continue;
                if (watched[i])
                    continue;
                try
                {
                    var fd = open(path, OReadOnly);
                    if (fd < 0)
                        continue;
                    if (!TryGetAxisBounds(fd, out var kind, out var minX, out var minY, out var maxX, out var maxY))
                    {
                        close(fd);
                        continue; // keyboards, audio jacks, power buttons: not our business
                    }
                    watched[i] = true;
                    openedFds.Add(fd);
                    readers++; // the estimate goes live with the first tracked device
                    var index = i;
                    var thread = new Thread(() => PointerLoop(index, fd, kind, minX, minY, maxX, maxY))
                    {
                        IsBackground = true,
                        Name = $"clippy-mouse-{i}",
                    };
                    thread.Start();
                    opened++;
                    Console.WriteLine($"MouseTracker: watching {path} ({kind}).");
                }
                catch (Exception)
                {
                    // Not readable (no input group): counted by readers below.
                }
            }
            opened = readers;
        }
        return opened;
    }

    /// <summary>
    /// The current cursor estimate, true when the tracker is live. The estimate is clamped to the
    /// union of the Xinerama monitors (re-queried every few seconds, so a hotplug is picked up);
    /// the caller still hit-tests the point against its own monitor list and falls back on a miss.
    /// </summary>
    public static bool TryGetCursor(out int x, out int y)
    {
        lock (gate)
        {
            x = 0;
            y = 0;
            if (!positioned || readers == 0)
                return false;
            RefreshGeometryIfStale();
            x = Clamp(Math.Round(cursorX), boundsMinX, boundsMaxX);
            y = Clamp(Math.Round(cursorY), boundsMinY, boundsMaxY);
            return true;
        }
    }

    private static int Clamp(double value, int min, int max) =>
        (int)Math.Clamp(value, Math.Min(min, max), Math.Max(min, max));

    // --- Per-device reading ---

    private enum PointerKind { Relative, Absolute }

    private sealed record AxisBounds(int Min, int Max);

    /// <summary>
    /// True when the device is a pointer: EV_REL with both REL_X and REL_Y (a mouse), or EV_ABS
    /// with both ABS_X and ABS_Y (a tablet / touch panel). The axis ranges are read along the way
    /// so the absolute mapping needs no second round of ioctls.
    /// </summary>
    private static bool TryGetAxisBounds(int fd, out PointerKind kind,
        out int minX, out int minY, out int maxX, out int maxY)
    {
        minX = minY = maxX = maxY = 0;
        kind = PointerKind.Relative;

        Span<byte> types = stackalloc byte[8];
        if (ioctl(fd, EviocgbitTypes, types) < 0)
            return false;
        var hasRel = (types[0] & (1 << 2 /* EV_REL */)) != 0;
        var hasAbs = (types[0] & (1 << 3 /* EV_ABS */)) != 0;

        if (hasRel)
        {
            Span<byte> relBits = stackalloc byte[8];
            if (ioctl(fd, EviocgbitRel, relBits) >= 0 && (relBits[0] & 0x03) == 0x03) // REL_X | REL_Y
            {
                kind = PointerKind.Relative;
                return true;
            }
        }

        if (hasAbs)
        {
            Span<byte> absBits = stackalloc byte[64];
            if (ioctl(fd, EviocgbitAbs, absBits) >= 0 && (absBits[0] & 0x03) == 0x03) // ABS_X | ABS_Y
            {
                if (!TryReadAbsInfo(fd, AbsX, out var ax) || !TryReadAbsInfo(fd, AbsY, out var ay))
                    return false;
                kind = PointerKind.Absolute;
                minX = ax.Min;
                minY = ay.Min;
                maxX = ax.Max;
                maxY = ay.Max;
                return true;
            }
        }

        return false;
    }

    private static bool TryReadAbsInfo(int fd, int code, out AxisBounds bounds)
    {
        bounds = new AxisBounds(0, 0);
        Span<byte> info = stackalloc byte[24]; // struct input_absinfo
        if (ioctl(fd, EviocgabsBase + (uint)code, info) < 0)
            return false;
        // value, minimum, maximum, fuzz, flat, resolution -- we need min and max, little-endian.
        var minimum = info[4] | info[5] << 8 | info[6] << 16 | info[7] << 24;
        var maximum = info[8] | info[9] << 8 | info[10] << 16 | info[11] << 24;
        if (maximum <= minimum)
            return false;
        bounds = new AxisBounds(minimum, maximum);
        return true;
    }

    /// <summary>
    /// One blocking read loop per device, the same shape the hotkey service uses. Relative devices
    /// sum their deltas onto the shared estimate; absolute devices contribute deltas derived from
    /// the raw axis values (scaled range-to-screen), skipping the first sample after a pause --
    /// that one is a re-touch, not a movement.
    /// </summary>
    private static void PointerLoop(int index, int fd, PointerKind kind, int absMinX, int absMinY, int absMaxX, int absMaxY)
    {
        // input_event on linux-x64: timeval (2 x i64), u16 type, u16 code, i32 value, pad = 24
        // bytes; the type/code/value fields start AFTER the 64-bit timeval, at offsets 16/18/20.
        var record = new byte[24];
        var lastAbsX = 0;
        var lastAbsY = 0;
        var lastAbsTicks = 0L;
        var absSeen = false;

        try
        {
            while (true)
            {
            var bytes = evdev_read(fd, record, (nuint)record.Length);
            if (bytes < 24)
            {
                if (bytes == 0 || (bytes < 0 && Marshal.GetLastWin32Error() != ErrnoAgain))
                    return; // the device went away (unplugged mouse); the estimate stays where it is
                continue;
            }

            var type = BitConverter.ToUInt16(record, 16);
            var code = BitConverter.ToUInt16(record, 18);
            var value = BitConverter.ToInt32(record, 20);

            if (kind == PointerKind.Relative)
            {
                // EV_REL=2, REL_X=0, REL_Y=1. Locked per event: mouse events are sparse (hundreds
                // a second at most), the lock is uncontended and keeps the sum exact.
                if (type == 2 && code is 0 or 1)
                {
                    lock (gate)
                    {
                        if (code == 0)
                            cursorX += value;
                        else
                            cursorY += value;
                        ClampToScreen();
                    }
                }
                continue;
            }

            // Absolute device: raw axis value -> screen delta, scaled by the axis range. A sample
            // after a >=300 ms pause is the pen/finger coming back down somewhere else: absorb it
            // as the new origin instead of dragging the estimate across the screen.
            if (type != 3 /* EV_ABS */ || (code != AbsX && code != AbsY))
                continue;
            var now = Environment.TickCount64;
            var resumed = absSeen && now - lastAbsTicks > 300;
            lastAbsTicks = now;
            var previous = code == AbsX ? lastAbsX : lastAbsY;
            if (code == AbsX)
                lastAbsX = value;
            else
                lastAbsY = value;
            if (!absSeen)
            {
                absSeen = true;
                resumed = false; // the very first sample just establishes the origin
            }
            if (resumed)
                continue;

            // The screen spans were read at open time (boundsMin/Max); the axis spans came
            // from EVIOCGABS. Both X and Y may report in one report, so each axis applies alone.
            if (code == AbsX)
            {
                var delta = (value - previous) * (boundsMaxX - boundsMinX) / (double)(absMaxX - absMinX);
                lock (gate)
                {
                    cursorX += delta;
                    ClampToScreen();
                }
            }
            else
            {
                var delta = (value - previous) * (boundsMaxY - boundsMinY) / (double)(absMaxY - absMinY);
                lock (gate)
                {
                    cursorY += delta;
                    ClampToScreen();
                }
            }
            }
        }
        finally
        {
            // The device went away (or the loop died): free its slot, so the periodic rescan can
            // take it back if it returns, and the estimate dies with the last tracked device.
            lock (gate)
            {
                if (watched[index])
                {
                    watched[index] = false;
                    readers--;
                }
            }
        }
    }

    // --- Screen union, from Xinerama ---

    /// <summary>Clamps the estimate into the current screen union, inside the caller's lock.</summary>
    private static void ClampToScreen()
    {
        if (cursorX < boundsMinX) cursorX = boundsMinX;
        if (cursorX > boundsMaxX) cursorX = boundsMaxX;
        if (cursorY < boundsMinY) cursorY = boundsMinY;
        if (cursorY > boundsMaxY) cursorY = boundsMaxY;
    }

    private static void RefreshGeometryIfStale()
    {
        if (Environment.TickCount64 - lastGeometryTicks < GeometryRefreshMs)
            return;
        RefreshGeometry();
        WatchNewDevices(); // the same cadence picks up a mouse plugged in since startup
    }

    /// <summary>The union rectangle of every connected monitor, in X root coordinates.</summary>
    private static void RefreshGeometry()
    {
        try
        {
            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = int.MinValue;
            var maxY = int.MinValue;
            var found = false;
            var infos = XineramaQueryScreens(display, out var count);
            if (infos != 0 && count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var monitor = Marshal.PtrToStructure<XineramaScreenInfo>(infos + i * Marshal.SizeOf<XineramaScreenInfo>());
                    if (monitor.width <= 0 || monitor.height <= 0)
                        continue;
                    minX = Math.Min(minX, monitor.x_org);
                    minY = Math.Min(minY, monitor.y_org);
                    maxX = Math.Max(maxX, monitor.x_org + monitor.width - 1);
                    maxY = Math.Max(maxY, monitor.y_org + monitor.height - 1);
                    found = true;
                }
                XFree(infos);
            }
            if (!found)
            {
                minX = 0;
                minY = 0;
                maxX = XDisplayWidth(display, screen) - 1;
                maxY = XDisplayHeight(display, screen) - 1;
            }
            boundsMinX = minX;
            boundsMinY = minY;
            boundsMaxX = maxX;
            boundsMaxY = maxY;
            lastGeometryTicks = Environment.TickCount64;
        }
        catch (Exception)
        {
            // A geometry hiccup keeps the previous bounds; there is always a first query that
            // succeeded, or Start would not have opened the readers.
        }
    }

    // --- libc and Xlib ---

    private const int OReadOnly = 0;
    private const int ErrnoAgain = 11;

    // EVIOCGBIT(ev, len) = _IOC(_IOC_READ=2, 'E', 0x20 + ev, len); EVIOCGABS(code) adds 0x40.
    private const uint EviocgbitTypes = 0x80084520u;
    private const uint EviocgbitRel = 0x80084522u;
    private const uint EviocgbitAbs = 0x80404523u;
    private const int AbsX = 0;
    private const int AbsY = 1;
    private const uint EviocgabsBase = 0x80244540u;

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint evdev_read(int fd, byte[] buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int ioctl(int fd, uint request, nint argument);

    private static unsafe int ioctl(int fd, uint request, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
            return ioctl(fd, request, (nint)p);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XineramaScreenInfo
    {
        public int screen_number;
        public short x_org;
        public short y_org;
        public short width;
        public short height;
    }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XOpenDisplay(string? name);

    [LibraryImport("libX11.so.6")]
    private static partial int XDefaultScreen(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XDisplayWidth(nint display, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial int XDisplayHeight(nint display, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryPointer(nint display, nint window,
        out nint rootReturn, out nint childReturn,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport("libXinerama.so.1")]
    private static partial nint XineramaQueryScreens(nint display, out int number);

    [LibraryImport("libX11.so.6")]
    private static partial int XFree(nint data);

    private static readonly List<int> openedFds = []; // fds stay open for the process lifetime
}
#endif
