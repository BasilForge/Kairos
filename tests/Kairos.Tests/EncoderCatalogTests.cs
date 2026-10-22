using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

public static class EncoderCatalogTests
{
    static EncoderPlan PlanFor(VideoCodec c) =>
        EncoderPresets.Plan(c, QualityPreset.Ultra, 2560, 1440, 60, 22, 40, nvencAvailable: true);

    [Test]
    public static void Amd_only_machine_gets_amf_av1()
    {
        var pick = EncoderCatalog.Pick(VideoCodec.Av1, ["av1_texture_amf", "h265_texture_amf", "h264_texture_amf", "obs_x264"]);
        Assert.Equal("av1_texture_amf", pick.Id, "id");
        Assert.Equal(EncoderVendor.Amd, pick.Vendor, "vendor");
        Assert.Equal(VideoCodec.Av1, pick.Codec, "codec");
    }

    [Test]
    public static void Missing_codec_falls_back_to_the_next_one()
    {
        var pick = EncoderCatalog.Pick(VideoCodec.Av1, ["obs_nvenc_hevc_tex", "obs_nvenc_h264_tex"]);
        Assert.Equal("obs_nvenc_h264_tex", pick.Id, "H.264 next: it plays everywhere (HEVC only when asked for, SmartSetup.ManualCodec)");
        var h264Only = EncoderCatalog.Pick(VideoCodec.Hevc, ["obs_qsv11_v2"]);
        Assert.Equal("obs_qsv11_v2", h264Only.Id, "h264 qsv");
        Assert.Equal(VideoCodec.H264, h264Only.Codec, "codec");
    }

    [Test]
    public static void No_hardware_encoder_uses_x264()
    {
        var pick = EncoderCatalog.Pick(VideoCodec.Av1, ["obs_x264", "ffmpeg_aac"]);
        Assert.Equal("obs_x264", pick.Id, "id");
        Assert.Equal(EncoderVendor.Software, pick.Vendor, "vendor");
    }

    [Test]
    public static void Software_encoding_is_limited_to_1080p30()
    {
        Assert.Equal(((1920, 1080), 30), EncoderCatalog.LimitForSoftware((2560, 1440), 60), "1440p60");
        Assert.Equal(((1280, 720), 24), EncoderCatalog.LimitForSoftware((1280, 720), 24), "small stays");
        Assert.Equal(((2580, 1080), 30), EncoderCatalog.LimitForSoftware((3440, 1440), 60), "ultrawide keeps aspect");
    }

    [Test]
    public static void Nvidia_settings_use_cqvbr()
    {
        var s = EncoderCatalog.Settings(PlanFor(VideoCodec.Av1), new EncoderChoice("obs_nvenc_av1_tex", EncoderVendor.Nvidia, VideoCodec.Av1));
        Assert.Equal("CQVBR", (string)s["rate_control"], "rc");
        Assert.Equal(22L, (long)s["target_quality"], "cq");
        Assert.Equal(60000L, (long)s["max_bitrate"], "max");
        Assert.Equal(false, (bool)s["lookahead"], "no cuda lookahead");
    }

    [Test]
    public static void Amd_settings_use_qvbr_quality_level()
    {
        var s = EncoderCatalog.Settings(PlanFor(VideoCodec.Hevc), new EncoderChoice("h265_texture_amf", EncoderVendor.Amd, VideoCodec.Hevc));
        Assert.Equal("QVBR", (string)s["rate_control"], "rc");
        Assert.Equal(18L, (long)s["cqp"], "quality level on the 51 scale");
        Assert.Equal("quality", (string)s["preset"], "preset");
    }

    [Test]
    public static void Intel_settings_use_icq()
    {
        var s = EncoderCatalog.Settings(PlanFor(VideoCodec.H264), new EncoderChoice("obs_qsv11_v2", EncoderVendor.Intel, VideoCodec.H264));
        Assert.Equal("ICQ", (string)s["rate_control"], "rc");
        Assert.Equal(18L, (long)s["icq_quality"], "quality");
        Assert.Equal("TU4", (string)s["target_usage"], "usage");
    }

    [Test]
    public static void Software_settings_use_crf_veryfast()
    {
        var plan = EncoderPresets.Plan(VideoCodec.H264, QualityPreset.Ultra, 1920, 1080, 30, 22, 40, nvencAvailable: false);
        var s = EncoderCatalog.Settings(plan, new EncoderChoice("obs_x264", EncoderVendor.Software, VideoCodec.H264));
        Assert.Equal("CRF", (string)s["rate_control"], "rc");
        Assert.Equal("veryfast", (string)s["preset"], "preset");
    }

    /// <summary>An encoder that could not start is left out of the next pick, down to x264, which runs anywhere.</summary>
    [Test]
    public static void A_failed_encoder_is_left_out_of_the_next_pick()
    {
        var fallback = new EncoderFallback();
        string[] amd = ["av1_texture_amf", "h265_texture_amf", "h264_texture_amf", "obs_x264"];
        Assert.Equal("h265_texture_amf", EncoderCatalog.Pick(VideoCodec.Hevc, fallback.Usable(amd)).Id, "the wanted one first");
        Assert.True(fallback.Failed("h265_texture_amf"), "noted: try the next");
        Assert.Equal("h264_texture_amf", EncoderCatalog.Pick(VideoCodec.Hevc, fallback.Usable(amd)).Id, "AMD's H.264 next");
        Assert.True(fallback.Failed("h264_texture_amf"), "noted again");
        Assert.Equal("av1_texture_amf", EncoderCatalog.Pick(VideoCodec.Hevc, fallback.Usable(amd)).Id, "AMD's AV1 where the card has it");
        Assert.True(fallback.Failed("av1_texture_amf"), "noted");
        var last = EncoderCatalog.Pick(VideoCodec.Hevc, fallback.Usable(amd));
        Assert.Equal("obs_x264", last.Id, "then the CPU");
        Assert.True(!fallback.Failed("obs_x264"), "x264 failing is the end: nothing left to try");
        Assert.True(!fallback.Failed("h265_texture_amf"), "an encoder already noted is not tried again");
        Assert.Equal("h265_texture_amf,h264_texture_amf,av1_texture_amf", string.Join(",", fallback.Excluded), "for the log and the status");
    }

    [Test]
    public static void An_encoder_is_described_by_codec_and_vendor()
    {
        Text.Language = "en";
        Assert.Equal("HEVC (AMD)", EncoderCatalog.Describe(new EncoderChoice("h265_texture_amf", EncoderVendor.Amd, VideoCodec.Hevc)), "hardware");
        Assert.Equal("H.264 (CPU)", EncoderCatalog.Describe(new EncoderChoice("obs_x264", EncoderVendor.Software, VideoCodec.H264)), "software");
        Assert.Equal("AV1 (NVIDIA)", EncoderCatalog.Describe(new EncoderChoice("obs_nvenc_av1_tex", EncoderVendor.Nvidia, VideoCodec.Av1)), "nvidia");
    }

    /// <summary>NVENC takes HEVC B-frames from Turing (RTX, GTX 16 but the 1650) on; older cards fail to start with them.</summary>
    [Test]
    public static void Hevc_b_frames_only_where_the_card_has_them()
    {
        Assert.True(Gpus.NvencHevcBFrames("NVIDIA GeForce RTX 3050") && Gpus.NvencHevcBFrames("NVIDIA GeForce GTX 1660 SUPER")
                    && Gpus.NvencHevcBFrames("NVIDIA GeForce GTX 1650 SUPER"), "Turing and newer");
        Assert.True(!Gpus.NvencHevcBFrames("NVIDIA GeForce GTX 1060 6GB") && !Gpus.NvencHevcBFrames("NVIDIA GeForce GTX 960")
                    && !Gpus.NvencHevcBFrames("NVIDIA GeForce MX450") && !Gpus.NvencHevcBFrames("NVIDIA GeForce GTX 1650"), "Pascal, Maxwell, MX, the 1650");
        var pascal = new EncoderChoice("obs_nvenc_hevc_tex", EncoderVendor.Nvidia, VideoCodec.Hevc) { NoBFrames = true };
        Assert.Equal(0L, (long)EncoderCatalog.Settings(PlanFor(VideoCodec.Hevc), pascal)["bf"], "no B-frames asked of a Pascal card");
        Assert.Equal(2L, (long)EncoderCatalog.Settings(PlanFor(VideoCodec.Hevc), pascal with { NoBFrames = false })["bf"], "an RTX card keeps them");
    }

    /// <summary>A start that fails with B-frames is tried once more on the same encoder without them.</summary>
    [Test]
    public static void A_failed_start_with_b_frames_retries_without_them_first()
    {
        var fallback = new EncoderFallback();
        Assert.True(fallback.Failed("obs_nvenc_hevc_tex", withBFrames: true), "try again");
        Assert.True(fallback.NoBFrames("obs_nvenc_hevc_tex") && fallback.Excluded.Count == 0, "the same encoder, without B-frames");
        Assert.True(fallback.Failed("obs_nvenc_hevc_tex", withBFrames: false), "failed again: the next encoder");
        Assert.Equal("obs_nvenc_hevc_tex", string.Join(",", fallback.Excluded), "left out now");
    }
}
