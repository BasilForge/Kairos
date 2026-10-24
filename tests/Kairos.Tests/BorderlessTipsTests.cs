using Kairos.App;

namespace Kairos.Tests;

/// <summary>
/// A game Kairos cannot capture from inside (run as administrator) is recorded from the screen, which a game in exclusive
/// full screen leaves black: the user is told once per game to set it to Borderless.
/// </summary>
public static class BorderlessTipsTests
{
    [Test]
    public static void A_game_gets_the_tip_once_ever()
    {
        var tipped = new List<string>();
        var tips = new BorderlessTips(() => tipped);
        var shown = new List<string>();
        tips.Seen("Client-Win64-Shipping", "Wuthering Waves");
        Assert.True(tips.TryShow(name => { shown.Add(name); return true; }), "shown");
        Assert.True(shown.SequenceEqual(["Wuthering Waves"]) && tipped.SequenceEqual(["Client-Win64-Shipping"]), "by its window's name, kept by its exe");
        tips.Seen("client-win64-shipping", "Wuthering Waves");
        Assert.True(!tips.TryShow(_ => throw new InvalidOperationException("shown twice")), "the same game again: nothing");
        var next = new BorderlessTips(() => tipped);
        next.Seen("Client-Win64-Shipping", "Wuthering Waves");
        Assert.True(!next.TryShow(_ => throw new InvalidOperationException("shown after a restart")), "kept across starts");
    }

    [Test]
    public static void A_tip_windows_will_not_show_over_the_game_waits_for_it_to_be_left()
    {
        var tipped = new List<string>();
        var tips = new BorderlessTips(() => tipped);
        tips.Seen("Arknights", "Arknights: Endfield");
        Assert.True(!tips.TryShow(_ => false), "exclusive full screen: Windows shows no card");
        Assert.True(tipped.Count == 0 && tips.Pending, "kept for later, not counted as told");
        Assert.True(tips.TryShow(_ => true) && !tips.Pending && tipped.SequenceEqual(["Arknights"]), "the game left: shown");
    }

    [Test]
    public static void Nothing_seen_shows_nothing()
    {
        var tips = new BorderlessTips(() => []);
        Assert.True(!tips.Pending && !tips.TryShow(_ => throw new InvalidOperationException("nothing to show")), "no game: no card");
        tips.Seen("Game", "");
        Assert.True(tips.TryShow(name => name == "Game"), "a window with no title: its exe's name");
    }
}
