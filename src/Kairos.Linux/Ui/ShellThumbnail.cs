using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using Kairos.Engine.Share;
using Kairos.App;
using SkiaSharp;

namespace Kairos.Ui;

/// <summary>Thumbnails of clips and screenshots, made on one background thread: a video frame, or the picture scaled down.</summary>
public static class ShellThumbnail
{
    const int Width = 320;

    static readonly BlockingCollection<(string Path, Action<BitmapSource?> Done)> Queue = [];

    static ShellThumbnail()
    {
        new Thread(() =>
        {
            foreach (var (path, done) in Queue.GetConsumingEnumerable())
            {
                BitmapSource? image = null;
                try { image = Load(path); }
                catch (Exception) { }
                done(image);
            }
        }) { IsBackground = true, Name = "Kairos thumbnails", Priority = ThreadPriority.BelowNormal }.Start();
    }

    /// <summary>Queues a thumbnail; <paramref name="done"/> runs on the worker thread with the image or null.</summary>
    public static void Request(string path, Action<BitmapSource?> done) => Queue.Add((path, done));

    static BitmapSource? Load(string path)
    {
        if (Engine.ClipOrganizer.IsPicture(path))
        {
            using var picture = SKBitmap.Decode(path);
            if (picture == null)
                return null;
            int height = Math.Max(1, picture.Height * Width / picture.Width);
            using var small = picture.Resize(new SKImageInfo(Width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
            return BitmapSource.FromBgra(small.Bytes, small.Width, small.Height);
        }

        using var grabber = new FrameGrabber(path);
        var frame = grabber.Grab(1.0, Width, exact: false);
        return frame == null ? null : BitmapSource.FromBgra(frame.Bgra, frame.Width, frame.Height);
    }
}
