using Kairos.Settings;

namespace Kairos.Engine;

public enum EncoderVendor { Nvidia, Amd, Intel, Software }

public sealed record EncoderChoice(string Id, EncoderVendor Vendor, VideoCodec Codec)
{
    /// <summary>The card cannot take B-frames for this codec (NVENC HEVC before Turing), or a start with them failed.</summary>
    public bool NoBFrames { get; init; }

    /// <summary>x264's speed: faster on a CPU with fewer threads (it records while the game runs).</summary>
    public string X264Preset { get; init; } = "veryfast";
}

/// <summary>Hardware encoders from every GPU vendor libobs supports, and how to configure each one.</summary>
public static class EncoderCatalog
{
    public const string X264 = "obs_x264";

    /// <summary>
    /// Each vendor's texture encoder first (the picture stays on the card), then the one that copies it over: what a laptop
    /// with two GPUs gets, as Kairos draws on the one driving the screen (Intel) and the NVIDIA or AMD card encodes.
    /// </summary>
    static readonly (VideoCodec Codec, EncoderVendor Vendor, string Id)[] Table =
    [
        (VideoCodec.Av1, EncoderVendor.Nvidia, "obs_nvenc_av1_tex"),
        (VideoCodec.Av1, EncoderVendor.Nvidia, "obs_nvenc_av1_soft"),
        (VideoCodec.Av1, EncoderVendor.Amd, "av1_texture_amf"),
        (VideoCodec.Av1, EncoderVendor.Amd, "av1_fallback_amf"),
        (VideoCodec.Av1, EncoderVendor.Intel, "obs_qsv11_av1"),
        (VideoCodec.Hevc, EncoderVendor.Nvidia, "obs_nvenc_hevc_tex"),
        (VideoCodec.Hevc, EncoderVendor.Nvidia, "obs_nvenc_hevc_soft"),
        (VideoCodec.Hevc, EncoderVendor.Amd, "h265_texture_amf"),
        (VideoCodec.Hevc, EncoderVendor.Amd, "h265_fallback_amf"),
        (VideoCodec.Hevc, EncoderVendor.Intel, "obs_qsv11_hevc"),
        (VideoCodec.H264, EncoderVendor.Nvidia, "obs_nvenc_h264_tex"),
        (VideoCodec.H264, EncoderVendor.Nvidia, "obs_nvenc_h264_soft"),
        (VideoCodec.H264, EncoderVendor.Amd, "h264_texture_amf"),
        (VideoCodec.H264, EncoderVendor.Amd, "h264_fallback_amf"),
        (VideoCodec.H264, EncoderVendor.Intel, "obs_qsv11_v2"),
    ];

    /// <summary>
    /// Best available encoder: the card first (NVIDIA, AMD, then Intel: a dedicated card before the integrated one),
    /// on it the wanted codec, then H.264 (it plays everywhere; HEVC only when asked for, as Windows may not play it), HEVC,
    /// AV1; x264 last.
    /// </summary>
    public static EncoderChoice Pick(VideoCodec wanted, IReadOnlyCollection<string> available)
    {
        foreach (var vendor in new[] { EncoderVendor.Nvidia, EncoderVendor.Amd, EncoderVendor.Intel })
            foreach (VideoCodec codec in new[] { wanted, VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 }.Distinct())
                foreach (var row in Table.Where(r => r.Vendor == vendor && r.Codec == codec))
                    if (available.Contains(row.Id))
                        return new EncoderChoice(row.Id, row.Vendor, row.Codec);
        return new EncoderChoice(X264, EncoderVendor.Software, VideoCodec.H264);
    }

    /// <summary>An encoder as the Status page names it: "HEVC (AMD)", "H.264 (CPU)".</summary>
    public static string Describe(EncoderChoice choice) =>
        $"{CodecName(choice.Codec)} ({(choice.Vendor == EncoderVendor.Software ? "CPU" : choice.Vendor == EncoderVendor.Nvidia ? "NVIDIA" : choice.Vendor == EncoderVendor.Amd ? "AMD" : "Intel")})";

    /// <summary>The choice an encoder id is (x264 for an unknown one).</summary>
    public static EncoderChoice ById(string id) =>
        Table.FirstOrDefault(r => r.Id == id) is { Id: not null } row ? new EncoderChoice(row.Id, row.Vendor, row.Codec)
            : new EncoderChoice(X264, EncoderVendor.Software, VideoCodec.H264);

    public static string CodecName(VideoCodec codec) => codec switch { VideoCodec.Av1 => "AV1", VideoCodec.Hevc => "HEVC", _ => "H.264" };

    /// <summary>
    /// The encoders of the card <see cref="Pick"/> records on (NVIDIA, AMD, then Intel): a laptop's integrated chip with AV1
    /// beside an NVIDIA card without it must not make AV1 look available. Empty when there is no card encoder.
    /// </summary>
    public static IReadOnlyCollection<string> FirstCard(IReadOnlyCollection<string> available) =>
        new[] { EncoderVendor.Nvidia, EncoderVendor.Amd, EncoderVendor.Intel }
            .Select(v => Table.Where(r => r.Vendor == v && available.Contains(r.Id)).Select(r => r.Id).ToArray())
            .FirstOrDefault(ids => ids.Length > 0) ?? [];

    /// <summary>Whether a hardware encoder exists for <paramref name="codec"/>.</summary>
    public static bool HasHardware(VideoCodec codec, IReadOnlyCollection<string> available) =>
        Table.Any(r => r.Codec == codec && available.Contains(r.Id));

    /// <summary>CPU encoding is kept to 1080p at 30 fps so it stays usable while gaming.</summary>
    public static ((int W, int H) Size, int Fps) LimitForSoftware((int W, int H) size, int fps)
    {
        var (w, h) = size;
        if (h > 1080)
        {
            w = (int)Math.Round(w * 1080.0 / h) & ~1;
            h = 1080;
        }
        return ((w, h), Math.Min(fps, 30));
    }

    /// <summary>libobs encoder settings for <paramref name="choice"/>, from the quality numbers in <paramref name="plan"/>.</summary>
    public static Dictionary<string, object> Settings(EncoderPlan plan, EncoderChoice choice)
    {
        // plan.Cq is on the 1-63 scale for AV1 and the 1-51 scale for HEVC/H.264 (see EncoderPresets).
        long cq = plan.Cq;
        long cq51 = choice.Codec == VideoCodec.Av1 ? (long)Math.Round(plan.Cq * 51.0 / 63.0, MidpointRounding.AwayFromZero) : plan.Cq;
        string profile = choice.Codec == VideoCodec.H264 ? "high" : "main";

        var s = new Dictionary<string, object> { ["keyint_sec"] = (long)plan.KeyintSec };
        switch (choice.Vendor)
        {
            case EncoderVendor.Nvidia:
                s["rate_control"] = "CQVBR";
                s["target_quality"] = cq;
                s["max_bitrate"] = (long)plan.MaxBitrateKbps;
                s["bitrate"] = (long)plan.MaxBitrateKbps;
                s["preset"] = plan.Preset;
                s["tune"] = "hq";
                s["multipass"] = "qres";
                s["profile"] = profile;
                s["bf"] = choice.NoBFrames ? 0L : (long)plan.BFrames;
                s["lookahead"] = false;
                s["adaptive_quantization"] = true;
                break;
            case EncoderVendor.Amd:
                s["rate_control"] = "QVBR";
                s["cqp"] = choice.Codec == VideoCodec.Av1 ? cq : cq51;
                s["bitrate"] = (long)plan.MaxBitrateKbps;
                s["preset"] = "quality";
                s["profile"] = profile;
                s["bf"] = choice.Codec == VideoCodec.Hevc ? 0L : 2L;
                break;
            case EncoderVendor.Intel:
                s["rate_control"] = "ICQ";
                s["icq_quality"] = cq51;
                s["max_bitrate"] = (long)plan.MaxBitrateKbps;
                s["target_usage"] = "TU4";
                s["profile"] = profile;
                break;
            default:
                s["rate_control"] = "CRF";
                s["crf"] = Math.Max(cq51, 20);
                s["preset"] = choice.X264Preset;
                s["profile"] = "high";
                break;
        }
        return s;
    }
}
