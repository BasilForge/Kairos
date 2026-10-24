using Kairos.App;
using Kairos.Engine.Audio;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>Sound mixer apps' virtual devices: the game's is recorded, a chat one is said to miss the game.</summary>
public static class VirtualAudioTests
{
    [Test]
    public static void Virtual_devices_are_told_apart_by_name()
    {
        string Role(string name) => VirtualAudio.Classify(name) is { } v ? $"{v.Brand}/{v.Role}" : "real";
        Assert.Equal("SteelSeries Sonar/Game", Role("SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)"), "Sonar gaming");
        Assert.Equal("SteelSeries Sonar/Chat", Role("SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)"), "Sonar chat");
        Assert.Equal("SteelSeries Sonar/Media", Role("SteelSeries Sonar - Media (SteelSeries Sonar Virtual Audio Device)"), "Sonar media");
        Assert.Equal("GoXLR/System", Role("System (TC-Helicon GoXLR Mini)"), "GoXLR system");
        Assert.Equal("GoXLR/Game", Role("Game (TC-Helicon GoXLR)"), "GoXLR game");
        Assert.Equal("GoXLR/Chat", Role("Chat (TC-Helicon GoXLR Mini)"), "GoXLR chat");
        Assert.Equal("GoXLR/Media", Role("Music (TC-Helicon GoXLR)"), "GoXLR music");
        Assert.Equal("Wave Link/Chat", Role("Voice Chat (Elgato Virtual Audio)"), "Wave Link chat");
        Assert.Equal("Voicemeeter/System", Role("Voicemeeter Input (VB-Audio Voicemeeter VAIO)"), "Voicemeeter");
        Assert.Equal("NVIDIA Broadcast/System", Role("Speakers (NVIDIA Broadcast)"), "Broadcast");
        Assert.Equal("real", Role("Speakers (Realtek(R) Audio)"), "a real device");
        Assert.Equal("real", Role("26 (soundcore Q20i)"), "a headset");

        Assert.Equal("SteelSeries Sonar - Chat", VirtualAudio.ShortName("SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)"), "its driver dropped");
        Assert.Equal("Chat (TC-Helicon GoXLR)", VirtualAudio.ShortName("Chat (TC-Helicon GoXLR)"), "the brand only in brackets: kept");
        Assert.Equal("Headphones (Arctis Nova Pro Wireless)", VirtualAudio.ShortName("Headphones (Arctis Nova Pro Wireless)"), "a real device: kept");
    }

    [Test]
    public static void The_game_device_is_found_and_a_chat_device_misses_the_game()
    {
        string[] sonar = ["Speakers (Realtek(R) Audio)", "SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)",
            "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)"];
        Assert.Equal(sonar[2], VirtualAudio.GameDevice(sonar), "Sonar's gaming device");
        Assert.True(VirtualAudio.MissesTheGame(sonar[1]) && !VirtualAudio.MissesTheGame(sonar[2]) && !VirtualAudio.MissesTheGame(sonar[0]), "chat misses it");
        string[] goxlr = ["System (TC-Helicon GoXLR Mini)", "Chat (TC-Helicon GoXLR Mini)", "Music (TC-Helicon GoXLR Mini)"];
        Assert.Equal(null, VirtualAudio.GameDevice(goxlr), "no game device: system is where games play");
        Assert.True(!VirtualAudio.MissesTheGame(goxlr[0]) && VirtualAudio.MissesTheGame(goxlr[1]), "GoXLR chat misses it");
    }

    static SettingsViewModel Together(params NameValue[] speakers)
    {
        Text.Language = "en";
        var s = new AppSettings();
        s.Audio.Layout = AudioLayout.Together;
        s.Normalize();
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        vm.Seed(null, new CapsDto([], [], [], [new("Default", "default"), .. speakers], [], "32.2.2", DefaultSpeaker: speakers[0].Value));
        return vm;
    }

    [Test]
    public static void The_audio_page_warns_when_the_system_device_is_a_chat_device()
    {
        // Wave Link plays all of it on the headphones (its Monitor Mix): any one of its devices misses the rest, as Sonar's.
        var vm = Together(new("Voice Chat (Elgato Virtual Audio)", "chat-id"), new("Game (Elgato Virtual Audio)", "game-id"));
        Assert.True(vm.SystemDeviceWarning.Contains("Wave Link splits it", StringComparison.Ordinal), "default is a Chat device (" + vm.SystemDeviceWarning + ")");
        Assert.True(vm.CaptureWarning == vm.SystemDeviceWarning, "the Status page says it too");
        vm.SystemDevice = "game-id";
        Assert.True(vm.SystemDeviceWarning.Contains("Wave Link splits it", StringComparison.Ordinal), "the game device misses chat and music");
        vm = Together(new("Voice Chat (Voicemeeter VAIO)", "chat-id"), new("Game (Voicemeeter VAIO)", "game-id"));
        Assert.True(vm.SystemDeviceWarning.Contains("Game (Voicemeeter VAIO)", StringComparison.Ordinal), "a chat device of a mixer that plays nothing on the headphones: pick Game (" + vm.SystemDeviceWarning + ")");
        vm.SystemDevice = "game-id";
        Assert.Equal("", vm.SystemDeviceWarning, "the game device: nothing to say");
        vm.AudioLayout = AudioLayout.Separate;
        vm.SystemDevice = "chat-id";
        Assert.Equal("", vm.SystemDeviceWarning, "separate sources take the game from its own process");
    }

    /// <summary>
    /// Sonar splits the sound by kind and plays all of it on the headphones you listen on: any one Sonar device misses
    /// the rest (Default, which Sonar makes its Gaming, recorded no Discord and no music: a test PC, 2026-10-21).
    /// </summary>
    [Test]
    public static void Every_sonar_device_points_to_the_headphones_sonar_plays_on()
    {
        const string gaming = "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)";
        var vm = Together(new(gaming, "game-id"), new("SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)", "chat-id"),
            new("Headphones (4- Arctis Nova Pro Wireless)", "arctis-id"), new("Speakers (NVIDIA Broadcast)", "broadcast-id"));
        Assert.Equal("SteelSeries Sonar - Gaming has only part of your sound (SteelSeries Sonar splits it), so some sounds would be missing. "
                     + "Pick Headphones (4- Arctis Nova Pro Wireless), or choose Separate sources.", vm.SystemDeviceWarning, "Default is Sonar's Gaming, named short");
        vm.SystemDevice = "chat-id";
        Assert.True(vm.SystemDeviceWarning.Contains("Pick Headphones (4- Arctis", StringComparison.Ordinal), "Chat: the headphones too, not Gaming");
        vm.SystemDevice = "arctis-id";
        Assert.Equal("", vm.SystemDeviceWarning, "the headphones: all of it");

        var two = Together(new(gaming, "game-id"), new("Headphones (Arctis)", "a"), new("Speakers (Realtek(R) Audio)", "b"));
        Assert.True(two.SystemDeviceWarning.Contains("Pick the headphones or speakers you listen on", StringComparison.Ordinal), "two real devices: not guessed");
    }

    /// <summary>
    /// GoXLR: its five playback devices are the mixer's inputs and the mix is made in the hardware; the whole mix comes
    /// back as one recording device, Broadcast Stream Mix, and the voice as Chat Mic (its manual; GoXLR Utility's
    /// routing). Windows names it "GoXLR" or "GO XLR" (the Mini's setup guide).
    /// </summary>
    static SettingsViewModel GoXlr(AudioLayout layout, string micDevice = "default", bool system = true, string defaultMic = "mix")
    {
        Text.Language = "en";
        var s = new AppSettings();
        s.Audio.Layout = layout;
        s.Audio.System = system;
        s.Normalize();
        s.Audio.SelectMic(micDevice);
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        vm.Seed(null, new CapsDto([], [],
            [new("Default", "default"), new("Broadcast Stream Mix (TC-Helicon GoXLR)", "mix"), new("Chat Mic (TC-Helicon GoXLR)", "voice")],
            [new("Default", "default"), new("System (TC-Helicon GoXLR)", "system"), new("Game (TC-Helicon GoXLR)", "game"),
                new("Chat (TC-Helicon GoXLR)", "chat"), new("Music (TC-Helicon GoXLR)", "music"), new("Sample (TC-Helicon GoXLR)", "sample")],
            [], "32.2.2", DefaultSpeaker: "system", DefaultMic: defaultMic));
        return vm;
    }

    [Test]
    public static void Goxlr_is_known_however_windows_spells_it()
    {
        string Role(string name) => VirtualAudio.Classify(name) is { } v ? $"{v.Brand}/{v.Role}" : "real";
        Assert.Equal("GoXLR/System", Role("System (TC-Helicon GO XLR MINI)"), "the Mini's guide");
        Assert.Equal("GoXLR/Game", Role("Game (TC-Helicon GoXLR)"), "the full GoXLR");
        Assert.Equal("GoXLR,GoXLR", VirtualAudio.WholeMixOf("Broadcast Stream Mix (TC-Helicon GoXLR Mini)") + "," + VirtualAudio.WholeMixOf("Broadcast Stream Mix (TC-HELICON GO XLR)"),
            "Broadcast Stream Mix is the whole mix");
        Assert.Equal(null, VirtualAudio.WholeMixOf("Chat Mic (TC-Helicon GoXLR)") ?? VirtualAudio.WholeMixOf("Microphone (Shure MV7)"), "a voice is not");
    }

    /// <summary>GoXLR's VOD is its mix too (all but the music, for streams' recordings): never a source of its own.</summary>
    [Test]
    public static void Goxlr_vod_is_a_whole_mix_and_its_app_no_source()
    {
        Assert.Equal("GoXLR", VirtualAudio.WholeMixOf("VOD (2- TC-HELICON GoXLR Mini)"), "VOD holds the game, chat and voice");
        Assert.True(VirtualAudio.IsMixerApp("GoXLR App"), "GoXLR's app is no sound of its own");
        Assert.True(!VirtualAudio.IsMixerApp("Steam"), "an ordinary app");
    }

    /// <summary>
    /// RØDECaster Duo and Pro II mix in the hardware like GoXLR: Main Stereo comes back as a recording device holding all
    /// of it. Elgato Wave Link plays its Monitor Mix on your headphones and sends its Stream Mix as a recording device.
    /// </summary>
    [Test]
    public static void Rodecaster_and_wave_link_are_known()
    {
        string Role(string name) => VirtualAudio.Classify(name) is { } v ? $"{v.Brand}/{v.Role}/{v.MixOnYourDevice}/{v.MixOnARecordingDevice}" : "real";
        Assert.Equal("RØDECaster/System/False/True", Role("Speakers (RODECaster Duo Main Stereo)"), "Duo's main output");
        Assert.Equal("RØDECaster/Chat/False/True", Role("Speakers (RODECaster Duo Chat)"), "its chat output");
        Assert.Equal("RØDECaster/System/False/True", Role("Speakers (RØDECaster Pro II Main Stereo)"), "the Pro II, spelled with Ø");
        Assert.Equal("RØDECaster", VirtualAudio.WholeMixOf("Microphone (RODECaster Duo Main Stereo)"), "its main mix as a recording device");
        Assert.Equal("Wave Link/System/True/True", Role("System (Elgato Virtual Audio)"), "Wave Link's System");
        Assert.Equal("Wave Link", VirtualAudio.WholeMixOf("Stream Mix (Elgato Virtual Audio)"), "its stream mix");
        Assert.True(VirtualAudio.IsMixerApp("WaveLink"), "Wave Link plays every app's sound again");
    }

    /// <summary>
    /// Logitech MIXLINE (a user's PC, 2026-10-24): one playback device, its mix played again on the headset by MIXLINE.exe,
    /// and two recording devices with its mixes, Stream and Record.
    /// </summary>
    [Test]
    public static void Logitech_mixline_is_known()
    {
        string Role(string name) => VirtualAudio.Classify(name) is { } v ? $"{v.Brand}/{v.Role}/{v.MixOnYourDevice}/{v.MixOnARecordingDevice}" : "real";
        Assert.Equal("Logitech MIXLINE/System/True/True", Role("Speakers (MIXLINE)"), "its playback device");
        Assert.Equal("Logitech MIXLINE", VirtualAudio.WholeMixOf("Microphone (MIXLINE Stream)"), "its stream mix");
        Assert.Equal("Logitech MIXLINE", VirtualAudio.WholeMixOf("Microphone (MIXLINE Record)"), "its record mix");
        Assert.True(VirtualAudio.IsMixerApp("MIXLINE"), "MIXLINE plays every app's sound again");
        Assert.Equal("real", Role("Speakers (PRO X 2 LIGHTSPEED)"), "the Logitech headset itself");
    }

    /// <summary>
    /// No GoXLR playback device has all the sound, and its whole mix as the microphone would have Smart Save hear the game
    /// and the voice track hold everything: separate sources (each app from itself) record it right.
    /// </summary>
    [Test]
    public static void Every_goxlr_output_points_to_separate_sources()
    {
        var vm = GoXlr(AudioLayout.Together, micDevice: "voice");
        Assert.Equal("System (TC-Helicon GoXLR) has only part of your sound: GoXLR sends chat like Discord to another device, so it would be missing. "
                     + "Choose Separate sources to record everything.", vm.SystemDeviceWarning, "Default is GoXLR's System");
        vm.SystemDevice = "game";
        Assert.True(vm.SystemDeviceWarning.StartsWith("Game (TC-Helicon GoXLR) has only part", StringComparison.Ordinal), "Game misses chat and music too");
    }

    [Test]
    public static void A_whole_mix_as_the_microphone_says_what_is_recorded_twice()
    {
        const string twice = "Broadcast Stream Mix (TC-Helicon GoXLR) is GoXLR's full mix: the game, chat and music are already in it, so they would be "
                             + "recorded twice. Pick Chat Mic (TC-Helicon GoXLR) for your voice.";
        Assert.Equal(twice, GoXlr(AudioLayout.Separate, micDevice: "mix").MicDeviceWarning, "separate sources, the mix chosen");
        Assert.Equal(twice, GoXlr(AudioLayout.Separate).MicDeviceWarning, "Default, which Windows has on the mix");
        Assert.Equal("", GoXlr(AudioLayout.Separate, micDevice: "voice").MicDeviceWarning, "Chat Mic: the voice only");
        Assert.Equal("", GoXlr(AudioLayout.Separate, defaultMic: "voice").MicDeviceWarning, "Default on Chat Mic");
        Assert.Equal(twice, GoXlr(AudioLayout.Together, micDevice: "mix").MicDeviceWarning, "together, System sound on: the PC's sound twice");
        Assert.Equal("", GoXlr(AudioLayout.Together, micDevice: "mix", system: false).MicDeviceWarning, "together without System sound: nothing twice");
        Assert.True(GoXlr(AudioLayout.Separate, micDevice: "voice").AddAudioDevices.All(o => !o.Label.Contains("Broadcast", StringComparison.Ordinal)),
            "Add source never offers the whole mix: it would hold every other row again");
    }

    /// <summary>
    /// Process sound comes in 7.1 and is folded here: asked for stereo, Windows folded an app on Sonar's 7.1 Gaming device
    /// to -98.5 dB from -85 (measured 2026-10-21); in 7.1 it came on front left and right at -85.
    /// </summary>
    [Test]
    public static void Surround_folds_to_stereo_keeping_a_stereo_apps_level()
    {
        float[] surround =
        [
            0.5f, -0.25f, 0, 0, 0, 0, 0, 0, // a stereo app: as it plays
            0, 0, 1, 0, 0, 0, 0, 0,         // centre: both sides at -3 dB
            0, 0, 0, 1, 0, 0, 0, 0,         // LFE: left out
            0, 0, 0, 0, 1, 0, 0, 1,         // back left, side right
        ];
        var stereo = new float[8];
        ProcessLoopback.Downmix(surround, stereo);
        Assert.Equal("0.5,-0.25,0.707,0.707,0,0,0.707,0.707",
            string.Join(",", stereo.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))), "folded");
    }

    /// <summary>
    /// A user's Windows 10 (19045) gave process loopback a buffer size far past an int: the stereo buffer sized from it
    /// asked for a negative size, AllocHGlobal threw OutOfMemoryException on the capture thread and Kairos closed as the
    /// replay started. The buffer now follows the packets themselves; an impossible packet is a source error.
    /// </summary>
    [Test]
    public static void The_stereo_buffer_follows_the_packets_not_a_reported_size()
    {
        using var buffer = new ProcessLoopback.StereoBuffer();
        Assert.True(buffer.For(480) != IntPtr.Zero && buffer.Frames == ProcessLoopback.SampleRate, "a second to start");
        IntPtr first = buffer.For(ProcessLoopback.SampleRate);
        Assert.True(buffer.For(480) == first, "a packet that fits: the same buffer");
        Assert.True(buffer.For(96_000) != IntPtr.Zero && buffer.Frames == 96_000, "a larger packet: grown to it");
        bool refused = false;
        try { buffer.For(uint.MaxValue); }
        catch (InvalidOperationException) { refused = true; }
        Assert.True(refused, "an impossible packet: an error for the source, not OutOfMemoryException");
    }
}
