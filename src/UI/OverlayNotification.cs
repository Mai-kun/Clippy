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
    private const int BaseHeight = 65;
    private const int BaseCornerRadius = 12;
    private const int BaseBorder = 2;
    private const int BaseMargin = 16;

    // Icon at the left, vertically centred; text to its right.
    private const int BaseIconSize = 32;
    private const int BaseIconX = 14;
    private const int BaseTextX = 58;
    private const int BaseTitleY = 13;
    private const int BaseMessageY = 35;

    // Font heights are in pixels here (the memory DC is a screen DC, so 1 unit = 1 pixel) and are
    // the one place the scale has to reach: a 16 px title on a 200% monitor must be 32 px or it is
    // unreadable next to the game's own UI.
    internal const int BaseTitleFontPx = 16;
    private const int BaseMessageFontPx = 14;

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
    private const uint DI_NORMAL = 0x0003;
    private const int TRANSPARENT_BK = 1;
    private const int DEFAULT_CHARSET = 1;
    private const int FW_BOLD = 700;
    private const uint SPI_GETWORKAREA = 0x0030;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // COLORREF values are 0x00BBGGRR.
    private const uint BgColor = 0x001E1E1E;      // #1E1E1E dark card
    private const uint AccentColor = 0x002A2AE8;  // red accent border
    private const uint TitleColor = 0x00F0F0F0;   // near-white bold title
    private const uint MessageColor = 0x00AAAAAA; // grey message

    private enum Phase { Idle, FadeIn, Hold, FadeOut }

    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private volatile bool disposed;

    // Written on the capture thread before PostMessage, read on the overlay thread in WM_SHOW.
    private volatile string pendingTitle = "";
    private volatile string pendingMessage = "";

    // Overlay-thread state.
    private nint hwnd;
    private nint memDc;
    private nint dib;
    private nint oldBitmap;
    private nint bitsPtr;
    private nint accentBrush;
    private nint bgBrush;
    private nint titleFont;
    private nint messageFont;
    private nint iconHandle;
    private int posX;
    private int posY;
    private Phase phase = Phase.Idle;
    private int alpha;
    private long phaseStartTicks;

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
    internal int TitleFontPx;
    private int messageFontPx = BaseMessageFontPx;

    private int CornerRadius = BaseCornerRadius;
    private int Border = BaseBorder;
    private int IconSize = BaseIconSize;
    private int IconX = BaseIconX;
    private int IconY;
    private int TextX = BaseTextX;
    private int TitleY = BaseTitleY;

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
    /// Queues a card to show. Thread-safe: called from the export thread, delivered on the overlay
    /// thread. A card already on screen is smoothly re-timed rather than flickering back in.
    /// </summary>
    public void Show(string title, string message)
    {
        if (disposed || hwnd == 0)
            return;

        pendingTitle = title;
        pendingMessage = message;
        PostMessage(hwnd, WM_SHOW, 0, 0);
    }

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

    /// <summary>Creates the memory DC and the app icon, both DPI-independent, then the per-size DIB.</summary>
    private unsafe void CreateGraphicsResources()
    {
        var screenDc = GetDC(0);
        memDc = CreateCompatibleDC(screenDc);
        if (screenDc != 0)
            ReleaseDC(0, screenDc);

        iconHandle = LoadApplicationIcon();
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
        if (accentBrush != 0) { DeleteObject(accentBrush); accentBrush = 0; }
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

        accentBrush = CreateSolidBrush(AccentColor);
        bgBrush = CreateSolidBrush(BgColor);

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
        if (accentBrush != 0) { DeleteObject(accentBrush); accentBrush = 0; }
        if (bgBrush != 0) { DeleteObject(bgBrush); bgBrush = 0; }
        if (titleFont != 0) { DeleteObject(titleFont); titleFont = 0; }
        if (messageFont != 0) { DeleteObject(messageFont); messageFont = 0; }
        // The icon handle from ExtractIconEx is deliberately never destroyed; see TrayIcon.
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
        IconSize = Scaled(BaseIconSize);
        IconX = Scaled(BaseIconX);
        IconY = (Height - IconSize) / 2;
        TextX = Scaled(BaseTextX);
        TitleY = Scaled(BaseTitleY);
        MessageY = Scaled(BaseMessageY);
        TitleFontPx = Scaled(BaseTitleFontPx);
        messageFontPx = Scaled(BaseMessageFontPx);
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

    /// <summary>The icon embedded in this exe, or 0 when there is none.</summary>
    private static nint LoadApplicationIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) && ExtractIconEx(path, 0, out _, out var icon, 1) && icon != 0)
            return icon;

        return LoadIcon(0, new nint(32512)); // IDI_APPLICATION
    }

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
            RenderCard();
    }

    private void BeginShow()
    {
        // Recomputed per clip, not once per process: the game may have been moved to the other
        // monitor since the last one, and the card has to follow it. Also where a DPI change is
        // noticed -- the DIB and fonts cannot be resized after the fact, they have to be rebuilt.
        var placement = ComputePlacement();
        posX = placement.X;
        posY = placement.Y;

        // Repositioned unconditionally, unlike the graphics: the window can move without its DPI
        // changing (a game dragged between two 100% monitors), and only the position encodes which
        // monitor it is on.
        if (placement.Dpi != graphicsDpi)
        {
            graphicsDpi = placement.Dpi;
            ResizeGraphics();
        }

        // SWP_NOZORDER keeps TOPMOST without re-asserting it, SWP_NOACTIVATE keeps the game focused:
        // this is a reposition, not an activation.
        SetWindowPos(hwnd, 0, posX, posY, Width, Height, SWP_NOZORDER | SWP_NOACTIVATE);

        RenderCard();

        // A card already on screen keeps its place and just gets more time ("smooth restart"); an
        // idle window fades in from nothing.
        phase = alpha > 0 ? Phase.Hold : Phase.FadeIn;
        phaseStartTicks = Environment.TickCount64;

        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTimer(hwnd, TimerId, TimerIntervalMs, 0);
        Paint();
    }

    private void StepAnimation()
    {
        var elapsed = Environment.TickCount64 - phaseStartTicks;

        switch (phase)
        {
            case Phase.FadeIn:
                if (elapsed >= FadeInMs)
                {
                    alpha = 255;
                    phase = Phase.Hold;
                    phaseStartTicks = Environment.TickCount64;
                }
                else
                {
                    alpha = (int)(elapsed * 255 / FadeInMs);
                }
                break;

            case Phase.Hold:
                alpha = 255;
                if (elapsed >= HoldMs)
                {
                    phase = Phase.FadeOut;
                    phaseStartTicks = Environment.TickCount64;
                }
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
    /// Draws the card into the DIB. The shape (background + accent border) and the text are drawn by
    /// GDI, which never touches the alpha byte; a final managed pass then sets alpha to 255 inside
    /// the rounded rectangle and 0 in the corners, so the result is correct regardless of what GDI
    /// left behind.
    /// </summary>
    private unsafe void RenderCard()
    {
        if (memDc == 0 || bitsPtr == 0)
            return;

        new Span<byte>((void*)bitsPtr, Width * Height * 4).Clear();

        var title = Fit(pendingTitle, TitleMaxChars);
        var message = Fit(pendingMessage, MessageMaxChars);

        var outer = CreateRoundRectRgn(0, 0, Width, Height, CornerRadius * 2, CornerRadius * 2);
        var inner = CreateRoundRectRgn(Border, Border, Width - Border, Height - Border,
            (CornerRadius - Border) * 2, (CornerRadius - Border) * 2);

        FillRgn(memDc, outer, accentBrush); // accent border ring
        FillRgn(memDc, inner, bgBrush);     // dark interior

        if (iconHandle != 0)
            DrawIconEx(memDc, IconX, IconY, IconSize, IconSize, iconHandle, 0, 0, DI_NORMAL);

        SetBkMode(memDc, TRANSPARENT_BK);

        SelectObject(memDc, titleFont);
        SetTextColor(memDc, TitleColor);
        TextOut(memDc, TextX, TitleY, title, title.Length);

        SelectObject(memDc, messageFont);
        SetTextColor(memDc, MessageColor);
        TextOut(memDc, TextX, MessageY, message, message.Length);

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

    /// <summary>Truncates with an ellipsis so text never runs past the card.</summary>
    private static string Fit(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "\u2026");

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


    [LibraryImport("user32.dll", EntryPoint = "GetDC")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport("user32.dll", EntryPoint = "DrawIconEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DrawIconEx(
        nint hdc, int xLeft, int yTop, int cxWidth, int cyHeight,
        nint hIcon, uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

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

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExtractIconEx(string exePath, int iconIndex, out nint large, out nint small, uint count);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? name);
}

