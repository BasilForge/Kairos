using System.Windows.Forms;

namespace Kairos.App;

/// <summary>
/// Which hotkeys fire, from one look at the keys (<see cref="HotkeyWatcher"/> takes the looks, ~40 a second, so a look
/// allocates nothing): a hotkey fires once as its key goes down with exactly its modifiers held (F13-F24 also with Shift
/// or Ctrl held, <see cref="HotkeyParser.AllowsHeldKeys"/>), never again until its key is let go. A key or modifier
/// pressed and let go between two looks still counts (Windows keeps that it was pressed since the last look): a Stream
/// Deck or macro sends a whole Alt+F8 within a few milliseconds.
/// </summary>
public sealed class HotkeyMatcher
{
    /// <summary>A key's look, by its virtual-key code: down now, pressed since the last look.</summary>
    public const byte Down = 1, Tapped = 2;

    /// <summary>The modifier keys every look reads besides the hotkeys' own keys.</summary>
    public static readonly Keys[] ModifierKeys = [Keys.ControlKey, Keys.Menu, Keys.ShiftKey, Keys.LWin, Keys.RWin];

    [Flags]
    enum Mods : byte { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

    /// <summary>A hotkey as a look tests it: its key, the modifiers it wants and those that may be held too.</summary>
    struct Entry(string id, Hotkey h)
    {
        public readonly string Id = id;
        public readonly Keys Key = h.Key;
        public readonly Mods Want = (h.Ctrl ? Mods.Ctrl : 0) | (h.Alt ? Mods.Alt : 0) | (h.Shift ? Mods.Shift : 0) | (h.Win ? Mods.Win : 0);
        public readonly Mods MayAlsoHold = HotkeyParser.AllowsHeldKeys(h.Key) ? Mods.Ctrl | Mods.Shift : Mods.None;
        public bool IsDown;
    }

    Entry[] _entries = [];
    readonly List<string> _fired = [];

    /// <summary>The hotkeys' own keys (read with <see cref="ModifierKeys"/>); none without hotkeys.</summary>
    public Keys[] Watched { get; private set; } = [];

    public void Set(IReadOnlyDictionary<string, Hotkey> keys)
    {
        _entries = [.. keys.Select(k => new Entry(k.Key, k.Value))];
        Watched = [.. _entries.Select(e => e.Key).Distinct()];
    }

    /// <summary>
    /// One look (<paramref name="states"/>: <see cref="Down"/> and <see cref="Tapped"/> by virtual-key code); the
    /// hotkeys that fire now, in a list kept for the next look.
    /// </summary>
    public IReadOnlyList<string> Look(byte[] states)
    {
        _fired.Clear();
        Mods held = Modifiers(states, Down), pressed = Modifiers(states, Down | Tapped);
        for (int i = 0; i < _entries.Length; i++)
        {
            ref var e = ref _entries[i];
            byte key = states[(int)e.Key];
            // A key held now wants its modifiers held now; one let go already (a macro's whole combo) takes them tapped too.
            Mods have = (key & Down) != 0 ? held : pressed;
            if (key != 0 && (e.Want & ~have) == 0 && (held & ~(e.Want | e.MayAlsoHold)) == 0 && !e.IsDown)
            {
                e.IsDown = true;
                _fired.Add(e.Id);
            }
            if ((key & Down) == 0)
                e.IsDown = false;
        }
        return _fired;
    }

    /// <summary>The hotkeys held as watching starts (a key just typed into a hotkey box) count as down: no fire until pressed anew.</summary>
    public void Prime(byte[] states)
    {
        for (int i = 0; i < _entries.Length; i++)
            _entries[i].IsDown = false;
        Look(states);
    }

    static Mods Modifiers(byte[] states, int bits) =>
        ((states[(int)Keys.ControlKey] & bits) != 0 ? Mods.Ctrl : 0) | ((states[(int)Keys.Menu] & bits) != 0 ? Mods.Alt : 0)
        | ((states[(int)Keys.ShiftKey] & bits) != 0 ? Mods.Shift : 0)
        | (((states[(int)Keys.LWin] | states[(int)Keys.RWin]) & bits) != 0 ? Mods.Win : 0);
}
