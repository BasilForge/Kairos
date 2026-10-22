using Kairos.Settings;

namespace Kairos.Engine;

public static class ResolutionOptions
{
    internal static readonly (OutputResolution Value, int Height)[] Presets =
    [
        (OutputResolution.P2160, 2160), (OutputResolution.P1440, 1440), (OutputResolution.P1080, 1080), (OutputResolution.P720, 720),
    ];

    /// <summary>The height a choice records at (Native: the screen's own, <paramref name="native"/>).</summary>
    public static int Height(OutputResolution resolution, int native) =>
        Array.Find(Presets, p => p.Value == resolution) is { Height: > 0 } preset ? preset.Height : native;

    /// <summary>The biggest preset no taller than <paramref name="height"/> (720p below that).</summary>
    public static OutputResolution ForHeight(int height) => Presets.FirstOrDefault(p => p.Height <= height, Presets[^1]).Value;

    /// <summary>Native first, then every preset strictly smaller than the monitor (no upscaling choices).</summary>
    public static List<OutputResolution> For(int baseHeight) =>
        [OutputResolution.Native, .. Presets.Where(p => p.Height < baseHeight).Select(p => p.Value)];
}
