using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Where the camera goes on the picture: a corner box, or the top band of a 9:16 picture.</summary>
public static class CameraMath
{
    /// <summary>
    /// The camera's own file takes every n-th frame so it runs at 60 fps or less: a camera gives 30 or 60, and a 144 fps
    /// file would only repeat its frames at a higher bitrate.
    /// </summary>
    public static uint FileFpsDivisor(int fps) => (uint)Math.Max(1, (fps + 59) / 60);

    /// <summary>
    /// The biggest of a camera's sizes up to its own file's 1080p, either way up (a phone held upright: 1080x1920); null
    /// when none fits, and the camera opens at its own default.
    /// </summary>
    public static string? BestSize(IEnumerable<string> sizes) => sizes.Where(s => FitArea(s) > 0).MaxBy(FitArea);

    static long FitArea(string size) => TryParseSize(size, out int w, out int h)
        && Math.Max(w, h) <= CameraLayer.FileWidth && Math.Min(w, h) <= CameraLayer.FileHeight ? (long)w * h : 0;

    /// <summary>A size as win-dshow lists a camera's ("1920x1080").</summary>
    public static bool TryParseSize(string size, out int w, out int h)
    {
        (w, h) = (0, 0);
        return size.Split('x') is [var a, var b] && int.TryParse(a, out w) && int.TryParse(b, out h) && w > 0 && h > 0;
    }

    /// <summary>The top band of a vertical picture the camera fills.</summary>
    public const double CameraBand = 0.35;

    /// <summary>
    /// The corner box in pixels: <see cref="CameraSettings.Size"/> of the width, the camera's shape (a square for a circle),
    /// at its place, pushed back inside the picture.
    /// </summary>
    public static (double X, double Y, double W, double H) Corner(CameraSettings c, int width, int height, double cameraAspect)
    {
        double w = Math.Round(c.Size * width);
        double h = Math.Round(c.Shape == CameraShape.Round ? w : w / (cameraAspect > 0 ? cameraAspect : 16 / 9.0));
        if (h > height)
        {
            w = Math.Round(w * height / h);
            h = height;
        }
        return (Math.Clamp(Math.Round(c.X * width), 0, width - w), Math.Clamp(Math.Round(c.Y * height), 0, height - h), w, h);
    }

    /// <summary>
    /// The 9:16 picture for a size choice: 1080x1920 at 1080p, 720x1280 at 720p, 1440x2560 at 1440p and 4K; Native takes
    /// the screen's height as its width (1440 at most).
    /// </summary>
    public static (int W, int H) VerticalSize(OutputResolution resolution, int screenHeight)
    {
        int width = Math.Min(ResolutionOptions.Height(resolution, screenHeight), 1440) & ~1;
        return (width, (int)Math.Round(width * 16 / 9.0) & ~1);
    }

    /// <summary>The camera's band (the top <see cref="CameraBand"/>) and the game's below it, in pixels, even.</summary>
    public static ((int X, int Y, int W, int H) Camera, (int X, int Y, int W, int H) Game) Bands(int width, int height)
    {
        int top = (int)Math.Round(height * CameraBand) & ~1;
        return ((0, 0, width, top), (0, top, width, height - top));
    }
}
