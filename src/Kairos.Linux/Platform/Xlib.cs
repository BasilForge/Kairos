using System.Runtime.InteropServices;

namespace Kairos.Platform;

/// <summary>The few Xlib calls Kairos needs: global key grabs, and the window properties that name the app in front.</summary>
static unsafe class Xlib
{
    const string X11 = "libX11.so.6";
    const string Xrandr = "libXrandr.so.2";

    public const int KeyPress = 2, KeyRelease = 3, BadAccess = 10;
    public const long KeyPressMask = 1, KeyReleaseMask = 2;
    public const int GrabModeAsync = 1;
    public const nuint AnyPropertyType = 0, XaWindow = 33, XaCardinal = 6, XaAtom = 4;

    // XKeyEvent on 64-bit Linux: state at 80, keycode at 84.
    public const int KeyStateOffset = 80, KeycodeOffset = 84, EventSize = 192;

    public const uint Shift = 1, Lock = 2, Control = 4, Mod1 = 8, Mod2 = 16, Mod4 = 64;

    [StructLayout(LayoutKind.Sequential)]
    public struct ErrorEvent
    {
        public int Type;
        public IntPtr Display;
        public nuint ResourceId;
        public nuint Serial;
        public byte ErrorCode, RequestCode, MinorCode;
    }

    public delegate int ErrorHandler(IntPtr display, ErrorEvent* e);

    [DllImport(X11)] public static extern int XInitThreads();
    [DllImport(X11)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] public static extern nuint XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(X11)] public static extern int XPending(IntPtr display);
    [DllImport(X11)] public static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(X11)] public static extern int XSync(IntPtr display, int discard);
    [DllImport(X11)] public static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport(X11)] public static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, nuint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X11)] public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, nuint grabWindow);
    [DllImport(X11)] public static extern int XSelectInput(IntPtr display, nuint window, long mask);
    [DllImport(X11)] public static extern nuint XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport(X11)] public static extern int XFree(IntPtr data);
    [DllImport(X11)] public static extern IntPtr XSetErrorHandler(ErrorHandler? handler);
    [DllImport(X11)] public static extern int XkbSetDetectableAutoRepeat(IntPtr display, int detectable, out int supported);
    [DllImport(X11)] public static extern int XGetWindowProperty(IntPtr display, nuint window, nuint property, long offset, long length, int delete,
        nuint requestedType, out nuint actualType, out int actualFormat, out nuint items, out nuint bytesAfter, out IntPtr data);
    [DllImport(X11)] public static extern int XGetGeometry(IntPtr display, nuint drawable, out nuint root, out int x, out int y,
        out uint width, out uint height, out uint border, out uint depth);
    [DllImport(X11)] public static extern int XTranslateCoordinates(IntPtr display, nuint src, nuint dest, int srcX, int srcY,
        out int destX, out int destY, out nuint child);
    [DllImport(Xrandr)] public static extern IntPtr XRRGetMonitors(IntPtr display, nuint window, int getActive, out int count);
    [DllImport(Xrandr)] public static extern void XRRFreeMonitors(IntPtr monitors);

    /// <summary>The 32-bit items of a window property (atoms, windows and cardinals come as C longs), or empty.</summary>
    public static nuint[] Items(IntPtr display, nuint window, nuint property)
    {
        if (XGetWindowProperty(display, window, property, 0, 1024, 0, AnyPropertyType, out _, out int format, out nuint count, out _, out IntPtr data) != 0
            || data == IntPtr.Zero)
            return [];
        try
        {
            if (format != 32)
                return [];
            var items = new nuint[count];
            for (int i = 0; i < items.Length; i++)
                items[i] = (nuint)Marshal.ReadInt64(data, i * 8);
            return items;
        }
        finally
        {
            XFree(data);
        }
    }

    /// <summary>The bytes of a string-like window property (WM_CLASS holds two names, NUL-separated).</summary>
    public static byte[] Bytes(IntPtr display, nuint window, nuint property)
    {
        if (XGetWindowProperty(display, window, property, 0, 1024, 0, AnyPropertyType, out _, out int format, out nuint count, out _, out IntPtr data) != 0
            || data == IntPtr.Zero)
            return [];
        try
        {
            if (format != 8)
                return [];
            var bytes = new byte[count];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            XFree(data);
        }
    }
}
