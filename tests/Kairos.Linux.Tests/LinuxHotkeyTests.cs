using Kairos.App;

namespace Kairos.Tests;

/// <summary>
/// The HotkeyManager tests of the Windows suite, on keys a PC keyboard has (Ctrl+Alt+Shift with F1-F12). They need an X
/// server: without DISPLAY they pass trivially, as there is nothing to grab on.
/// </summary>
public static class LinuxHotkeyTests
{
    static bool NoX => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    static Hotkey Hk(string s)
    {
        Assert.True(HotkeyParser.TryParse(s, out var h), $"{s} should parse");
        return h;
    }

    [Test]
    public static void Combo_taken_by_another_owner_reports_failure()
    {
        if (NoX) return;
        var h = Hk("Ctrl+Alt+Shift+F12");
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
        if (NoX) return;
        var h = Hk("Ctrl+Alt+Shift+F11");
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
        if (NoX) return;
        var h = Hk("Ctrl+Alt+Shift+F10");
        using var kairos = new HotkeyManager();
        var other = new HotkeyManager();
        Assert.True(other.Register("x", h), "the other app holds it");
        Assert.True(!kairos.Register("save", h), "taken");
        kairos.UnregisterAll(); // the user picked other keys
        other.Dispose();
        kairos.RetryPending();
        Assert.True(!kairos.Status.ContainsKey("save"), "the old key stays let go");
    }

    [Test]
    public static void A_key_is_free_only_when_no_app_holds_it()
    {
        if (NoX) return;
        var h = Hk("Ctrl+Alt+Shift+F9");
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
        if (NoX) return;
        var h = Hk("Ctrl+Alt+Shift+F8");
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
        if (NoX) return;
        var h = Hk("Ctrl+Alt+Shift+F7");
        using var kairos = new HotkeyManager();
        kairos.Suspend();
        kairos.Register("save", h); // settings applied while the box still had focus
        Assert.True(HotkeyManager.IsFree(h), "not while typing");
        kairos.Resume();
        Assert.True(!HotkeyManager.IsFree(h), "then Kairos's");
    }

    [Test]
    public static void The_pressed_key_reaches_the_manager()
    {
        if (NoX || !File.Exists("/usr/bin/xdotool")) return;
        var h = Hk("Ctrl+Alt+Shift+Z");
        using var kairos = new HotkeyManager();
        var pressed = new ManualResetEventSlim();
        kairos.Pressed += id => { if (id == "save") pressed.Set(); };
        Assert.True(kairos.Register("save", h), "registered");
        System.Diagnostics.Process.Start("xdotool", "key ctrl+alt+shift+z")?.WaitForExit();
        Assert.True(pressed.Wait(3000), "the grab delivered the key");
    }
}
