using System.Runtime.InteropServices;

namespace Kairos.App;

/// <summary>
/// A file's picture from the Windows shell (its thumbnail), without WPF: the Library turns it into an image
/// (<see cref="Ui.ShellThumbnail"/>); the engine only has it made, so the shell keeps it in its cache.
/// </summary>
public static class ShellImage
{
    /// <summary>A file kept online only is asked for its cached thumbnail only: making one would download it.</summary>
    public static int Flags(FileAttributes attributes) => 0x1 /* BIGGERSIZEOK */ | (CloudFiles.IsLocal(attributes) ? 0 : 0x10 /* INCACHEONLY */);

    /// <summary>The thumbnail as an HBITMAP (let go with <see cref="Delete"/>), or zero. Called on an STA thread.</summary>
    public static IntPtr Get(string path, int width, int height)
    {
        int flags = Flags(File.GetAttributes(path));
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out IShellItemImageFactory factory);
        try
        {
            return factory.GetImage(new SIZE { cx = width, cy = height }, flags, out IntPtr hbitmap) == 0 ? hbitmap : IntPtr.Zero;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    public static void Delete(IntPtr hbitmap) => DeleteObject(hbitmap);

    /// <summary>Has the shell make the file's thumbnail (on a short background thread), then runs <paramref name="then"/> there.</summary>
    public static void Warm(string path, Action then)
    {
        var worker = new Thread(() =>
        {
            try
            {
                Delete(Get(path, 320, 180));
            }
            catch (Exception)
            {
                // No thumbnail: the file is still freed.
            }
            then();
        }) { IsBackground = true, Name = "Kairos thumbnail", Priority = ThreadPriority.BelowNormal };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
