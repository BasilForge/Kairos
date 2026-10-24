using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>
/// A log sent to us must show the root of a problem (settings, hotkeys, why the replay stopped,
/// health, what the user was told) and nothing personal.
/// </summary>
public static class LogLinesTests
{
    static string Kind(string folder) => folder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : "D:";

    static AppSettings Made()
    {
        var s = new AppSettings();
        s.Normalize();
        return s;
    }

    [Test]
    public static void The_settings_line_says_what_shapes_kairos_and_nothing_personal()
    {
        var s = Made();
        s.Capture.Mode = CaptureMode.Window;
        s.Capture.Apps = ["Bank of Alex - Secret tab:Chrome_WidgetWin_1:chrome.exe", "General | My Server:Chrome_WidgetWin_1:Discord.exe"];
        s.Normalize();
        s.Output.Folder = @"C:\Users\Alex\OneDrive\Kairos";
        s.General.OnlyWhenGaming = true;
        string line = LogLines.Settings(s, Kind);
        foreach (string part in new[] { "mode=Window", "apps=chrome,Discord", "only-gaming=on", "quality=", "replay=on", "folder=OneDrive", "lang=" })
            Assert.True(line.Contains(part, StringComparison.Ordinal), $"{part} in: {line}");
        foreach (string secret in new[] { "Bank", "Secret", "My Server", "Alex", @"C:\Users" })
            Assert.True(!line.Contains(secret, StringComparison.Ordinal), $"no {secret} (window titles, paths): {line}");
        Assert.True(!line.Contains("key.", StringComparison.Ordinal), "the hotkeys have their own line");
    }

    [Test]
    public static void A_settings_change_names_what_changed_only()
    {
        var before = Made();
        var after = SettingsStore.Copy(before);
        Assert.Equal(null, LogLines.Changes(before, after, Kind), "nothing changed: no line");
        after.General.OnlyWhenGaming = !before.General.OnlyWhenGaming;
        after.Hotkeys.Save = "Ctrl+F8";
        string changes = LogLines.Changes(before, after, Kind)!;
        Assert.True(changes.Contains($"only-gaming {(before.General.OnlyWhenGaming ? "on→off" : "off→on")}", StringComparison.Ordinal), changes);
        Assert.True(changes.Contains($"key.save {before.Hotkeys.Save}→Ctrl+F8", StringComparison.Ordinal), changes);
        Assert.True(!changes.Contains("mode", StringComparison.Ordinal), "only what changed: " + changes);

        // A source added on the Audio page (found when Spotify's row was added and the log said nothing).
        before.Audio.Layout = AudioLayout.Separate;
        before.Normalize();
        var added = SettingsStore.Copy(before);
        added.Audio.Sources.Add(new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", OwnTrack = true });
        added.Audio.Sources.Add(new AudioSource { Id = "input:x", Kind = AudioSourceKind.Input, Target = "{0.0.1.00000000}.{guid}", Label = "Alex's AirPods" });
        string sources = LogLines.Changes(before, added, Kind)!;
        Assert.True(sources.Contains("sources ", StringComparison.Ordinal) && sources.Contains("Spotify*", StringComparison.Ordinal), sources);
        Assert.True(!sources.Contains("AirPods", StringComparison.Ordinal) && !sources.Contains("guid", StringComparison.Ordinal),
            "a device row by its kind only, never its name: " + sources);
    }

    [Test]
    public static void The_hotkeys_line_says_each_key_and_whether_kairos_holds_it()
    {
        string line = LogLines.Hotkeys([("save", "Alt+F10", true), ("quick", "", false), ("toggle", "Alt+Shift+F10", false)]);
        Assert.Equal("hotkeys: save=Alt+F10 ok · quick=none · toggle=Alt+Shift+F10 taken", line, "held, unset, taken by another app");
    }

    [Test]
    public static void The_health_line_counts_frames_since_the_last_one()
    {
        static EngineStatus Status(uint lagged, uint skipped, uint total, uint rendered) =>
            new(true, false, 59.9, lagged, skipped, total, 300, 300, "obs_nvenc_h264_tex", 2560, 1440, 310, 600, @"D:\Clips", false, null,
                RenderedFrames: rendered);
        string line = LogLines.Health(Status(10, 20, 36_000, 36_000), Status(4, 2, 0, 0), cpuPercent: 3.4, ramMb: 312, drive: "C:", freeGb: 140);
        Assert.Equal("health: replay on · 59.9 fps · skipped 0.05% · lagged 0.02% · cpu 3% · ram 312 MB · C: 140 GB free", line,
            "skipped and lagged out of the frames since the last line");
        Assert.True(LogLines.Health(Status(1, 1, 100, 5100), Status(9, 9, 5000, 5000), 1, 300, "C:", 10).Contains("skipped 1.00%", StringComparison.Ordinal),
            "counters reset by a rebuild: counted from zero");
        // A user's log after a rebuild: "lagged 885.50%" (the session's late pictures over the new pipeline's frames).
        string rebuilt = LogLines.Health(Status(21_400, 0, 600, 2_000_600), Status(21_338, 360_567, 366_141, 2_000_000), 1, 300, "C:", 10);
        Assert.True(rebuilt.Contains("skipped 0.00% · lagged 10.33%", StringComparison.Ordinal),
            "late pictures out of the pictures rendered since the last line, whatever the encoder's counters did: " + rebuilt);
        Assert.True(LogLines.Health(null, null, 0.4, 200, "C:", 10).StartsWith("health: replay not built", StringComparison.Ordinal), "no pipeline");
    }

    /// <summary>A game filling an 8 GB card shrinks Kairos's budget there: the log says it, over budget plainly.</summary>
    [Test]
    public static void The_gpu_memory_line_says_kaiross_share_of_the_card()
    {
        const ulong Mb = 1 << 20;
        Assert.Equal("gpu memory: Kairos uses 412 MB of its 6851 MB budget on the card · 3 MB in system memory",
            LogLines.GpuMemory(412 * Mb, 6851 * Mb, 3 * Mb), "within its budget");
        Assert.True(LogLines.GpuMemory(500 * Mb, 300 * Mb, 220 * Mb).EndsWith("over budget: Windows pages it out", StringComparison.Ordinal),
            "over it: said");
    }

    [Test]
    public static void A_clip_from_the_disk_says_how_full_its_picture_was()
    {
        Assert.Equal("clip from the disk: 10800 pictures, 100% of the frame rate", LogLines.RingClip(10800, 1, 0), "a full clip");
        Assert.Equal("clip from the disk: 12 pictures, 1% of the frame rate · the newest picture 95 s behind the sound: the encoder fell behind",
            LogLines.RingClip(12, 0.0066, 95.2), "a starved encoder");
    }

    [Test]
    public static void The_session_line_tells_the_whole_story()
    {
        Assert.Equal("session: 5h11m · clips saved 3 · not saved 1 · screenshots 0 · hotkey presses 0",
            LogLines.Session(TimeSpan.FromMinutes(311), saved: 3, notSaved: 1, shots: 0, presses: 0), "one line at the end");
    }

    [Test]
    public static void Log_lines_never_carry_the_users_folder()
    {
        string profile = @"C:\Users\Alex";
        Assert.Equal(@"pipeline built: folder=%USERPROFILE%\Videos\Kairos", ObsLog.Redact(@"pipeline built: folder=C:\Users\Alex\Videos\Kairos", profile),
            "the user's name goes");
        Assert.Equal(@"module %USERPROFILE%/AppData/x.dll", ObsLog.Redact(@"module c:/users/alex/AppData/x.dll", profile), "either slash, any case");
        Assert.Equal("nothing here", ObsLog.Redact("nothing here", profile), "other lines as they are");
    }

    [Test]
    public static void The_screen_check_measures_how_much_of_the_picture_is_lit()
    {
        var black = new ScreenshotFrame(new byte[ScreenCheck.Width * ScreenCheck.Height * 4], ScreenCheck.Width, ScreenCheck.Height);
        Assert.Equal(0.0, ScreenCheck.LitShare(black), "all black");
        var half = new byte[ScreenCheck.Width * ScreenCheck.Height * 4];
        for (int i = 0; i < half.Length / 2; i += 4)
            half[i + 2] = 200;
        Assert.Near(0.5, ScreenCheck.LitShare(new ScreenshotFrame(half, ScreenCheck.Width, ScreenCheck.Height)), 0.001, "half lit");
    }

    [Test]
    public static void A_test_run_with_its_own_data_folder_leaves_the_installed_kaiross_registry_alone()
    {
        // The engine's own test runs (KAIROS_DATA) would otherwise point Windows's start-up entry at the test copy, or remove it.
        Assert.True(!SettingsStore.WritesSystemSettings(@"C:\Temp\kairos-test-data"), "own data folder: hands off");
        Assert.True(SettingsStore.WritesSystemSettings(null) && SettingsStore.WritesSystemSettings(""), "the installed Kairos: as before");
    }

    [Test]
    public static void A_test_run_with_its_own_pipe_has_its_own_engine_lock()
    {
        Assert.Equal(AppInfo.EngineMutex, AppInfo.EngineLock(null), "the installed Kairos: its one lock");
        Assert.Equal(AppInfo.EngineMutex, AppInfo.EngineLock(""), "no pipe given: the same");
        Assert.True(AppInfo.EngineLock("KairosLogTest42") != AppInfo.EngineMutex, "a test run's own: beside the installed one");
    }
}
