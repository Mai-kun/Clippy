using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// A Medal / ShadowPlay-style game overlay notification: a compact dark card that slides into the
/// top-right corner, fades in, holds, and fades out -- without ever stealing focus from the running
/// game.
///
/// ponytail: this is raw Win32 for the same reason <see cref="TrayIcon"/> is. A WPF/WinForms toast
/// would drag a UI stack into a NativeAOT console app that has none, and a layered window is a few
/// hundred bytes of interop. The window lives on its own thread with its own message pump because
/// the fade animation needs a timer and the capture thread pumps nothing.
///
/// The card is drawn into a 32-bit ARGB DIB in a memory DC and pushed to the screen with
/// UpdateLayeredWindow. Per-pixel alpha (set by a final managed pass, so it is correct no matter
/// what GDI leaves in the alpha byte) gives the rounded corners; the global fade is done by varying
/// the blend's SourceConstantAlpha, which multiplies the per-pixel alpha -- so corners stay
/// transparent while the whole card fades.
///
/// WS_EX_NOACTIVATE (0x08000000) keeps the window from taking foreground activation and
/// WS_EX_TRANSPARENT makes clicks pass straight through it: neither may disturb a full-screen game.
/// </summary>
internal sealed partial class OverlayNotification : IDisposable
{
    // Design units, as laid out at 96 DPI. Everything below is multiplied by the target monitor's
    // scale before it is used: a card built at 320 physical pixels is a quarter of its intended size
    // on a 200% display, and its 16 px font with it. See Layout().
    private const int BaseWidth = 320;
    private const int BaseHeight = 68;
    private const int BaseCornerRadius = 12;
    private const int BaseBorder = 1;
    private const int BaseMargin = 16;

    // The progress strip rides below the text block when a payload carries one: the card grows by
    // BarZone design pixels and draws a 6 px pill there, track and fill from the shared palette
    // (NotificationCenter owns the colours, so both platforms' bars are the same bar).
    private const int BaseBarZone = NotificationCenter.BarZoneDesignPx;

    // Text only, no left-hand icon: the inset is the same on both sides, so the two lines read as
    // one centred block instead of a label stuck beside a picture. 16 px of air on every side of
    // the text block -- the 14/12 px fonts inside the 68 px card leave 18 px above and below, so
    // the ink never touches the border.
    private const int BaseTextX = 16;
    // Vertical rhythm of the two lines: title, a small gap, subtitle. Layout() centres the whole
    // block on the card's height, so this gap is the only thing separating the lines.
    private const int BaseLineGap = 5;

    // Font heights are in pixels here (the memory DC is a screen DC, so 1 unit = 1 pixel) and are
    // the one place the scale has to reach: a 14 px title on a 200% monitor must be 28 px or it is
    // unreadable next to the game's own UI.
    internal const int BaseTitleFontPx = 14;
    private const int BaseMessageFontPx = 12;

    // Enough to fit the 320 px card at Segoe UI without wrapping; a longer string is ellipsised
    // rather than allowed to spill past the rounded border. Character counts do not scale: the card
    // grows in pixels at the same rate as the font, so the capacity is the same at every DPI.
    private const int TitleMaxChars = 28;
    private const int MessageMaxChars = 38;

    private const int FadeInMs = 120;
    private const int HoldMs = 2600;
    private const int FadeOutMs = 220;
    private const int TimerIntervalMs = 15;

    private const int TimerId = 1;

    // Window messages.
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_TIMER = 0x0113;
    private const int WM_DESTROY = 0x0002;
    private const int WM_APP = 0x8000;
    private const int WM_SHOW = WM_APP + 1;
    private const int WM_STOP = WM_APP + 2;

    // Extended / normal window styles.
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_POPUP = unchecked((int)0x80000000);

    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    // SetWindowPos flags: never activate, never reorder z, and the two "I am only changing this" ones.
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    // WM_MOUSEACTIVATE answer: "do not activate, do not discard the mouse message".
    private const int MA_NOACTIVATE = 3;

    // MONITOR_DEFAULTTONEAREST: a null rectangle gets the closest monitor instead of nothing, so a
    // minimized or destroyed foreground window still resolves to a real screen.
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MDT_EFFECTIVE_DPI = 0;   // the DPI the shell draws at, not the raw one

    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int TRANSPARENT_BK = 1;
    private const int DEFAULT_CHARSET = 1;
    private const int FW_BOLD = 700;
    private const uint SPI_GETWORKAREA = 0x0030;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // COLORREF values are 0x00BBGGRR.
    private const uint BgColor = 0x001E1E1E;      // #1E1E1E dark card
    private const uint BorderColor = 0x003D3D3D;  // #3D3D3D neutral dark grey hairline
    private const uint TitleColor = 0x00F0F0F0;   // near-white bold title
    private const uint MessageColor = 0x00AAAAAA; // grey message
    private const uint TrackColor = 0x003D3D3D;   // #3D3D3D the progress bar's groove
    private const uint AccentColor = 0x005EC522;  // #22C55E the moving fill (0x00BBGGRR of the shared accent)

    private enum Phase { Idle, FadeIn, Hold, StickyHold, FadeOut }

    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private volatile bool disposed;

    // Written on whatever thread calls ShowOrUpdate before PostMessage, read on the overlay thread
    // in WM_SHOW. A reference write is atomic, and the payload record is immutable once built, so
    // no lock is needed: the overlay thread either sees the old payload or the new one, both valid.
    private volatile NotificationPayload pendingPayload = new() { Title = "", Subtitle = "" };

    // Overlay-thread state.
    private nint hwnd;
    private nint memDc;
    private nint dib;
    private nint oldBitmap;
    private nint bitsPtr;
    private nint borderBrush;
    private nint bgBrush;
    private nint trackBrush;
    private nint accentBrush;
    private nint titleFont;
    private nint messageFont;
    private int posX;
    private int posY;
    private Phase phase = Phase.Idle;
    private int alpha;
    private long phaseStartTicks;
    // How long the current card holds before fading out; payload-driven, because the completed
    // card wants to be read rather than glimpsed.
    private int holdMs = HoldMs;
    // The wall-clock moment the current card APPEARED, anchoring the lifecycle watchdogs.
    private long shownAtTicks;

    /// <summary>A sticky card parks for at most this long without a payload refresh; a hung
    /// download step must not hold the screen hostage (same net the Linux card hangs).</summary>
    private const int StickyRefreshTimeoutMs = 30_000;

    // The card's real pixel metrics for the monitor it is currently aimed at, derived from the
    // Base* design units by Layout(). Not constants: the game can sit on a 100% screen one clip and
    // a 200% one the next, and the DIB and the fonts are built at one size, so graphicsDpi records
    // which size that was and BeginShow rebuilds them when it stops matching.
    private float layoutScale = 1f;
    private uint graphicsDpi;

    // Read by the smoke test: it asserts the scaled numbers without a monitor of its own.
    internal int Width;
    internal int Height;
    internal int Margin;
    internal int MessageY;
    internal int TitleY;
    internal int TextX;
    internal int Border;
    /// <summary>Height of the two text lines plus the gap between them; what <see cref="TitleY"/> centres.</summary>
    internal int TextHeight;
    internal int TitleFontPx;
    private int messageFontPx = BaseMessageFontPx;

    private int CornerRadius = BaseCornerRadius;

    // Kept alive for the life of the window: if the GC collected the delegate while the window still
    // existed, Windows would call into freed memory.
    private readonly WndProcDelegate wndProc;

    public OverlayNotification()
    {
        wndProc = WndProc;
        // The design units have to land in the fields before the window exists: CreateWindowEx and
        // the DIB both read them, and 0 would make a zero-sized window.
        Layout(96);
    }

    /// <summary>
    /// Creates the overlay window on its own thread and waits until it exists (or fails). Safe to
    /// call before any <see cref="Show"/>.
    /// </summary>
    public void Start()
    {
        if (disposed)
            return;

        thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "clippy-overlay",
        };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// True once the overlay window exists and can be shown. Checked after <see cref="Start"/> so the
    /// caller can fall back to the Windows toast when the layered window could not be created (a rare
    /// headless / restricted-session case) rather than announcing a clip by silence.
    /// </summary>
    public bool IsReady => hwnd != 0;

    /// <summary>
    /// Queues a payload to show. Thread-safe: called from any thread, delivered on the overlay
    /// thread. A card already on screen is updated IN PLACE -- same window, new content, no fade
    /// restart -- and the payload decides whether the close timer runs (sticky cards live until a
    /// non-sticky payload replaces them).
    /// </summary>
    public void ShowOrUpdate(NotificationPayload payload)
    {
        if (disposed || hwnd == 0)
            return;

        pendingPayload = payload;
        PostMessage(hwnd, WM_SHOW, 0, 0);
    }

    /// <summary>The plain two-line card: a payload without a bar, for simple callers.</summary>
    public void Show(string title, string message) =>
        ShowOrUpdate(new NotificationPayload { Title = title, Subtitle = message });

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        if (hwnd != 0)
            PostMessage(hwnd, WM_STOP, 0, 0);

        thread?.Join(TimeSpan.FromSeconds(2));
        ready.Dispose();
    }

    private void Pump()
    {
        const string className = "ClippyOverlayWindow";

        var windowClass = new WindowClassEx
        {
            cbSize = Marshal.SizeOf<WindowClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = Marshal.StringToHGlobalUni(className),
        };

        var registered = RegisterClassEx(ref windowClass);
        Marshal.FreeHGlobal(windowClass.lpszClassName);
        if (registered == 0)
        {
            Console.WriteLine($"Overlay: RegisterClassEx failed (error {Marshal.GetLastWin32Error()}); notifications disabled.");
            ready.Set();
            return;
        }

        var placement = ComputePlacement();
        posX = placement.X;
        posY = placement.Y;

        // WS_POPUP (no frame), WS_EX_NOACTIVATE (never foreground), WS_EX_TRANSPARENT (clicks pass
        // through), WS_EX_TOOLWINDOW (out of Alt+Tab), WS_EX_TOPMOST (over the game), WS_EX_LAYERED
        // (per-pixel alpha). Created hidden; WM_SHOW is what makes it appear.
        hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            className, "ClippyOverlay", WS_POPUP,
            posX, posY, Width, Height, 0, 0, windowClass.hInstance, 0);
        if (hwnd == 0)
        {
            Console.WriteLine($"Overlay: CreateWindowEx failed (error {Marshal.GetLastWin32Error()}); notifications disabled.");
            ready.Set();
            return;
        }

        CreateGraphicsResources();
        ready.Set();

        while (GetMessage(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        // GDI objects are thread-affine (a DC may only be deleted by the thread that made it), so
        // they are freed here on the overlay thread rather than in Dispose.
        FreeGraphicsResources();
    }

    /// <summary>Creates the memory DC, then the per-size DIB.</summary>
    private unsafe void CreateGraphicsResources()
    {
        var screenDc = GetDC(0);
        memDc = CreateCompatibleDC(screenDc);
        if (screenDc != 0)
            ReleaseDC(0, screenDc);

        ResizeGraphics();
    }

    /// <summary>
    /// Rebuilds everything whose size depends on the target monitor's DPI: the DIB and the two fonts.
    /// Called when the DPI changes, which means a new card on another monitor, or a resolution change
    /// under a running Clippy. Stretching a 320 px bitmap to 640 would be the blurry result DPI
    /// awareness exists to avoid, so the pixels are redrawn at the real size instead.
    /// </summary>
    private unsafe void ResizeGraphics()
    {
        if (memDc == 0)
            return;

        if (dib != 0)
        {
            SelectObject(memDc, oldBitmap);
            DeleteObject(dib);
            dib = 0;
            bitsPtr = 0;
        }
        if (titleFont != 0) { DeleteObject(titleFont); titleFont = 0; }
        if (messageFont != 0) { DeleteObject(messageFont); messageFont = 0; }
        if (borderBrush != 0) { DeleteObject(borderBrush); borderBrush = 0; }
        if (bgBrush != 0) { DeleteObject(bgBrush); bgBrush = 0; }

        var header = new BitmapInfoHeader
        {
            biSize = (uint)sizeof(BitmapInfoHeader),
            biWidth = Width,
            biHeight = -Height, // negative => top-down rows, which is what UpdateLayeredWindow wants
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        dib = CreateDIBSection(memDc, &header, 0, out bitsPtr, 0, 0);
        if (dib != 0 && bitsPtr != 0)
            oldBitmap = SelectObject(memDc, dib);

        borderBrush = CreateSolidBrush(BorderColor);
        bgBrush = CreateSolidBrush(BgColor);
        trackBrush = CreateSolidBrush(TrackColor);
        accentBrush = CreateSolidBrush(AccentColor);

        // CreateFont's height is in pixels on a screen DC and must be negative for "cell height",
        // which is what a design size means. TitleFontPx / messageFontPx already carry the monitor's
        // scale from Layout(); scaling again here would compound it.
        titleFont = CreateFont(-TitleFontPx, 0, 0, 0, FW_BOLD, 0, 0, 0, DEFAULT_CHARSET,
            0, 0, 0, 0, "Segoe UI");
        messageFont = CreateFont(-messageFontPx, 0, 0, 0, 0, 0, 0, 0, DEFAULT_CHARSET,
            0, 0, 0, 0, "Segoe UI");
    }

    private void FreeGraphicsResources()
    {
        if (memDc != 0)
        {
            if (dib != 0)
                SelectObject(memDc, oldBitmap);
            DeleteDC(memDc);
            memDc = 0;
        }

        if (dib != 0) { DeleteObject(dib); dib = 0; }
        if (borderBrush != 0) { DeleteObject(borderBrush); borderBrush = 0; }
        if (bgBrush != 0) { DeleteObject(bgBrush); bgBrush = 0; }
        if (trackBrush != 0) { DeleteObject(trackBrush); trackBrush = 0; }
        if (accentBrush != 0) { DeleteObject(accentBrush); accentBrush = 0; }
        if (titleFont != 0) { DeleteObject(titleFont); titleFont = 0; }
        if (messageFont != 0) { DeleteObject(messageFont); messageFont = 0; }
    }

    /// <summary>
    /// Decides where the card goes and how big it is, from the monitor the player is actually
    /// looking at: the one holding the foreground window (the game), not the primary display. On a
    /// two-monitor setup the old code always drew on monitor 0, so a notification about a clip of a
    /// game running on monitor 1 appeared where the player was not looking.
    ///
    /// The DPI of THAT monitor drives the scale. A monitor is not a property of the process: 125% on
    /// one screen and 200% on the other is the normal multi-display setup, and the card follows the
    /// game rather than the desktop it was launched from.
    /// </summary>
    private Placement ComputePlacement()
    {
        // MONITOR_DEFAULTTONEAREST rather than NULL: the foreground window can be gone by the time
        // this runs (it is read on the overlay thread, after the game closed), and "nearest" still
        // yields a real screen instead of a null handle that would silently disable the feature.
        var monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
        var dpi = MonitorDpi(monitor);
        Layout(dpi);

        var work = new Rect();
        if (!GetMonitorInfo(monitor, ref work) &&
            !SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0))
        {
            // Neither call worked: fall back to the whole primary screen rather than drawing at 0,0.
            work = new Rect
            {
                right = GetSystemMetrics(SM_CXSCREEN),
                bottom = GetSystemMetrics(SM_CYSCREEN),
            };
        }

        // rcWork, not rcMonitor: the taskbar is part of the screen and the card must not sit under
        // it. 16 design px in from the right and top edges, scaled with everything else.
        var spot = CornerInWorkArea(work.right, work.top, Width, Margin);
        return new Placement(spot.x, spot.y, dpi);
    }

    /// <summary>
    /// Where the card's left edge goes: inside the right edge of the work area it was given, by its
    /// own width plus the margin. Separate from <see cref="ComputePlacement"/> because this is the
    /// one line that decides whether the card lands on the game's monitor or somewhere else, and
    /// therefore the one line worth testing against a second monitor's rectangle (see SmokeTest).
    /// </summary>
    internal static (int x, int y) CornerInWorkArea(int workRight, int workTop, int width, int margin) =>
        (workRight - margin - width, workTop + margin);

    /// <summary>
    /// Recomputes every pixel metric from the Base* design units for <paramref name="dpi"/>.
    /// Called on each show, so a game dragged to another display re-lays-out the card.
    /// </summary>
    internal void Layout(uint dpi)
    {
        layoutScale = dpi / 96f;

        Width = Scaled(BaseWidth);
        Height = Scaled(BaseHeight);
        CornerRadius = Scaled(BaseCornerRadius);
        Border = Math.Max(1, Scaled(BaseBorder));
        Margin = Scaled(BaseMargin);
        TextX = Scaled(BaseTextX);
        TitleFontPx = Scaled(BaseTitleFontPx);
        messageFontPx = Scaled(BaseMessageFontPx);

        // One text block, centred as a whole: title + gap + subtitle. Centring each line separately
        // would leave the two lines drifting apart as the card and the fonts scale at different
        // rounding steps, which is exactly the "the text sits low" look this replaces.
        var lineGap = Scaled(BaseLineGap);
        TextHeight = TitleFontPx + lineGap + messageFontPx;
        TitleY = (Height - TextHeight) / 2;
        MessageY = TitleY + TitleFontPx + lineGap;
    }

    private int Scaled(int design) => (int)MathF.Round(design * layoutScale);

    /// <summary>
    /// The effective DPI of a monitor, or 96 when it cannot be determined -- which is the right
    /// default because 96 is the scale every measurement above is expressed in, so "unknown" means
    /// "as designed" rather than "wrong".
    /// </summary>
    private uint MonitorDpi(nint monitor)
    {
        if (monitor != 0 &&
            GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var x, out _) == 0 /* S_OK */ && x > 0)
            return x;

        // shcore is Win8.1+ and refuses to answer before the window exists; the window's own DPI is
        // the same number on a per-monitor-aware process.
        if (hwnd != 0 && GetDpiForWindow(hwnd) is var fromWindow and > 0)
            return fromWindow;

        return GetDpiForSystem() is var fromSystem and > 0 ? fromSystem : 96;
    }

    /// <summary>Top-right of the work area (the screen minus the taskbar), with a margin.</summary>
    private readonly record struct Placement(int X, int Y, uint Dpi);

    private nint WndProc(nint hWnd, int message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_SHOW:
                BeginShow();
                return 0;
            case WM_TIMER when wParam == TimerId:
                StepAnimation();
                return 0;
            case WM_STOP:
                DestroyWindow(hWnd);
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;

            // Anything that would activate this window is refused here rather than prevented by
            // style alone: WS_EX_NOACTIVATE covers the ShowWindow calls, and MA_NOACTIVATE covers the
            // one path styles do not -- a click. Even though the window is WS_EX_TRANSPARENT, a click
            // on the very edge or during the fade still asks for activation, and taking focus off a
            // running game (which in fullscreen means a mode switch or a stutter) is exactly what a
            // notification must never do. Returning 3 instead of calling DefWindowProc is what makes
            // the window unfocusable by mouse.
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;

            // The window's own DPI changed (it was dragged to another monitor, or the user changed
            // the scaling). Answering with a resize+reposition from the suggested rectangle is the
            // documented contract; the geometry is ours to choose, so keep the card's top-right
            // corner anchored and its designed size at the new scale.
            case WM_DPICHANGED:
                // wParam packs the new DPI's low word.
                MonitorDpiOnChange((uint)(wParam.ToInt64() & 0xFFFF));
                return 0;

            default:
                return DefWindowProc(hWnd, message, wParam, lParam);
        }
    }

    /// <summary>
    /// Applies a WM_DPICHANGED for the window itself: re-layout, rebuild the DIB, and keep the card
    /// in the top-right of whatever work area it now lives on. Suggested rectangles are ignored on
    /// purpose -- the shell proposes a rectangle for a resizable window, while this one keeps its
    /// designed size and only its position follows the monitor.
    /// </summary>
    private void MonitorDpiOnChange(uint dpi)
    {
        if (dpi == 0)
            return;

        Layout(dpi);
        graphicsDpi = dpi;
        ResizeGraphics();
        ApplyPayloadSize(pendingPayload, WorkAreaWidthOf(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)));

        // This monitor's work area, not the primary one's: the window already sits on the monitor
        // whose DPI changed, and SPI_GETWORKAREA would move the card back to the main screen.
        var work = new Rect();
        if (GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref work))
        {
            var spot = CornerInWorkArea(work.right, work.top, Width, Margin);
            posX = spot.x;
            posY = spot.y;
            SetWindowPos(hwnd, 0, posX, posY, Width, Height, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        if (phase != Phase.Idle)
            RenderCard(pendingPayload);
    }

    private void BeginShow()
    {
        // Recomputed per payload, not once per process: the game may have been moved to the other
        // monitor since the last card, and the card has to follow it. Also where a DPI change is
        // noticed -- the DIB and fonts cannot be resized after the fact, they have to be rebuilt.
        var payload = pendingPayload;
        var monitor = MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);
        var dpi = MonitorDpi(monitor);
        Layout(dpi);
        if (dpi != graphicsDpi)
        {
            graphicsDpi = dpi;
            ResizeGraphics();
        }

        // The card grows before the work area is consulted, so the position below uses the final
        // width: text wider than the design width widens the card (up to the screen), and a bar
        // payload adds its zone to the height.
        ApplyPayloadSize(payload, WorkAreaWidthOf(monitor));

        var work = WorkAreaOf(monitor);
        posX = work.right - Margin - Width;
        posY = work.top + Margin;

        // SWP_NOZORDER keeps TOPMOST without re-asserting it, SWP_NOACTIVATE keeps the game focused:
        // this is a reposition, not an activation.
        SetWindowPos(hwnd, 0, posX, posY, Width, Height, SWP_NOZORDER | SWP_NOACTIVATE);

        RenderCard(payload);

        // The in-place contract: a card already on screen swaps its content at the current alpha
        // and re-times itself per the payload -- a sticky one parks in hold indefinitely, a normal
        // one restarts its hold. An idle window fades in from nothing.
        if (alpha > 0)
        {
            phase = payload.Sticky ? Phase.StickyHold : Phase.Hold;
            phaseStartTicks = Environment.TickCount64;
            holdMs = payload.HoldMs;
        }
        else
        {
            phase = Phase.FadeIn;
            phaseStartTicks = Environment.TickCount64;
            holdMs = payload.HoldMs;
        }
        shownAtTicks = Environment.TickCount64;

        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTimer(hwnd, TimerId, TimerIntervalMs, 0);
        Paint();
    }

    /// <summary>The work area's width, the card's growth ceiling: never wider than its screen.</summary>
    private int WorkAreaWidthOf(nint monitor)
    {
        var work = WorkAreaOf(monitor);
        return Math.Max(Scaled(BaseWidth), work.right - work.left - 2 * Margin);
    }

    private Rect WorkAreaOf(nint monitor)
    {
        var work = new Rect();
        if (!GetMonitorInfo(monitor, ref work) &&
            !SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0))
        {
            work = new Rect
            {
                right = GetSystemMetrics(SM_CXSCREEN),
                bottom = GetSystemMetrics(SM_CYSCREEN),
            };
        }
        return work;
    }

    /// <summary>
    /// Sizes the card for this payload. Long text GROWS the card instead of being clipped -- the
    /// width goes to the longest measured line plus the symmetric text insets, capped by the
    /// monitor; a payload with a progress bar adds the bar zone to the height. Everything is
    /// scaled already, so the numbers here are physical pixels.
    /// </summary>
    private void ApplyPayloadSize(NotificationPayload payload, int maxWidth)
    {
        var hasBar = payload.Progress is not null || payload.Indeterminate;
        var targetHeight = Scaled(hasBar ? BaseHeight + BaseBarZone : BaseHeight);

        var (titleW, messageW) = MeasureText(payload.Title, payload.Subtitle);
        var wanted = TextX * 2 + Math.Max(titleW, messageW);
        var targetWidth = Math.Clamp(wanted, Scaled(BaseWidth), Math.Max(Scaled(BaseWidth), maxWidth));

        if (targetWidth != Width || targetHeight != Height)
        {
            Width = targetWidth;
            Height = targetHeight;
            ResizeGraphics();
        }
    }

    /// <summary>The rendered widths of the two lines, via GDI on the card's own fonts.</summary>
    private (int Title, int Message) MeasureText(string title, string message)
    {
        SelectObject(memDc, titleFont);
        GetTextExtentPoint32(memDc, title, title.Length, out var titleSize);
        SelectObject(memDc, messageFont);
        GetTextExtentPoint32(memDc, message, message.Length, out var messageSize);
        return (titleSize.cx, messageSize.cx);
    }

    private void StepAnimation()
    {
        var now = Environment.TickCount64;
        var elapsed = now - phaseStartTicks;

        // The safety nets of the lifecycle, on the wall clock since the card appeared: a non-sticky
        // card must be gone holdMs + FadeIn + FadeOut + 500 ms after it was shown, whatever the
        // phase machine believes; a sticky one must not hold the screen for more than 30 s without
        // a payload refresh (a hung network step would otherwise park Checking forever).
        if (!pendingPayload.Sticky && now - shownAtTicks > holdMs + FadeInMs + FadeOutMs + 500)
        {
            alpha = 0;
            KillTimer(hwnd, TimerId);
            ShowWindow(hwnd, SW_HIDE);
            phase = Phase.Idle;
            return;
        }
        if (phase == Phase.StickyHold && now - shownAtTicks > StickyRefreshTimeoutMs)
        {
            phase = Phase.FadeOut;
            phaseStartTicks = now;
        }

        switch (phase)
        {
            case Phase.FadeIn:
                if (elapsed >= FadeInMs)
                {
                    alpha = 255;
                    // THE stuck-card bug, fixed: the fade-in must land in the phase the PAYLOAD
                    // asks for. Unconditionally entering StickyHold here parked every non-sticky
                    // card on the screen until the next payload arrived to re-time it.
                    phase = pendingPayload.Sticky ? Phase.StickyHold : Phase.Hold;
                    phaseStartTicks = now;
                }
                else
                {
                    alpha = (int)(elapsed * 255 / FadeInMs);
                }
                break;

            case Phase.Hold:
                alpha = 255;
                if (elapsed >= holdMs)
                {
                    phase = Phase.FadeOut;
                    phaseStartTicks = now;
                }
                break;

            case Phase.StickyHold:
                // Full opacity, no close timer: the card lives until a non-sticky payload replaces
                // it. The one thing that still animates here is the indeterminate sweep, which
                // redraws the card each tick with the pill at its new position.
                alpha = 255;
                if (pendingPayload.Indeterminate)
                    RenderCard(pendingPayload);
                else
                    return; // nothing changed this tick; do not push the same bitmap again
                break;

            case Phase.FadeOut:
                var remaining = FadeOutMs - elapsed;
                if (remaining <= 0)
                {
                    alpha = 0;
                    KillTimer(hwnd, TimerId);
                    ShowWindow(hwnd, SW_HIDE);
                    phase = Phase.Idle;
                    return;
                }
                alpha = (int)(remaining * 255 / FadeOutMs);
                break;
        }

        Paint();
    }

    /// <summary>Pushes the current bitmap at the current alpha to the layered window.</summary>
    private unsafe void Paint()
    {
        if (memDc == 0 || bitsPtr == 0)
            return;

        var dst = new Point { x = posX, y = posY };
        var src = new Point { x = 0, y = 0 };
        var size = new Size { cx = Width, cy = Height };
        var blend = new BlendFunction
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = (byte)Math.Clamp(alpha, 0, 255),
            AlphaFormat = AC_SRC_ALPHA,
        };

        UpdateLayeredWindow(hwnd, 0, &dst, &size, memDc, &src, 0, &blend, ULW_ALPHA);
    }

    /// <summary>
    /// Draws the card into the DIB. The shape (background + border), the text and -- when the
    /// payload carries one -- the progress bar are drawn by GDI, which never touches the alpha
    /// byte; a final managed pass then sets alpha to 255 inside the rounded rectangle and 0 in the
    /// corners, so the result is correct regardless of what GDI left behind.
    ///
    /// With a bar present the text block centres in the space ABOVE the bar zone, so the card
    /// grows downward and the two lines keep the exact same offsets they have on a bar-less card.
    /// </summary>
    private unsafe void RenderCard(NotificationPayload payload)
    {
        if (memDc == 0 || bitsPtr == 0)
            return;

        new Span<byte>((void*)bitsPtr, Width * Height * 4).Clear();

        var title = Fit(payload.Title, TitleMaxChars);
        var message = Fit(payload.Subtitle, MessageMaxChars);
        var hasBar = payload.Progress is not null || payload.Indeterminate;

        // GDI's round-rect region is exclusive of its right and bottom edges: CreateRoundRectRgn(0, 0,
        // W, H) covers x in [0, W-1] but y only up to H-2, so the last row and column of the card
        // were never painted -- the bottom border came out transparent black against a dark game.
        // The +1 makes the region reach the final pixel; the interior is inset the same way, so the
        // hairline keeps its 1 px thickness on all four sides.
        var outer = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, CornerRadius * 2, CornerRadius * 2);
        var inner = CreateRoundRectRgn(Border, Border, Width - Border + 1, Height - Border + 1,
            (CornerRadius - Border) * 2, (CornerRadius - Border) * 2);

        FillRgn(memDc, outer, borderBrush); // hairline border ring
        FillRgn(memDc, inner, bgBrush);     // dark interior

        SetBkMode(memDc, TRANSPARENT_BK);

        // The text block's vertical centre: the space above the bar zone when one rides along, the
        // whole card otherwise -- which reproduces Layout()'s centring exactly (TitleY/MessageY are
        // that same computation for the bar-less height). A single-line payload (empty subtitle)
        // centres the title alone, with the same air above and below it.
        var lineGap = Scaled(BaseLineGap);
        var singleLine = message.Length == 0;
        var blockHeight = singleLine ? TitleFontPx : TitleFontPx + lineGap + messageFontPx;
        var zoneHeight = hasBar ? Height - Scaled(BaseBarZone) : Height;
        var titleY = (zoneHeight - blockHeight) / 2;
        var messageY = titleY + TitleFontPx + lineGap;

        SelectObject(memDc, titleFont);
        SetTextColor(memDc, TitleColor);
        TextOut(memDc, TextX, titleY, title, title.Length);

        if (!singleLine)
        {
            SelectObject(memDc, messageFont);
            SetTextColor(memDc, MessageColor);
            TextOut(memDc, TextX, messageY, message, message.Length);
        }

        if (hasBar)
            DrawProgress(payload, messageY + messageFontPx);

        DeleteObject(outer);
        DeleteObject(inner);

        // Per-pixel alpha: opaque inside the rounded rectangle, transparent in the corners.
        var pixels = (byte*)bitsPtr;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 4;
                pixels[i + 3] = InsideRoundedRect(x, y) ? (byte)255 : (byte)0;
            }
        }
    }

    /// <summary>
    /// The progress strip: a rounded track spanning the text's width, the fill (or the sweep pill)
    /// drawn over it. Same insets as the text above, so bar and text read as one composition.
    /// </summary>
    private void DrawProgress(NotificationPayload payload, int afterMessageY)
    {
        var barHeight = Scaled(NotificationCenter.BarHeightDesignPx);
        var zone = Scaled(BaseBarZone);
        var y = Height - zone + (zone - barHeight) / 2;
        var x0 = TextX;
        var x1 = Width - TextX;
        if (x1 - x0 < barHeight)
            return;

        FillPill(x0, y, x1, y + barHeight, trackBrush);

        if (payload.Indeterminate)
        {
            // A quarter-width pill gliding back and forth: honest motion for an unknown total.
            var travel = x1 - x0;
            var pillWidth = Math.Max(barHeight, travel / 4);
            var period = 1600;
            var t = (Environment.TickCount64 % period) / (double)(period / 2);
            if (t > 1)
                t = 2 - t; // ping-pong
            var position = (int)((travel - pillWidth) * t);
            FillPill(x0 + position, y, x0 + position + pillWidth, y + barHeight, accentBrush);
        }
        else if (payload.Progress is { } progress)
        {
            var fillWidth = (int)((x1 - x0) * Math.Clamp(progress, 0, 1));
            if (fillWidth > 0)
                FillPill(x0, y, x0 + Math.Max(fillWidth, barHeight), y + barHeight, accentBrush);
        }
    }

    /// <summary>One rounded pill; the +1s extend the region to the final pixel, like the card's own ring.</summary>
    private void FillPill(int x0, int y0, int x1, int y1, nint brush)
    {
        var radius = (y1 - y0) * 2;
        var region = CreateRoundRectRgn(x0, y0, x1 + 1, y1 + 1, radius, radius);
        FillRgn(memDc, region, brush);
        DeleteObject(region);
    }

    /// <summary>Truncates with an ellipsis so text never runs past the card.</summary>
    private static string Fit(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "\u2026");

    /// <summary>
    /// Renders one card into an off-screen DIB and returns its BGRA pixels, so a test can assert on
    /// what actually reaches the screen -- the hairline's colour, and where the ink starts and stops
    /// -- rather than only on the numbers <see cref="Layout"/> computed. Both are needed: every
    /// measurement can be right while the drawn card is wrong (a stale brush, a column of icon that
    /// no layout metric reserves room for any more).
    ///
    /// The DC, DIB and fonts are all made and freed on the calling thread, because a GDI DC may only
    /// be deleted by the thread that created it. The overlay's own pump thread is never started
    /// here, so this draws without a window existing at all.
    /// </summary>
    internal unsafe byte[] RenderForTest(string title, string message, uint dpi = 96)
    {
        Layout(dpi);
        CreateGraphicsResources();
        if (memDc == 0 || bitsPtr == 0)
        {
            FreeGraphicsResources();
            throw new InvalidOperationException(
                "No GDI device context in this session, so the card cannot be rendered off-screen.");
        }

        // A plain two-line card, exactly what a payload without a bar renders: the test asserts the
        // design geometry, so the card is NOT widened here (only BeginShow grows it).
        RenderCard(new NotificationPayload { Title = title, Subtitle = message });

        var pixels = new byte[Width * Height * 4];
        new Span<byte>((void*)bitsPtr, pixels.Length).CopyTo(pixels);
        FreeGraphicsResources();
        return pixels;
    }

    /// <summary>The pixel at (x, y) of a DIB rendered by <see cref="RenderForTest"/>.</summary>
    internal static (byte blue, byte green, byte red) PixelAt(byte[] pixels, int width, int x, int y)
    {
        var i = (y * width + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2]);
    }

    /// <summary>Whether (x, y) lies inside the card's rounded rectangle of radius <see cref="CornerRadius"/>.</summary>
    private bool InsideRoundedRect(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return false;

        var r = CornerRadius;
        var cx = x < r ? r : (x >= Width - r ? Width - 1 - r : -1);
        var cy = y < r ? r : (y >= Height - r ? Height - 1 - r : -1);
        if (cx >= 0 && cy >= 0)
        {
            var dx = x - cx;
            var dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }

        return true;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int left; public int top; public int right; public int bottom; }

    /// <summary>MONITORINFO: rcWork is the work area, i.e. the screen minus the taskbar.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    /// <summary>
    /// The work area of a monitor, i.e. the screen minus taskbars and docked appbars. This is what
    /// positions the card: SPI_GETWORKAREA only ever reports the primary display, which is the very
    /// limitation being removed here.
    /// </summary>
    private static bool GetMonitorInfo(nint monitor, ref Rect work)
    {
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(monitor, ref info))
            return false;

        work = info.rcWork;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
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

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProcDelegate(nint hWnd, int message, nint wParam, nint lParam);


    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ushort RegisterClassEx(ref WindowClassEx windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hWnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static partial nint MonitorFromWindow(nint hWnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll", EntryPoint = "GetDpiForMonitor")]
    private static partial int GetDpiForMonitor(nint monitor, uint dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetDpiForSystem")]
    private static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg message, nint hWnd, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(ref Msg message);

    [LibraryImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static partial void PostQuitMessage(int code);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hWnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetTimer")]
    private static partial nint SetTimer(nint hWnd, nint idEvent, uint elapse, nint timerProc);

    [LibraryImport("user32.dll", EntryPoint = "KillTimer")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool KillTimer(nint hWnd, nint idEvent);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int command);

    [LibraryImport("user32.dll", EntryPoint = "UpdateLayeredWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateLayeredWindow(
        nint hwnd, nint hdcDst, Point* pptDst, Size* psize,
        nint hdcSrc, Point* pptSrc, uint crKey, BlendFunction* pblend, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, ref Rect rect, uint winini);

    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextExtentPoint32(nint hdc, string text, int length, out Size size);


    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDIBSection", SetLastError = true)]
    private static unsafe partial nint CreateDIBSection(
        nint hdc, BitmapInfoHeader* pbmi, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll", EntryPoint = "SelectObject")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint h);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateSolidBrush")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateRoundRectRgn")]
    private static partial nint CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [LibraryImport("gdi32.dll", EntryPoint = "FillRgn")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FillRgn(nint hdc, nint hrgn, nint hbr);

    [LibraryImport("gdi32.dll", EntryPoint = "SetBkMode")]
    private static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "SetTextColor")]
    private static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32.dll", EntryPoint = "TextOutW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TextOut(nint hdc, int x, int y, string text, int count);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);
}

