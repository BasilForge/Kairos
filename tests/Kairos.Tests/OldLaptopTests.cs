using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Audio;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>
/// Older laptops: one with two GPUs (Kairos draws on the Intel one, the screen's) still records on its NVIDIA or AMD
/// card, and a Windows 10 older than 2004 (no app-only sound) records all the PC's sound together.
/// </summary>
public static class OldLaptopTests
{
    [Test]
    public static void A_laptop_with_two_gpus_still_records_on_its_nvidia_or_amd_card()
    {
        // Drawing on Intel: libobs offers NVIDIA's encoders that copy each picture over, not the texture ones.
        var nvidia = EncoderCatalog.Pick(VideoCodec.Hevc, ["obs_nvenc_hevc_soft", "obs_nvenc_h264_soft", "obs_qsv11_v2", "obs_x264"]);
        Assert.Equal("obs_nvenc_hevc_soft", nvidia.Id, "NVIDIA's copying encoder, before Intel's");
        Assert.Equal(EncoderVendor.Nvidia, nvidia.Vendor, "NVIDIA's settings");
        Assert.Equal("HEVC (NVIDIA)", EncoderCatalog.Describe(nvidia), "said as NVIDIA");
        var amd = EncoderCatalog.Pick(VideoCodec.H264, ["h264_fallback_amf", "obs_qsv11_v2", "obs_x264"]);
        Assert.Equal("h264_fallback_amf", amd.Id, "AMD's copying encoder, before Intel's");
        Assert.Equal(EncoderVendor.Amd, amd.Vendor, "AMD's settings");
        // Drawing on the card itself: the texture encoder stays first (no copy).
        Assert.Equal("obs_nvenc_hevc_tex", EncoderCatalog.Pick(VideoCodec.Hevc, ["obs_nvenc_hevc_tex", "obs_nvenc_hevc_soft"]).Id, "texture first");
        Assert.Equal(EncoderVendor.Nvidia, EncoderCatalog.ById("obs_nvenc_av1_soft").Vendor, "known by its id too");
    }

    [Test]
    public static void Without_app_only_sound_windows_records_all_the_pcs_sound_together()
    {
        bool was = ProcessAudioSupport.Available;
        ProcessAudioSupport.Available = false;
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", "oldwin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal(AudioLayout.Together, SettingsStore.Load(Path.Combine(dir, "settings.json")).Audio.Layout, "a new install");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """{"audio":{"layout":"Separate","sourceAudioOnly":true},"capture":{"mode":"Game"}}""");
            var old = SettingsStore.Load(file);
            Assert.Equal(AudioLayout.Together, old.Audio.Layout, "a file that asks for separate sources");
            Assert.True(!old.Audio.SourceAudioOnly, "and no game-only sound (it needs the same Windows)");
            Text.Language = "en";
            var vm = new SettingsViewModel();
            vm.SeedSettings(old);
            Assert.True(!vm.SeparateSourcesAvailable && !vm.SourceAudioPossible, "the page offers neither");
            Assert.True(vm.SeparateSourcesNote.Contains("2004", StringComparison.Ordinal), "and says why");
        }
        finally
        {
            ProcessAudioSupport.Available = was;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        Assert.Equal(AudioLayout.Separate, AppSettingsFor(true).Audio.Layout, "a newer Windows keeps them");
    }

    static AppSettings AppSettingsFor(bool supported)
    {
        bool was = ProcessAudioSupport.Available;
        ProcessAudioSupport.Available = supported;
        try
        {
            return SettingsStore.Deserialize("""{"audio":{"layout":"Separate"}}""");
        }
        finally
        {
            ProcessAudioSupport.Available = was;
        }
    }
}
