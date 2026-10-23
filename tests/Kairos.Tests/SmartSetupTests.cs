using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>"Automatic": what the hardware can record best, from the reports people sent.</summary>
public static class SmartSetupTests
{
    static HardwareProfile Pc(string gpu, string cpu, int threads, int width = 1920, int height = 1080, int hz = 144, bool battery = false,
        params string[] others) =>
        new(gpu, [gpu, .. others], null, threads, cpu, hz, width, height, battery);

    static readonly string[] Rtx40 = ["obs_nvenc_av1_tex", "obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"];
    static readonly string[] Nvenc = ["obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"];

    static string Line(SmartChoice c) => $"{c.Encoder.Id} {c.Resolution} {c.Fps} {c.Quality}{(c.Encoder.NoBFrames ? " noB" : "")}";

    [Test]
    public static void An_rtx_3050_records_h264()
    {
        var c = SmartSetup.Choose(Pc("NVIDIA GeForce RTX 3050", "Intel(R) Core(TM) i5-10400F", 12), Nvenc);
        Assert.Equal("obs_nvenc_h264_tex Native 60 High", Line(c), "H.264: it plays everywhere");
    }

    [Test]
    public static void Automatic_records_h264_on_every_card()
    {
        // H.264 for everyone, the most stable and it plays everywhere; AV1 and HEVC are a choice.
        // A user (GTX 1660 Ti, 1.0.1) had HEVC clips black in Windows' player without the HEVC extension.
        Assert.Equal("obs_nvenc_h264_tex Native 60 High",
            Line(SmartSetup.Choose(Pc("NVIDIA GeForce GTX 1660 Ti", "AMD Ryzen 5 5600GT with Radeon Graphics", 12, hz: 180), Nvenc)), "a card without AV1");
        Assert.Equal("obs_nvenc_h264_tex Native 60 High",
            Line(SmartSetup.Choose(Pc("NVIDIA GeForce RTX 4070 Ti", "Intel Core i7-13700K", 24, 2560, 1440), Rtx40)), "a card with AV1: still H.264");
        Assert.Equal("h264_texture_amf Native 60 High",
            Line(SmartSetup.Choose(Pc("AMD Radeon RX 7800 XT", "AMD Ryzen 7 7800X3D", 16), ["av1_texture_amf", "h265_texture_amf", "h264_texture_amf", "obs_x264"])),
            "AMD too");
    }

    [Test]
    public static void A_chosen_format_the_card_lacks_falls_to_h264()
    {
        // A 1.0.1 updater on manual AV1 with a GTX 1660 Ti: H.264, never HEVC by itself.
        Assert.Equal(VideoCodec.H264, SmartSetup.ManualCodec(VideoCodec.Av1, Nvenc), "no AV1 on the card: H.264");
        Assert.Equal(VideoCodec.Hevc, SmartSetup.ManualCodec(VideoCodec.Hevc, Nvenc), "a format the card has: kept as chosen");
        Assert.Equal(VideoCodec.Av1, SmartSetup.ManualCodec(VideoCodec.Av1, Rtx40), "AV1 chosen on a card with it: AV1");
        Assert.Equal(VideoCodec.H264, SmartSetup.ManualCodec(VideoCodec.Av1, ["obs_x264"]), "no card encoder at all: H.264 on the CPU");
        var pc = Pc("NVIDIA GeForce GTX 1660 Ti", "AMD Ryzen 5 5600GT with Radeon Graphics", 12);
        Assert.Equal("obs_nvenc_h264_tex", SmartSetup.PickChosen(VideoCodec.Av1, Nvenc, pc).Id, "the engine's pick: H.264");
    }

    [Test]
    public static void A_laptop_whose_integrated_chip_has_av1_asks_the_card_that_records()
    {
        // An RTX 3060 laptop with a Radeon 780M: NVIDIA records (it comes first), and it has no AV1.
        string[] laptop = ["obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "av1_texture_amf", "h264_texture_amf", "obs_x264"];
        var pc = Pc("NVIDIA GeForce RTX 3060 Laptop GPU", "AMD Ryzen 7 7840HS", 16);
        Assert.Equal("obs_nvenc_h264_tex", SmartSetup.Choose(pc, laptop).Encoder.Id, "Automatic: H.264 on the NVIDIA card");
        Assert.Equal(VideoCodec.H264, SmartSetup.ManualCodec(VideoCodec.Av1, laptop), "manual AV1: no AV1 on the card that records");
        Assert.Equal("obs_nvenc_h264_tex", SmartSetup.PickChosen(VideoCodec.Av1, laptop, pc).Id, "and that card records it");
    }

    [Test]
    public static void A_gtx_960_with_an_old_driver_records_on_the_cpu()
    {
        // NVENC did not load (outdated driver); the HD 4600 is a Haswell iGPU and the screen is on the GTX 960.
        var pc = Pc("NVIDIA GeForce GTX 960", "Intel(R) Core(TM) i7-4790 CPU", 8, others: "Intel(R) HD Graphics 4600");
        var c = SmartSetup.Choose(pc, ["obs_qsv11_v2", "obs_qsv11_hevc", "obs_x264"]);
        Assert.Equal("obs_x264 P720 30 Balanced", Line(c), "no Quick Sync off a screen it does not drive: x264 at 720p30");
        Assert.Equal("veryfast", c.Encoder.X264Preset, "eight threads: veryfast");
    }

    [Test]
    public static void A_gtx_1060_records_h264_at_its_screen_size()
    {
        var c = SmartSetup.Choose(Pc("NVIDIA GeForce GTX 1060 6GB", "AMD Ryzen 5 3600", 12, 2560, 1440), Nvenc);
        Assert.Equal("obs_nvenc_h264_tex Native 60 High", Line(c), "Pascal: H.264, 1440p kept");
    }

    [Test]
    public static void A_240_hz_screen_still_records_60()
    {
        var c = SmartSetup.Choose(Pc("NVIDIA GeForce RTX 3070 Ti", "AMD Ryzen 7 5800X", 16, hz: 240), Nvenc);
        Assert.Equal("obs_nvenc_h264_tex Native 60 High", Line(c), "1080p at 60: what every site plays");
        var slow = SmartSetup.Choose(Pc("NVIDIA GeForce RTX 3070 Ti", "AMD Ryzen 7 5800X", 16, hz: 50), Nvenc);
        Assert.Equal(50, slow.Fps, "never above the screen's own rate");
    }

    [Test]
    public static void A_4k_screen_records_up_to_1440p_and_an_apu_less()
    {
        Assert.Equal("obs_nvenc_h264_tex P1440 60 High", Line(SmartSetup.Choose(Pc("NVIDIA GeForce RTX 4070 Ti", "Intel Core i7-13700K", 24, 3840, 2160), Rtx40)),
            "Ada on a 4K screen: 1440p (H.264 at 4K would be huge)");
        Assert.Equal("obs_nvenc_h264_tex P1440 60 High", Line(SmartSetup.Choose(Pc("NVIDIA GeForce GTX 1070", "Intel Core i7-8700", 12, 3840, 2160), Nvenc)),
            "Pascal on a 4K screen: the same");
        var ally = Pc("AMD Radeon(TM) Graphics", "AMD Ryzen Z2 A", 8, hz: 120, battery: true);
        Assert.Equal("h264_texture_amf Native 60 Light", Line(SmartSetup.Choose(ally, ["h264_texture_amf", "obs_x264"])),
            "an APU (1080p, balanced) on battery: one step lighter");
    }

    [Test]
    public static void Skipped_frames_step_down_quality_then_rate_then_size()
    {
        var pc = Pc("NVIDIA GeForce RTX 3060", "AMD Ryzen 5 5600X", 12, 2560, 1440);
        Assert.Equal("obs_nvenc_h264_tex Native 60 Balanced", Line(SmartSetup.Choose(pc, Nvenc, step: 1)), "one step: lighter quality");
        Assert.Equal("obs_nvenc_h264_tex Native 30 Balanced", Line(SmartSetup.Choose(pc, Nvenc, step: 2)), "two: half the frame rate");
        Assert.Equal("obs_nvenc_h264_tex P1080 30 Balanced", Line(SmartSetup.Choose(pc, Nvenc, step: 3)), "three: a smaller picture");
    }

    [Test]
    public static void The_card_comes_before_the_codec()
    {
        var pick = EncoderCatalog.Pick(VideoCodec.Av1, ["obs_nvenc_hevc_tex", "obs_qsv11_av1"]);
        Assert.Equal("obs_nvenc_hevc_tex", pick.Id, "NVIDIA's HEVC before Intel's AV1");
    }

    [Test]
    public static void Old_intel_graphics_are_told_apart()
    {
        Assert.True(Gpus.IsOldIntel("Intel(R) HD Graphics 4600"), "HD 4600");
        Assert.True(Gpus.IsOldIntel("Intel(R) HD Graphics"), "HD Graphics");
        Assert.True(Gpus.IsOldIntel("Intel(R) Iris(TM) Pro Graphics 5200"), "Iris Pro 5200");
        Assert.True(!Gpus.IsOldIntel("Intel(R) HD Graphics 530") && !Gpus.IsOldIntel("Intel(R) UHD Graphics 770") && !Gpus.IsOldIntel("Intel(R) Iris(R) Xe Graphics")
                    && !Gpus.IsOldIntel("Intel(R) Arc(TM) A770 Graphics") && !Gpus.IsOldIntel("NVIDIA GeForce RTX 4070 Ti"), "Skylake and after");
    }

    [Test]
    public static void Quick_sync_only_when_it_drives_the_screen()
    {
        string[] ids = ["obs_nvenc_hevc_tex", "obs_qsv11_hevc", "obs_x264"];
        Assert.Equal("obs_nvenc_hevc_tex,obs_x264", string.Join(",", SmartSetup.Usable(ids, Pc("NVIDIA GeForce RTX 4070 Ti", "i7", 24, others: "Intel(R) UHD Graphics 770"))),
            "the screen is on the NVIDIA card");
        Assert.Equal("obs_nvenc_hevc_tex,obs_qsv11_hevc,obs_x264", string.Join(",", SmartSetup.Usable(ids, Pc("Intel(R) UHD Graphics 620", "i5", 8, others: "NVIDIA GeForce MX150"))),
            "a laptop's screen on the Intel GPU");
    }
}
