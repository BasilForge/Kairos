using Kairos.App;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>Options that said the same thing twice, or did nothing where they showed.</summary>
public static class OptionConflictTests
{
    static SettingsViewModel Vm(Action<AppSettings> change)
    {
        Text.Language = "en";
        var s = new AppSettings();
        change(s);
        s.Normalize();
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        return vm;
    }

    [Test]
    public static void Only_while_gaming_waits_for_a_game_in_full_screen_only()
    {
        foreach (var mode in new[] { CaptureMode.Screen, CaptureMode.Window, CaptureMode.Game })
        {
            var s = new AppSettings();
            s.Capture.Mode = mode;
            s.General.OnlyWhenGaming = true;
            Assert.Equal(mode == CaptureMode.Screen, Kairos.Engine.SceneRules.PausesWithoutGame(s), $"{mode}");
        }
    }

    /// <summary>
    /// Games with "Show the screen when no game runs" off records games only: with no game, nothing (no black picture
    /// with the mic and a browser's sound, and no clip of it).
    /// </summary>
    [Test]
    public static void Games_without_the_screen_waits_for_a_game()
    {
        var s = new AppSettings();
        s.Capture.Mode = CaptureMode.Game;
        s.Capture.GameFallbackToScreen = false;
        Assert.True(Kairos.Engine.SceneRules.PausesWithoutGame(s), "games only: the replay waits for one");
        s.Capture.GameFallbackToScreen = true;
        Assert.True(!Kairos.Engine.SceneRules.PausesWithoutGame(s), "the screen when no game runs: it keeps recording");
        s.Capture.Mode = CaptureMode.Window;
        s.Capture.GameFallbackToScreen = false;
        Assert.True(!Kairos.Engine.SceneRules.PausesWithoutGame(s), "the switch belongs to Games only");
    }

    [Test]
    public static void A_quick_clip_without_a_hotkey_says_so()
    {
        Assert.True(Vm(s => s.Replay.QuickEnabled = true).QuickNeedsKey, "on, no key");
        Assert.True(!Vm(s => { s.Replay.QuickEnabled = true; s.Hotkeys.Quick = "Alt+F6"; }).QuickNeedsKey, "on, with a key");
        Assert.True(!Vm(_ => { }).QuickNeedsKey, "off");
        string? page = null;
        var vm = Vm(s => s.Replay.QuickEnabled = true);
        vm.NavigateRequested += p => page = p;
        vm.ShowHotkeysCommand.Execute(null);
        Assert.Equal("hotkeys", page, "the link opens Hotkeys");
    }

    [Test]
    public static void An_app_with_two_windows_is_offered_once()
    {
        var vm = Vm(s => s.Capture.Mode = CaptureMode.Window);
        vm.Seed(null, new CapsDto([], [new("[Spotify.exe]: Spotify", "Spotify:Chrome_WidgetWin_1:Spotify.exe"),
            new("[Spotify.exe]: Spotify", "Spotify:Chrome_WidgetWin_0:Spotify.exe"), new("[notepad.exe]: Notes", "Notes:Notepad:notepad.exe")], [], [], [], "32.2.2"));
        Assert.Equal(2, vm.AddableApps.Count, "Spotify once, Notepad once");
    }

    [Test]
    public static void A_latest_clip_that_is_gone_is_not_named()
    {
        var vm = Vm(_ => { });
        vm.Seed(new StatusDto(null, [], Path.Combine(Path.GetTempPath(), "Kairos gone", "Desktop 2026-10-20 10-00-00.mp4"), null, false, false, ""), null);
        Assert.Equal(Text.Get("status.noClip"), vm.LastClipName, "named as no clip");
    }

    [Test]
    public static void An_edited_copy_edited_again_keeps_one_edited_mark()
    {
        Text.Language = "en";
        string once = Kairos.Ui.Edit.EditJobs.NewCopyPath(@"D:\Clips\Desktop 2026-10-20 12-46-44.mp4");
        Assert.Equal(@"D:\Clips\Desktop 2026-10-20 12-46-44 (edited).mp4", once, "first edit");
        Assert.Equal(once, Kairos.Ui.Edit.EditJobs.NewCopyPath(once), "second edit: the same name (the save numbers it)");
        Assert.Equal(once, Kairos.Ui.Edit.EditJobs.NewCopyPath(@"D:\Clips\Desktop 2026-10-20 12-46-44 (edited) (2).mp4"), "a numbered copy");
        Assert.Equal(once, Kairos.Ui.Edit.EditJobs.NewCopyPath(@"D:\Clips\Desktop 2026-10-20 12-46-44 (معدّل).mp4"), "a copy made in Arabic");
    }

    [Test]
    public static void A_cloud_whose_app_is_closed_is_named()
    {
        Text.Language = "en";
        Assert.True(!CloudFolders.ClientRunning(CloudKind.OneDrive, _ => false), "closed");
        Assert.True(CloudFolders.ClientRunning(CloudKind.GoogleDrive, name => name == "GoogleDriveFS"), "Google Drive for desktop's process");
        Assert.Equal("OneDrive isn't running, so clips won't upload", Text.Format("general.cloudOff", "OneDrive"), "the line");
    }
}
