using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Kairos.Engine;
using Kairos.Platform;

namespace Kairos.App;

/// <summary>
/// System-wide hotkeys through X11 key grabs on the root window: the X server matches the keys itself and Kairos wakes
/// only for its own combos. A key another app holds is tried again by <see cref="RetryPending"/>; while the user types a
/// key into a hotkey box, Kairos's keys are let go (<see cref="Suspend"/>), so the box gets them and nothing runs.
/// Same surface as the Windows class. Wayland has no key grabs: its sessions use the GlobalShortcuts portal instead.
/// </summary>
public sealed unsafe class HotkeyManager : IDisposable
{
    /// <summary>Num Lock and Caps Lock change the modifier mask, so each combo is grabbed with every mix of them.</summary>
    static readonly uint[] LockMasks = [0, Xlib.Lock, Xlib.Mod2, Xlib.Lock | Xlib.Mod2];

    static readonly object ErrorGate = new();
    static bool _grabRefused;
    static readonly Xlib.ErrorHandler Handler = OnError;

    readonly IntPtr _display;
    readonly nuint _root;
    readonly Thread _reader;
    readonly Dictionary<string, bool> _status = [];
    readonly Dictionary<string, Hotkey> _wanted = [];
    readonly Dictionary<string, Hotkey> _pending = [];
    /// <summary>What is grabbed now: keycode and modifiers to the action's id.</summary>
    readonly ConcurrentDictionary<(int Keycode, uint Mods), string> _grabbed = new();
    readonly HashSet<int> _down = [];
    volatile bool _stop;

    public event Action<string>? Pressed;

    public IReadOnlyDictionary<string, bool> Status => _status;

    public bool Suspended { get; private set; }

    public HotkeyManager()
    {
        _display = Xlib.XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            return;
        _root = Xlib.XDefaultRootWindow(_display);
        Xlib.XSetErrorHandler(Handler);
        Xlib.XkbSetDetectableAutoRepeat(_display, 1, out _); // a held key is one press, not a press and release over and over
        Xlib.XSelectInput(_display, _root, 0);
        _reader = new Thread(Read) { IsBackground = true, Name = "Kairos hotkeys" };
        _reader.Start();
    }

    static int OnError(IntPtr display, Xlib.ErrorEvent* e)
    {
        if (e->ErrorCode == Xlib.BadAccess)
            lock (ErrorGate)
                _grabRefused = true;
        return 0;
    }

    /// <summary>Whether no app (Kairos included) holds these keys: grabbed for an instant and let go.</summary>
    public static bool IsFree(Hotkey hotkey)
    {
        IntPtr display = Xlib.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return true;
        try
        {
            Xlib.XSetErrorHandler(Handler);
            nuint root = Xlib.XDefaultRootWindow(display);
            if (!KeyMap.TryCode(display, hotkey, out int code, out uint mods))
                return true;
            bool free = Grab(display, root, code, mods);
            if (free)
                Ungrab(display, root, code, mods);
            return free;
        }
        finally
        {
            Xlib.XCloseDisplay(display);
        }
    }

    public bool Register(string id, Hotkey hotkey)
    {
        _wanted[id] = hotkey;
        if (Suspended)
        {
            _status[id] = true; // taken when typing ends
            return true;
        }
        bool ok = Reserve(id, hotkey);
        if (ok)
            _pending.Remove(id);
        else
            _pending[id] = hotkey;
        return ok;
    }

    /// <summary>Tries the keys another app held once more (when the settings window asks for the status).</summary>
    public void RetryPending()
    {
        if (_pending.Count == 0 || Suspended)
            return;
        foreach (var (id, hotkey) in _pending.ToList())
            if (Reserve(id, hotkey))
                _pending.Remove(id);
    }

    public void Suspend()
    {
        if (Suspended)
            return;
        ReleaseAll();
        Suspended = true;
    }

    public void Resume()
    {
        if (!Suspended)
            return;
        Suspended = false;
        _pending.Clear();
        foreach (var (id, hotkey) in _wanted)
            if (!Reserve(id, hotkey))
                _pending[id] = hotkey;
    }

    public void UnregisterAll()
    {
        ReleaseAll();
        _status.Clear();
        _wanted.Clear();
        _pending.Clear();
    }

    void ReleaseAll()
    {
        if (_display == IntPtr.Zero)
            return;
        foreach (var (code, mods) in _grabbed.Keys.ToList())
            Ungrab(_display, _root, code, mods);
        _grabbed.Clear();
        Xlib.XSync(_display, 0);
    }

    bool Reserve(string id, Hotkey hotkey)
    {
        bool ok = _display != IntPtr.Zero && KeyMap.TryCode(_display, hotkey, out int code, out uint mods) && Grab(_display, _root, code, mods);
        if (ok)
        {
            KeyMap.TryCode(_display, hotkey, out int c, out uint m);
            _grabbed[(c, m)] = id;
        }
        _status[id] = ok;
        return ok;
    }

    /// <summary>Grabs the combo with every lock-key mix; false (and nothing grabbed) when another client holds it.</summary>
    static bool Grab(IntPtr display, nuint root, int code, uint mods)
    {
        lock (ErrorGate)
            _grabRefused = false;
        foreach (uint extra in LockMasks)
            Xlib.XGrabKey(display, code, mods | extra, root, 0, Xlib.GrabModeAsync, Xlib.GrabModeAsync);
        Xlib.XSync(display, 0); // errors arrive here
        bool refused;
        lock (ErrorGate)
            refused = _grabRefused;
        if (refused)
            Ungrab(display, root, code, mods);
        return !refused;
    }

    static void Ungrab(IntPtr display, nuint root, int code, uint mods)
    {
        foreach (uint extra in LockMasks)
            Xlib.XUngrabKey(display, code, mods | extra, root);
    }

    void Read()
    {
        var fd = new Pollfd { Fd = Xlib.XConnectionNumber(_display), Events = 1 /* POLLIN */ };
        IntPtr ev = Marshal.AllocHGlobal(Xlib.EventSize);
        try
        {
            while (!_stop)
            {
                if (Xlib.XPending(_display) == 0)
                {
                    poll(ref fd, 1, 250);
                    continue;
                }
                Xlib.XNextEvent(_display, ev);
                int type = Marshal.ReadInt32(ev);
                int code = (int)(uint)Marshal.ReadInt32(ev, Xlib.KeycodeOffset);
                if (type == Xlib.KeyRelease)
                {
                    _down.Remove(code);
                    continue;
                }
                if (type != Xlib.KeyPress || !_down.Add(code))
                    continue;
                uint mods = (uint)Marshal.ReadInt32(ev, Xlib.KeyStateOffset) & (Xlib.Shift | Xlib.Control | Xlib.Mod1 | Xlib.Mod4);
                if (_grabbed.TryGetValue((code, mods), out string? id))
                    UiThread.Post(() => Pressed?.Invoke(id));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ev);
        }
    }

    public void Dispose()
    {
        if (_display == IntPtr.Zero)
            return;
        _stop = true;
        _reader.Join(1000);
        UnregisterAll();
        Xlib.XCloseDisplay(_display);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Pollfd { public int Fd; public short Events, Revents; }

    [DllImport("libc")] static extern int poll(ref Pollfd fds, ulong count, int timeout);
}
