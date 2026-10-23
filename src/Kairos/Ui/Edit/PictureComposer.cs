using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

/// <summary>
/// Writes an edited screenshot: the colour look and blurred boxes on the picture's own pixels, the items drawn on top
/// at full size, then the crop. Written through a part file; the original is only read.
/// </summary>
public static class PictureComposer
{
    /// <returns>The file written: <paramref name="target"/>, or "name (2)" beside it when that is open in another program.</returns>
    public static string Save(string source, EditProject p, string target)
    {
        var picture = Compose(source, p);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        string temp = $"{target}.{Guid.NewGuid():N}.part";
        try
        {
            using (var file = File.Create(temp))
                encoder.Save(file);
            return Engine.Share.ClipTranscoder.Place(temp, target);
        }
        finally
        {
            Engine.Share.ClipTranscoder.TryDelete(temp);
        }
    }

    /// <summary>The edited picture (also what the editor shows for a screenshot).</summary>
    public static BitmapSource Compose(string source, EditProject p)
    {
        var (width, height, bgra) = Read(source);
        if (ColourLooks.Filter(p.Look, p.LookStrength) is { } look)
            using (var filter = new FrameFilter(width, height, look))
                filter.Apply(bgra);
        foreach (var blur in p.Items.OfType<BlurItem>())
            FrameFilter.Blur(bgra, width, height, Pixels(blur.Box, width, height));

        var visual = new DrawingVisual();
        var frame = new Size(width, height);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4), new Rect(frame));
            foreach (var item in p.Items)
                ItemRenderer.Draw(dc, item, frame);
        }
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        if (p.Crop is not { } crop)
            return rendered;
        var box = Pixels(crop, width, height);
        var cropped = new CroppedBitmap(rendered, box);
        cropped.Freeze();
        return cropped;
    }

    static (int Width, int Height, byte[] Bgra) Read(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        var bgra = new byte[w * h * 4];
        converted.CopyPixels(bgra, w * 4, 0);
        return (w, h, bgra);
    }

    static Int32Rect Pixels(Rect box, int width, int height)
    {
        int x = Math.Clamp((int)Math.Round(box.X * width), 0, width - 1);
        int y = Math.Clamp((int)Math.Round(box.Y * height), 0, height - 1);
        int w = Math.Clamp((int)Math.Round(box.Width * width), 1, width - x);
        int h = Math.Clamp((int)Math.Round(box.Height * height), 1, height - y);
        return new Int32Rect(x, y, w, h);
    }
}
