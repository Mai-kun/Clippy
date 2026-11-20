#if LINUX
using System.Runtime.InteropServices;

namespace Clippy;

/// <summary>
/// What the X SERVER thinks about the overlay window, for tests: finds the window by its name
/// ("ClippyOverlay", set with XStoreName) among the root's children on its own display connection,
/// and reads XGetWindowAttributes.map_state. The overlay's own "mapped" flag says what the code
/// MEANT to do; map_state says what is actually on the screen -- the difference is exactly where
/// a missing XFlush hides.
///
/// map_state: 0 = IsUnmapped, 1 = IsUnviewable, 2 = IsViewable.
/// </summary>
internal static partial class LinuxOverlayProbe
{
    public const int IsUnmapped = 0;

    /// <summary>The overlay window's map state, or -1 when the window (or the display) is gone.</summary>
    public static unsafe int GetMapState()
    {
        var display = LinuxX11.TryOpenDisplay();
        if (display == 0)
            return -1;

        try
        {
            var root = XDefaultRootWindow(display);
            if (XQueryTree(display, root, out _, out _, out var children, out var count) == 0)
                return -1;

            var result = -1;
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var window = Marshal.ReadIntPtr(children, i * sizeof(nint));
                    if (window == 0 || XFetchName(display, window, out var name) == 0 || name == 0)
                        continue;
                    var text = Marshal.PtrToStringAnsi(name);
                    XFree(name);
                    if (text != "ClippyOverlay")
                        continue;

                    var attributes = Marshal.AllocHGlobal(AttributeSize);
                    try
                    {
                        if (XGetWindowAttributes(display, window, attributes) != 0)
                            result = Marshal.ReadInt32(attributes, MapStateOffset);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(attributes);
                    }
                    break;
                }
            }
            finally
            {
                if (children != 0)
                    XFree(children);
            }
            return result;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    // XWindowAttributes through the one field this probe reads. Offsets are the LP64 x86-64
    // layout: five ints, a pointer, a Window, two more ints and padding land the MapState enum at
    // byte 92; the buffer is padded past the struct's tail so Xlib can fill it all.
    private const int MapStateOffset = 92;
    private const int AttributeSize = 136;

    [LibraryImport("libX11.so.6")]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XQueryTree(nint display, nint window,
        out nint rootReturn, out nint parentReturn, out nint childrenReturn, out uint countReturn);

    [LibraryImport("libX11.so.6")]
    private static partial int XFetchName(nint display, nint window, out nint nameReturn);

    [LibraryImport("libX11.so.6")]
    private static partial int XGetWindowAttributes(nint display, nint window, nint attributes);

    [LibraryImport("libX11.so.6")]
    private static partial int XFree(nint data);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);
}
#endif
