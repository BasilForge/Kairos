using Kairos.App;

namespace Kairos.Tests;

/// <summary>
/// A game Kairos cannot capture from inside (run as administrator, or guarded by its anti-cheat) is recorded from the
/// screen, which a game in exclusive full screen can leave black (some recorders stop recording there). The user is
/// told to set it to Borderless when its picture records black, once a game each run: a game that records is never told.
/// </summary>
public static class BorderlessTipsTests
{
    [Test]
    public static void A_game_recorded_black_gets_the_tip_once_a_run()
    {
        var tips = new BorderlessTips();
        var shown = new List<string>();
        tips.SawBlack("Client-Win64-Shipping", "Wuthering Waves");
        Assert.True(tips.TryShow(name => { shown.Add(name); return true; }), "shown");
        Assert.True(shown.SequenceEqual(["Wuthering Waves"]), "by its window's name");
        tips.SawBlack("client-win64-shipping", "Wuthering Waves");
        Assert.True(!tips.TryShow(_ => throw new InvalidOperationException("shown twice")), "the same game again this run: nothing");
        var next = new BorderlessTips();
        next.SawBlack("Client-Win64-Shipping", "Wuthering Waves");
        Assert.True(next.TryShow(_ => true), "still black at the next start: its clips are still black, told again");
    }

    [Test]
    public static void A_tip_windows_will_not_show_over_the_game_waits_for_it_to_be_left()
    {
        var tips = new BorderlessTips();
        tips.SawBlack("Arknights", "Arknights: Endfield");
        Assert.True(!tips.TryShow(_ => false), "exclusive full screen: Windows shows no card");
        Assert.True(tips.Pending, "kept for later, not counted as told");
        Assert.True(tips.TryShow(_ => true) && !tips.Pending, "the game left: shown");
    }

    [Test]
    public static void Nothing_seen_shows_nothing()
    {
        var tips = new BorderlessTips();
        Assert.True(!tips.Pending && !tips.TryShow(_ => throw new InvalidOperationException("nothing to show")), "no game: no card");
        tips.SawBlack("Game", "");
        Assert.True(tips.TryShow(name => name == "Game"), "a window with no title: its exe's name");
    }
}
