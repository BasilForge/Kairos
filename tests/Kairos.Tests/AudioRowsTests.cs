using Kairos.App;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>The Audio page's rows: one per sound source, each with its own track while one is left.</summary>
public static class AudioRowsTests
{
    static SettingsViewModel Vm(AudioLayout layout)
    {
        Text.Language = "en";
        var s = new AppSettings();
        s.Audio.Layout = layout;
        s.Normalize();
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        return vm;
    }

    [Test]
    public static void The_rows_follow_the_separate_layout()
    {
        var vm = Vm(AudioLayout.Separate);
        Assert.Equal("Game,Discord,Microphone,Other sounds", string.Join(",", vm.AudioRows.Select(r => r.Name)), "the built-in rows");
        Assert.True(vm.AudioRows.All(r => !r.Removable && r.OwnTrack), "built in: each on its own track, none removable");
        Assert.Equal(0, Vm(AudioLayout.Together).AudioRows.Count, "the together layout has no rows");
    }

    [Test]
    public static void An_added_app_has_its_row_and_can_go_again()
    {
        var vm = Vm(AudioLayout.Separate);
        vm.AddAudioApp(new NameValue("Spotify", "Spotify"));
        var spotify = vm.AudioRows[^1];
        Assert.True(spotify.Name == "Spotify" && spotify.Removable && spotify.OwnTrack, "its row, on its own track (one was left)");
        Assert.True(vm.IsDirty, "unsaved until Save");
        vm.RemoveAudioRow(spotify);
        Assert.Equal(4, vm.AudioRows.Count, "gone again");
    }

    [Test]
    public static void Own_track_is_greyed_when_no_track_is_left()
    {
        var vm = Vm(AudioLayout.Separate);
        vm.AddAudioApp(new NameValue("Spotify", "Spotify"));
        vm.AddAudioApp(new NameValue("Brave", "brave"));
        var spotify = vm.AudioRows.Single(r => r.Name == "Spotify");
        var brave = vm.AudioRows.Single(r => r.Name == "Brave");
        Assert.True(!brave.OwnTrack && !brave.CanOwnTrack, "six tracks used: it shares the last");
        Assert.True(!spotify.OwnTrack, "the shared track takes the last own one");
        Assert.True(vm.AudioRows.Single(r => r.Name == "Game").OwnTrack, "the first rows keep theirs");
        vm.AudioRows.Single(r => r.Name == "Discord").OwnTrack = false;
        Assert.True(spotify.OwnTrack, "Discord shares now: Spotify has a track again");
    }

    [Test]
    public static void A_row_says_when_it_hears_nothing()
    {
        var vm = Vm(AudioLayout.Separate);
        vm.Seed(new StatusDto(new Kairos.Engine.EngineStatus(true, false, 60, 0, 0, 0, 300, 300, "x", 1920, 1080, 0, 0, @"D:\Clips", false, null,
            QuietAudioRows: ["game", "discord"]), [], null, null, false, false, "1.1.0"), null);
        Assert.Equal(Text.Get("audio.waitingGame"), vm.AudioRows.Single(r => r.Name == "Game").State, "no game yet");
        Assert.Equal(Text.Get("audio.notRunning"), vm.AudioRows.Single(r => r.Name == "Discord").State, "Discord closed");
        Assert.Equal("", vm.AudioRows.Single(r => r.Name == "Other sounds").State, "something plays");
    }

    [Test]
    public static void Rows_not_saved_yet_say_nothing_about_their_app()
    {
        // Together saved, Separate chosen on the page, in a Discord call: "Discord not running".
        // The engine still records everything together, so it tracks no row: none may say its app is closed.
        var vm = Vm(AudioLayout.Separate);
        vm.Seed(new StatusDto(new Kairos.Engine.EngineStatus(true, false, 60, 0, 0, 0, 300, 300, "x", 1920, 1080, 0, 0, @"D:\Clips", false, null,
            QuietAudioRows: []), [], null, null, false, false, "1.1.0"), null);
        Assert.True(vm.AudioRows.All(r => r.State == ""), string.Join(", ", vm.AudioRows.Select(r => $"{r.Name}: {r.State}")));
    }

    [Test]
    public static void The_recorded_microphone_is_not_offered_again()
    {
        var vm = Vm(AudioLayout.Separate);
        vm.MicDevice = "mv7";
        vm.Seed(null, new CapsDto([], [], [new("Default", "default"), new("Shure MV7", "mv7"), new("Line In", "line")], [], [], "32.2.2"));
        Assert.Equal("Line In", string.Join(",", vm.AddAudioDevices.Select(o => o.Label)), "the mic row's own device is already recorded");
    }

    /// <summary>
    /// "Add source" offers the apps open when it is pressed, playing or not (Spotify opened after
    /// the page, or open but quiet, was missing: only apps with sound at the page's last look were offered).
    /// </summary>
    [Test]
    public static void Add_source_offers_the_apps_open_when_pressed()
    {
        var processes = new Kairos.Engine.Audio.ProcInfo[]
        {
            new(10, 1, "Spotify"), new(11, 1, "brave"), new(12, 11, "brave"), new(13, 1, "Kairos"), new(14, 1, "Discord"),
            new(15, 1, "explorer"), new(16, 1, "svchost"), new(17, 1, "TextInputHost"), new(18, 1, "vlc"),
        };
        var picked = Kairos.App.RunningApps.Pick(processes, withWindow: new HashSet<uint> { 10, 11, 13, 14, 15, 17, 18 }, withSound: new HashSet<uint> { 12, 18 },
            (pid, name) => name == "brave" ? "Brave" : name == "vlc" ? "VLC media player" : name);
        Assert.Equal("Brave,Spotify,VLC media player", string.Join(",", picked.Select(a => a.Name)),
            "open apps, quiet or playing, one line each, A to Z; never Kairos, Discord (its own row) or Windows's shell");
        Assert.Equal("brave,Spotify,vlc", string.Join(",", picked.Select(a => a.Value)), "each followed by its exe name");

        var vm = Vm(AudioLayout.Separate);
        var open = new List<Kairos.App.NameValue> { new("Spotify", "Spotify") };
        SettingsViewModel.OpenApps = () => open;
        try
        {
            Assert.Equal("Spotify", string.Join(",", vm.AddAudioApps.Select(o => o.Label)), "read when the menu opens, not at the page's last look");
            open.Add(new("Telegram", "Telegram"));
            Assert.Equal("Spotify,Telegram", string.Join(",", vm.AddAudioApps.Select(o => o.Label)), "an app opened since is there");
        }
        finally
        {
            SettingsViewModel.OpenApps = Kairos.App.RunningApps.Now;
        }
    }

    /// <summary>A sound mixer app is never offered: its sound is every other app's again.</summary>
    [Test]
    public static void Add_source_never_offers_a_sound_mixer()
    {
        var processes = new Kairos.Engine.Audio.ProcInfo[]
        {
            new(10, 1, "Spotify"), new(11, 1, "SteelSeriesSonar"), new(12, 1, "NVIDIA Broadcast"), new(13, 1, "voicemeeter8x64"),
        };
        var picked = Kairos.App.RunningApps.Pick(processes, withWindow: new HashSet<uint> { 10, 12, 13 }, withSound: new HashSet<uint> { 11, 12 },
            (pid, name) => name);
        Assert.Equal("Spotify", string.Join(",", picked.Select(a => a.Name)), "Sonar, Broadcast and Voicemeeter are left out");
    }

    /// <summary>A row added and not saved says so: the engine has no such source until Save (its meter stays still).</summary>
    [Test]
    public static void A_new_source_says_it_starts_when_saved()
    {
        var vm = Vm(AudioLayout.Separate);
        vm.AddAudioAppCommand.Execute(new Kairos.App.NameValue("Spotify", "Spotify"));
        var spotify = vm.AudioRows.Single(r => r.Name == "Spotify");
        Assert.Equal("Starts when you save", spotify.State, "not saved yet");
        Assert.True(vm.AudioRows.Where(r => r != spotify).All(r => r.State != "Starts when you save"), "the saved rows say nothing of the kind");
        var saved = new AppSettings();
        saved.Audio.Layout = AudioLayout.Separate;
        saved.Normalize();
        saved.Audio.Sources.Add(new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify" });
        vm.SeedSettings(saved);
        Assert.True(vm.AudioRows.Single(r => r.Name == "Spotify").State != "Starts when you save", "saved: it runs");
    }

    [Test]
    public static void The_game_row_can_follow_a_pinned_app()
    {
        var vm = Vm(AudioLayout.Separate);
        vm.Seed(null, new CapsDto([], [], [], [], [], "32.2.2", [new("Spotify", "Spotify")]));
        Assert.Equal("The full-screen game,Spotify", string.Join(",", vm.GameFollowChoices.Select(o => o.Label)), "auto, then the apps with sound");
        var game = vm.AudioRows.Single(r => r.IsGame);
        game.Follows = "Spotify";
        Assert.True(vm.IsDirty && vm.AudioRows.Single(r => r.IsGame).Follows == "Spotify", "pinned, unsaved");
        vm.Seed(null, new CapsDto([], [], [], [], [], "32.2.2", []));
        Assert.True(vm.GameFollowChoices.Any(o => (string)o.Value == "Spotify"), "a pinned app that went away is still offered, so it can be unpinned");
    }

    [Test]
    public static void Closing_the_window_with_unsaved_levels_puts_the_saved_ones_back()
    {
        var vm = Vm(AudioLayout.Separate);
        var sent = new List<string>();
        vm.Send = (message, _) => { sent.Add(message); return "ok"; };
        vm.AudioRows[0].Volume = 40;
        vm.Stop();
        var saved = new AppSettings();
        saved.Audio.Layout = AudioLayout.Separate;
        saved.Normalize();
        Assert.Equal(Ipc.LiveLine(saved.Audio), sent[^1], "the engine hears the saved volumes again");
    }

    [Test]
    public static void Choosing_what_the_game_row_follows_keeps_its_list_and_the_choice()
    {
        var vm = Vm(AudioLayout.Separate);
        vm.Seed(null, new CapsDto([], [], [], [], [], "32.2.2", [new("Spotify", "Spotify"), new("Roblox", "RobloxPlayerBeta")]));
        var choices = vm.GameFollowChoices;
        Assert.True(ReferenceEquals(choices, vm.GameFollowChoices), "the same list while nothing in it changes (a new one empties the list's choice)");
        var game = vm.AudioRows.Single(r => r.IsGame);
        game.Follows = "RobloxPlayerBeta";
        Assert.True(ReferenceEquals(choices, vm.GameFollowChoices), "an app from the list chosen: still the same list");
        Assert.Equal("RobloxPlayerBeta", game.Follows, "kept");
        Assert.True(vm.IsDirty, "the choice is a change");
        var fresh = Vm(AudioLayout.Separate);
        fresh.AudioRows.Single(r => r.IsGame).Follows = "";
        Assert.True(!fresh.IsDirty, "the same choice again changes nothing");
    }
}
