using SkiaSharp;

namespace Kairos.Engine.Share;

/// <summary>A JPEG copy of a screenshot for a site that will not take the PNG's size.</summary>
public static class PictureJpeg
{
    static readonly int[] Qualities = [95, 90, 85, 80];

    /// <summary>
    /// Writes <paramref name="target"/> no bigger than <paramref name="maxBytes"/>: the best quality that fits, then,
    /// if even quality 80 is too big, the picture a little smaller each time.
    /// </summary>
    public static ShareResult Make(string source, string target, long maxBytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var original = SKBitmap.Decode(source) ?? throw new ShareException("The picture could not be read.");
        double scale = 1;
        for (int round = 0; round < 8; round++, scale *= 0.85)
        {
            using var picture = scale == 1 ? original.Copy() : original.Resize(
                new SKImageInfo(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale))),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var image = SKImage.FromBitmap(picture);
            foreach (int quality in Qualities)
            {
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (data.Size <= maxBytes)
                {
                    File.WriteAllBytes(target, data.ToArray());
                    return new ShareResult(target, data.Size, $"jpeg q{quality}");
                }
            }
        }
        throw new ShareException("The picture could not be made small enough for this site.");
    }
}
