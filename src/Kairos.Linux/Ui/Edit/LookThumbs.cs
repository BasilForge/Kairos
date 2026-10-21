using System.Windows.Media.Imaging;
using Kairos.Engine.Edit;
using Kairos.Engine.Share;

namespace Kairos.Ui.Edit;

/// <summary>
/// The colour look tiles' pictures: the frame at the playhead, small, through each look (the same filters the saved clip
/// gets), so a look is chosen by what it does to this clip. Made in the background; a look that fails shows its name only.
/// </summary>
public static class LookThumbs
{
    public const int Width = 112, Height = 64;

    /// <summary>A BGRA picture shrunk to the tile size (each tile pixel the average of the pixels it covers).</summary>
    public static byte[] Shrink(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var small = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        {
            int y0 = y * height / Height, y1 = Math.Max(y0 + 1, (y + 1) * height / Height);
            for (int x = 0; x < Width; x++)
            {
                int x0 = x * width / Width, x1 = Math.Max(x0 + 1, (x + 1) * width / Width);
                // A few samples per tile pixel are enough at this size.
                int b = 0, g = 0, r = 0, n = 0;
                for (int sy = y0; sy < y1; sy += Math.Max(1, (y1 - y0) / 3))
                    for (int sx = x0; sx < x1; sx += Math.Max(1, (x1 - x0) / 3))
                    {
                        int i = (sy * width + sx) * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                        n++;
                    }
                int d = (y * Width + x) * 4;
                small[d] = (byte)(b / n);
                small[d + 1] = (byte)(g / n);
                small[d + 2] = (byte)(r / n);
                small[d + 3] = 255;
            }
        }
        return small;
    }

    /// <summary>One small picture per look (null where the filter failed), made on a background thread.</summary>
    public static Task<IReadOnlyList<BitmapSource?>> Make(byte[] small, IReadOnlyList<ColourLook> looks) => Task.Run(() =>
    {
        var made = new List<BitmapSource?>();
        foreach (var look in looks)
        {
            try
            {
                var pixels = (byte[])small.Clone();
                if (ColourLooks.Filter(look, 1) is { } filter)
                {
                    using var frame = new FrameFilter(Width, Height, filter);
                    frame.Apply(pixels);
                }
                made.Add(BitmapSource.FromBgra(pixels, Width, Height));
            }
            catch (Exception e) when (e is ShareException or InvalidOperationException or DllNotFoundException)
            {
                made.Add(null);
            }
        }
        return (IReadOnlyList<BitmapSource?>)made;
    });
}
