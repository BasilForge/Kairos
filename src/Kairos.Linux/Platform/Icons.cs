using Kairos.Settings;
using SkiaSharp;

namespace Kairos.App;

/// <summary>
/// Kairos's mark: a rounded plate in the window's brown, a ring of three arcs and a dot in the theme color. The same
/// drawing as the Windows app's, in Skia.
/// </summary>
public static class AppIconArt
{
    /// <summary>The ring's arcs (start, sweep; degrees clockwise from 3 o'clock), measured from the logo in the sidebar.</summary>
    static readonly (float Start, float Sweep)[] Arcs = [(354.5f, 101.5f), (118.5f, 95.5f), (236.5f, 95.5f)];

    public static void DrawRing(SKCanvas canvas, SKPaint stroke, SKRect box)
    {
        foreach (var (start, sweep) in Arcs)
            canvas.DrawArc(box, start, sweep, false, stroke);
    }

    public static SKBitmap Draw(int size, uint accent)
    {
        var bmp = new SKBitmap(size, size);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        float w = size - 1;
        using (var fill = new SKPaint { IsAntialias = true, Color = Opaque(ThemeColors.Background) })
            canvas.DrawRoundRect(new SKRect(0, 0, w, w), size * 0.22f, size * 0.22f, fill);
        float margin = size * 0.2f;
        using (var ring = new SKPaint { IsAntialias = true, IsStroke = true, StrokeWidth = Math.Max(1.5f, size * 0.09f), Color = Opaque(accent) })
            DrawRing(canvas, ring, new SKRect(margin, margin, size - margin, size - margin));
        float dot = size * 0.24f;
        using (var brush = new SKPaint { IsAntialias = true, Color = Opaque(accent) })
            canvas.DrawOval(new SKRect((size - dot) / 2, (size - dot) / 2, (size + dot) / 2, (size + dot) / 2), brush);
        return bmp;
    }

    public static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static SKColor Opaque(uint rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
}
