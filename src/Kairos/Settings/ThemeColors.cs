namespace Kairos.Settings;

/// <summary>
/// Kairos's one theme (the user's pick): a warm brown-grey window with a rose accent. The window colors live in
/// Ui/Theme.xaml; these are the ones code draws with (tray icon, app icon, notice card) and the shades derived from them.
/// </summary>
public static class ThemeColors
{
    public const uint Accent = 0xF48BA4;
    public const uint Background = 0x2E2726;
    /// <summary>Text and icons on the accent: the window's brown, darker (white on rose would be 2.3:1).</summary>
    public const uint OnAccent = 0x221C1B;
    /// <summary>Theme.xaml's Warning and Success: something to look at, and saved.</summary>
    public const uint Warning = 0xFFB020, Saved = 0x2ED47A;

    /// <summary>Mouse-over shade: 15% toward white.</summary>
    public static uint Hover => Mix(Accent, 0xFFFFFF, 0.15);

    /// <summary>Selected-row tint: 13% of the accent over the window background.</summary>
    public static uint Soft => Mix(Background, Accent, 0.13);

    public static uint Mix(uint from, uint to, double amount)
    {
        uint Channel(int shift)
        {
            double a = (from >> shift) & 0xFF, b = (to >> shift) & 0xFF;
            return (uint)Math.Round(a + (b - a) * amount) << shift;
        }
        return Channel(16) | Channel(8) | Channel(0);
    }

    /// <summary>WCAG relative luminance.</summary>
    public static double Luminance(uint rgb)
    {
        double Linear(int shift)
        {
            double c = ((rgb >> shift) & 0xFF) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(16) + 0.7152 * Linear(8) + 0.0722 * Linear(0);
    }

    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
