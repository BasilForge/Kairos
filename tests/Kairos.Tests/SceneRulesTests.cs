using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>What the canvas shows when the chosen capture has no picture: the screen, never black.</summary>
public static class SceneRulesTests
{
    [Test]
    public static void Window_mode_without_a_window_records_the_screen()
    {
        Assert.Equal(CaptureMode.Screen, SceneRules.BuildMode(new CaptureSettings { Mode = CaptureMode.Window, Apps = [] }), "empty");
        Assert.Equal(CaptureMode.Screen, SceneRules.BuildMode(new CaptureSettings { Mode = CaptureMode.Window, Apps = ["  "] }), "blank");
        Assert.Equal(CaptureMode.Window, SceneRules.BuildMode(new CaptureSettings { Mode = CaptureMode.Window, Apps = ["VALORANT:UnrealWindow:VALORANT-Win64-Shipping.exe"] }), "chosen");
        Assert.Equal(CaptureMode.Game, SceneRules.BuildMode(new CaptureSettings { Mode = CaptureMode.Game }), "games");
    }

    [Test]
    public static void A_window_that_is_gone_shows_the_screen_after_two_checks()
    {
        var w = new WindowPresence();
        Assert.True(!w.Update(false) && !w.ShowScreen, "one miss: a window still starting");
        Assert.True(w.Update(false) && w.ShowScreen, "two misses: the screen");
        Assert.True(!w.Update(false) && w.ShowScreen, "still gone: no change");
    }

    [Test]
    public static void A_window_that_comes_back_hides_the_screen_at_once()
    {
        var w = new WindowPresence();
        w.Update(false);
        w.Update(false);
        Assert.True(w.Update(true) && !w.ShowScreen, "a picture hides the screen on the first check");
    }

    [Test]
    public static void A_window_missing_at_the_start_shows_the_screen_at_once()
    {
        var w = new WindowPresence(showScreen: true);
        Assert.True(w.ShowScreen, "closed when recording starts: the screen from the first frame");
        Assert.True(w.Update(true) && !w.ShowScreen, "the app opens: its window at once");
    }

    [Test]
    public static void A_window_counts_the_way_obs_finds_it()
    {
        // OBS's window capture (priority 2) goes by the exe: a visible top-level window that is not a tool window. A
        // minimized window counts: it keeps its last picture (recording the desktop instead would show other windows).
        Assert.True(WindowTarget.Counts(visible: true, style: 0, exStyle: 0), "an app window");
        Assert.True(WindowTarget.Counts(visible: true, style: 0x20000000 /* WS_MINIMIZE */, exStyle: 0), "minimized: still the app's");
        Assert.True(!WindowTarget.Counts(visible: false, style: 0, exStyle: 0), "hidden");
        Assert.True(!WindowTarget.Counts(visible: true, style: 0, exStyle: 0x80 /* WS_EX_TOOLWINDOW */), "a tool window");
        Assert.True(!WindowTarget.Counts(visible: true, style: 0x40000000 /* WS_CHILD */, exStyle: 0), "a child window");
        Assert.True(!WindowTarget.IsOpen("Kairos selftest:none:kairos-missing.exe"), "an app that isn't running");
        Assert.True(!WindowTarget.IsOpen(""), "no app chosen");
    }

    [Test]
    public static void Clips_go_under_the_chosen_app_only_while_it_is_recorded()
    {
        var c = new CaptureSettings { Mode = CaptureMode.Window, Apps = ["VALORANT:UnrealWindow:VALORANT-Win64-Shipping.exe"] };
        Assert.True(SceneRules.RecordsChosenWindow(c, captureNotice: null), "its window is recorded");
        Assert.True(!SceneRules.RecordsChosenWindow(c, "status.captureGone"), "closed: the screen is recorded, filed like it");
        Assert.True(!SceneRules.RecordsChosenWindow(new CaptureSettings { Mode = CaptureMode.Screen }, null), "full screen");
        Assert.True(!SceneRules.RecordsChosenWindow(new CaptureSettings { Mode = CaptureMode.Window, Apps = [] }, null), "no app chosen");
    }

    [Test]
    public static void Vanguard_or_its_games_turn_the_guard_on()
    {
        Assert.True(AntiCheatGuard.IsActive(["explorer", "vgc", "RiotClientServices"]), "Vanguard while the Riot Client starts a game");
        Assert.True(!AntiCheatGuard.IsActive(["explorer", "vgc", "r5apex"]), "Vanguard left running after the Riot game closed: other games are hooked");
        Assert.True(!AntiCheatGuard.IsActive(["RiotClientServices"]), "the Riot Client without Vanguard");
        Assert.True(AntiCheatGuard.IsActive(["valorant-win64-shipping"]), "VALORANT, any case");
        Assert.True(AntiCheatGuard.IsActive(["League of Legends"]), "League");
        Assert.True(!AntiCheatGuard.IsActive(["explorer", "eldenring", "cs2"]), "other games are hooked as before");
        Assert.True(!AntiCheatGuard.IsActive([]), "nothing running");
    }

    [Test]
    public static void Each_protected_game_has_its_own_sound_without_a_hook()
    {
        // "Game sound only" in Games mode: while the guard keeps the hook out, the game's sound comes from its process.
        Assert.Equal(AntiCheatGuard.Games.Length, AntiCheatGuard.GameSoundWindows.Length, "one per protected game");
        foreach (string game in AntiCheatGuard.Games)
            Assert.True(AntiCheatGuard.GameSoundWindows.Any(w => WindowTarget.ExeName(w) == game), $"{game} has a window string");
    }

    /// <summary>
    /// Games run as administrator (or guarded by their anti-cheat) cannot be opened, so not hooked: libobs tried every
    /// 2 s for ever (a user's log, 6,000 lines). Kairos tells them apart first and leaves them to the screen.
    /// </summary>
    [Test]
    public static void Games_kairos_cannot_open_are_told_apart()
    {
        Assert.True(ProcessList.CanBeHooked((uint)Environment.ProcessId), "a process of the user's own");
        Assert.True(!ProcessList.CanBeHooked(4), "the System process: no rights, as a game run as administrator");
        Assert.Equal(ProcessList.SelfName, ProcessList.NameOf((uint)Environment.ProcessId), "its name from the snapshot");
        Assert.Equal(null, ProcessList.NameOf(uint.MaxValue - 2), "none: null");
    }

    [Test]
    public static void The_running_list_has_this_process()
    {
        string self = Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        Assert.True(AntiCheatGuard.AnyRunning(name => name.Equals(self, StringComparison.OrdinalIgnoreCase)), "the test runner itself");
        Assert.True(!AntiCheatGuard.AnyRunning(name => name == "kairos-no-such-process"), "a name nothing has");
    }

    [Test]
    public static void A_screen_pipeline_has_no_capture_notice()
    {
        // Not built (no libobs here) and so not disposed: a new pipeline starts with nothing to say.
        var pipeline = new CapturePipeline(new AppSettings());
        Assert.Equal(null, pipeline.CaptureNotice, "nothing to say about the full screen");
    }

    /// <summary>
    /// A fullscreen game over the screen (or the chosen app's own window): the layer takes the game's window when it fills
    /// the screen, shows once it has a picture, hides on alt-tab, and hooks again when no picture comes for 8 s.
    /// </summary>
    [Test]
    public static void The_game_layer_follows_the_fullscreen_window()
    {
        var t0 = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);
        var layer = new GameOverlay();
        Assert.Equal((null, false), layer.Update(null, hasPicture: false, t0), "no game: nothing hooked, nothing shown");
        Assert.Equal(("VALORANT:UnrealWindow:VALORANT-Win64-Shipping.exe", false),
            layer.Update("VALORANT:UnrealWindow:VALORANT-Win64-Shipping.exe", false, t0), "a game fills the screen: it is targeted");
        Assert.Equal((null, true), layer.Update("VALORANT:UnrealWindow:VALORANT-Win64-Shipping.exe", true, t0.AddSeconds(1)), "its picture shows");
        Assert.Equal((null, false), layer.Update(null, true, t0.AddSeconds(2)), "alt-tab: the desktop shows at once, the target is kept");
        Assert.Equal((null, true), layer.Update("VALORANT:UnrealWindow:VALORANT-Win64-Shipping.exe", true, t0.AddSeconds(3)), "back in the game");
        Assert.Equal(("Other:Cls:other.exe", false), layer.Update("Other:Cls:other.exe", true, t0.AddSeconds(4)), "another game: targeted anew");
        Assert.Equal((null, false), layer.Update("Other:Cls:other.exe", false, t0.AddSeconds(8)), "waiting for its picture");
        Assert.Equal(("Other:Cls:other.exe", false), layer.Update("Other:Cls:other.exe", false, t0.AddSeconds(13)), "8 s without a picture: hooked again");
        Assert.Equal((null, false), layer.Update("Other:Cls:other.exe", false, t0.AddSeconds(14)), "and waits again");
    }

    /// <summary>
    /// A user's RTX 3060 Ti in ARC Raiders: the screen was copied under the hooked game, every picture of the game taken
    /// twice, and the encoder skipped 76-99 % of them (a frozen picture with sound). The screen is copied only while no
    /// game gives its picture: what OBS does with a game capture alone.
    /// </summary>
    [Test]
    public static void The_screen_under_a_game_is_copied_only_while_the_game_gives_no_picture()
    {
        Assert.True(!SceneRules.ScreenUnderGame(CaptureMode.Screen, gamePicture: true, gamesFallBack: false), "Screen, the game shows: not copied");
        Assert.True(SceneRules.ScreenUnderGame(CaptureMode.Screen, gamePicture: false, gamesFallBack: false), "Screen, no game: the screen");
        Assert.True(!SceneRules.ScreenUnderGame(CaptureMode.Game, gamePicture: true, gamesFallBack: true), "Games, hooked: not copied");
        Assert.True(SceneRules.ScreenUnderGame(CaptureMode.Game, gamePicture: false, gamesFallBack: true), "Games, no game: falls back to the screen");
        Assert.True(!SceneRules.ScreenUnderGame(CaptureMode.Game, gamePicture: false, gamesFallBack: false), "Games without the fallback: games only");
        Assert.True(!GameOverlay.MayHook("SnippingTool") && !GameOverlay.MayHook("snippingtool"), "the snipping tool's overlay is never hooked");
        Assert.True(GameOverlay.MayHook("PioneerGame"), "ARC Raiders is");
    }

    [Test]
    public static void A_window_string_is_written_the_way_libobs_reads_it()
    {
        Assert.Equal("VALORANT  #3A #22 1:UnrealWindow:VALORANT-Win64-Shipping.exe",
            Kairos.App.WindowTarget.Encode("VALORANT  : # 1", "UnrealWindow", "VALORANT-Win64-Shipping.exe"), "':' and '#' escaped");
        Assert.Equal("VALORANT-Win64-Shipping", Kairos.App.WindowTarget.ExeName(Kairos.App.WindowTarget.Encode("a:b", "c", "VALORANT-Win64-Shipping.exe")), "read back");
    }

    [Test]
    public static void A_game_whose_title_changes_is_not_hooked_again()
    {
        var t0 = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);
        var layer = new GameOverlay();
        Assert.Equal(("Game 60 fps:Cls:game.exe", false), layer.Update("Game 60 fps:Cls:game.exe", false, t0), "targeted");
        Assert.Equal((null, false), layer.Update("Game 59 fps:Cls:game.exe", false, t0.AddSeconds(1)), "its title changed: the same window, no new hook");
        Assert.Equal((null, true), layer.Update("Game 61 fps:Cls:game.exe", true, t0.AddSeconds(2)), "its picture shows");
        Assert.Equal(("Game:Other:other.exe", false), layer.Update("Game:Other:other.exe", true, t0.AddSeconds(3)), "another window: targeted anew");
    }

    /// <summary>
    /// A user's Call of Duty took the hook and dropped it, again and again: hooked anew every 8 s for four hours (1,900
    /// tries, a 3 MB log). With no picture the waits double, up to five minutes; a picture starts them over.
    /// </summary>
    [Test]
    public static void A_game_that_never_gives_a_picture_is_hooked_less_and_less_often()
    {
        var t0 = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        const string cod = "Call of Duty:Cls:cod.exe";
        var layer = new GameOverlay();
        var at = t0;
        Assert.Equal((cod, false), layer.Update(cod, false, at), "targeted");
        var waits = new List<double>();
        for (int s = 1; s <= 3600 && waits.Count < 8; s++)
            if (layer.Update(cod, false, t0.AddSeconds(s)).Retarget != null)
            {
                waits.Add((t0.AddSeconds(s) - at).TotalSeconds);
                at = t0.AddSeconds(s);
            }
        Assert.Equal("8,16,32,64,128,256,300,300", string.Join(",", waits), "the waits double up to five minutes");
        Assert.Equal((null, true), layer.Update(cod, true, at.AddSeconds(1)), "a picture at last");
        Assert.Equal((cod, false), layer.Update(cod, false, at.AddSeconds(10)), "lost again: 8 s again");
    }

    [Test]
    public static void Browsers_and_players_in_fullscreen_are_not_hooked_as_games()
    {
        Assert.True(!GameOverlay.MayHook("brave") && !GameOverlay.MayHook("chrome") && !GameOverlay.MayHook("msedge") && !GameOverlay.MayHook("firefox"),
            "a browser in F11: left to the screen (OBS leaves them too)");
        Assert.True(!GameOverlay.MayHook("vlc") && !GameOverlay.MayHook("explorer") && !GameOverlay.MayHook("Discord"), "players and the shell too");
        Assert.True(GameOverlay.MayHook("VALORANT-Win64-Shipping") && GameOverlay.MayHook("r5apex"), "games are hooked");
    }

    [Test]
    public static void The_chosen_app_in_front_is_recorded()
    {
        string[] apps = ["Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe", "Minecraft:GLFW30:javaw.exe"];
        var open = new HashSet<string> { "RobloxPlayerBeta", "javaw" };
        bool IsOpen(string w) => open.Contains(WindowTarget.ExeName(w)!);
        Assert.Equal(apps[1], AppTargets.Pick(apps, "javaw", IsOpen, apps[0]), "the one in front");
        Assert.Equal(apps[1], AppTargets.Pick(apps, "discord", IsOpen, apps[1]), "another app in front: the last one stays");
        open.Remove("javaw");
        Assert.Equal(apps[0], AppTargets.Pick(apps, "discord", IsOpen, apps[1]), "it closed: an open one");
        open.Clear();
        Assert.Equal(apps[1], AppTargets.Pick(apps, null, IsOpen, apps[1]), "none open: kept (the screen shows)");
        Assert.Equal(apps[0], AppTargets.Pick(apps, null, IsOpen, null), "none yet: the first");
        Assert.Equal(null, AppTargets.Pick([], "javaw", IsOpen, null), "no apps");
    }
}
