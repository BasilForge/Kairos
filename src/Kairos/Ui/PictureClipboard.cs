using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Kairos.Ui;

/// <summary>A picture on the clipboard both as an image (Paint, WhatsApp, X's composer) and as the file (Discord, Explorer).</summary>
public static class PictureClipboard
{
    /// <summary>What <see cref="Copy"/> puts on the clipboard, without touching it.</summary>
    public static DataObject Data(string path)
    {
        var data = new DataObject();
        if (TryLoad(path) is { } image)
            data.SetImage(image); // an unreadable picture still goes as the file
        data.SetFileDropList(new StringCollection { path });
        return data;
    }

    /// <summary>Puts the picture on the clipboard. False when another app kept it busy.</summary>
    public static bool Copy(string path)
    {
        if (App.SideEffects.Held($"copy picture {path}"))
            return true;
        var data = Data(path);
        // Another app can hold the clipboard for a moment; a few short retries cover it.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }

    /// <summary>The picture, or null when it is gone or cannot be read.</summary>
    public static BitmapSource? TryLoad(string path)
    {
        try
        {
            return Load(path);
        }
        // A file cut short or not a picture after all: WPF's decoders say so with FileFormatException or a COM error.
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException
                                       or FormatException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>The picture, read whole so the file is not kept open (it can still be deleted or moved).</summary>
    public static BitmapSource Load(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
