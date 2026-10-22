using System.Windows;

namespace Kairos.Engine.Edit;

/// <summary>Zoom ("VideoZoom" beside the timeline's own zoom): areas of the picture as 0..1 of the frame, and the ease between them.</summary>
public static class VideoZoom
{
    public static readonly Rect Full = new(0, 0, 1, 1);

    /// <summary>How long a zoom takes to go in or out, in seconds as seen.</summary>
    public const double EaseSeconds = 0.3;

    /// <summary>The smallest zoom: a tenth of each side.</summary>
    public const double MinSize = 0.1;

    /// <summary>0..1 over <see cref="EaseSeconds"/> from an edge, smooth at both ends (smoothstep).</summary>
    public static double Ease(double secondsFromEdge)
    {
        double t = Math.Clamp(secondsFromEdge / EaseSeconds, 0, 1);
        return t * t * (3 - 2 * t);
    }

    public static Rect Lerp(Rect from, Rect to, double t) => new(
        from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t,
        from.Width + (to.Width - from.Width) * t, from.Height + (to.Height - from.Height) * t);

    /// <summary>An area in pixels of a <paramref name="width"/>×<paramref name="height"/> picture: even (4:2:0), inside it.</summary>
    public static (int W, int H, int X, int Y) Crop(Rect zoom, int width, int height)
    {
        static int Even(double v) => (int)Math.Round(v / 2) * 2;
        int w = Math.Clamp(Even(zoom.Width * width), 2, width & ~1), h = Math.Clamp(Even(zoom.Height * height), 2, height & ~1);
        return (w, h, Math.Clamp(Even(zoom.X * width), 0, width - w), Math.Clamp(Even(zoom.Y * height), 0, height - h));
    }

    /// <summary>
    /// A drawn box as a zoom: the same share of each side (the video's shape), as wide as the wider of the two, around the
    /// box's centre, no smaller than <see cref="MinSize"/>, inside the picture.
    /// </summary>
    public static Rect Fit(Rect drawn)
    {
        double side = Math.Clamp(Math.Max(drawn.Width, drawn.Height), MinSize, 1);
        double x = Math.Clamp(drawn.X + drawn.Width / 2 - side / 2, 0, 1 - side);
        double y = Math.Clamp(drawn.Y + drawn.Height / 2 - side / 2, 0, 1 - side);
        return new Rect(x, y, side, side);
    }
}
