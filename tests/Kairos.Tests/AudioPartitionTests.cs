using Kairos.App;
using Kairos.Engine.Audio;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Which processes each sound row captures: no sound twice, none lost.</summary>
public static class AudioPartitionTests
{
    // explorer(10) ── steam(20) ── game(21)
    //              ├─ Discord(30) ── Discord(31), Discord(32)
    //              ├─ chrome(40) ── chrome(41: its audio)
    //              ├─ Spotify(50)
    //              ├─ Kairos(60, this engine) and Kairos(61, its settings window)
    //              └─ KairosTone(70)
    static readonly ProcInfo[] Processes =
    [
        new(10, 1, "explorer"), new(20, 10, "steam"), new(21, 20, "r5apex"),
        new(30, 10, "Discord"), new(31, 30, "Discord"), new(32, 30, "Discord"),
        new(40, 10, "chrome"), new(41, 40, "chrome"), new(50, 10, "Spotify"),
        new(60, 10, "Kairos"), new(61, 60, "Kairos"), new(70, 10, "KairosTone"),
    ];

    static PartitionInput Rows(uint game = 21, params AudioSource[] added) =>
        new(game, [.. AudioSettings.BuiltInSources(), .. added], SelfName: "Kairos");

    static string Other(IReadOnlyDictionary<string, uint[]> a) => string.Join(",", a.GetValueOrDefault("other", []).Order());

    [Test]
    public static void The_game_and_discord_are_their_rows_the_rest_is_other_sounds()
    {
        var a = AudioPartition.Assign(Processes, new HashSet<uint> { 21, 31, 41, 50, 60, 70 }, Rows());
        Assert.Equal("21", string.Join(",", a["game"]), "the game");
        Assert.Equal("30", string.Join(",", a["discord"]), "Discord's first process: its children with it");
        Assert.Equal("41,50,70", Other(a), "chrome's audio, Spotify, the helper; Kairos itself not");
    }

    /// <summary>
    /// A sound mixer app plays every other app's sound again (SteelSeries Sonar on the headphones): as Other sounds it
    /// put Discord and the game in the clip twice, a moment apart (measured with Sonar on, 2026-10-21).
    /// </summary>
    [Test]
    public static void A_sound_mixer_app_is_never_other_sounds()
    {
        ProcInfo[] processes = [.. Processes, new(80, 10, "SteelSeriesSonar"), new(81, 10, "NVIDIA Broadcast"), new(82, 10, "voicemeeterpro")];
        var a = AudioPartition.Assign(processes, new HashSet<uint> { 31, 50, 80, 81, 82 }, Rows());
        Assert.Equal("50", Other(a), "Spotify; not Sonar, Broadcast or Voicemeeter (each app is heard from itself)");
    }

    [Test]
    public static void A_launcher_is_never_captured_with_its_game()
    {
        var a = AudioPartition.Assign(Processes, new HashSet<uint> { 20, 21 }, Rows());
        Assert.Equal("", Other(a), "Steam's tree holds the game: capturing it would record the game twice");
    }

    [Test]
    public static void A_process_inside_another_captured_tree_is_left_to_it()
    {
        var a = AudioPartition.Assign(Processes, new HashSet<uint> { 40, 41, 32 }, Rows());
        Assert.Equal("40", Other(a), "chrome's tree has its child; Discord's child goes with Discord");
    }

    [Test]
    public static void An_added_app_takes_its_process_out_of_other_sounds()
    {
        var spotify = new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", Label = "Spotify" };
        var a = AudioPartition.Assign(Processes, new HashSet<uint> { 50, 41 }, Rows(21, spotify));
        Assert.Equal("50", string.Join(",", a["app:spotify"]), "its own row");
        Assert.Equal("41", Other(a), "not in other sounds as well");
    }

    [Test]
    public static void When_the_game_changes_the_old_one_is_other_sounds_again()
    {
        var sound = new HashSet<uint> { 21, 50 };
        Assert.Equal("50", Other(AudioPartition.Assign(Processes, sound, Rows(game: 21))), "while it is the game");
        var later = AudioPartition.Assign(Processes, sound, Rows(game: 50));
        Assert.Equal("50", string.Join(",", later["game"]), "the new game");
        Assert.Equal("21", Other(later), "the old game: other sounds again (Steam, above it, is still left out)");
        Assert.Equal("", string.Join(",", AudioPartition.Assign(Processes, sound, Rows(game: 0)).GetValueOrDefault("game", [])), "no game: the row waits");
    }

    [Test]
    public static void A_process_that_plays_sound_has_a_session()
    {
        // This test process plays the self-test's tone for a moment (7 kHz at -85 dBFS: nobody hears it). Its session
        // stays listed after the sound ends, until the process does.
        Kairos.Engine.SelfTest.Tone(["--tone", "0.3"]);
        var pids = CoreAudio.ProcessesWithSound();
        Assert.True(pids.Contains((uint)Environment.ProcessId), $"this process ({Environment.ProcessId}) among {pids.Count}");
        Assert.True(!pids.Contains(0), "system sounds are not a process");
    }

    [Test]
    public static void The_game_row_follows_the_full_screen_app_and_keeps_it_while_it_runs()
    {
        var running = new HashSet<uint> { 21, 50 };
        Assert.Equal(21u, GameFollow.Next(following: 0, fullscreen: true, front: 21, running, self: 60), "a full-screen app in front");
        Assert.Equal(21u, GameFollow.Next(following: 21, fullscreen: false, front: 30, running, self: 60), "alt-tab to Discord: still the game");
        Assert.Equal(50u, GameFollow.Next(following: 21, fullscreen: true, front: 50, running, self: 60), "another full-screen app");
        Assert.Equal(0u, GameFollow.Next(following: 99, fullscreen: false, front: 30, running, self: 60), "the game closed");
        Assert.Equal(21u, GameFollow.Next(following: 21, fullscreen: true, front: 60, running, self: 60), "Kairos itself is never the game");
    }

    /// <summary>A sound is never in two named rows: the game pid inside a tree another row claims leaves the Game row empty.</summary>
    [Test]
    public static void Two_rows_never_capture_the_same_tree()
    {
        var sound = new HashSet<uint> { 21, 31, 41, 50 };
        var spotify = new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify" };
        var a = AudioPartition.Assign(Processes, sound, Rows(game: 50, spotify));
        Assert.Equal("50", string.Join(",", a["app:spotify"]), "the added app keeps its row");
        Assert.True(!a.ContainsKey("game"), "the Game row waits: the full-screen app is Spotify, already a row");
        var discord = AudioPartition.Assign(Processes, sound, Rows(game: 31));
        Assert.True(!discord.ContainsKey("game") && discord["discord"].Single() == 30, "Discord full screen: Discord's row, not the game's");
    }

    /// <summary>An app with two root processes: the first is its row, the others are heard as Other sounds (nothing lost).</summary>
    [Test]
    public static void Extra_roots_of_an_app_are_other_sounds()
    {
        ProcInfo[] two = [.. Processes, new(80, 10, "Discord"), new(81, 80, "Discord")];
        var a = AudioPartition.Assign(two, new HashSet<uint> { 31, 81 }, Rows(game: 0));
        Assert.Equal("30", string.Join(",", a["discord"]), "the first Discord");
        Assert.Equal("80", Other(a), "the second one is other sounds");
    }

    /// <summary>A parent pid that was reused (it started after its child) is no launcher of that child.</summary>
    [Test]
    public static void A_reused_parent_pid_is_not_a_launcher()
    {
        // chrome(40) got Discord's old parent id: Discord(30) says parent 40, but 40 started after 30.
        ProcInfo[] reused = [new(10, 1, "explorer"), new(40, 10, "chrome"), new(41, 40, "chrome"), new(30, 40, "Discord"), new(60, 10, "Kairos")];
        var sound = new HashSet<uint> { 40, 30 };
        Assert.Equal("", Other(AudioPartition.Assign(reused, sound, Rows(game: 0))), "without start times chrome counts as Discord's launcher");
        var a = AudioPartition.Assign(reused, sound, Rows(game: 0), startedAt: pid => pid == 40 ? 200 : 100);
        Assert.Equal("40", Other(a), "with them, chrome is heard");
    }

    /// <summary>
    /// Every 2 s on a PC with hundreds of processes: a start time is asked (a process opened) only along the trees the
    /// rows claim and the sound makers' parents, each process at most once; never every process (the game's anti-cheat
    /// among them).
    /// </summary>
    [Test]
    public static void Start_times_are_asked_only_where_they_decide_something_and_once()
    {
        var many = Processes.Concat(Enumerable.Range(0, 400).Select(i => new ProcInfo((uint)(1000 + i), 4, "svchost"))).ToArray();
        var asked = new List<uint>();
        var a = AudioPartition.Assign(many, new HashSet<uint> { 21, 31, 41, 50 }, Rows(), startedAt: pid => { asked.Add(pid); return 100; });
        Assert.Equal("21", string.Join(",", a["game"]), "the same rows");
        Assert.Equal("41,50", Other(a), "the same other sounds");
        Assert.True(asked.Count < 40, $"few processes opened ({asked.Count} of {many.Length})");
        Assert.Equal(asked.Count, asked.Distinct().Count(), "each at most once");
    }

    /// <summary>A process whose sound cannot be captured is tried again ever less often, not on every 2 s ask (a thread and a log line each).</summary>
    [Test]
    public static void A_failing_capture_is_tried_again_less_and_less_often()
    {
        var waits = Enumerable.Range(0, 8).Select(ProcessAudioSource.RetryAfter).Select(t => (int)t.TotalSeconds).ToList();
        Assert.Equal("2,4,8,16,32,60,60,60", string.Join(",", waits), "2 s, doubling, a minute at most");
    }
}
