using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kairos.App;

/// <summary>
/// System-wide hotkeys, heard two ways that cover each other's gaps, a press running once however many heard it
/// (<see cref="Heard"/>):
/// <list type="bullet">
/// <item>looking at their keys, as OBS does: a thread of its own looks at only the hotkeys' keys and the modifiers
/// (GetAsyncKeyState) every 25 ms, and <see cref="HotkeyMatcher"/> says which fire. Any key works (keyboard, the mouse's
/// middle and side buttons), a key another app holds too, and a game that turns Windows' hotkeys off. Measured
/// 2026-10-25: under 0.16 % of one core for 14 keys, and a look allocates nothing.</item>
/// <item>Windows' own hotkeys for the keyboard ones (<see cref="WindowsHotkeys"/>, free while you play): the only way
/// heard while a game run as administrator is in front, where the looks see nothing (measured 2026-10-25 with Task
/// Manager in front).</item>
/// </list>
/// No hotkeys, or while one is typed into a hotkey box (<see cref="Suspend"/>): the thread sleeps, Windows holds no key.
/// </summary>
public sealed class HotkeyWatcher : IDisposable
{
    const int LookEveryMs = 25;
    /// <summary>The other way hears a press within this of the first (the looks up to a look later): the same press.</summary>
    const int SamePressMs = 250;
    /// <summary>A press only one way heard is forgotten after this (Windows' late from a busy UI thread still pairs).</summary>
    const int KeepUnpairedMs = 5000;

    readonly HotkeyMatcher _matcher = new();
    readonly byte[] _states = new byte[256];
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Action<Action> _post;
    readonly Func<int, short> _read;
    readonly Thread? _thread;
    readonly WindowsHotkeys? _windows;
    /// <summary>Each hotkey's presses that ran and only one way heard yet (when, which way), kept a few seconds.</summary>
    readonly Dictionary<string, List<(int At, bool ByWindows)>> _unpaired = [];
    IReadOnlyDictionary<string, Hotkey> _keys = new Dictionary<string, Hotkey>();
    bool _prime, _disposed;

    /// <summary>A hotkey's id fired, and whether Windows heard it (else the looks): raised through the watcher's post (Kairos's UI thread).</summary>
    public event Action<string, bool>? Pressed;

    /// <summary>Whether watching stopped because the user is typing a key into a hotkey box.</summary>
    public bool Suspended { get; private set; }

    /// <param name="post">Runs a fired hotkey's action where it belongs (Kairos's UI thread, which makes the watcher).</param>
    public HotkeyWatcher(Action<Action> post) : this(post, GetAsyncKeyState, start: true)
    {
    }

    /// <summary>
    /// Tests: keys read by <paramref name="read"/>; when not <paramref name="start"/>, no thread (<see cref="LookOnce"/>
    /// instead) and no Windows hotkeys (<see cref="Heard"/> instead).
    /// </summary>
    internal HotkeyWatcher(Action<Action> post, Func<int, short> read, bool start)
    {
        _post = post;
        _read = read;
        if (!start)
            return;
        _windows = new WindowsHotkeys((id, at) => Heard(id, at, byWindows: true));
        _thread = new Thread(Run) { IsBackground = true, Name = "Kairos hotkeys", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Watches these hotkeys from now on (keys held now do not fire until pressed anew); the ids whose keys another app
    /// holds in Windows (heard by the looks only).
    /// </summary>
    public IReadOnlyList<string> Set(IReadOnlyDictionary<string, Hotkey> keys)
    {
        lock (_gate)
        {
            _matcher.Set(keys);
            _unpaired.Clear();
            _prime = true;
        }
        _keys = keys;
        _wake.Set();
        return Suspended || _windows == null ? [] : _windows.Register(keys);
    }

    /// <summary>Stops watching while a key is typed into a hotkey box (Windows lets the keys go, so the box gets them).</summary>
    public void Suspend()
    {
        Suspended = true;
        _windows?.Release();
    }

    /// <summary>Watches again after typing (a key still held from the box does not fire).</summary>
    public void Resume()
    {
        lock (_gate)
            _prime = true;
        Suspended = false;
        _windows?.Register(_keys);
        _wake.Set();
    }

    /// <summary>
    /// A hotkey heard one way (<paramref name="byWindows"/>, else the looks) for a press at <paramref name="at"/>
    /// (Environment.TickCount): run, unless the other way ran this same press already (the closest in time of its unpaired
    /// presses, so quick presses heard late still pair one to one).
    /// </summary>
    internal void Heard(string id, int at, bool byWindows)
    {
        lock (_gate)
        {
            if (!_unpaired.TryGetValue(id, out var runs))
                _unpaired[id] = runs = [];
            runs.RemoveAll(r => Gap(at, r.At) >= KeepUnpairedMs);
            int pair = -1;
            for (int i = 0; i < runs.Count; i++)
                if (runs[i].ByWindows != byWindows && Gap(at, runs[i].At) < SamePressMs && (pair < 0 || Gap(at, runs[i].At) < Gap(at, runs[pair].At)))
                    pair = i;
            if (pair >= 0)
            {
                runs.RemoveAt(pair); // both ways heard this press: it ran once
                return;
            }
            runs.Add((at, byWindows));
        }
        _post(() => Pressed?.Invoke(id, byWindows));
    }

    /// <summary>Milliseconds between two Environment.TickCount moments, either order, across its wrap.</summary>
    static long Gap(int a, int b) => Math.Abs((long)unchecked(a - b));

    void Run()
    {
        while (!_disposed)
        {
            if (LookOnce())
                Thread.Sleep(LookEveryMs);
            else
                _wake.WaitOne(); // nothing to watch: no wake-up until there is
        }
    }

    /// <summary>One look and the hotkeys it fires (<see cref="Heard"/>); false when there is nothing to watch.</summary>
    internal bool LookOnce()
    {
        string[]? fired = null;
        int at;
        lock (_gate)
        {
            if (Suspended || _matcher.Watched.Length == 0)
                return false;
            at = Environment.TickCount;
            Look(_matcher.Watched);
            if (_prime)
                _matcher.Prime(_states);
            else if (_matcher.Look(_states) is { Count: > 0 } now)
                fired = [.. now];
            _prime = false;
        }
        if (fired != null)
            foreach (string id in fired)
                Heard(id, at, byWindows: false);
        return true;
    }

    /// <summary>
    /// One Windows call per hotkey key and modifier into <see cref="_states"/> (down now, pressed since the last look).
    /// The modifiers are read every look too: "pressed since" counts from the last read, so a modifier read only now and
    /// then would bring back a press from minutes ago.
    /// </summary>
    void Look(Keys[] keys)
    {
        foreach (var key in keys)
            Read(key);
        foreach (var key in HotkeyMatcher.ModifierKeys)
            Read(key);
    }

    void Read(Keys key)
    {
        short s = _read((int)key);
        _states[(int)key] = (byte)(((s & 0x8000) != 0 ? HotkeyMatcher.Down : 0) | ((s & 1) != 0 ? HotkeyMatcher.Tapped : 0));
    }

    /// <summary>On the UI thread that made the watcher (Windows' hotkeys are let go there).</summary>
    public void Dispose()
    {
        _disposed = true;
        _windows?.Dispose();
        _wake.Set();
        if (_thread == null || _thread.Join(1000))
            _wake.Dispose(); // else the thread still ends on its own (background): it may still use the event
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
}
