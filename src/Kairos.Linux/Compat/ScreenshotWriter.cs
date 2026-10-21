using System.Runtime.InteropServices;
using SkiaSharp;

namespace Kairos.Engine;

/// <summary>One copied frame of the canvas: 32-bit BGRA rows, <see cref="Width"/> × 4 bytes each.</summary>
public sealed record ScreenshotFrame(byte[] Bgra, int Width, int Height);

/// <summary>Saves a screenshot as a PNG: opaque, at the frame's own size, never over another file.</summary>
public static class ScreenshotWriter
{
    /// <summary>A part file this old was left by a crash (a write takes well under a second).</summary>
    static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Writes <paramref name="frame"/> to <paramref name="path"/>, or to "name (2).png" and so on when that is taken.
    /// The file appears whole (written beside it, then renamed). Returns where it went.
    /// </summary>
    public static string Write(string path, ScreenshotFrame frame)
    {
        string folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        ClearLeftovers(folder);
        // A dot file: the Clips page never lists it, even for the moment it exists.
        string temp = Path.Combine(folder, "." + Path.GetFileName(path) + ".part");
        var pin = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            // Opaque: the canvas is see-through where nothing is drawn, the picture is not.
            var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var image = SKImage.FromPixels(info, pin.AddrOfPinnedObject(), frame.Width * 4);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(temp);
            data.SaveTo(file);
        }
        finally
        {
            pin.Free();
        }
        try
        {
            return ClipOrganizer.Move(temp, path);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>Part files a crash left in <paramref name="folder"/>; one being written right now is left alone.</summary>
    static void ClearLeftovers(string folder)
    {
        foreach (var part in new DirectoryInfo(folder).EnumerateFiles("*.png.part"))
            if (DateTime.Now - part.LastWriteTime > Stale)
                try
                {
                    part.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
    }
}
