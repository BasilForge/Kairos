using Kairos.App;
using Kairos.Engine.Audio;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>
/// The first time Kairos sees a sound mixer it sets the sound up for it, once, with nothing to choose: separate sources,
/// and the voice alone as the microphone. A later choice is the person's own.
/// </summary>
public static class MixerSetupTests
{
    static readonly List<NameValue> Sonar =
    [
        new("Headphones (4- Arctis Nova Pro Wireless)", "arctis"),
        new("SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)", "sonar-game"),
        new("SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)", "sonar-chat"),
    ];
    static readonly List<NameValue> SonarMics = [new("SteelSeries Sonar - Microphone (SteelSeries Sonar Virtual Audio Device)", "sonar-mic")];
    static readonly List<NameValue> GoXlr = [new("System (TC-Helicon GO XLR MINI)", "system"), new("Game (TC-Helicon GO XLR MINI)", "game")];
    static readonly List<NameValue> GoXlrMics = [new("Broadcast Stream Mix (TC-Helicon GO XLR MINI)", "mix"), new("Chat Mic (TC-Helicon GO XLR MINI)", "voice")];

    static AppSettings With(AudioLayout layout, string micDevice = "default", bool mic = true)
    {
        var s = new AppSettings();
        s.Audio.Layout = layout;
        s.Audio.MicDevice = micDevice;
        s.Audio.Mic = mic;
        return s;
    }

    [Test]
    public static void A_mixer_seen_the_first_time_sets_separate_sources_once()
    {
        var s = With(AudioLayout.Together);
        var done = MixerSetup.FirstTime(s, Sonar, SonarMics, "sonar-mic", separateSources: true);
        Assert.Equal(AudioLayout.Separate, s.Audio.Layout, "all of it, once, whatever Sonar does");
        Assert.True(s.Audio.Sources.Any(r => r.Kind == AudioSourceKind.Game), "with its rows");
        Assert.True(done.Count == 1 && done[0].StartsWith("SteelSeries Sonar", StringComparison.Ordinal), string.Join(" | ", done));
        Assert.True(s.Audio.MixersSetUp.SequenceEqual(["SteelSeries Sonar"]), "remembered");

        s.Audio.Layout = AudioLayout.Together; // the person's own choice afterwards
        Assert.Equal(0, MixerSetup.FirstTime(s, Sonar, SonarMics, "sonar-mic", separateSources: true).Count, "never again");
        Assert.Equal(AudioLayout.Together, s.Audio.Layout, "left as chosen (the Audio page says what it misses)");
        MixerSetup.FirstTime(s, GoXlr, GoXlrMics, "voice", separateSources: true);
        Assert.True(s.Audio.Layout == AudioLayout.Together && s.Audio.MixersSetUp.Contains("GoXLR"), "a second mixer later: the layout still the person's");
    }

    [Test]
    public static void A_goxlr_microphone_on_its_whole_mix_becomes_chat_mic()
    {
        var onDefault = With(AudioLayout.Separate);
        MixerSetup.FirstTime(onDefault, GoXlr, GoXlrMics, defaultMic: "mix", separateSources: true);
        Assert.Equal("voice", onDefault.Audio.MicDevice, "Default, which Windows had on Broadcast Stream Mix");

        var chosen = With(AudioLayout.Together, micDevice: "mix", mic: false);
        MixerSetup.FirstTime(chosen, GoXlr, GoXlrMics, defaultMic: "voice", separateSources: true);
        Assert.True(chosen.Audio.MicDevice == "voice" && chosen.Audio.Layout == AudioLayout.Separate && !chosen.Audio.Mic,
            "the mix picked before: Chat Mic, the mic switch as it was");

        var voice = With(AudioLayout.Separate);
        MixerSetup.FirstTime(voice, GoXlr, GoXlrMics, defaultMic: "voice", separateSources: true);
        Assert.Equal("default", voice.Audio.MicDevice, "Default on Chat Mic already: left alone");
    }

    [Test]
    public static void Nothing_changes_without_a_mixer_or_without_separate_sources()
    {
        var plain = With(AudioLayout.Together);
        Assert.Equal(0, MixerSetup.FirstTime(plain, [new("Speakers (Realtek(R) Audio)", "r"), new("Speakers (NVIDIA Broadcast)", "b")], [], null, true).Count,
            "no mixer that splits the sound");
        Assert.True(plain.Audio.Layout == AudioLayout.Together && plain.Audio.MixersSetUp.Count == 0, "untouched");

        var old = With(AudioLayout.Together);
        MixerSetup.FirstTime(old, Sonar, SonarMics, "sonar-mic", separateSources: false);
        Assert.Equal(AudioLayout.Together, old.Audio.Layout, "a Windows without an app's own sound keeps all together");
    }

    [Test]
    public static void The_mixers_set_up_are_kept_in_the_settings_file()
    {
        var s = new AppSettings();
        s.Audio.MixersSetUp.Add("GoXLR");
        Assert.True(SettingsStore.Copy(s).Audio.MixersSetUp.SequenceEqual(["GoXLR"]), "round trip");
        Assert.Equal(0, SettingsStore.Deserialize("""{"Audio":{"MixersSetUp":null}}""").Audio.MixersSetUp.Count, "a null list reads as none");
    }
}
