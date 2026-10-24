using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kairos.App;

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a message-only window: Windows matches the keys itself and Kairos wakes
/// only for its own combos, so it costs nothing while you play. A key another app holds is tried again by
/// <see cref="RetryPending"/>; while the user types a key into a hotkey box, Kairos's keys are let go
/// (<see cref="Suspend"/>), so the box gets them and nothing runs.
/// </summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    const int ProbeId = 0xBFFF;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly Dictionary<int, string> _idsByAtom = [];
    /// <summary>The extra registrations of F13-F24 keys with Shift or Ctrl held (<see cref="HotkeyParser.WithHeldKeys"/>).</summary>
    readonly Dictionary<int, Hotkey> _held = [];
    readonly Dictionary<string, bool> _status = [];
    /// <summary>Every key Kairos wants, by id (kept while suspended, registered again on resume).</summary>
    readonly Dictionary<string, Hotkey> _wanted = [];
    /// <summary>Keys another app held when they were registered, tried again by <see cref="RetryPending"/>.</summary>
    readonly Dictionary<string, Hotkey> _pending = [];
    int _nextAtom = 1;

    public event Action<string>? Pressed;

    public IReadOnlyDictionary<string, bool> Status => _status;

    /// <summary>Whether Kairos's keys are let go because the user is typing one into a hotkey box.</summary>
    public bool Suspended { get; private set; }

    public HotkeyManager()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
    }

    /// <summary>Whether no app (Kairos included) holds these keys: reserved for an instant and let go.</summary>
    public static bool IsFree(Hotkey hotkey)
    {
        bool free = RegisterHotKey(IntPtr.Zero, ProbeId, Modifiers(hotkey), (uint)hotkey.Key);
        if (free)
            UnregisterHotKey(IntPtr.Zero, ProbeId);
        return free;
    }

    /// <summary>Registers <paramref name="hotkey"/> under <paramref name="id"/>; false when another app owns it.</summary>
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

    /// <summary>
    /// Tries the keys another app held once more (when the settings window asks for the status, so only while it is
    /// open): one Windows call per such key, nothing at all when every key works.
    /// </summary>
    public void RetryPending()
    {
        if (_pending.Count == 0 || Suspended)
            return;
        foreach (var (id, hotkey) in _pending.ToList())
            if (Reserve(id, hotkey))
                _pending.Remove(id);
    }

    /// <summary>Lets Kairos's keys go while one is typed into a hotkey box.</summary>
    public void Suspend()
    {
        if (Suspended)
            return;
        ReleaseAll();
        Suspended = true;
    }

    /// <summary>Takes the keys again after typing (the ones saved meanwhile included).</summary>
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
        foreach (int atom in _idsByAtom.Keys)
            UnregisterHotKey(Handle, atom);
        _idsByAtom.Clear();
        _held.Clear();
    }

    bool Reserve(string id, Hotkey hotkey)
    {
        // A held-key variant of an earlier key that is this one: it gives way (Kairos's own keys come first).
        foreach (int atom in _held.Where(v => v.Value == hotkey).Select(v => v.Key).ToList())
        {
            UnregisterHotKey(Handle, atom);
            _held.Remove(atom);
            _idsByAtom.Remove(atom);
        }
        bool ok = Take(id, hotkey);
        if (ok)
            foreach (var held in HotkeyParser.WithHeldKeys(hotkey))
                if (!_wanted.ContainsValue(held) && !_held.ContainsValue(held) && Take(id, held)) // another app holds it: left to it
                    _held[_nextAtom - 1] = held;
        _status[id] = ok;
        return ok;
    }

    /// <summary>One registration under a new atom; false when another app holds the keys.</summary>
    bool Take(string id, Hotkey hotkey)
    {
        int atom = _nextAtom++;
        bool ok = RegisterHotKey(Handle, atom, Modifiers(hotkey), (uint)hotkey.Key);
        if (ok)
            _idsByAtom[atom] = id;
        return ok;
    }

    static uint Modifiers(Hotkey hotkey)
    {
        uint mods = MOD_NOREPEAT;
        if (hotkey.Ctrl) mods |= MOD_CONTROL;
        if (hotkey.Alt) mods |= MOD_ALT;
        if (hotkey.Shift) mods |= MOD_SHIFT;
        if (hotkey.Win) mods |= MOD_WIN;
        return mods;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && _idsByAtom.TryGetValue((int)m.WParam, out string? id))
            Pressed?.Invoke(id);
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
