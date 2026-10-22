using Kairos.Engine;

namespace Kairos.Tests;

/// <summary>
/// a user's report (1.0.1, a Ryzen 5 5600GT with a GTX 1660 Ti): every clip of the desktop black, no error in the log.
/// A black screen picture makes Kairos try Windows Graphics Capture, kept when it shows the screen.
/// </summary>
public static class ScreenCheckTests
{
    static ScreenshotFrame Frame(Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var bgra = new byte[ScreenCheck.Width * ScreenCheck.Height * 4];
        for (int y = 0; y < ScreenCheck.Height; y++)
            for (int x = 0; x < ScreenCheck.Width; x++)
            {
                var (r, g, b) = pixel(x, y);
                int i = (y * ScreenCheck.Width + x) * 4;
                (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]) = (b, g, r, 255);
            }
        return new ScreenshotFrame(bgra, ScreenCheck.Width, ScreenCheck.Height);
    }

    [Test]
    public static void An_empty_look_is_not_taken_for_black()
    {
        Assert.True(!ScreenCheck.IsBlack(new ScreenshotFrame([], 0, 0)), "nothing seen is no proof of black (the screen method stays)");
    }

    [Test]
    public static void Only_a_picture_with_no_light_counts_as_black()
    {
        Assert.True(ScreenCheck.IsBlack(Frame((_, _) => (0, 0, 0))), "all black");
        Assert.True(ScreenCheck.IsBlack(Frame((_, _) => (6, 6, 6))), "near black");
        Assert.True(!ScreenCheck.IsBlack(Frame((_, y) => y >= 33 ? ((byte)40, (byte)40, (byte)40) : ((byte)0, (byte)0, (byte)0))),
            "a black wallpaper with the taskbar");
        Assert.True(!ScreenCheck.IsBlack(Frame((x, y) => ((byte)(x * 4), (byte)(y * 7), (byte)90))), "a desktop");
    }
}
