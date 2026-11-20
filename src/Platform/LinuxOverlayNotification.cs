#if LINUX
using System.Runtime.InteropServices;
using System.Text;

namespace Clippy;

/// <summary>
/// The Medal / ShadowPlay-style game overlay card on Linux: a compact dark card at the top-right
/// of the screen that fades in, holds and fades out -- without ever stealing focus from the
/// running game, on KDE Plasma and on Niri alike.
///
/// It is a raw X11 window for the same reason the Windows card is raw Win32
/// (UI/OverlayNotification.cs, the DESIGN REFERENCE this file mirrors 1-to-1): dragging a UI
/// toolkit into a NativeAOT recorder is not an option, and an ARGB window is a few dozen lines of
/// interop. The card is the Windows card's geometry -- 320x65 design px, a plain rectangle with
/// 12 px rounded corners on all four sides, a 1 px #3D3D3D hairline around a #1E1E1E body, a 16 px
/// bold near-white title over a 14 px grey subtitle, no icon -- rendered in software into a
/// premultiplied BGRA buffer, the text drawn through Xft (the same library Plasma itself links).
/// The fade is the Windows trick: every pixel of the buffer is scaled by the current alpha each
/// frame, so the rounded corners stay transparent while the whole card fades.
///
/// ShowOrUpdate is the in-place contract shared with the Windows card: a payload arriving while a
/// card is already on screen swaps its content at the current alpha -- no fade restart, no
/// flicker -- and a sticky payload (a download in flight) parks the card at full opacity until a
/// non-sticky one replaces it. The progress bar rides in a zone below the text block: the card
/// grows by that zone's height, and the fill (or the indeterminate sweep) moves per frame.
///
/// The window is created with override_redirect = True: the window manager (KWin, or Niri's
/// XWayland handling) never decorates, tiles or focuses it. Its event mask is zero AND its X11
/// input shape is empty (XShapeCombineRectangles with zero rectangles), so the X server skips it
/// entirely on pointer picks -- clicks fall through to the game underneath, and the overlay can
/// never take focus, which is the one thing a notification must not do during a full-screen game.
/// On Wayland this all runs inside XWayland, which exists in every KDE and Niri session that can
/// run X11 apps; a session without X11 at all falls back to the desktop toast (see
/// LinuxNotificationPresenter).
/// </summary>
internal sealed partial class LinuxOverlayNotification : IDisposable
{
    // Design units, identical to the Windows card's (UI/OverlayNotification.cs) so the two cards
    // are the same card: 320x68, 12 px radius on ALL four corners, 1 px hairline, 16 px margin,
    // 16 px text insets on both sides, 14/12 px fonts with a 5 px line gap -- the sizes that leave
    // 18 px of air above and below the text block instead of ink touching the border. Every use
    // goes through Scaled.
    private const int BaseWidth = 320;
    private const int BaseHeight = 68;
    private const int BaseCornerRadius = 12;
    private const int BaseBorder = 1;
    private const int BaseMargin = 16;

    private const int BaseTextX = 16;
    private const int BaseTextRightInset = 16;
    private const int BaseLineGap = 5;

    private const int TitleFontPx = 14;
    private const int MessageFontPx = 12;

    // The progress strip's design metrics, shared with the Windows card through NotificationCenter.
    private const int BarZone = NotificationCenter.BarZoneDesignPx;
    private const int BarHeight = NotificationCenter.BarHeightDesignPx;

    // Animation timings, identical to the Windows overlay.
    private const int FadeInMs = 120;
    private const int HoldMs = 2600;
    private const int FadeOutMs = 220;
    private const int StepMs = 15;

    // The Windows card's palette, byte for byte (COLORREF 0x00BBGGRR there, channels here).
    private const byte BgR = 30, BgG = 30, BgB = 30;               // #1E1E1E
    private const byte BorderR = 61, BorderG = 61, BorderB = 61;   // #3D3D3D
    private const byte TitleR = 240, TitleG = 240, TitleB = 240;   // #F0F0F0
    private const byte MessageR = 170, MessageG = 170, MessageB = 170; // #AAAAAA

    private enum Phase { Idle, FadeIn, Hold, StickyHold, FadeOut }

    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private volatile bool disposed;

    private readonly AutoResetEvent kick = new(false);
    private readonly object queueGate = new();
    private readonly Queue<NotificationPayload> queue = new();

    // X11 objects, all created and destroyed on the overlay thread.
    private nint display;
    private nint window;
    private nint graphicsContext;
    private nint image;
    private nint frameBuffer;
    private nint scratchPixmap;      // offscreen ARGB pixmap where text is baked into the card
    private nint scratchImage;
    private nint scratchBuffer;
    private nint draw;               // XftDraw: Xft text rendered onto the scratch pixmap
    private nint titleFont;
    private nint messageFont;
    private nint visual;             // the 32-bit ARGB visual the window was created with
    private nint colormap;           // the colormap paired with that visual
    private XftColor titleColor;
    private XftColor messageColor;
    private int screen;

    // Pre-rendered card at full opacity (premultiplied BGRA); each frame scales it by the phase
    // alpha, so the hold frames do not re-render the shape at all. An indeterminate sweep is
    // composited onto the scaled copy per frame -- the base never changes per tick.
    private byte[]? cardBase;

    // The card's CURRENT pixel size: 320x65 design, grown per show when a status line is longer
    // than the card or a payload carries a progress bar (see EnsureSize). The window, both
    // XImages and the scratch pixmap are all sized to this and rebuilt on change.
    private int cardWidth = BaseWidth;
    private int cardHeight = BaseHeight;

    // The bar's pixel geometry, recorded by the last render so the per-frame sweep knows where to
    // draw without re-measuring.
    private int trackX0, trackX1, barY0, barY1;

    /// <summary>The per-display scale factor: 1 at 96 DPI, 2 at 192. Taken from Xft.dpi.</summary>
    private float scale = 1f;

    /// <summary>True once the overlay window exists and text can be drawn. Checked after Start.</summary>
    public bool IsReady { get; private set; }

    public void Start()
    {
        if (disposed)
            return;

        thread = new Thread(Pump) { IsBackground = true, Name = "clippy-overlay" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Shows a payload, or updates a card already on screen in place. Thread-safe, called from any
    /// thread; the overlay thread dequeues the newest payload and either fades a card in or swaps
    /// the content of the one already showing. True when the payload was queued (the overlay is
    /// ready); false means the caller should fall back to the desktop toast.
    /// </summary>
    public bool ShowOrUpdate(NotificationPayload payload)
    {
        if (disposed || !IsReady)
            return false;

        lock (queueGate)
        {
            // Keep the newest: a download ticking at 11 Hz must not pile up a render per tick, and
            // the intermediate percentages are obsolete the moment a newer one exists.
            if (queue.Count > 1)
                queue.Dequeue();
            queue.Enqueue(payload);
        }
        kick.Set();
        return true;
    }

    /// <summary>The plain two-line card, for callers that have no payload to give.</summary>
    public bool Show(string title, string message) =>
        ShowOrUpdate(new NotificationPayload { Title = title, Subtitle = message });

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        kick.Set();
        thread?.Join(TimeSpan.FromSeconds(2));
        kick.Dispose();
        ready.Dispose();
    }

    // --- The overlay thread ---

    private void Pump()
    {
        try
        {
            SetUp();
            if (!IsReady)
                return;

            // One cadence drives everything: payloads are consumed and rendered in place the
            // moment they arrive, the phase machine advances the fades, and the indeterminate
            // sweep redraws its pill -- all at the same 15 ms tick the fade animation uses.
            while (!disposed)
            {
                ProcessPending();
                Tick();
                kick.WaitOne(StepMs);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Overlay: the X11 overlay stopped ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            TearDown();
        }
    }

    private bool TryDequeueLatest(out NotificationPayload payload)
    {
        lock (queueGate)
        {
            if (queue.Count == 0)
            {
                payload = null!;
                return false;
            }
            // The last one wins: everything queued before it is a stale intermediate step of the
            // same story (Checking → Downloading 12% → 45% → ...), and only the newest matters.
            while (queue.Count > 1)
                queue.Dequeue();
            payload = queue.Dequeue();
            return true;
        }
    }

    /// <summary>
    /// Consumes the newest payload. A visible card is re-rendered IN PLACE at its current alpha --
    /// the same window, new content, no fade restart, no flicker -- and re-timed per the payload:
    /// sticky parks in hold for as long as the operation runs, non-sticky restarts the hold that
    /// leads to the fade-out. A hidden card starts a fresh fade-in.
    /// </summary>
    private void ProcessPending()
    {
        if (!TryDequeueLatest(out var payload))
            return;

        currentPayload = payload;
        shownAtTicks = Environment.TickCount64;
        RenderCardBase(payload);

        if (CurrentAlpha > 0)
        {
            phase = payload.Sticky ? Phase.StickyHold : Phase.Hold;
            phaseStartTicks = Environment.TickCount64;
            holdMs = payload.HoldMs;
        }
        else
        {
            // The window may have been UNMAPPED at the end of the last fade-out: nothing the
            // DrawFrame below writes is visible until it is mapped again.
            ShowWindowMapped();
            phase = Phase.FadeIn;
            phaseStartTicks = Environment.TickCount64;
            holdMs = payload.HoldMs;
        }
    }

    /// <summary>Mapping is state, not a per-frame concern: the fade-out unmaps once, the next
    /// fade-in maps once, and every frame in between is a plain XPutImage.</summary>
    private bool mapped;

    private void ShowWindowMapped()
    {
        if (mapped || window == 0)
            return;
        XMapWindow(display, window);
        XRaiseWindow(display, window);
        XFlush(display); // the map must be on the wire before the first frame is drawn onto it
        mapped = true;
    }

    private void HideWindowMapped()
    {
        if (!mapped || window == 0)
            return;
        XUnmapWindow(display, window);
        // Xlib only queues the unmap: without a flush right here the command sits in the client's
        // buffer while this thread moves on to sleep, and the card hangs on the screen past the
        // end of its own animation -- exactly the stuck-card report.
        XFlush(display);
        mapped = false;
    }

    /// <summary>The payload the card is currently showing; its stickiness is what the fade-in
    /// completion consults. A volatile field: ProcessPending and Tick share the overlay thread,
    /// but keeping the payload named beats re-deriving it from the phase.</summary>
    private volatile NotificationPayload currentPayload = new() { Title = "", Subtitle = "" };
    private long shownAtTicks;

    /// <summary>The phase machine, one step per tick; mirrors the Windows card's StepAnimation.</summary>
    private Phase phase = Phase.FadeIn;
    private long phaseStartTicks;
    private int holdMs = HoldMs;

    /// <summary>Alpha of the card on screen, read and written only on the overlay thread.</summary>
    private int CurrentAlpha;

    /// <summary>
    /// The safety nets of the lifecycle. A non-sticky card must be gone HoldMs + FadeIn + FadeOut
    /// + 500 ms after it was shown, whatever the phase machine believes; a sticky one must not
    /// hold the screen for more than 30 s without a payload refresh (a hung network step would
    /// otherwise park Checking on the desktop forever). Neither fires in the normal flow -- they
    /// exist because the normal flow is exactly what a bug bends.
    /// </summary>
    private const int StickyRefreshTimeoutMs = 30_000;

    private void Tick()
    {
        if (phase == Phase.Idle)
            return;

        var now = Environment.TickCount64;
        var elapsed = now - phaseStartTicks;

        // The watchdogs first: they inspect the WALL CLOCK since the card appeared, not the
        // phase-local timer, so a wedged transition cannot outlive them.
        if (!currentPayload.Sticky)
        {
            if (now - shownAtTicks > holdMs + FadeInMs + FadeOutMs + 500)
            {
                CurrentAlpha = 0;
                HideWindowMapped();
                phase = Phase.Idle;
                return;
            }
        }
        else if (phase == Phase.StickyHold && now - shownAtTicks > StickyRefreshTimeoutMs)
        {
            // A sticky operation that stopped updating: fade it out like a finished one, so a
            // hung download does not hold the screen hostage for hours.
            phase = Phase.FadeOut;
            phaseStartTicks = now;
        }

        switch (phase)
        {
            case Phase.FadeIn:
                if (elapsed >= FadeInMs)
                {
                    CurrentAlpha = 255;
                    // THE stuck-card bug, fixed: the fade-in must land in the phase the PAYLOAD
                    // asks for. Unconditionally entering StickyHold here parked every non-sticky
                    // card on the screen until the next payload arrived to re-time it.
                    phase = currentPayload.Sticky ? Phase.StickyHold : Phase.Hold;
                    phaseStartTicks = now;
                    if (phase == Phase.Hold)
                        shownAtTicks = now; // the hold's clock and the watchdog's share the anchor
                }
                else
                    CurrentAlpha = (int)(elapsed * 255 / FadeInMs);
                break;

            case Phase.Hold:
                CurrentAlpha = 255;
                if (elapsed >= holdMs)
                {
                    phase = Phase.FadeOut;
                    phaseStartTicks = now;
                }
                break;

            case Phase.StickyHold:
                // Full opacity, no close timer: the card lives until a non-sticky payload replaces
                // it. The indeterminate sweep still animates, inside DrawFrame.
                CurrentAlpha = 255;
                break;

            case Phase.FadeOut:
                var remaining = FadeOutMs - elapsed;
                if (remaining <= 0)
                {
                    // Gone means GONE: alpha zero still leaves a mapped window on the screen, so
                    // the card is unmapped (with the flush that puts the unmap on the wire now)
                    // and the machine idles until the next payload.
                    CurrentAlpha = 0;
                    HideWindowMapped();
                    phase = Phase.Idle;
                    return;
                }
                CurrentAlpha = (int)(remaining * 255 / FadeOutMs);
                break;
        }

        DrawFrame(CurrentAlpha);
    }

    /// <summary>
    /// Resizes every size-dependent resource to the card's current pixel size: the
    /// override-redirect window (its RIGHT edge stays anchored at the margin, so the card keeps
    /// hanging in the top-right corner as it grows), the frame image the animation pushes, and the
    /// scratch pixmap/image pair the text is baked on. Called per render, only when the size
    /// actually changes, so the common clip card never rebuilds anything.
    /// </summary>
    private void EnsureSize(int width, int height)
    {
        if (width == cardWidth && height == cardHeight)
            return;

        if (window != 0)
        {
            var (x, y) = ComputeOrigin(width);
            XResizeWindow(display, window, (uint)width, (uint)height);
            XMoveWindow(display, window, x, y);

            if (image != 0) { XDestroyImage(image); image = 0; frameBuffer = 0; }
            frameBuffer = Marshal.AllocHGlobal(width * height * 4);
            image = XCreateImage(display, visual, 32, ZPixmap, 0, frameBuffer, (uint)width, (uint)height, 32, 0);

            // The XftDraw targets the scratch pixmap, so it is rebuilt with it.
            if (draw != 0) { XftDrawDestroy(draw); draw = 0; }
            if (scratchImage != 0) { XDestroyImage(scratchImage); scratchImage = 0; scratchBuffer = 0; }
            if (scratchPixmap != 0) { XFreePixmap(display, scratchPixmap); scratchPixmap = 0; }
            scratchBuffer = Marshal.AllocHGlobal(width * height * 4);
            scratchImage = XCreateImage(display, visual, 32, ZPixmap, 0, scratchBuffer, (uint)width, (uint)height, 32, 0);
            scratchPixmap = XCreatePixmap(display, window, (uint)width, (uint)height, 32);
            draw = XftDrawCreate(display, scratchPixmap, visual, colormap);
        }

        cardWidth = width;
        cardHeight = height;
        XFlush(display);
    }

    /// <summary>
    /// Per-show reposition: the cursor may have moved to the other monitor since the last card,
    /// and a same-size card would otherwise stay on the old screen. Cheap (one pointer query),
    /// so it rides along every render.
    /// </summary>
    private void MoveToActiveMonitor()
    {
        if (window == 0)
            return;
        var (x, y) = ComputeOrigin(cardWidth);
        XMoveWindow(display, window, x, y);
        XRaiseWindow(display, window);
    }

    // --- X11 setup / teardown ---

    private void SetUp()
    {
        // The default X error handler TERMINATES the process: one BadMatch in a notification card
        // must never take the recorder down with it. Ours logs the code and moves on; the delegate
        // is kept in a field so the GC cannot collect it while Xlib holds the pointer.
        xErrorHandler = HandleXError;
        XSetErrorHandler(xErrorHandler);

        // Not a bare XOpenDisplay(null): at boot DISPLAY may be missing from this process's
        // environment while XWayland is already listening, and the probe finds those sockets by
        // name. A first-notification failure here used to disable the overlay for the whole
        // session (see LinuxNotificationPresenter for the retry side of that).
        display = LinuxX11.TryOpenDisplay();
        if (display == 0)
        {
            Console.WriteLine("Overlay: no X11 display (DISPLAY unset or XWayland absent); overlay disabled.");
            ready.Set();
            return;
        }

        // The Windows card scales every design unit by its monitor's DPI; the X11 answer is the
        // Xft.dpi resource the desktop sets for HiDPI sessions. 96 (no scaling) is the default --
        // the same "unknown means as-designed" rule the Windows card uses.
        try
        {
            var dpiText = XGetDefault(display, "Xft", "dpi");
            if (dpiText != 0 && float.TryParse(Marshal.PtrToStringAnsi(dpiText), out var dpi) && dpi > 0)
                scale = dpi / 96f;
        }
        catch (DllNotFoundException)
        {
            // XGetDefault lives in libX11 itself, so this is unreachable in practice; the fallback
            // is the unscaled card.
        }

        screen = XDefaultScreen(display);
        if (XMatchVisualInfo(display, screen, 32, TrueColorClass, out XVisualInfo visualInfo) == 0)
        {
            Console.WriteLine("Overlay: no 32-bit TrueColor visual; overlay disabled.");
            ready.Set();
            return;
        }

        // The evdev cursor tracker starts once, alongside the overlay: on Wayland XQueryPointer
        // goes stale over native Wayland surfaces, and the tracker is what keeps the card on the
        // monitor the cursor is REALLY on. It stands down quietly when /dev/input is unreadable.
        LinuxMouseTracker.Start();

        // Fonts and colours first: a card without text is worse than no card, so an Xft failure
        // here degrades the whole overlay and lets the caller fall back to the desktop toast.
        // PIXELS, not points: a bare "Sans-14" pattern sets the size in POINTS, and Xft scales it
        // by the display's DPI -- at 96 DPI the title rendered ~18 px and ate every padding the
        // layout reserved. pixelsize is the exact pixel height the Windows card's CreateFont
        // -height gives, already carrying the same scale factor.
        titleFont = XftFontOpenName(display, screen, $"{SansFamily}:pixelsize={ScaledFont(TitleFontPx)}:bold");
        messageFont = XftFontOpenName(display, screen, $"{SansFamily}:pixelsize={ScaledFont(MessageFontPx)}");
        if (titleFont == 0 || messageFont == 0)
        {
            Console.WriteLine("Overlay: Xft could not open a font; overlay disabled.");
            ready.Set();
            return;
        }

        var root = XDefaultRootWindow(display);
        visual = visualInfo.Visual;
        colormap = XCreateColormap(display, root, visual, ColormapAllocNone);

        var attributes = new XSetWindowAttributes
        {
            BackgroundPixel = 0,
            BorderPixel = 0,
            OverrideRedirect = True_ /* no WM management: no frame, no tiling, no focus */,
            Colormap = this.colormap,
        };
        const nuint valueMask = CWBackPixel | CWBorderPixel | CWColormap | CWOverrideRedirect;

        // Top-right of the monitor the cursor is on -- not of the union root, which on a
        // dual-head setup is always the rightmost screen.
        var (originX, originY) = ComputeOrigin(Scaled(BaseWidth));
        window = XCreateWindow(display, root, originX, originY,
            Scaled(BaseWidth), Scaled(BaseHeight), 0, 32, InputOutputClass, visual,
            valueMask, ref attributes);
        if (window == 0)
        {
            Console.WriteLine("Overlay: XCreateWindow failed; overlay disabled.");
            ready.Set();
            return;
        }

        // Window type / state hints, before mapping. _NET_WM_WINDOW_TYPE_NOTIFICATION keeps
        // compositors from animating the window like a normal client; _NET_WM_STATE_ABOVE keeps
        // it over the game.
        var typeAtom = XInternAtom(display, "_NET_WM_WINDOW_TYPE", false);
        var notificationAtom = XInternAtom(display, "_NET_WM_WINDOW_TYPE_NOTIFICATION", false);
        var stateAtom = XInternAtom(display, "_NET_WM_STATE", false);
        var aboveAtom = XInternAtom(display, "_NET_WM_STATE_ABOVE", false);
        XChangeProperty(display, window, typeAtom, AtomType, 32, PropModeReplace, [(long)notificationAtom], 1);
        XChangeProperty(display, window, stateAtom, AtomType, 32, PropModeReplace, [(long)aboveAtom], 1);
        XStoreName(display, window, "ClippyOverlay");

        // No input, twice over: an empty event mask means the window never ASKS for events, and
        // the empty input shape means the server skips it entirely on pointer picks -- a click on
        // the card reaches whatever is underneath, focus stays with the game.
        graphicsContext = XCreateGC(display, window, 0, 0);
        try
        {
            XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, Unsorted);
        }
        catch (DllNotFoundException)
        {
            // libXext missing: rare, and the zero event mask already covers focus stealing.
            Console.WriteLine("Overlay: libXext unavailable; input passing falls back to the empty event mask.");
        }

        // The frame buffer backs an XImage the server reads straight from our memory: XPutImage
        // copies it into the window, the image owns nothing we need to free by hand.
        frameBuffer = Marshal.AllocHGlobal(Scaled(BaseWidth) * Scaled(BaseHeight) * 4);
        image = XCreateImage(display, visual, 32, ZPixmap, 0, frameBuffer,
            (uint)Scaled(BaseWidth), (uint)Scaled(BaseHeight), 32, 0);
        if (image == 0)
        {
            Console.WriteLine("Overlay: XCreateImage failed; overlay disabled.");
            ready.Set();
            return;
        }
        // The card's tracked size is in PIXELS, matching the buffers just allocated -- EnsureSize
        // compares against these, so the design numbers would desynchronise it on a HiDPI screen.
        cardWidth = Scaled(BaseWidth);
        cardHeight = Scaled(BaseHeight);

        // The scratch pixmap is where text gets baked into the card ONCE per payload (Xft draws
        // via XRender, which only targets drawables, not our byte buffer). Drawing the text
        // directly onto the window every animation frame would need two X operations per frame --
        // the compositor catches the state between them and the card visibly re-paints, text
        // popping in and out. With the composite pre-baked, every frame is ONE XPutImage: atomic.
        scratchBuffer = Marshal.AllocHGlobal(Scaled(BaseWidth) * Scaled(BaseHeight) * 4);
        scratchImage = XCreateImage(display, visual, 32, ZPixmap, 0, scratchBuffer,
            (uint)Scaled(BaseWidth), (uint)Scaled(BaseHeight), 32, 0);
        scratchPixmap = XCreatePixmap(display, window, (uint)Scaled(BaseWidth), (uint)Scaled(BaseHeight), 32);

        draw = XftDrawCreate(display, scratchPixmap, visual, colormap);
        // Colours must be allocated against the window's own visual and colormap (the 32-bit ARGB
        // pair), not the screen defaults, or XRender blends the text with the wrong pixel format.
        titleColor = AllocColor(titleColorRef());
        messageColor = AllocColor(messageColorRef());

        // The window stays UNMAPPED until the first payload fades it in: mapping it here would
        // flash whatever the not-yet-drawn framebuffer holds, and an idle overlay must be exactly
        // as invisible as it claims to be.
        XFlush(display);

        IsReady = true;
        ready.Set();
    }

    /// <summary>"Sans" -- fontconfig resolves it to the session's default sans (Noto, DejaVu...),
    /// the closest thing X11 has to the Windows card's Segoe UI.</summary>
    private const string SansFamily = "Sans";

    private int ScaledFont(int designPx) => Math.Max(1, (int)MathF.Round(designPx * scale));

    private void TearDown()
    {
        IsReady = false;
        if (draw != 0) { XftDrawDestroy(draw); draw = 0; }
        if (titleFont != 0) { XftFontClose(display, titleFont); titleFont = 0; }
        if (messageFont != 0) { XftFontClose(display, messageFont); messageFont = 0; }
        if (image != 0) { XDestroyImage(image); image = 0; frameBuffer = 0; } // destroy_image frees the data
        if (scratchImage != 0) { XDestroyImage(scratchImage); scratchImage = 0; scratchBuffer = 0; }
        if (scratchPixmap != 0) { XFreePixmap(display, scratchPixmap); scratchPixmap = 0; }
        if (graphicsContext != 0) { XFreeGC(display, graphicsContext); graphicsContext = 0; }
        if (window != 0) { XDestroyWindow(display, window); window = 0; }
        if (colormap != 0) { XFreeColormap(display, colormap); colormap = 0; }
        if (display != 0) { XCloseDisplay(display); display = 0; }
        ready.Set();
    }

    private XRenderColor titleColorRef() => new() { red = Scale(TitleR), green = Scale(TitleG), blue = Scale(TitleB) };
    private XRenderColor messageColorRef() => new() { red = Scale(MessageR), green = Scale(MessageG), blue = Scale(MessageB) };

    private static ushort Scale(byte channel) => (ushort)(channel * 0x101); // 8-bit -> 16-bit

    private XftColor AllocColor(XRenderColor render)
    {
        render.alpha = 0xFFFF;
        var color = new XftColor();
        XftColorAllocValue(display, visual, colormap, ref render, out color);
        return color;
    }

    // --- Rendering: the card shape in software, the text through Xft ---

    /// <summary>
    /// Renders the card at full opacity into <see cref="cardBase"/>: premultiplied BGRA, the
    /// Windows card's rectangle (12 px rounded corners on all four sides) with its hairline
    /// border, all anti-aliased by signed distance coverage; the text baked in through Xft,
    /// left-aligned at the text inset exactly like the Windows card's TextOut; and, when the
    /// payload carries one, the progress track with its determinate fill baked in below the text.
    /// </summary>
    private void RenderCardBase(NotificationPayload payload)
    {
        // Layout first: it measures the lines and grows the card when a status message is wider
        // than the 320 px design width or a bar payload adds its zone -- every width below then
        // reads the new card size.
        textLayout = ComputeTextLayout(payload);
        MoveToActiveMonitor();
        var width = cardWidth;
        var height = cardHeight;
        var radius = Scaled(BaseCornerRadius);
        var border = Scaled(BaseBorder);
        var buffer = new byte[width * height * 4];

        var hasBar = payload.Progress is not null || payload.Indeterminate;
        sweepActive = payload.Indeterminate;
        var trackX0 = Scaled(BaseTextX);
        var trackX1 = width - Scaled(BaseTextX);
        var barHeightPx = Scaled(BarHeight);
        var zone = Scaled(BarZone);
        var barY0 = height - zone + (zone - barHeightPx) / 2;
        var barY1 = barY0 + barHeightPx;
        this.trackX0 = trackX0;
        this.trackX1 = trackX1;
        this.barY0 = barY0;
        this.barY1 = barY1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;

                // The card is one rounded box: the Windows card's shape, anti-aliased by signed
                // distance coverage.
                var coverage = Math.Clamp(0.5f - SdRoundBox(px, py, width / 2f, height / 2f,
                    width / 2f, height / 2f, radius), 0, 1);
                if (coverage <= 0)
                    continue;

                // The interior is the same box eroded by the border, so the 1 px hairline keeps
                // its thickness on all four sides.
                var interior = Math.Clamp(0.5f - SdRoundBox(px, py, width / 2f, height / 2f,
                    width / 2f - border, height / 2f - border, Math.Max(0, radius - border)), 0, 1);

                var r = BorderR * (coverage - interior) + BgR * interior;
                var g = BorderG * (coverage - interior) + BgG * interior;
                var b = BorderB * (coverage - interior) + BgB * interior;

                // The progress track rides in its zone below the text, same insets as the text:
                // a rounded pill filled with the groove colour.
                if (hasBar && py >= barY0 && py <= barY1 && px >= trackX0 && px <= trackX1)
                {
                    var trackCov = Math.Clamp(0.5f - SdPill(px, py, trackX0, barY0, trackX1, barY1), 0, 1);
                    if (trackCov > 0)
                    {
                        r = r * (1 - trackCov) + NotificationCenter.TrackR * trackCov;
                        g = g * (1 - trackCov) + NotificationCenter.TrackG * trackCov;
                        b = b * (1 - trackCov) + NotificationCenter.TrackB * trackCov;
                    }

                    // The determinate fill is part of the base render: it only moves when a new
                    // payload arrives (throttled upstream), not per animation frame.
                    if (payload.Progress is { } progress && progress > 0)
                    {
                        var fillWidth = (trackX1 - trackX0) * Math.Clamp((float)progress, 0f, 1f);
                        var fillX1 = Math.Max(trackX0 + barHeightPx, trackX0 + fillWidth);
                        var fillCov = Math.Clamp(0.5f - SdPill(px, py, trackX0, barY0, fillX1, barY1), 0, 1);
                        if (fillCov > 0)
                        {
                            r = r * (1 - fillCov) + NotificationCenter.AccentR * fillCov;
                            g = g * (1 - fillCov) + NotificationCenter.AccentG * fillCov;
                            b = b * (1 - fillCov) + NotificationCenter.AccentB * fillCov;
                        }
                    }
                }

                var i = (y * width + x) * 4;
                var alpha = coverage * 255;
                buffer[i] = (byte)(b * alpha / 255);
                buffer[i + 1] = (byte)(g * alpha / 255);
                buffer[i + 2] = (byte)(r * alpha / 255);
                buffer[i + 3] = (byte)alpha;
            }
        }

        // Bake the text into the card while everything still sits at full opacity.
        unsafe
        {
            buffer.CopyTo(new Span<byte>((void*)scratchBuffer, buffer.Length));
        }
        XPutImage(display, scratchPixmap, graphicsContext, scratchImage, 0, 0, 0, 0,
            (uint)width, (uint)height);
        if (textLayout is { } layout)
        {
            XftDrawStringUtf8(draw, ref titleColor, titleFont, layout.TitleX, layout.TitleBaseline, layout.Title, layout.Title.Length);
            XftDrawStringUtf8(draw, ref messageColor, messageFont, layout.MessageX, layout.MessageBaseline, layout.Message, layout.Message.Length);
        }
        XFlush(display);

        // Read the composite back. The returned image owns its own data buffer, which
        // XDestroyImage frees -- our scratchBuffer stays ours.
        cardBase = buffer;
        var captured = XGetImage(display, scratchPixmap, 0, 0, (uint)width, (uint)height, AllPlanes, ZPixmap);
        if (captured != 0)
        {
            unsafe
            {
                var head = (XImageHeader*)captured;
                if (head->Data != 0)
                    new Span<byte>((void*)head->Data, buffer.Length).CopyTo(cardBase);
            }
            XDestroyImage(captured);
        }
    }

    /// <summary>
    /// Signed distance to a rounded rectangle's boundary; negative inside. The Windows card's
    /// shape is exactly this: a box whose corners are cut at radius r.
    /// </summary>
    private static float SdRoundBox(float px, float py, float cx, float cy, float halfWidth, float halfHeight, float radius)
    {
        var qx = MathF.Abs(px - cx) - (halfWidth - radius);
        var qy = MathF.Abs(py - cy) - (halfHeight - radius);
        var outsideX = MathF.Max(qx, 0);
        var outsideY = MathF.Max(qy, 0);
        return MathF.Sqrt(outsideX * outsideX + outsideY * outsideY)
            + MathF.Min(MathF.Max(qx, qy), 0)
            - radius;
    }

    /// <summary>A pill (a fully-round-ended bar): the rounded box whose radius is half its height.</summary>
    private static float SdPill(float px, float py, float x0, float y0, float x1, float y1)
    {
        var halfHeight = (y1 - y0) / 2f;
        return SdRoundBox(px, py, (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2, halfHeight, halfHeight);
    }

    // --- Text: Xft layout and per-frame drawing ---

    /// <summary>Everything needed to draw the two lines, measured once per card.</summary>
    private (byte[] Title, byte[] Message, int TitleX, int TitleBaseline, int MessageX, int MessageBaseline)? textLayout;

    private (byte[] Title, byte[] Message, int TitleX, int TitleBaseline, int MessageX, int MessageBaseline)
        ComputeTextLayout(NotificationPayload payload)
    {
        var hasBar = payload.Progress is not null || payload.Indeterminate;
        var height = Scaled(hasBar ? BaseHeight + BarZone : BaseHeight);
        var textX = Scaled(BaseTextX);
        var inset = Scaled(BaseTextRightInset);

        var titleBytes = Utf8(payload.Title);
        var messageBytes = Utf8(payload.Subtitle);
        XftTextExtentsUtf8(display, titleFont, titleBytes, titleBytes.Length, out var titleExtents);
        XftTextExtentsUtf8(display, messageFont, messageBytes, messageBytes.Length, out var messageExtents);

        // GROW, don't truncate: an update summary is longer than the 320 px design card, and
        // clipping it would defeat the announcement. The card widens to the longest line plus the
        // two insets; the screen width (margin on both sides) is the only hard ceiling, and past
        // it the lines ellipsize as before.
        var textWidth = Math.Max(titleExtents.width, messageExtents.width);
        var screenRoom = XDisplayWidth(display, screen) - 2 * Scaled(BaseMargin) - textX - inset;
        var wanted = Math.Clamp(textX + textWidth + inset,
            Scaled(BaseWidth), Math.Max(Scaled(BaseWidth), textX + Math.Max(0, screenRoom) + inset));
        EnsureSize(wanted, height);

        var maxWidth = cardWidth - textX - inset;
        while (titleExtents.width > maxWidth && titleBytes.Length > 1)
        {
            titleBytes = TruncateWithEllipsis(titleBytes);
            XftTextExtentsUtf8(display, titleFont, titleBytes, titleBytes.Length, out titleExtents);
        }
        while (messageExtents.width > maxWidth && messageBytes.Length > 1)
        {
            messageBytes = TruncateWithEllipsis(messageBytes);
            XftTextExtentsUtf8(display, messageFont, messageBytes, messageBytes.Length, out messageExtents);
        }

        // One centred block, left-aligned at the text inset -- the Windows card's exact rhythm:
        // both lines start at TextX, the pair is centred vertically on the card (or, with a bar,
        // on the space above the bar zone, so the growth happens BELOW the text). A single-line
        // payload (empty subtitle) centres the title alone, with the same air above and below.
        var gap = Scaled(BaseLineGap);
        var singleLine = messageBytes.Length == 0;
        var blockHeight = singleLine ? TitleFontPx : TitleFontPx + gap + MessageFontPx;
        var zoneHeight = hasBar ? cardHeight - Scaled(BarZone) : cardHeight;
        var top = Math.Max(0, (zoneHeight - blockHeight) / 2);
        // XGlyphInfo.y is the ink's rise ABOVE the baseline, so the baseline that puts the ink top
        // at `top` is top + y.
        var titleBaseline = top + titleExtents.y;
        var messageBaseline = top + TitleFontPx + gap + messageExtents.y;

        return (titleBytes, messageBytes, textX, titleBaseline, textX, messageBaseline);
    }

    /// <summary>
    /// Scales the full-opacity card by <paramref name="alpha"/> and pushes it to the window. When
    /// the payload's bar is indeterminate, the sweep pill is composited onto the scaled copy here,
    /// per frame -- the base (shape, text, track) stays untouched, so the animation costs nothing
    /// but the pill's own few hundred pixels.
    /// </summary>
    private unsafe void DrawFrame(int alpha)
    {
        if (cardBase is null || image == 0 || frameBuffer == 0 || !IsReady)
            return;

        var bytes = cardWidth * cardHeight * 4;
        var target = new Span<byte>((void*)frameBuffer, bytes);
        var source = cardBase;
        // Premultiplied pixels scale channel-wise: multiplying all four bytes by the fade keeps
        // the colours exact at every alpha, corners and text anti-aliasing included.
        for (var i = 0; i < bytes; i++)
            target[i] = (byte)(source[i] * alpha / 255);

        // The indeterminate sweep, drawn over the copied track. Same 1.6 s ping-pong the Windows
        // card runs; premultiplication means the pill's own channels carry the card's alpha.
        if (sweepActive && alpha > 0)
        {
            var travel = trackX1 - trackX0;
            if (travel > 0)
            {
                var pillWidth = Math.Max(barY1 - barY0, travel / 4);
                var period = 1600;
                var t = (Environment.TickCount64 % period) / (double)(period / 2);
                if (t > 1)
                    t = 2 - t;
                var position = (int)((travel - pillWidth) * t);
                DrawSweepPill(frameBuffer, trackX0 + position, pillWidth, alpha);
            }
        }

        // The single X operation per frame: no intermediate "card without text" state can ever
        // reach the compositor, which is what made the card flicker.
        XPutImage(display, window, graphicsContext, image, 0, 0, 0, 0,
            (uint)cardWidth, (uint)cardHeight);
        // Re-assert the stacking on every frame: a game or an IDE raising itself mid-fade must
        // never bury the card -- XRaiseWindow is fire-and-forget on the X stream, no round trip.
        XRaiseWindow(display, window);
        XFlush(display);
    }

    /// <summary>Whether the CURRENT payload's bar is the sweeping kind; set by RenderCardBase.</summary>
    private bool sweepActive;

    /// <summary>
    /// Draws the moving pill over the track already in the buffer, straight in premultiplied
    /// space: each pixel of the pill's coverage blends the accent over what the track left there.
    /// </summary>
    private unsafe void DrawSweepPill(nint buffer, int x0, int pillWidth, int alpha)
    {
        var y0 = barY0;
        var y1 = barY1;
        var x1 = Math.Min(x0 + pillWidth, trackX1);
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var cov = Math.Clamp(0.5f - SdPill(x + 0.5f, y + 0.5f, x0, y0, x1, y1), 0, 1);
                if (cov <= 0)
                    continue;

                var i = (y * cardWidth + x) * 4;
                var ptr = (byte*)buffer + i;
                var accentScale = cov * alpha / 255f;
                ptr[0] = (byte)(ptr[0] * (1 - cov) + NotificationCenter.AccentB * accentScale);
                ptr[1] = (byte)(ptr[1] * (1 - cov) + NotificationCenter.AccentG * accentScale);
                ptr[2] = (byte)(ptr[2] * (1 - cov) + NotificationCenter.AccentR * accentScale);
            }
        }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Cuts a UTF-8 buffer down and hangs an ellipsis on the last glyph that survives.</summary>
    private static byte[] TruncateWithEllipsis(byte[] bytes)
    {
        // One code point at a time, from the end; UTF-8 continuation bytes are 10xxxxxx.
        var cut = bytes.Length - 1;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;
        var kept = bytes[..Math.Max(1, cut)];
        return [.. kept, .. Utf8("\u2026")];
    }

    private int Scaled(int design) => Math.Max(1, (int)MathF.Round(design * scale));

    /// <summary>Swallows X protocol errors (BadMatch on a raced XPutImage, etc.) with a log line.</summary>
    private int HandleXError(nint displayPointer, nint errorEvent)
    {
        // XErrorEvent is a struct we do not otherwise use; offset-chasing its error_code byte is
        // not worth the fragility, so this logs the event's arrival only.
        Console.WriteLine("Overlay: an X11 protocol error was ignored.");
        return 0;
    }

    // --- Multi-monitor: where the card goes when more than one screen is connected ---

    private sealed record MonitorRect(int X, int Y, int Width, int Height);

    /// <summary>
    /// The connected monitors, via Xinerama (libXinerama ships with every X11 stack, XWayland
    /// included). Re-queried per show, so a hotplugged monitor is picked up without a restart;
    /// a missing libXinerama degrades to "the whole root is one screen".
    /// </summary>
    private List<MonitorRect> QueryMonitors()
    {
        var list = new List<MonitorRect>();
        try
        {
            var infos = XineramaQueryScreens(display, out var count);
            if (infos != 0 && count > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var monitor = Marshal.PtrToStructure<XineramaScreenInfo>(infos + i * Marshal.SizeOf<XineramaScreenInfo>());
                    if (monitor.width > 0 && monitor.height > 0)
                        list.Add(new MonitorRect(monitor.x_org, monitor.y_org, monitor.width, monitor.height));
                }
                XFree(infos);
            }
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("Overlay: libXinerama unavailable; the whole desktop counts as one monitor.");
        }

        if (list.Count == 0)
            list.Add(new MonitorRect(0, 0, XDisplayWidth(display, screen), XDisplayHeight(display, screen)));
        return list;
    }

    /// <summary>
    /// Where the card's top-right corner hangs: in the top-right of the monitor the CURSOR is
    /// currently on -- the screen the player is looking at -- not of the union root, which on a
    /// dual-head setup would always pick the rightmost screen. The position comes from the evdev
    /// tracker first (on Wayland XQueryPointer only updates over X11/XWayland windows and goes
    /// stale over native Wayland surfaces); XQueryPointer is the fallback for a session where the
    /// tracker could not start, and no answer at all falls back to the first monitor.
    /// </summary>
    private (int X, int Y) ComputeOrigin(int width)
    {
        var monitors = QueryMonitors();
        var chosen = monitors[0];
        var cursorX = -1;
        var cursorY = -1;
        if (LinuxMouseTracker.TryGetCursor(out cursorX, out cursorY))
        {
            chosen = monitors.FirstOrDefault(m =>
                cursorX >= m.X && cursorX < m.X + m.Width &&
                cursorY >= m.Y && cursorY < m.Y + m.Height) ?? chosen;
        }
        else if (XQueryPointer(display, XDefaultRootWindow(display),
                     out _, out _, out cursorX, out cursorY, out _, out _, out _) != 0)
        {
            chosen = monitors.FirstOrDefault(m =>
                cursorX >= m.X && cursorX < m.X + m.Width &&
                cursorY >= m.Y && cursorY < m.Y + m.Height) ?? chosen;
        }

        return (chosen.X + chosen.Width - width - Scaled(BaseMargin), chosen.Y + Scaled(BaseMargin));
    }

    // Kept in a field: the GC must never collect the delegate while Xlib holds its pointer.
    private XErrorHandlerDelegate? xErrorHandler;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandlerDelegate(nint display, nint errorEvent);

    [LibraryImport("libX11.so.6")]
    private static partial nint XSetErrorHandler(XErrorHandlerDelegate handler);

    // --- Xlib, via libX11.so.6 ---

    private const int TrueColorClass = 4;
    private const int InputOutputClass = 1;
    private const int ColormapAllocNone = 0;
    private const int True_ = 1;
    private const int ZPixmap = 2;
    private const ulong AllPlanes = ulong.MaxValue; // plane_mask ~0: every plane of the drawable
    private const int PropModeReplace = 0;

    private const nuint CWBackPixel = 1 << 1;
    private const nuint CWBorderPixel = 1 << 3;
    private const nuint CWColormap = 1 << 13;
    private const nuint CWOverrideRedirect = 1 << 9;

    // XShape (libXext.so.6): kinds and ops for the empty input region.
    private const int ShapeSet = 0;
    private const int ShapeInput = 2;
    private const int Unsorted = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XVisualInfo
    {
        public nint Visual;
        public ulong VisualId;
        public int Screen;
        public uint Depth;
        public int Class;
        public ulong RedMask;
        public ulong GreenMask;
        public ulong BlueMask;
        public int ColormapSize;
        public int BitsPerRgb;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XSetWindowAttributes
    {
        public nint BackgroundPixmap;
        public ulong BackgroundPixel;
        public nint BorderPixmap;
        public ulong BorderPixel;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public ulong BackingPlanes;
        public ulong BackingPixel;
        public int SaveUnder;
        public long EventMask;
        public long DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Colormap;
        public nint Cursor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRenderColor
    {
        public ushort red;
        public ushort green;
        public ushort blue;
        public ushort alpha;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XftColor
    {
        public ulong pixel;
        public ushort red;
        public ushort green;
        public ushort blue;
        public ushort alpha;
    }

    /// <summary>The head of XImage, far enough to reach the data pointer (offset 16 on LP64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct XImageHeader
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XGlyphInfo
    {
        public ushort width;
        public ushort height;
        public short x;
        public short y;
        public short xOff;
        public short yOff;
    }

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XDefaultScreen(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial nint XDefaultVisual(nint display, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial nint XDefaultColormap(nint display, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial int XDisplayWidth(nint display, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial int XDisplayHeight(nint display, int screen);

    [LibraryImport("libX11.so.6")]
    private static partial int XMatchVisualInfo(nint display, int screen, int depth, int klass, out XVisualInfo visualInfo);

    [LibraryImport("libX11.so.6")]
    private static partial nint XCreateColormap(nint display, nint window, nint visual, int alloc);

    [LibraryImport("libX11.so.6")]
    private static partial nint XCreateWindow(nint display, nint parent, int x, int y, int width, int height,
        int borderWidth, int depth, int klass, nint visual, nuint valueMask, ref XSetWindowAttributes attributes);

    [LibraryImport("libX11.so.6")]
    private static partial int XMapWindow(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XUnmapWindow(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XResizeWindow(nint display, nint window, uint width, uint height);

    [LibraryImport("libX11.so.6")]
    private static partial int XMoveWindow(nint display, nint window, int x, int y);

    [LibraryImport("libX11.so.6")]
    private static partial int XDestroyWindow(nint display, nint window);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    [LibraryImport("libX11.so.6")]
    private static partial int XChangeProperty(nint display, nint window, nint property, nint type,
        int format, int mode, long[] data, int nelements);

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int XStoreName(nint display, nint window, string name);

    [LibraryImport("libX11.so.6")]
    private static partial nint XCreatePixmap(nint display, nint drawable, uint width, uint height, uint depth);

    [LibraryImport("libX11.so.6")]
    private static partial int XFreePixmap(nint display, nint pixmap);

    [LibraryImport("libX11.so.6")]
    private static partial nint XGetImage(nint display, nint drawable, int x, int y,
        uint width, uint height, ulong planeMask, int format);

    [LibraryImport("libX11.so.6")]
    private static partial nint XCreateGC(nint display, nint drawable, nuint valueMask, nint values);

    [LibraryImport("libX11.so.6")]
    private static partial int XFreeGC(nint display, nint graphicsContext);

    [LibraryImport("libX11.so.6")]
    private static partial nint XCreateImage(nint display, nint visual, uint depth, int format, int offset,
        nint data, uint width, uint height, int bitmapPad, int bytesPerLine);

    [LibraryImport("libX11.so.6")]
    private static partial int XPutImage(nint display, nint drawable, nint graphicsContext, nint image,
        int srcX, int srcY, int destX, int destY, uint width, uint height);

    [LibraryImport("libX11.so.6")]
    private static partial int XDestroyImage(nint image);

    [LibraryImport("libX11.so.6")]
    private static partial int XFreeColormap(nint display, nint colormap);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XRaiseWindow(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryPointer(nint display, nint window,
        out nint rootReturn, out nint childReturn,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport("libX11.so.6")]
    private static partial int XFree(nint data);

    // Xinerama: the monitor geometry, per screen, in root coordinates.
    [StructLayout(LayoutKind.Sequential)]
    private struct XineramaScreenInfo
    {
        public int screen_number;
        public short x_org;
        public short y_org;
        public short width;
        public short height;
    }

    [LibraryImport("libXinerama.so.1")]
    private static partial nint XineramaQueryScreens(nint display, out int number);

    [LibraryImport("libXext.so.6")]
    private static partial int XShapeCombineRectangles(nint display, nint window, int kind,
        int xOffset, int yOffset, nint rectangles, int count, int operation, int ordering);

    // The Xlib header spells this "Atom"; in wire terms it is just an XID, and the property type
    // for the ATOM format is the "ATOM" atom itself.
    private nint AtomType => XInternAtom(display, "ATOM", false);

    // --- Xft, via libXft.so.2 (the text stack Plasma itself links) ---

    [LibraryImport("libXft.so.2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XftFontOpenName(nint display, int screen, string pattern);

    [LibraryImport("libXft.so.2")]
    private static partial void XftFontClose(nint display, nint font);

    [LibraryImport("libXft.so.2")]
    private static partial nint XftDrawCreate(nint display, nint drawable, nint visual, nint colormap);

    [LibraryImport("libXft.so.2")]
    private static partial void XftDrawDestroy(nint draw);

    [LibraryImport("libXft.so.2")]
    private static partial int XftColorAllocValue(nint display, nint visual, nint colormap,
        ref XRenderColor color, out XftColor result);

    [LibraryImport("libXft.so.2")]
    private static unsafe partial void XftDrawStringUtf8(nint draw, ref XftColor color, nint font,
        int x, int y, byte[] text, int length);

    [LibraryImport("libXft.so.2")]
    private static partial void XftTextExtentsUtf8(nint display, nint font, byte[] text, int length, out XGlyphInfo extents);

    // XGetDefault reads the per-display resource database -- where the desktop parks Xft.dpi for
    // HiDPI sessions. Same source of truth the Windows card gets from GetDpiForMonitor.
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XGetDefault(nint display, string program, string option);
}
#endif
