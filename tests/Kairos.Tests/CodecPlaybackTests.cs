using Kairos.Engine;
using Kairos.Settings;
using Kairos.App;
using Kairos.Ui;
using Microsoft.Win32;

namespace Kairos.Tests;

/// <summary>
/// A user (GTX 1660 Ti): Automatic chose HEVC, and Windows without the HEVC extension shows such clips black. The format
/// chain: AV1 when the card records it (its free extension offered), HEVC only when Windows plays it, else H.264.
/// </summary>
public static class CodecPlaybackTests
{
    /// <summary>A Store package of this name is installed for this user (the registry's package list).</summary>
    static bool PackageInstalled(params string[] names)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        return key?.GetSubKeyNames().Any(full => names.Any(n => full.StartsWith(n + "_", StringComparison.OrdinalIgnoreCase))) == true;
    }

    [Test]
    public static void Windows_playing_a_format_follows_its_extension()
    {
        Assert.True(CodecPlayback.CanPlay(VideoCodec.H264), "H.264 is built into Windows");
        Assert.Equal(PackageInstalled("Microsoft.AV1VideoExtension"), CodecPlayback.CanPlay(VideoCodec.Av1), "AV1: its Store extension");
        Assert.Equal(PackageInstalled("Microsoft.HEVCVideoExtension", "Microsoft.HEVCVideoExtensions"), CodecPlayback.CanPlay(VideoCodec.Hevc),
            "HEVC: the paid extension or the PC maker's");
    }
}

public static class Av1ExtensionNoteTests
{
    [Test]
    public static void A_pc_recording_av1_that_windows_cannot_play_is_offered_the_free_extension()
    {
        Text.Language = "en";
        var s = new AppSettings();
        s.Video.Auto = false;
        s.Video.Codec = VideoCodec.Av1;
        s.Normalize();
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        SettingsViewModel.Plays = c => c != VideoCodec.Av1;
        try
        {
            Assert.True(vm.Av1NeedsExtension, "AV1 recorded, Windows cannot play it: offered");
            vm.Codec = VideoCodec.H264;
            Assert.True(!vm.Av1NeedsExtension, "H.264: nothing to install");
            SettingsViewModel.Plays = _ => true;
            vm.Codec = VideoCodec.Av1;
            Assert.True(!vm.Av1NeedsExtension, "the extension is there: no note");
        }
        finally
        {
            SettingsViewModel.Plays = CodecPlayback.CanPlay;
        }
    }

    [Test]
    public static void The_page_shows_what_a_card_without_the_chosen_format_records()
    {
        Text.Language = "en";
        var s = new AppSettings();
        s.Video.Auto = false;
        s.Video.Codec = VideoCodec.Av1;
        s.Normalize();
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        vm.Seed(null, new Kairos.App.CapsDto([], [], [], [], ["obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"], "32.2.2"));
        Assert.Equal(VideoCodec.H264, vm.Codec, "AV1 chosen, the card has none: H.264 ticked, as the engine records");
        Assert.True(!vm.Av1Available && vm.HevcAvailable && vm.H264Available, "AV1 greyed out; HEVC a choice");
        Assert.Equal(VideoCodec.H264, new AppSettings().Video.Codec, "a new install: H.264");
    }

    [Test]
    public static void Each_format_says_its_size_and_only_what_this_pc_still_needs()
    {
        Text.Language = "en";
        var s = new AppSettings();
        s.Normalize();
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        try
        {
            // A GTX 1660 Ti on a Windows without the HEVC extension.
            SettingsViewModel.Plays = c => c == VideoCodec.H264;
            vm.Seed(null, new Kairos.App.CapsDto([], [], [], [], ["obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"], "32.2.2"));
            Assert.Equal(Text.Get("quality.unsupported"), vm.Av1Tip, "no AV1 on the card: says so");
            Assert.Equal(Text.Get("quality.codecHevcNeedsExtension"), vm.HevcTip, "HEVC: its size, and the extension Windows lacks");
            Assert.Equal(Text.Get("quality.codecH264Tip"), vm.H264Tip, "H.264: plays everywhere");

            // An RTX 4070 on a Windows with both extensions: only the sizes.
            SettingsViewModel.Plays = _ => true;
            vm.Seed(null, new Kairos.App.CapsDto([], [], [], [], ["obs_nvenc_av1_tex", "obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"], "32.2.2"));
            Assert.Equal(Text.Get("quality.codecAv1Tip"), vm.Av1Tip, "AV1 plays: its size only");
            Assert.Equal(Text.Get("quality.codecHevcTip"), vm.HevcTip, "HEVC plays: its size only");

            SettingsViewModel.Plays = c => c != VideoCodec.Av1;
            vm.Seed(null, new Kairos.App.CapsDto([], [], [], [], ["obs_nvenc_av1_tex", "obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"], "32.2.2"));
            Assert.Equal(Text.Get("quality.codecAv1NeedsExtension"), vm.Av1Tip, "AV1 without its extension: the free extension");
        }
        finally
        {
            SettingsViewModel.Plays = CodecPlayback.CanPlay;
        }
    }
}
