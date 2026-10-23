using Kairos.Settings;

namespace Kairos.Tests;

public static class SettingsTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), "settings.json");

    [Test]
    public static void Defaults_match_spec()
    {
        var s = new AppSettings();
        Assert.Equal(CaptureMode.Screen, s.Capture.Mode, "mode");
        Assert.Equal(300, s.Replay.Seconds, "buffer seconds");
        Assert.Equal(OutputResolution.P1440, s.Video.Resolution, "resolution");
        Assert.Equal(60, s.Video.Fps, "fps");
        Assert.Equal(VideoCodec.H264, s.Video.Codec, "codec: H.264, plays everywhere");
        Assert.Equal(QualityPreset.Ultra, s.Video.Quality, "quality");
        Assert.Equal(true, s.Audio.System, "system audio");
        Assert.Equal(true, s.Audio.Mic, "mic on");
        Assert.Equal("default", s.Audio.MicDevice, "windows default mic");
        Assert.Equal("default", s.Audio.SystemDevice, "windows default output");
        Assert.Equal(true, s.Audio.SeparateTracks, "separate tracks");
        Assert.Equal("Alt+F10", s.Hotkeys.Save, "save hotkey");
        Assert.Equal("Alt+Shift+F10", s.Hotkeys.ToggleBuffer, "toggle hotkey");
        Assert.Equal("Alt+F9", s.Hotkeys.Record, "record hotkey");
        Assert.Equal(false, s.Replay.QuickEnabled, "quick clip");
        Assert.Equal(true, s.General.StartWithWindows, "autostart");
        Assert.Equal(true, s.General.SoundOnSave, "sound");
        Assert.Equal(true, s.General.ToastOnSave, "toast");
        Assert.Equal("ar", s.General.Language, "language");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kairos"), s.Output.Folder, "clips folder");
    }

    [Test]
    public static void First_run_language_follows_windows()
    {
        Assert.Equal("ar", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("ar-SA")), "arabic");
        Assert.Equal("ar", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("ar-EG")), "arabic egypt");
        Assert.Equal("en", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("en-US")), "english");
        Assert.Equal("en", AppSettings.DefaultLanguage(new System.Globalization.CultureInfo("fr-FR")), "other");
    }

    [Test]
    public static void Save_then_load_round_trips()
    {
        string path = TempFile();
        var s = new AppSettings();
        s.Capture.Mode = CaptureMode.Game;
        s.Video.Codec = VideoCodec.Hevc;
        s.Replay.Seconds = 120;
        s.Hotkeys.Save = "Alt+F7";
        s.Output.Folder = @"E:\Clips";
        SettingsStore.Save(path, s);

        var loaded = SettingsStore.Load(path);
        Assert.Equal(CaptureMode.Game, loaded.Capture.Mode, "mode");
        Assert.Equal(VideoCodec.Hevc, loaded.Video.Codec, "codec");
        Assert.Equal(120, loaded.Replay.Seconds, "seconds");
        Assert.Equal("Alt+F7", loaded.Hotkeys.Save, "hotkey");
        Assert.Equal(@"E:\Clips", loaded.Output.Folder, "folder");
    }

    [Test]
    public static void Missing_file_gives_defaults()
    {
        var loaded = SettingsStore.Load(TempFile());
        Assert.Equal(300, loaded.Replay.Seconds, "seconds");
    }

    [Test]
    public static void Corrupt_json_gives_defaults_and_keeps_backup()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");

        var loaded = SettingsStore.Load(path);
        Assert.Equal(300, loaded.Replay.Seconds, "seconds");
        string backup = Path.Combine(Path.GetDirectoryName(path)!, "settings.bad.json");
        Assert.True(File.Exists(backup), "settings.bad.json should exist");
        Assert.Equal("{ not json", File.ReadAllText(backup), "backup content");
    }

    [Test]
    public static void Buffer_seconds_are_clamped()
    {
        string path = TempFile();
        var s = new AppSettings();
        s.Replay.Seconds = 5;
        SettingsStore.Save(path, s);
        Assert.Equal(15, SettingsStore.Load(path).Replay.Seconds, "low clamp");

        s.Replay.Seconds = 99999;
        SettingsStore.Save(path, s);
        Assert.Equal(1200, SettingsStore.Load(path).Replay.Seconds, "high clamp");
    }

    [Test]
    public static void Unknown_enum_value_falls_back_to_default_and_keeps_other_fields()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "video": { "codec": "vp9", "fps": 30 } }""");

        var loaded = SettingsStore.Load(path);
        Assert.Equal(VideoCodec.Av1, loaded.Video.Codec, "codec");
        Assert.Equal(30, loaded.Video.Fps, "fps");
    }

    [Test]
    public static void The_chosen_app_becomes_the_first_of_the_apps()
    {
        var old = SettingsStore.Deserialize("""{"Capture":{"Mode":"Window","Window":"Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe"}}""");
        Assert.True(old.Capture.Apps!.SequenceEqual(["Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe"]), "kept as the first app");
        old.Capture.Apps = [];
        old.Normalize();
        Assert.Equal("", old.Capture.Window, "all removed: none chosen");
        Assert.Equal(CaptureMode.Screen, Kairos.Engine.SceneRules.BuildMode(old.Capture), "and the screen is recorded");
        var many = new AppSettings();
        many.Capture.Apps = ["a:b:one.exe", "a:b:two.exe", "a:b:three.exe", "a:b:four.exe", "a:b:five.exe", "a:b:six.exe", "a:b:one.exe"];
        many.Normalize();
        Assert.Equal(5, many.Capture.Apps!.Count, "five at most, each once");
        Assert.Equal("a:b:one.exe", many.Capture.Window, "the first one, for what reads one app");
    }

    /// <summary>The settings file 1.0.1 wrote, read the way the engine and the window read it (Load, not Deserialize).</summary>
    [Test]
    public static void A_1_0_1_settings_file_keeps_its_chosen_app_when_loaded()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"capture":{"mode":"Window","window":"Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe"},"general":{"language":"ar"}}""");
        var loaded = SettingsStore.Load(path);
        Assert.True(loaded.Capture.Apps.SequenceEqual(["Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe"]), "kept as the first app");
        Assert.Equal(CaptureMode.Window, Kairos.Engine.SceneRules.BuildMode(loaded.Capture), "and that app is still what is recorded");
        Assert.Equal("ar", loaded.General.Language, "the rest kept");
    }

    [Test]
    public static void Starting_fresh_after_a_save_is_off_and_needs_no_rebuild()
    {
        var a = new AppSettings();
        Assert.True(!a.Replay.FreshAfterSave, "off by default");
        var b = SettingsStore.Copy(a);
        b.Replay.FreshAfterSave = true;
        Assert.True(!SettingsDiff.NeedsRebuild(a, b), "read when a clip is saved: no rebuild");
        var c = SettingsStore.Copy(a);
        c.Capture.Apps = ["a:b:roblox.exe"];
        c.Normalize();
        Assert.True(SettingsDiff.NeedsRebuild(a, c), "another app: rebuilt");
    }

    [Test]
    public static void An_update_from_untouched_old_quality_defaults_turns_automatic_once()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string Write(string name, string video)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, "{\"Version\":1,\"Video\":{" + video + "},\"Replay\":{\"Seconds\":120}}");
            return path;
        }
        const string old = "\"Codec\":\"Av1\",\"Fps\":60,\"Resolution\":\"P1440\",\"Quality\":\"Ultra\",\"CustomCq\":22,\"CustomMbps\":40,\"Preset\":\"p5\"";

        var untouched = SettingsStore.Load(Write("a.json", old));
        Assert.True(untouched.Video.Auto, "1.0.1's own defaults, never changed: Automatic");
        Assert.Equal(AppSettings.CurrentVersion, untouched.Version, "marked done");
        Assert.Equal(120, untouched.Replay.Seconds, "everything else kept");

        var chosen = SettingsStore.Load(Write("b.json", old.Replace("\"Fps\":60", "\"Fps\":120")));
        Assert.True(!chosen.Video.Auto && chosen.Video.Fps == 120, "anything changed: the user's choice stays");

        string done = Path.Combine(dir, "c.json");
        File.WriteAllText(done, "{\"Version\":" + AppSettings.CurrentVersion + ",\"Video\":{" + old + ",\"Auto\":false}}");
        Assert.True(!SettingsStore.Load(done).Video.Auto, "Automatic turned off after the update: stays off");

        // A 1.1 build's file before this (Version 1, but Automatic already a choice in it): left as it is.
        string tried = Write("d.json", old + ",\"Auto\":false");
        Assert.True(!SettingsStore.Load(tried).Video.Auto, "a 1.1 tester who turned Automatic off: still off");

        string missing = Path.Combine(dir, "new", "settings.json");
        Assert.Equal(AppSettings.CurrentVersion, SettingsStore.Load(missing).Version, "a new install: current");

        // As 1.0.1 itself wrote it: no version at all (found by updating a real 1.0.1 install, 2026-10-21).
        string real = Path.Combine(dir, "real-1.0.1.json");
        File.WriteAllText(real, """
            {"capture":{"mode":"Screen"},"video":{"codec":"Av1","fps":60,"resolution":"P1440","quality":"Ultra","customCq":22,"customMbps":40,"preset":"p5"},
             "replay":{"enabled":true,"seconds":300},"general":{"language":"en"}}
            """);
        var updated = SettingsStore.Load(real);
        Assert.True(updated.Video.Auto, "a real 1.0.1 file on its own defaults: Automatic");
        Assert.Equal(AppSettings.CurrentVersion, updated.Version, "and marked done");
    }
}
