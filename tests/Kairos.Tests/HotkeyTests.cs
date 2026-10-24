using System.Windows.Forms;
using Kairos.App;

namespace Kairos.Tests;

public static class HotkeyTests
{
    [Test]
    public static void Parses_lowercase_and_formats_canonically()
    {
        Assert.True(HotkeyParser.TryParse("ctrl+f8", out var h), "should parse");
        Assert.Equal(Keys.F8, h.Key, "key");
        Assert.Equal(true, h.Ctrl, "ctrl");
        Assert.Equal("Ctrl+F8", HotkeyParser.Format(h), "format");
    }

    [Test]
    public static void Multi_modifier_round_trips()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Shift+F8", out var h), "should parse");
        Assert.Equal("Ctrl+Shift+F8", HotkeyParser.Format(h), "format");
        Assert.True(HotkeyParser.TryParse("shift+alt+win+ctrl+S", out var h2), "should parse");
        Assert.Equal("Ctrl+Alt+Shift+Win+S", HotkeyParser.Format(h2), "canonical order");
    }

    [Test]
    public static void Digits_format_without_d_prefix()
    {
        Assert.True(HotkeyParser.TryParse("Alt+1", out var h), "should parse");
        Assert.Equal(Keys.D1, h.Key, "key");
        Assert.Equal("Alt+1", HotkeyParser.Format(h), "format");
    }

    [Test]
    public static void Invalid_strings_are_rejected()
    {
        foreach (string s in new[] { "", "   ", "Ctrl+", "Foo", "Ctrl+Shift", "Ctrl+ControlKey", "Ctrl+F8+F9" })
            Assert.True(!HotkeyParser.TryParse(s, out _), $"'{s}' should not parse");
    }

    static App.Hotkey Hk(string s)
    {
        HotkeyParser.TryParse(s, out var h);
        return h;
    }

    /// <summary>The mouse's middle and side buttons are hotkeys too, alone or with keys held; never left or right.</summary>
    [Test]
    public static void Mouse_side_and_middle_buttons_round_trip()
    {
        Assert.True(HotkeyParser.TryParse("Mouse4", out var back) && back.Key == Keys.XButton1, "Mouse4: the back side button");
        Assert.True(HotkeyParser.TryParse("mouse5", out var forward) && forward.Key == Keys.XButton2, "Mouse5: the forward side button");
        Assert.True(HotkeyParser.TryParse("Ctrl+MouseMiddle", out var middle) && middle is { Key: Keys.MButton, Ctrl: true }, "the middle button with Ctrl");
        Assert.Equal("Mouse4", HotkeyParser.Format(back), "said as Mouse4");
        Assert.Equal("Shift+Mouse5", HotkeyParser.Format(forward with { Shift = true }), "with Shift");
        Assert.Equal("Ctrl+MouseMiddle", HotkeyParser.Format(middle), "the middle button");
        Assert.True(HotkeyParser.TryParse("XButton1", out var old) && old.Key == Keys.XButton1, "Windows's own name too");
        foreach (string s in new[] { "LButton", "RButton", "Ctrl+LButton", "MouseLeft" })
            Assert.True(!HotkeyParser.TryParse(s, out _), $"'{s}': left and right are pressed all through a game");
    }

    /// <summary>One look at the keys, as the watcher takes it: these keys down now.</summary>
    static byte[] Held(params Keys[] down) => Look(down, []);

    /// <summary>One look: <paramref name="down"/> held now, <paramref name="tapped"/> pressed and let go since the last look.</summary>
    static byte[] Look(Keys[] down, Keys[] tapped)
    {
        var states = new byte[256];
        foreach (var k in down)
            states[(int)k] |= HotkeyMatcher.Down;
        foreach (var k in tapped)
            states[(int)k] |= HotkeyMatcher.Tapped;
        return states;
    }

    static HotkeyMatcher Matcher(params (string Id, string Key)[] keys)
    {
        var m = new HotkeyMatcher();
        m.Set(keys.ToDictionary(k => k.Id, k => Hk(k.Key)));
        return m;
    }

    /// <summary>A hotkey fires once as its keys go down, never again while they stay down, and again after a release.</summary>
    [Test]
    public static void A_hotkey_fires_once_a_press()
    {
        var m = Matcher(("save", "Alt+F8"));
        Assert.True(m.Look(Held()).Count == 0, "nothing held");
        Assert.True(m.Look(Held(Keys.Menu)).Count == 0, "Alt alone");
        Assert.True(m.Look(Held(Keys.Menu, Keys.F8)).SequenceEqual(["save"]), "Alt+F8: save");
        Assert.True(m.Look(Held(Keys.Menu, Keys.F8)).Count == 0, "still held: once");
        Assert.True(m.Look(Held(Keys.Menu)).Count == 0, "F8 let go");
        Assert.True(m.Look(Held(Keys.Menu, Keys.F8)).SequenceEqual(["save"]), "pressed again: again");
    }

    /// <summary>Exactly its modifiers: Ctrl+Alt+F8 is not Alt+F8 (another app's, or another Kairos key).</summary>
    [Test]
    public static void A_hotkey_needs_exactly_its_modifiers()
    {
        var m = Matcher(("save", "Alt+F8"), ("shot", "Ctrl+Alt+F8"));
        Assert.True(m.Look(Held(Keys.F8)).Count == 0, "F8 alone");
        Assert.True(m.Look(Held(Keys.ControlKey, Keys.Menu, Keys.F8)).SequenceEqual(["shot"]), "Ctrl+Alt+F8: the other key only");
        Assert.True(Matcher(("save", "Alt+F8")).Look(Held(Keys.LWin, Keys.Menu, Keys.F8)).Count == 0, "with Win held: not it");
    }

    /// <summary>
    /// F13-F24 come from mouse and keyboard software for a side button, often with the game's run (Shift) or crouch
    /// (Ctrl) held: they fire with those held too. Never other keys (Shift+F10 opens menus).
    /// </summary>
    [Test]
    public static void Side_button_keys_fire_with_shift_or_ctrl_held()
    {
        Assert.True(Matcher(("save", "F17")).Look(Held(Keys.ShiftKey, Keys.ControlKey, Keys.F17)).SequenceEqual(["save"]), "F17 while running and crouching");
        Assert.True(Matcher(("save", "F17")).Look(Held(Keys.Menu, Keys.F17)).Count == 0, "not with Alt");
        Assert.True(Matcher(("save", "F8")).Look(Held(Keys.ShiftKey, Keys.F8)).Count == 0, "F8: exactly");
    }

    /// <summary>The mouse's side buttons: Mouse4 alone, or with Shift held; the left button never takes part.</summary>
    [Test]
    public static void Mouse_buttons_fire_like_keys()
    {
        var m = Matcher(("save", "Mouse4"), ("shot", "Shift+Mouse5"));
        Assert.True(m.Look(Held(Keys.LButton, Keys.XButton1)).SequenceEqual(["save"]), "Mouse4 while shooting");
        Assert.True(m.Look(Held(Keys.ShiftKey, Keys.XButton2)).SequenceEqual(["shot"]), "Shift+Mouse5");
        Assert.True(Matcher(("save", "Mouse4")).Look(Held(Keys.ShiftKey, Keys.XButton1)).Count == 0, "Mouse4 alone means alone");
    }

    /// <summary>
    /// A press shorter than one look is caught: the key says it was pressed since the last look. A Stream Deck or a macro
    /// sends a whole Alt+F8 within a few milliseconds, the modifier let go too by the next look.
    /// </summary>
    [Test]
    public static void A_quick_tap_between_looks_still_fires()
    {
        var m = Matcher(("save", "Alt+F8"));
        Assert.True(m.Look(Look([Keys.Menu], [Keys.F8])).SequenceEqual(["save"]), "F8 tapped while Alt is held");
        Assert.True(m.Look(Held(Keys.Menu)).Count == 0, "and once");
        Assert.True(m.Look(Look([], [Keys.Menu, Keys.F8])).SequenceEqual(["save"]), "the whole Alt+F8 sent between two looks");
        Assert.True(m.Look(Look([Keys.ShiftKey], [Keys.Menu, Keys.F8])).Count == 0, "with another modifier held: not Alt+F8");
    }

    /// <summary>
    /// A player lets go of Shift (running) just before pressing Alt+F7, within one look: only Alt+F7 (record) fires, not
    /// Alt+Shift+F7 (pause the recording) as well: a key held now wants its modifiers held now.
    /// </summary>
    [Test]
    public static void A_shift_let_go_just_before_a_hotkey_does_not_fire_its_shift_neighbour()
    {
        var m = Matcher(("record", "Alt+F7"), ("pauseRecord", "Alt+Shift+F7"));
        Assert.True(m.Look(Look([Keys.Menu, Keys.F7], [Keys.ShiftKey])).SequenceEqual(["record"]), "the recording's key only");
    }

    /// <summary>
    /// The watcher itself: no look without hotkeys; keys held as it starts, or still held from a hotkey box as it resumes,
    /// do not fire; while a key is typed into a hotkey box nothing is looked at.
    /// </summary>
    [Test]
    public static void The_watcher_primes_and_pauses()
    {
        var down = new HashSet<int>();
        var fired = new List<string>();
        using var w = new HotkeyWatcher(action => action(), vk => (short)(down.Contains(vk) ? 0x8000 : 0), start: false);
        w.Pressed += (id, _) => fired.Add(id);
        Assert.True(!w.LookOnce(), "no hotkeys: nothing to look at");
        down.UnionWith([(int)Keys.Menu, (int)Keys.F8]);
        w.Set(new Dictionary<string, App.Hotkey> { ["save"] = Hk("Alt+F8") });
        Assert.True(w.LookOnce() && fired.Count == 0, "held as watching starts: no save");
        down.Remove((int)Keys.F8);
        w.LookOnce();
        down.Add((int)Keys.F8);
        w.LookOnce();
        Assert.True(fired.SequenceEqual(["save"]), "pressed anew: save");
        w.Suspend();
        down.Remove((int)Keys.F8);
        Assert.True(!w.LookOnce(), "a key typed into a hotkey box: no look");
        down.Add((int)Keys.F8);
        w.Resume();
        w.LookOnce();
        Assert.True(fired.Count == 1, "the key typed into the box, still held as watching resumes: no save");
    }

    /// <summary>
    /// A press Windows and the looks both hear (a normal game) runs once, whichever hears it first; a press only one way
    /// hears (a game run as administrator: Windows only; a game that turns Windows' hotkeys off: the looks only) runs
    /// every time, two quick presses too.
    /// </summary>
    [Test]
    public static void A_press_heard_both_ways_runs_once()
    {
        var fired = new List<string>();
        using var w = new HotkeyWatcher(action => action(), _ => 0, start: false);
        w.Pressed += (id, _) => fired.Add(id);
        w.Set(new Dictionary<string, App.Hotkey> { ["save"] = Hk("Alt+F8"), ["screenshot"] = Hk("Alt+F12") });
        w.Heard("save", 1000, byWindows: true);
        w.Heard("save", 1020, byWindows: false);
        Assert.True(fired.SequenceEqual(["save"]), "Windows first, then the looks: one save");
        w.Heard("save", 1320, byWindows: false);
        w.Heard("save", 1300, byWindows: true);
        Assert.True(fired.SequenceEqual(["save", "save"]), "the next press, the looks first: one more save");
        w.Heard("screenshot", 1301, byWindows: false);
        Assert.True(fired.Count == 3, "another hotkey at the same moment is its own press");

        fired.Clear();
        w.Heard("save", 5000, byWindows: true);
        w.Heard("save", 5150, byWindows: true);
        Assert.True(fired.Count == 2, "a game run as administrator (Windows only): both quick presses save");
        w.Heard("save", 6000, byWindows: false);
        w.Heard("save", 6150, byWindows: false);
        Assert.True(fired.Count == 4, "a game that turns Windows' hotkeys off (the looks only): both quick presses save");

        fired.Clear();
        w.Heard("save", 7010, byWindows: false);
        w.Heard("save", 7160, byWindows: false);
        w.Heard("save", 7000, byWindows: true);
        w.Heard("save", 7150, byWindows: true);
        Assert.True(fired.Count == 2, "two quick presses, Windows' heard late (Kairos busy): two saves, each paired with its own");

        fired.Clear();
        w.Heard("save", int.MaxValue - 10, byWindows: true);
        w.Heard("save", int.MinValue + 5, byWindows: false);
        Assert.True(fired.Count == 1, "the same press as Windows' clock wraps (every 49 days): one save");
    }

    /// <summary>A press Windows heard is not run again by the look that sees its keys down a moment later.</summary>
    [Test]
    public static void A_look_after_windows_heard_the_press_does_not_run_it_again()
    {
        var down = new HashSet<int>();
        var fired = new List<string>();
        using var w = new HotkeyWatcher(action => action(), vk => (short)(down.Contains(vk) ? 0x8000 : 0), start: false);
        w.Pressed += (id, _) => fired.Add(id);
        w.Set(new Dictionary<string, App.Hotkey> { ["save"] = Hk("Alt+F8") });
        w.LookOnce();
        down.UnionWith([(int)Keys.Menu, (int)Keys.F8]);
        w.Heard("save", Environment.TickCount, byWindows: true);
        w.LookOnce();
        Assert.True(fired.SequenceEqual(["save"]), "one save");
    }

    /// <summary>The mouse buttons are for the looks only: Windows' own hotkeys take keyboard keys.</summary>
    [Test]
    public static void Mouse_buttons_are_told_apart_from_keys()
    {
        Assert.True(HotkeyParser.IsMouse(Keys.XButton1) && HotkeyParser.IsMouse(Keys.XButton2) && HotkeyParser.IsMouse(Keys.MButton), "the middle and side buttons");
        Assert.True(!HotkeyParser.IsMouse(Keys.F8) && !HotkeyParser.IsMouse(Keys.F13), "keys");
    }

    /// <summary>
    /// Keys held as the watcher starts again (a key just typed into a hotkey box, still down) do not fire: only a new
    /// press does.
    /// </summary>
    [Test]
    public static void Keys_held_as_watching_starts_do_not_fire()
    {
        var m = Matcher(("save", "Alt+F8"));
        m.Prime(Held(Keys.Menu, Keys.F8));
        Assert.True(m.Look(Held(Keys.Menu, Keys.F8)).Count == 0, "still held from the box");
        Assert.True(m.Look(Held()).Count == 0 && m.Look(Held(Keys.Menu, Keys.F8)).SequenceEqual(["save"]), "let go and pressed: saves");
    }

    /// <summary>Only the keys the hotkeys use are looked at, with the modifiers: a key costs one Windows call a look.</summary>
    [Test]
    public static void Only_the_hotkeys_keys_are_looked_at()
    {
        var m = Matcher(("save", "Alt+F8"), ("shot", "Mouse4"));
        Assert.True(m.Watched.ToHashSet().SetEquals([Keys.F8, Keys.XButton1]), "the hotkeys' own two keys (and the modifiers)");
        Assert.True(new HotkeyMatcher().Watched.Length == 0, "no hotkeys: nothing at all");
    }

    /// <summary>
    /// New installs take keys NVIDIA and OBS do not use (a key another app holds still works in Kairos by its looks, so a
    /// shared key would make both save): Alt+F8 and its neighbours.
    /// </summary>
    [Test]
    public static void The_default_keys_are_none_of_nvidias_or_obss()
    {
        var keys = new Kairos.Settings.HotkeySettings();
        Assert.Equal("Alt+F8 | Alt+F6 | Alt+F7 | Alt+F12", $"{keys.Save} | {keys.ToggleBuffer} | {keys.Record} | {keys.Screenshot}", "the defaults");
    }

    [Test]
    public static void Kairos_own_window_is_told_apart_from_a_game()
    {
        Assert.True(ForegroundApp.IsSameApp(@"D:\Kairos\bin\64bit\Kairos.exe", @"d:\kairos\BIN\64bit\kairos.exe"), "the settings window: same exe");
        Assert.True(!ForegroundApp.IsSameApp(@"C:\Games\Game.exe", @"D:\Kairos\bin\64bit\Kairos.exe"), "a game");
        Assert.True(!ForegroundApp.IsSameApp(null, @"D:\Kairos\bin\64bit\Kairos.exe"), "unknown");
    }
}
