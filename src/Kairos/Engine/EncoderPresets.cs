using Kairos.Settings;

namespace Kairos.Engine;

/// <param name="Cq">NVENC target quality (CQVBR), or CRF for x264.</param>
public sealed record EncoderPlan(
    string EncoderId,
    string RateControl,
    int Cq,
    int MaxBitrateKbps,
    int KeyintSec,
    string Preset,
    int BFrames);

public static class EncoderPresets
{
    // Reference point for the presets: 2560x1440 at 60 fps, AV1.
    const double ReferencePixelRate = 2560.0 * 1440 * 60;
    const int MinBitrateKbps = 4000;
    const int MaxBitrateKbps = 300_000;

    static (int Cq, int Kbps) Reference(QualityPreset q) => q switch
    {
        QualityPreset.Ultra => (22, 60000),
        QualityPreset.High => (26, 40000),
        QualityPreset.Balanced => (30, 25000),
        _ => (34, 15000),
    };

    public static EncoderPlan Plan(VideoCodec codec, QualityPreset q, int width, int height, int fps,
        int customCq, int customMbps, bool nvencAvailable, string nvencPreset = Settings.VideoSettings.DefaultPreset)
    {
        int av1Cq;
        int kbps;
        if (q == QualityPreset.Custom)
        {
            av1Cq = customCq;
            kbps = customMbps * 1000;
        }
        else
        {
            var r = Reference(q);
            double scale = Math.Pow(width * (double)height * fps / ReferencePixelRate, 0.75);
            double codecFactor = codec switch { VideoCodec.Hevc => 1.15, VideoCodec.H264 => 1.5, _ => 1.0 };
            av1Cq = r.Cq;
            kbps = (int)Math.Round(r.Kbps * codecFactor * scale);
        }

        kbps = Math.Clamp(kbps, MinBitrateKbps, MaxBitrateKbps);
        // NVENC AV1 quality runs 1-63, HEVC/H.264 run 1-51.
        int cq = codec == VideoCodec.Av1 ? av1Cq : (int)Math.Round(av1Cq * 51.0 / 63.0, MidpointRounding.AwayFromZero);
        cq = Math.Clamp(cq, 1, codec == VideoCodec.Av1 ? 63 : 51);

        if (!nvencAvailable)
            return new EncoderPlan("obs_x264", "CRF", Math.Clamp((int)Math.Round(av1Cq * 51.0 / 63.0), 1, 51),
                kbps, 1, "veryfast", 2);

        string id = codec switch
        {
            VideoCodec.Hevc => "obs_nvenc_hevc_tex",
            VideoCodec.H264 => "obs_nvenc_h264_tex",
            _ => "obs_nvenc_av1_tex",
        };
        return new EncoderPlan(id, "CQVBR", cq, kbps, 1, nvencPreset, 2);
    }

    /// <summary>Worst-case encoded size in MiB of <paramref name="seconds"/> at the max bitrate.</summary>
    public static int EstimateBufferMb(int maxBitrateKbps, int seconds) =>
        (int)Math.Ceiling(maxBitrateKbps * 1000.0 * seconds / 8 / (1024 * 1024));
}
