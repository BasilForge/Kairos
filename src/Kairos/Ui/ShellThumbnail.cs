using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Kairos.Ui;

/// <summary>Video thumbnails from the Windows shell, produced on one background STA thread.</summary>
public static class ShellThumbnail
{
    static readonly BlockingCollection<(string Path, Action<BitmapSource?> Done)> Queue = [];

    static ShellThumbnail()
    {
        var worker = new Thread(() =>
        {
            foreach (var (path, done) in Queue.GetConsumingEnumerable())
            {
                BitmapSource? image = null;
                try { image = Load(path, 320, 180); }
                catch (Exception) { }
                done(image);
            }
        }) { IsBackground = true, Name = "Kairos thumbnails", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>Queues a thumbnail; <paramref name="done"/> runs on the worker thread with a frozen image or null.</summary>
    public static void Request(string path, Action<BitmapSource?> done) => Queue.Add((path, done));

    static BitmapSource? Load(string path, int width, int height)
    {
        IntPtr hbitmap = App.ShellImage.Get(path, width, height);
        if (hbitmap == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            App.ShellImage.Delete(hbitmap);
        }
    }
}
