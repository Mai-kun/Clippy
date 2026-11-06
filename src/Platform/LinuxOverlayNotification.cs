#if LINUX
using System.Runtime.InteropServices;
using System.Text;

namespace Clippy;

/// <summary>
/// The Medal / ShadowPlay-style game overlay card on Linux: a compact dark card at the top-right
/// of the screen that fades in, holds ~2.6 s and fades out -- without ever stealing focus from the
/// running game, on KDE Plasma and on Niri alike.
///
/// It is a raw X11 window for the same reason the Windows card is raw Win32
/// (UI/OverlayNotification.cs, the design reference): dragging a UI toolkit into a NativeAOT
/// recorder is not an option, and a 240x58 ARGB window is a few dozen lines of interop. The card
/// shape -- a semicircular left cap fully filled by the green check badge (the card's contour
/// follows the badge exactly), a rectangular body with a hairline border and 12 px rounded right
/// corners -- is rendered in software into
/// a premultiplied BGRA buffer; the text is drawn on top through Xft (the same library Plasma
/// itself links), which gives anti-aliased text over the ARGB window for free. The fade is the
/// Windows trick transplanted: every pixel of the buffer is scaled by the current alpha each
/// frame, so the rounded corners stay transparent while the whole card fades.
///
/// The window is created with override_redirect = True: the window manager (KWin, or Niri's
/// XWayland handling) never decorates, tiles or focuses it. Its event mask is zero AND its X11
/// input shape is empty (XShapeCombineRectangles with zero rectangles), so the X server skips it
/// entirely on pointer picks -- clicks fall through to the game underneath, and the overlay can
/// never take focus, which is the one thing a notification must not do during a full-screen game.
/// On Wayland this all runs inside XWayland, which exists in every KDE and Niri session that can
/// run X11 apps; a session without X11 at all falls back to the desktop toast (see
/// LinuxNotifications).
/// </summary>
internal sealed partial class LinuxOverlayNotification : IDisposable
{
    // Design units, mirroring the user-facing spec of the Windows card: 240x58, 20 px from the
    // top-right corner, 12 px corner radius on the right, a 1 px #3D3D3D hairline around a
    // #1E1E1E card, and two text lines on the right of the accent badge. The LEFT end is a full
    // semicircular cap (radius = half the height) and the green check circle fills it exactly,
    // so the card's contour follows the badge with no dark corners poking out around it. Every
    // use goes through Scaled, so a per-display scale factor is a one-field change.
    private const int BaseWidth = 240;
    private const int BaseHeight = 58;
    private const int BaseCornerRadius = 12;
    private const int BaseBorder = 1;
    private const int BaseMargin = 20;

    // The text starts after the cap (its right edge sits at half the height) plus a small gap.
    private const int BaseTextX = 66;
    private const int BaseTextRightInset = 12;
    private const int BaseLineGap = 3;

    private const int TitleFontPx = 16;
    private const int MessageFontPx = 13;

    // Animation timings, identical to the Windows overlay.
    private const int FadeInMs = 120;
    private const int HoldMs = 2600;
    private const int FadeOutMs = 220;
    private const int StepMs = 15;

    // #RRGGBB straight from the Windows card's palette; the title is the near-white the spec
    // calls for, the message the same grey Tailwind names "gray-400".
    private const byte BgR = 30, BgG = 30, BgB = 30;           // #1E1E1E
    private const byte BorderR = 61, BorderG = 61, BorderB = 61; // #3D3D3D
    private const byte TitleR = 244, TitleG = 244, TitleB = 246; // #F4F4F6
    private const byte MessageR = 156, MessageG = 163, MessageB = 175; // #9CA3AF
    private const byte AccentR = 34, AccentG = 197, AccentB = 94;      // #22C55E

    private enum Phase { FadeIn, Hold, FadeOut }

    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private volatile bool disposed;

    private readonly AutoResetEvent kick = new(false);
    private readonly object queueGate = new();
    private readonly Queue<(string Title, string Message)> queue = new();

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
    // alpha, so the hold frames do not re-render the shape at all.
    private byte[]? cardBase;

    // The card's CURRENT pixel width: 240 design px, grown per show when a status line is longer
    // than the card (see ComputeTextLayout). The window, both XImages and the scratch pixmap are
    // all sized to this and rebuilt by EnsureWidth whenever it changes.
    private int cardWidth = BaseWidth;

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
    /// Queues a card. Thread-safe, called from the export thread; a card already on screen is
    /// re-timed by the pump loop rather than flickered. True when the card was queued for display
    /// (the overlay is ready); false means the caller should fall back to the desktop toast.
    /// </summary>
    public bool Show(string title, string message)
    {
        if (disposed || !IsReady)
            return false;

        lock (queueGate)
        {
            // Keep the newest; a burst of saves must not turn into a queue of cards.
            if (queue.Count > 2)
                queue.Dequeue();
            queue.Enqueue((title, message));
        }
        kick.Set();
        return true;
    }

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

            while (!disposed)
            {
                kick.WaitOne(200);
                while (!disposed && TryDequeue(out var item))
                    Animate(item.Title, item.Message);
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

    private bool TryDequeue(out (string Title, string Message) item)
    {
        lock (queueGate)
        {
            if (queue.Count == 0)
            {
                item = default;
                return false;
            }
            item = queue.Dequeue();
            return true;
        }
    }

    /// <summary>Fade in, hold, fade out -- one card, blocking this thread the whole way.</summary>
    private void Animate(string title, string message)
    {
        RenderCardBase(title, message);

        // A card already on screen keeps its alpha and just gets more time (the Windows "smooth
        // restart"); an idle window fades in from nothing.
        var phase = CurrentAlpha > 0 ? Phase.Hold : Phase.FadeIn;
        var phaseStart = Environment.TickCount64;

        while (!disposed)
        {
            var elapsed = Environment.TickCount64 - phaseStart;
            switch (phase)
            {
                case Phase.FadeIn:
                    if (elapsed >= FadeInMs)
                    {
                        CurrentAlpha = 255;
                        phase = Phase.Hold;
                        phaseStart = Environment.TickCount64;
                    }
                    else
                        CurrentAlpha = (int)(elapsed * 255 / FadeInMs);
                    break;

                case Phase.Hold:
                    CurrentAlpha = 255;
                    if (elapsed >= HoldMs)
                    {
                        phase = Phase.FadeOut;
                        phaseStart = Environment.TickCount64;
                    }
                    break;

                case Phase.FadeOut:
                    var remaining = FadeOutMs - elapsed;
                    if (remaining <= 0)
                    {
                        CurrentAlpha = 0;
                        DrawFrame(0);
                        return;
                    }
                    CurrentAlpha = (int)(remaining * 255 / FadeOutMs);
                    break;
            }

            DrawFrame(CurrentAlpha);
            Thread.Sleep(StepMs);

            // A newer card queued while this one animates takes over immediately (the window is
            // already visible, so the new card starts at hold, like the Windows one).
            if (phase != Phase.FadeOut && TryDequeue(out var next))
            {
                RenderCardBase(next.Title, next.Message);
                phase = Phase.Hold;
                phaseStart = Environment.TickCount64;
            }
        }
    }

    /// <summary>Alpha of the card on screen, read and written only on the overlay thread.</summary>
    private int CurrentAlpha;

    /// <summary>
    /// Resizes every size-dependent resource to <paramref name="width"/>: the override-redirect
    /// window (its RIGHT edge stays anchored at the margin, so the card keeps hanging in the
    /// top-right corner), the frame image the animation pushes, and the scratch pixmap/image pair
    /// the text is baked on. Called per show before rendering, only when the width actually
    /// changes, so the common clip card never rebuilds anything.
    /// </summary>
    private void EnsureWidth(int width)
    {
        if (width == cardWidth || window == 0)
            return;

        var height = Scaled(BaseHeight);
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

        cardWidth = width;
        XFlush(display);
    }

    /// <summary>
    /// Per-show reposition: the cursor may have moved to the other monitor since the last card,
    /// and a same-width card would otherwise stay on the old screen. Cheap (one pointer query),
    /// so it rides along every show.
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

        display = XOpenDisplay(null);
        if (display == 0)
        {
            Console.WriteLine("Overlay: no X11 display (DISPLAY unset or XWayland absent); overlay disabled.");
            ready.Set();
            return;
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
        titleFont = XftFontOpenName(display, screen, $"Sans-{TitleFontPx}:bold");
        messageFont = XftFontOpenName(display, screen, $"Sans-{MessageFontPx}");
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

        // The scratch pixmap is where text gets baked into the card ONCE per show (Xft draws via
        // XRender, which only targets drawables, not our byte buffer). Drawing the text directly
        // onto the window every animation frame would need two X operations per frame -- the
        // compositor catches the state between them and the card visibly re-paints, text popping
        // in and out. With the composite pre-baked, every frame is ONE XPutImage: atomic.
        scratchBuffer = Marshal.AllocHGlobal(Scaled(BaseWidth) * Scaled(BaseHeight) * 4);
        scratchImage = XCreateImage(display, visual, 32, ZPixmap, 0, scratchBuffer,
            (uint)Scaled(BaseWidth), (uint)Scaled(BaseHeight), 32, 0);
        scratchPixmap = XCreatePixmap(display, window, (uint)Scaled(BaseWidth), (uint)Scaled(BaseHeight), 32);

        draw = XftDrawCreate(display, scratchPixmap, visual, colormap);
        // Colours must be allocated against the window's own visual and colormap (the 32-bit ARGB
        // pair), not the screen defaults, or XRender blends the text with the wrong pixel format.
        titleColor = AllocColor(titleColorRef());
        messageColor = AllocColor(messageColorRef());

        XMapWindow(display, window);
        XRaiseWindow(display, window);
        XFlush(display);

        IsReady = true;
        ready.Set();
    }

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
    /// stadium-left shape (semicircular cap = the green badge, rectangular body, 12 px rounded
    /// right corners) with the hairline border, all anti-aliased by signed distance coverage, and
    /// the two text lines baked in through Xft. The text is composited on the scratch pixmap --
    /// XPutImage of the shape, XRender of the text, XGetImage of the result -- so the animation
    /// frames afterwards are a single atomic XPutImage each and the card can never appear on
    /// screen without its text.
    /// </summary>
    private void RenderCardBase(string title, string message)
    {
        // Layout first: it measures the lines and grows the card when a status message is wider
        // than the 240 px design width -- every width below then reads the new cardWidth.
        textLayout = ComputeTextLayout(title, message);
        MoveToActiveMonitor();
        var width = cardWidth;
        var height = Scaled(BaseHeight);
        var tailRadius = Scaled(BaseCornerRadius);
        var border = Scaled(BaseBorder);
        // The left cap: a semicircle on the card's vertical midline, and the badge inside it.
        var capRadius = height / 2f;
        var capCenterX = capRadius;
        var capCenterY = height / 2f;
        var buffer = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;

                // The card is the union of three primitives: the left cap, the straight body,
                // and the rounded right tail. Union = the nearest boundary = min of the SDFs.
                var body = SdBox(px, py, capRadius, 0, width - tailRadius, height);
                var cap = SdCircle(px, py, capCenterX, capCenterY, capRadius);
                var tail = SdCircle(px, py, width - tailRadius, capCenterY, tailRadius);
                var coverage = Math.Clamp(0.5f - MathF.Min(body, MathF.Min(cap, tail)), 0, 1);

                // The interior is the same union eroded by the border; the cap/body junction is
                // tangent-smooth, so union-of-erosions is exact there and the 1 px hairline keeps
                // its thickness all the way around.
                var bodyIn = SdBox(px, py, capRadius, border, width - tailRadius, height - border);
                var capIn = SdCircle(px, py, capCenterX, capCenterY, capRadius - border);
                var tailIn = SdCircle(px, py, width - tailRadius, capCenterY, tailRadius - border);
                var interior = Math.Clamp(0.5f - MathF.Min(bodyIn, MathF.Min(capIn, tailIn)), 0, 1);

                var ring = coverage - interior;
                if (coverage <= 0)
                    continue;

                var r = BorderR * ring + BgR * interior;
                var g = BorderG * ring + BgG * interior;
                var b = BorderB * ring + BgB * interior;

                // The accent badge fills the cap exactly -- its contour IS the card's contour on
                // the left -- and carries the check, blended over the card in straight colour;
                // everything is premultiplied once at the end. The coverage is the usual
                // half-pixel form: 0.5 - (distance - radius); an extra radius term here would
                // double the badge and spill green into the card body.
                var badgeCov = Math.Clamp(0.5f - SdCircle(px, py, capCenterX, capCenterY, capRadius), 0, 1);
                if (badgeCov > 0)
                {
                    r = r * (1 - badgeCov) + AccentR * badgeCov;
                    g = g * (1 - badgeCov) + AccentG * badgeCov;
                    b = b * (1 - badgeCov) + AccentB * badgeCov;
                }

                var checkCov = CheckCoverage(px, py, capCenterX, capCenterY, capRadius);
                if (checkCov > 0)
                {
                    r = r * (1 - checkCov) + 255 * checkCov;
                    g = g * (1 - checkCov) + 255 * checkCov;
                    b = b * (1 - checkCov) + 255 * checkCov;
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

    /// <summary>Signed distance to an axis-aligned box's boundary; negative inside.</summary>
    private static float SdBox(float px, float py, float x0, float y0, float x1, float y1)
    {
        var qx = MathF.Abs(px - (x0 + x1) / 2) - (x1 - x0) / 2;
        var qy = MathF.Abs(py - (y0 + y1) / 2) - (y1 - y0) / 2;
        var ax = MathF.Max(qx, 0);
        var ay = MathF.Max(qy, 0);
        return MathF.Sqrt(ax * ax + ay * ay) + MathF.Min(MathF.Max(qx, qy), 0);
    }

    private static float SdCircle(float px, float py, float cx, float cy, float radius) =>
        MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - radius;

    /// <summary>
    /// Coverage of the check mark: two thick segments with round caps, clipped to the circle so
    /// the stroke never pokes out of the badge. Half-pixel anti-aliasing like everything else.
    /// </summary>
    private float CheckCoverage(float px, float py, float cx, float cy, float radius)
    {
        var half = radius * 0.32f; // stroke half-width, proportional to the badge
        var a = (cx - radius * 0.42f, cy + radius * 0.05f);
        var b = (cx - radius * 0.10f, cy + radius * 0.38f);
        var c = (cx + radius * 0.48f, cy - radius * 0.34f);

        var cov = StrokeCoverage(px, py, a.Item1, a.Item2, b.Item1, b.Item2, half);
        cov = MathF.Max(cov, StrokeCoverage(px, py, b.Item1, b.Item2, c.Item1, c.Item2, half));
        return Math.Min(cov, Math.Clamp(0.5f - (SdCircle(px, py, cx, cy, radius) + 1.5f), 0, 1));
    }

    private static float StrokeCoverage(float px, float py, float ax, float ay, float bx, float by, float halfWidth)
    {
        var abx = bx - ax;
        var aby = by - ay;
        var lengthSquared = abx * abx + aby * aby;
        var t = lengthSquared > 0
            ? Math.Clamp(((px - ax) * abx + (py - ay) * aby) / lengthSquared, 0, 1)
            : 0;
        var dx = px - (ax + abx * t);
        var dy = py - (ay + aby * t);
        return Math.Clamp(halfWidth + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
    }

    // --- Text: Xft layout and per-frame drawing ---

    /// <summary>Everything needed to draw the two lines, measured once per card.</summary>
    private (byte[] Title, byte[] Message, int TitleX, int TitleBaseline, int MessageX, int MessageBaseline)? textLayout;

    private (byte[] Title, byte[] Message, int TitleX, int TitleBaseline, int MessageX, int MessageBaseline)
        ComputeTextLayout(string title, string message)
    {
        var height = Scaled(BaseHeight);
        var textX = Scaled(BaseTextX);
        var inset = Scaled(BaseTextRightInset);

        var titleBytes = Utf8(title);
        var messageBytes = Utf8(message);
        XftTextExtentsUtf8(display, titleFont, titleBytes, titleBytes.Length, out var titleExtents);
        XftTextExtentsUtf8(display, messageFont, messageBytes, messageBytes.Length, out var messageExtents);

        // GROW, don't truncate: a status line like "You are on the latest version (v1.6.1)" is
        // wider than the 240 px design card, and clipping it would defeat the announcement. The
        // card widens to the longest line plus the badge band and the right inset; the screen
        // width (margin on both sides) is the only hard ceiling, and past it the lines
        // ellipsize as before.
        var textWidth = Math.Max(titleExtents.width, messageExtents.width);
        var screenRoom = XDisplayWidth(display, screen) - 2 * Scaled(BaseMargin) - textX - inset;
        var wanted = Math.Clamp(textX + textWidth + inset,
            Scaled(BaseWidth), Math.Max(Scaled(BaseWidth), textX + Math.Max(0, screenRoom) + inset));
        EnsureWidth(wanted);

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

        // One centred block: title, small gap, message -- the Windows card's vertical rhythm.
        var gap = Scaled(BaseLineGap);
        var blockHeight = titleExtents.height + gap + messageExtents.height;
        var top = Math.Max(0, (height - blockHeight) / 2);
        // XGlyphInfo.y is the ink's rise ABOVE the baseline (probed against Noto Sans: 17 for the
        // 16 px face), so the baseline that puts the ink top at `top` is top + y, and the ink
        // bottom sits at baseline - y + height.
        var titleBaseline = top + titleExtents.y;
        var messageBaseline = top + titleExtents.height + gap + messageExtents.y;

        // Each line centred horizontally in the band right of the badge.
        var titleX = textX + Math.Max(0, (maxWidth - titleExtents.width) / 2);
        var messageX = textX + Math.Max(0, (maxWidth - messageExtents.width) / 2);
        return (titleBytes, messageBytes, titleX, titleBaseline, messageX, messageBaseline);
    }

    /// <summary>Scales the full-opacity card by <paramref name="alpha"/> and pushes it to the window.</summary>
    private unsafe void DrawFrame(int alpha)
    {
        if (cardBase is null || image == 0 || frameBuffer == 0 || !IsReady)
            return;

        var bytes = cardWidth * Scaled(BaseHeight) * 4;
        var target = new Span<byte>((void*)frameBuffer, bytes);
        var source = cardBase;
        // Premultiplied pixels scale channel-wise: multiplying all four bytes by the fade keeps
        // the colours exact at every alpha, corners and text anti-aliasing included.
        for (var i = 0; i < bytes; i++)
            target[i] = (byte)(source[i] * alpha / 255);

        // The single X operation per frame: no intermediate "card without text" state can ever
        // reach the compositor, which is what made the card flicker.
        XPutImage(display, window, graphicsContext, image, 0, 0, 0, 0,
            (uint)cardWidth, (uint)Scaled(BaseHeight));
        // Re-assert the stacking on every frame: a game or an IDE raising itself mid-fade must
        // never bury the card -- XRaiseWindow is fire-and-forget on the X stream, no round trip.
        XRaiseWindow(display, window);
        XFlush(display);
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

    private int Scaled(int design) => design; // the per-display scale factor lands here

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

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XOpenDisplay(string? name);

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
}
#endif
