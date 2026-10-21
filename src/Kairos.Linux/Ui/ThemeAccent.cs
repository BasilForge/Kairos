using Avalonia.Controls;
using Avalonia.Media;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>Puts the chosen theme color into the resources the theme and the pages reference dynamically.</summary>
public static class ThemeAccent
{
    public static void Apply(IResourceDictionary resources)
    {
        resources["Accent"] = Brush(ThemeColors.Accent);
        resources["AccentHover"] = Brush(ThemeColors.Hover);
        resources["AccentSoft"] = Brush(ThemeColors.Soft);
        resources["OnAccent"] = Brush(ThemeColors.OnAccent);
        resources["AccentColor"] = ColorOf(ThemeColors.Accent);
        // Fluent's own controls (sliders, focus rings, selection) take the accent from these; without them they use the system blue.
        foreach (string key in new[] { "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
                     "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3" })
            resources[key] = ColorOf(ThemeColors.Accent);
        foreach (string key in new[] { "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                     "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed",
                     "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush" })
            resources[key] = Brush(ThemeColors.Accent);
    }

    public static Color ColorOf(uint rgb) => Color.FromRgb((byte)(rgb >> 16 & 0xFF), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));

    public static SolidColorBrush Brush(uint rgb) => new(ColorOf(rgb));
}
