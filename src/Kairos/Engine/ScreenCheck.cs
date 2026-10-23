namespace Kairos.Engine;

/// <summary>
/// Screen capture's method on PCs where Automatic (DXGI duplication) gives a black picture without an error (a user's
/// report: a Ryzen 5600GT with a GTX 1660 Ti). Windows Graphics Capture is the other way Windows hands the screen over.
/// </summary>
public static class ScreenCheck
{
    /// <summary>The check's picture: small, the whole canvas scaled down.</summary>
    public const int Width = 64, Height = 36;

    /// <summary>
    /// A picture with no light in it: under half a percent of its pixels above near-black (a dark wallpaper, a window or
    /// the taskbar always show some).
    /// </summary>
    public static bool IsBlack(ScreenshotFrame frame) => IsBlack(LitShare(frame));

    /// <summary>Black by its lit share (<see cref="LitShare"/>): under half a percent.</summary>
    public static bool IsBlack(double litShare) => litShare < 0.005;

    /// <summary>The share of the picture's pixels above near-black (0 to 1): the log says it, so a check can be judged.</summary>
    public static double LitShare(ScreenshotFrame frame)
    {
        int lit = 0, pixels = frame.Width * frame.Height;
        if (pixels <= 0)
            return 1; // nothing seen: no proof of black (as a look that could not be taken)
        var p = frame.Bgra;
        for (int i = 0; i + 2 < p.Length; i += 4)
            if (Math.Max(p[i], Math.Max(p[i + 1], p[i + 2])) > 16)
                lit++;
        return (double)lit / pixels;
    }

    /// <summary>One small look at the whole canvas (<paramref name="canvasWidth"/> x <paramref name="canvasHeight"/>):
    /// black or not, null when no look could be taken.</summary>
    public static async Task<bool?> LookIsBlack(int canvasWidth, int canvasHeight) =>
        await LookLit(canvasWidth, canvasHeight) is { } lit ? IsBlack(lit) : null;

    /// <summary>One small look's lit share (<see cref="LitShare"/>), null when no look could be taken.</summary>
    public static async Task<double?> LookLit(int canvasWidth, int canvasHeight) =>
        await ScreenshotGrabber.Grab(Width, Height, TimeSpan.FromSeconds(2), canvasWidth, canvasHeight) is { } frame ? LitShare(frame) : null;
}
