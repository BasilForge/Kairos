using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>The two audio layouts: everyone who had Kairos keeps "all PC sound together"; new installs separate the sources.</summary>
public static class AudioSettingsTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), "settings.json");

    [Test]
    public static void An_existing_settings_file_keeps_its_audio()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "audio": { "system": true, "systemVolume": 0.7, "mic": false, "separateTracks": false } }""");
        var s = SettingsStore.Load(path);
        Assert.Equal(AudioLayout.Together, s.Audio.Layout, "1.0's audio, as it was");
        Assert.Equal(0.7, s.Audio.SystemVolume, "volume kept");
        Assert.Equal(false, s.Audio.SeparateTracks, "tracks kept");
        Assert.Equal(0, s.Audio.Sources.Count, "no rows made for the together layout");
    }

    [Test]
    public static void A_new_install_separates_the_sources()
    {
        var s = SettingsStore.Load(TempFile());
        Assert.Equal(AudioLayout.Separate, s.Audio.Layout, "new installs");
        Assert.Equal("Game,Discord,Microphone,OtherSounds", string.Join(",", s.Audio.Sources.Select(r => r.Kind)), "the built-in rows");
        Assert.True(s.Audio.Sources.All(r => r.OwnTrack && r.Volume == 1 && !r.Muted), "each on its own track, full, heard");
        Assert.Equal(4, s.Audio.Sources.Select(r => r.Id).Distinct().Count(), "ids are unique");
    }

    [Test]
    public static void Rows_survive_a_round_trip_and_built_ins_come_back()
    {
        var s = new AppSettings();
        s.Audio.Layout = AudioLayout.Separate;
        s.Normalize();
        s.Audio.Sources.Add(new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", Volume = 0.4, OwnTrack = false });
        s.Audio.Sources.RemoveAll(r => r.Kind == AudioSourceKind.Discord);
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        back.Normalize();
        Assert.True(back.Audio.Sources.Any(r => r.Kind == AudioSourceKind.Discord), "a built-in row can't go missing");
        var spotify = back.Audio.Sources.Single(r => r.Id == "app:spotify");
        Assert.True(spotify.Target == "Spotify" && spotify.Volume == 0.4 && !spotify.OwnTrack, "an added app row as it was");
    }

    [Test]
    public static void Volumes_and_mutes_of_rows_are_live()
    {
        var before = new AppSettings();
        before.Audio.Layout = AudioLayout.Separate;
        before.Normalize();
        var after = SettingsStore.Deserialize(SettingsStore.Serialize(before));
        after.Audio.Sources[0].Volume = 0.3;
        after.Audio.Sources[1].Muted = true;
        Assert.True(!SettingsDiff.NeedsRebuild(before, after), "set on the running sources");
        after.Audio.Sources[2].OwnTrack = false;
        Assert.True(SettingsDiff.NeedsRebuild(before, after), "a track more or less needs new encoders");
        var layout = SettingsStore.Deserialize(SettingsStore.Serialize(before));
        layout.Audio.Layout = AudioLayout.Together;
        Assert.True(SettingsDiff.NeedsRebuild(before, layout), "another layout");
    }

    [Test]
    public static void Volumes_travel_to_the_engine_on_one_line_while_dragging()
    {
        var window = new AppSettings();
        window.Audio.Layout = AudioLayout.Separate;
        window.Normalize();
        window.Audio.Sources[0].Volume = 0.25;
        window.Audio.Sources[1].Muted = true;
        window.Audio.MicVolume = 1.5;
        string line = Kairos.App.Ipc.LiveLine(window.Audio);
        Assert.True(line.StartsWith("live ") && !line.Contains('\n'), "one pipe line");

        var live = Kairos.App.Ipc.ParseLive(line["live ".Length..]);
        Assert.Equal(0.25, live.Rows.Single(r => r.Id == window.Audio.Sources[0].Id).Volume, "a row's volume");
        Assert.Equal(true, live.Rows.Single(r => r.Id == window.Audio.Sources[1].Id).Muted, "a row's mute");
        Assert.Equal(1.5, live.MicVolume, "the together layout's volumes too");
        var engine = new AppSettings();
        engine.Audio.Layout = AudioLayout.Separate;
        engine.Normalize();
        live.SetOn(engine.Audio);
        Assert.True(engine.Audio.Sources[0].Volume == 0.25 && engine.Audio.Sources[1].Muted, "set on settings as they are sent");
    }

    [Test]
    public static void The_live_values_of_a_row_are_read_by_its_id()
    {
        var s = new AppSettings();
        s.Audio.Layout = AudioLayout.Separate;
        s.Normalize();
        s.Audio.Sources[3].Muted = true;
        s.Audio.Sources[3].Volume = 0.3;
        var live = LiveAudio.Of(s.Audio);
        Assert.Equal((0.3, true), live.For("other"), "the row's volume and mute");
        Assert.Equal((1.0, false), live.For("app:new"), "a row it does not know: as recorded");
    }
}
