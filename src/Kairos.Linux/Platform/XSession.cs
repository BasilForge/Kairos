using System.Text;

namespace Kairos.Platform;

/// <summary>A short-lived X connection for asking about windows (the active one, its process, title and size).</summary>
sealed class XSession : IDisposable
{
    readonly IntPtr _display;
    readonly nuint _root;

    XSession(IntPtr display)
    {
        _display = display;
        _root = Xlib.XDefaultRootWindow(display);
    }

    public static XSession? Open()
    {
        IntPtr display = Xlib.XOpenDisplay(IntPtr.Zero);
        return display == IntPtr.Zero ? null : new XSession(display);
    }

    nuint Atom(string name) => Xlib.XInternAtom(_display, name, 0);

    public nuint? Active() => Xlib.Items(_display, _root, Atom("_NET_ACTIVE_WINDOW")) is { Length: > 0 } w && w[0] != 0 ? w[0] : null;

    public int? Pid(nuint window) => Xlib.Items(_display, window, Atom("_NET_WM_PID")) is { Length: > 0 } p ? (int)p[0] : null;

    public string? Title(nuint window)
    {
        byte[] name = Xlib.Bytes(_display, window, Atom("_NET_WM_NAME"));
        if (name.Length == 0)
            name = Xlib.Bytes(_display, window, Atom("WM_NAME"));
        return name.Length == 0 ? null : Encoding.UTF8.GetString(name);
    }

    /// <summary>The window's class (WM_CLASS holds "instance\0class\0"); the class is the second name.</summary>
    public string? Class(nuint window)
    {
        string[] names = Encoding.UTF8.GetString(Xlib.Bytes(_display, window, Atom("WM_CLASS"))).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return names.Length > 1 ? names[1] : names.FirstOrDefault();
    }

    public bool HasState(nuint window, string state)
    {
        nuint wanted = Atom(state);
        return Xlib.Items(_display, window, Atom("_NET_WM_STATE")).Contains(wanted);
    }

    /// <summary>The window's rectangle on the screen.</summary>
    public (int Left, int Top, int Right, int Bottom)? Rect(nuint window)
    {
        if (Xlib.XGetGeometry(_display, window, out _, out _, out _, out uint w, out uint h, out _, out _) == 0)
            return null;
        Xlib.XTranslateCoordinates(_display, window, _root, 0, 0, out int x, out int y, out _);
        return (x, y, x + (int)w, y + (int)h);
    }

    /// <summary>The monitor (RandR) that holds most of the window's top-left corner, or null when none is known.</summary>
    public unsafe (int Left, int Top, int Right, int Bottom)? MonitorAt(int x, int y)
    {
        IntPtr list = Xlib.XRRGetMonitors(_display, _root, 1, out int count);
        if (list == IntPtr.Zero)
            return null;
        try
        {
            // XRRMonitorInfo (56 bytes): name@0 primary@8 automatic@12 noutput@16 x@20 y@24 width@28 height@32.
            for (int i = 0; i < count; i++)
            {
                byte* m = (byte*)list + i * 56;
                int mx = *(int*)(m + 20), my = *(int*)(m + 24), mw = *(int*)(m + 28), mh = *(int*)(m + 32);
                if (x >= mx && x < mx + mw && y >= my && y < my + mh)
                    return (mx, my, mx + mw, my + mh);
            }
            return null;
        }
        finally
        {
            Xlib.XRRFreeMonitors(list);
        }
    }

    public void Dispose() => Xlib.XCloseDisplay(_display);
}
