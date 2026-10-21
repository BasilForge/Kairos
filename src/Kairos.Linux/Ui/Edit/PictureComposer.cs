using System.Windows.Media.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Kairos.Engine.Edit;
using SkiaSharp;

namespace Kairos.Ui.Edit;

/// <summary>
/// Writes an edited screenshot: the colour look and blurred boxes on the picture's own pixels, the items drawn on top
/// at full size, then the crop. Written through a part file; the original is only read.
/// </summary>
public static class PictureComposer
{
    public static void Save(string source, EditProject p, string target)
    {
        using var picture = Compose(source, p);
        string temp = $"{target}.{Guid.NewGuid():N}.part";
        try
        {
            picture.Bitmap.Save(temp);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>The picture with the edit's pixels and items drawn on it.</summary>
    sealed class Canvas(Avalonia.Media.Imaging.WriteableBitmap pixels, EditProject p, Avalonia.Size frame) : Control
    {
        public override void Render(DrawingContext context)
        {
            context.DrawImage(pixels, new Avalonia.Rect(0, 0, pixels.Size.Width, pixels.Size.Height), new Avalonia.Rect(frame));
            foreach (var item in p.Items)
                ItemRenderer.Draw(context, item, new System.Windows.Size(frame.Width, frame.Height));
        }
    }

    /// <summary>The edited picture (also what the editor shows for a screenshot). Call on the UI thread.</summary>
    public static BitmapSource Compose(string source, EditProject p)
    {
        var (width, height, bgra) = Read(source);
        if (ColourLooks.Filter(p.Look, p.LookStrength) is { } look)
            using (var filter = new FrameFilter(width, height, look))
                filter.Apply(bgra);
        foreach (var blur in p.Items.OfType<BlurItem>())
            FrameFilter.Blur(bgra, width, height, Pixels(blur.Box, width, height));

        var pixels = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var locked = pixels.Lock())
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, locked.Address, Math.Min(bgra.Length, locked.RowBytes * height));
        var frame = new Avalonia.Size(width, height);
        var canvas = new Canvas(pixels, p, frame) { Width = width, Height = height };
        canvas.Measure(frame);
        canvas.Arrange(new Avalonia.Rect(frame));
        var rendered = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rendered.Render(canvas);
        pixels.Dispose();
        if (p.Crop is not { } crop)
            return new BitmapSource(rendered);

        // Crop through Skia: Avalonia has no cropped bitmap.
        var box = Pixels(crop, width, height);
        using var stream = new MemoryStream();
        rendered.Save(stream);
        rendered.Dispose();
        stream.Position = 0;
        using var whole = SKBitmap.Decode(stream);
        using var part = new SKBitmap();
        whole.ExtractSubset(part, new SKRectI(box.X, box.Y, box.X + box.Width, box.Y + box.Height));
        using var image = SKImage.FromBitmap(part);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new BitmapSource(new Bitmap(new MemoryStream(data.ToArray())));
    }

    static (int Width, int Height, byte[] Bgra) Read(string path)
    {
        using var decoded = SKBitmap.Decode(path);
        using var bitmap = decoded.Copy(SKColorType.Bgra8888);
        return (bitmap.Width, bitmap.Height, bitmap.Bytes);
    }

    static System.Windows.Int32Rect Pixels(System.Windows.Rect box, int width, int height)
    {
        int x = Math.Clamp((int)Math.Round(box.X * width), 0, width - 1);
        int y = Math.Clamp((int)Math.Round(box.Y * height), 0, height - 1);
        int w = Math.Clamp((int)Math.Round(box.Width * width), 1, width - x);
        int h = Math.Clamp((int)Math.Round(box.Height * height), 1, height - y);
        return new System.Windows.Int32Rect(x, y, w, h);
    }
}
