using System.Text.RegularExpressions;

namespace Kairos.Engine;

/// <summary>What a graphics card's name says about its video encoder (the names Windows gives the display adapters).</summary>
public static partial class Gpus
{
    /// <summary>
    /// NVENC takes B-frames in HEVC from Turing on: every RTX, and the GTX 16 cards but the 1650 (it has Volta's
    /// encoder). Pascal (GTX 10, MX), Maxwell (GTX 900) and older fail to start an HEVC encoder that asks for them.
    /// </summary>
    public static bool NvencHevcBFrames(string? gpu)
    {
        if (gpu == null || !gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            return false;
        if (gpu.Contains("RTX", StringComparison.OrdinalIgnoreCase))
            return true;
        var gtx16 = Gtx16().Match(gpu);
        return gtx16.Success && !(gtx16.Groups[1].Value == "50" && !gpu.Contains("SUPER", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Intel graphics too old for Quick Sync in libobs (Haswell, Broadwell and before: "HD Graphics", "HD Graphics
    /// 4600", "Iris Pro 5200"); Skylake's "HD Graphics 530" and newer ("UHD", "Iris Xe", "Arc", "Intel(R) Graphics") work.
    /// </summary>
    public static bool IsOldIntel(string? gpu)
    {
        if (gpu == null || !gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return false;
        if (OldIris().IsMatch(gpu))
            return true;
        var hd = HdGraphics().Match(gpu);
        if (!hd.Success)
            return false;
        return hd.Groups[1].Value is not { Length: > 0 } number || int.Parse(number) is >= 1000 and < 6500 or < 500;
    }

    [GeneratedRegex(@"GTX\s*16(\d\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Gtx16();

    [GeneratedRegex(@"(?<!U)HD Graphics\s*(\d*)", RegexOptions.IgnoreCase)]
    private static partial Regex HdGraphics();

    [GeneratedRegex(@"Iris.*[56]\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex OldIris();
}
