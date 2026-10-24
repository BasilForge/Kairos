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

    [Test]
    public static void Combo_taken_by_another_owner_reports_failure()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Shift+F23", out var h), "should parse");
        using var first = new HotkeyManager();
        using var second = new HotkeyManager();
        Assert.True(first.Register("save", h), "first owner registers");
        Assert.True(!second.Register("save", h), "second owner must fail");
        Assert.Equal(false, second.Status["save"], "status records failure");
        Assert.Equal(true, first.Status["save"], "first status ok");
    }

    [Test]
    public static void A_taken_key_is_picked_up_once_the_other_app_lets_go()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Shift+F22", out var h), "should parse");
        using var kairos = new HotkeyManager();
        var other = new HotkeyManager();
        Assert.True(other.Register("x", h), "the other app holds it");
        Assert.True(!kairos.Register("save", h), "taken");
        kairos.RetryPending();
        Assert.Equal(false, kairos.Status["save"], "still taken");
        other.Dispose();
        kairos.RetryPending();
        Assert.Equal(true, kairos.Status["save"], "free now: Kairos takes it");
    }

    [Test]
    public static void A_key_that_was_let_go_is_not_retried()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Shift+F21", out var h), "should parse");
        using var kairos = new HotkeyManager();
        var other = new HotkeyManager();
        Assert.True(other.Register("x", h), "the other app holds it");
        Assert.True(!kairos.Register("save", h), "taken");
        kairos.UnregisterAll(); // the user picked other keys
        other.Dispose();
        kairos.RetryPending();
        Assert.True(!kairos.Status.ContainsKey("save"), "the old key stays let go");
    }

    /// <summary>
    /// A user's mouse button sent F17: Windows fires a key only with exactly its modifiers, so it never saved while the
    /// game's run (Shift) or crouch (Ctrl) key was held. F13-F24 now fire with them held; other keys keep only theirs.
    /// </summary>
    [Test]
    public static void Side_button_keys_fire_with_shift_or_ctrl_held()
    {
        static Hotkey Key(string s) => HotkeyParser.TryParse(s, out var h) ? h : throw new ArgumentException(s);
        static string Held(string s) => string.Join("|", HotkeyParser.WithHeldKeys(Key(s)).Select(HotkeyParser.Format));
        Assert.Equal("Shift+F17|Ctrl+F17|Ctrl+Shift+F17", Held("F17"), "F17 with Shift, Ctrl or both");
        Assert.Equal("Ctrl+Shift+F20", Held("Shift+F20"), "it has Shift: Ctrl added");
        Assert.Equal("", Held("Alt+F10"), "never other keys (Shift+F10 opens menus, Ctrl+F4 closes tabs)");
        Assert.Equal("", Held("F10"), "nor F1-F12");
        using var kairos = new HotkeyManager();
        Assert.True(kairos.Register("save", Key("F24")), "F24 taken");
        Assert.True(!HotkeyManager.IsFree(Key("Shift+F24")) && !HotkeyManager.IsFree(Key("Ctrl+F24")) && !HotkeyManager.IsFree(Key("Ctrl+Shift+F24")),
            "and with Shift, Ctrl or both held");
        Assert.True(kairos.Register("toggle", Key("Shift+F24")) && kairos.Status["toggle"], "a later Kairos key on Shift+F24: the variant gives way");
        kairos.UnregisterAll();
        Assert.True(HotkeyManager.IsFree(Key("F24")) && HotkeyManager.IsFree(Key("Shift+F24")) && HotkeyManager.IsFree(Key("Ctrl+F24")), "all let go");
    }

    [Test]
    public static void A_key_is_free_only_when_no_app_holds_it()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Shift+F20", out var h), "should parse");
        Assert.True(HotkeyManager.IsFree(h), "nobody has it");
        var other = new HotkeyManager();
        other.Register("x", h);
        Assert.True(!HotkeyManager.IsFree(h), "another app holds it");
        other.Dispose();
        Assert.True(HotkeyManager.IsFree(h), "and checking took nothing: still free");
    }

    [Test]
    public static void Typing_a_key_releases_kairos_keys_until_done()
    {
        // While a hotkey box has focus, Kairos's own keys reach the box (and are not run), and the free check is accurate.
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Shift+F19", out var h), "should parse");
        using var kairos = new HotkeyManager();
        Assert.True(kairos.Register("save", h), "registered");
        Assert.True(!HotkeyManager.IsFree(h), "Kairos holds it");
        kairos.Suspend();
        Assert.True(kairos.Suspended && HotkeyManager.IsFree(h), "typing: let go");
        kairos.Resume();
        Assert.True(!kairos.Suspended && !HotkeyManager.IsFree(h), "done: Kairos's again");
        Assert.Equal(true, kairos.Status["save"], "status");
    }

    [Test]
    public static void A_key_saved_while_typing_is_taken_when_typing_ends()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Alt+Shift+F18", out var h), "should parse");
        using var kairos = new HotkeyManager();
        kairos.Suspend();
        kairos.Register("save", h); // settings applied while the box still had focus
        Assert.True(HotkeyManager.IsFree(h), "not while typing");
        kairos.Resume();
        Assert.True(!HotkeyManager.IsFree(h), "then Kairos's");
    }

    [Test]
    public static void A_taken_key_is_told_once_ever()
    {
        string path = Path.Combine(Path.GetTempPath(), "kairos-taken-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var notice = new TakenKeyNotice(path);
            Assert.True(notice.ShouldTell("Alt+F10"), "the first time");
            Assert.True(!notice.ShouldTell("Alt+F10"), "not again");
            Assert.True(!new TakenKeyNotice(path).ShouldTell("Alt+F10"), "nor after a restart");
            Assert.True(notice.ShouldTell("Alt+F9"), "another key: once too");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public static void The_taken_message_names_one_key_or_counts_several()
    {
        Text.Language = "en";
        Assert.Equal(("Alt+F10 doesn't work", "Another app has it. Change it in Settings"), EngineApp.TakenToast(["Alt+F10"]), "one");
        Assert.Equal(("3 hotkeys don't work", "Alt+F10, Alt+Shift+F10, Alt+F9"), EngineApp.TakenToast(["Alt+F10", "Alt+Shift+F10", "Alt+F9"]), "three");
        Text.Language = "ar";
        Assert.Equal("تعذّر تشغيل 3 اختصارات", EngineApp.TakenToast(["Alt+F10", "Alt+Shift+F10", "Alt+F9"]).Title, "arabic");
        Assert.Equal("تعذّر تشغيل اختصارين", EngineApp.TakenToast(["Alt+F10", "Alt+F9"]).Title, "arabic, two");
        Text.Language = "en";
    }

    static Func<App.Hotkey, bool> FreeExcept(params string[] taken)
    {
        var set = taken.Select(Hk).ToHashSet();
        return h => !set.Contains(h);
    }

    static string KeysOf(Kairos.Settings.HotkeySettings k) => $"{k.Save} | {k.ToggleBuffer} | {k.Record} | {k.PauseRecord} | {k.Screenshot}";

    [Test]
    public static void A_first_install_keeps_the_usual_keys_when_they_are_free()
    {
        var keys = new Kairos.Settings.HotkeySettings();
        Assert.True(!HotkeyDefaults.PickFree(keys, _ => true), "nothing changed");
        Assert.Equal("Alt+F10 | Alt+Shift+F10 | Alt+F9 | Alt+Shift+F9 | Alt+F12", KeysOf(keys), "the usual keys");
    }

    [Test]
    public static void A_first_install_next_to_nvidia_gets_free_keys()
    {
        var keys = new Kairos.Settings.HotkeySettings();
        Assert.True(HotkeyDefaults.PickFree(keys, FreeExcept("Alt+F10", "Alt+Shift+F10", "Alt+F9")), "changed");
        Assert.Equal("Alt+F8 | Alt+F6 | Alt+F7 | Alt+Shift+F9 | Alt+F12", KeysOf(keys), "the next free ones");
    }

    [Test]
    public static void A_first_install_tries_the_second_choice_too()
    {
        var keys = new Kairos.Settings.HotkeySettings();
        HotkeyDefaults.PickFree(keys, FreeExcept("Alt+F10", "Alt+F8"));
        Assert.Equal("Ctrl+F8", keys.Save, "third in the list");
        var none = new Kairos.Settings.HotkeySettings();
        HotkeyDefaults.PickFree(none, FreeExcept("Alt+F12", "Ctrl+F12", "Ctrl+F3"));
        Assert.Equal("Alt+F12", none.Screenshot, "all taken: the usual key stays (and the one-time message says so)");
    }

    [Test]
    public static void The_choices_start_with_the_defaults_and_never_repeat()
    {
        var d = new Kairos.Settings.HotkeySettings();
        Assert.Equal($"{d.Save} | {d.ToggleBuffer} | {d.Record} | {d.PauseRecord ?? HotkeyParser.WithShift(d.Record)} | {d.Screenshot}",
            string.Join(" | ", HotkeyDefaults.Choices.Values.Select(c => c[0])), "the first choice is the default");
        var all = HotkeyDefaults.Choices.Values.SelectMany(c => c).ToList();
        Assert.Equal(all.Count, all.Distinct().Count(), "no key is a choice for two actions");
        Assert.True(all.All(k => HotkeyParser.TryParse(k, out _)), "all valid");
    }

    [Test]
    public static void Kairos_own_window_is_told_apart_from_a_game()
    {
        Assert.True(ForegroundApp.IsSameApp(@"D:\Kairos\bin\64bit\Kairos.exe", @"d:\kairos\BIN\64bit\kairos.exe"), "the settings window: same exe");
        Assert.True(!ForegroundApp.IsSameApp(@"C:\Games\Game.exe", @"D:\Kairos\bin\64bit\Kairos.exe"), "a game");
        Assert.True(!ForegroundApp.IsSameApp(null, @"D:\Kairos\bin\64bit\Kairos.exe"), "unknown");
    }
}
