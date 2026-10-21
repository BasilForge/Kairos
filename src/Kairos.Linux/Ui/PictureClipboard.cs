using System.Windows.Media.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace Kairos.Ui;

/// <summary>A screenshot as a picture to show, and onto the clipboard.</summary>
public static class PictureClipboard
{
    /// <summary>The picture in <paramref name="path"/>, or null when it is gone or unreadable.</summary>
    public static BitmapSource? TryLoad(string path)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            if (bitmap == null)
                return null;
            using var bgra = bitmap.Copy(SKColorType.Bgra8888);
            return BitmapSource.FromBgra(bgra.Bytes, bgra.Width, bgra.Height);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts the picture on the clipboard as an image; false when there is no clipboard to put it on.</summary>
    public static bool Copy(string path)
    {
        try
        {
            var clipboard = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault()?.Clipboard;
            if (clipboard == null)
                return false;
            using var stream = File.OpenRead(path);
            var bitmap = new Bitmap(stream);
            _ = clipboard.SetBitmapAsync(bitmap); // not waited for: on the UI thread that would never return
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
