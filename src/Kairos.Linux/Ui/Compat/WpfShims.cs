// The view models shared with the Windows app talk to WPF's dispatcher, timer and bitmap types. These are the same names
// over Avalonia, with only the members those view models use.
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls;

namespace System.Windows
{
    public sealed class Application
    {
        public static Application? Current { get; } = new();

        public Threading.Dispatcher Dispatcher { get; } = new();

        public IResourceDictionary Resources => Avalonia.Application.Current!.Resources;
    }

    public enum FlowDirection
    {
        LeftToRight,
        RightToLeft,
    }

    public enum GridUnitType
    {
        Auto,
        Pixel,
        Star,
    }

    /// <summary>A grid column's share (the share panel's crop frame); a column takes it as an Avalonia length.</summary>
    public readonly record struct GridLength(double Value, GridUnitType GridUnitType)
    {
        public static implicit operator Avalonia.Controls.GridLength(GridLength length) =>
            new(length.Value, length.GridUnitType switch { GridUnitType.Pixel => Avalonia.Controls.GridUnitType.Pixel, GridUnitType.Star => Avalonia.Controls.GridUnitType.Star, _ => Avalonia.Controls.GridUnitType.Auto });
    }
}

namespace System.Windows.Threading
{
    public sealed class Dispatcher
    {
        /// <summary>The thread's dispatcher: the UI's (the only one these view models are made on).</summary>
        public static Dispatcher CurrentDispatcher { get; } = new();

        public Task BeginInvoke(Action action) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action).GetTask();

        public void Invoke(Action action) => Avalonia.Threading.Dispatcher.UIThread.Invoke(action);
    }

    public sealed class DispatcherTimer
    {
        readonly Avalonia.Threading.DispatcherTimer _timer = new();

        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }

        public bool IsEnabled => _timer.IsEnabled;

        public Dispatcher Dispatcher { get; } = new();

        public event EventHandler? Tick
        {
            add => _timer.Tick += value;
            remove => _timer.Tick -= value;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}

namespace System.Windows.Media.Imaging
{
    /// <summary>A decoded picture the UI can show: an <see cref="IImage"/> that wraps an Avalonia bitmap.</summary>
    public class BitmapSource : IImage, IDisposable
    {
        readonly Bitmap _bitmap;

        public BitmapSource(Bitmap bitmap) => _bitmap = bitmap;

        public Avalonia.Size Size => _bitmap.Size;

        public int PixelWidth => _bitmap.PixelSize.Width;

        public int PixelHeight => _bitmap.PixelSize.Height;

        public Bitmap Bitmap => _bitmap;

        public void Draw(DrawingContext context, Avalonia.Rect sourceRect, Avalonia.Rect destRect) => ((IImage)_bitmap).Draw(context, sourceRect, destRect);

        /// <summary>WPF freezes a bitmap to share it across threads; these are not changed once made.</summary>
        public void Freeze()
        {
        }

        /// <summary>A picture from 32-bit BGRA rows (what the engine's frame grabber returns).</summary>
        public static BitmapSource FromBgra(byte[] bgra, int width, int height)
        {
            var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var locked = bitmap.Lock())
                System.Runtime.InteropServices.Marshal.Copy(bgra, 0, locked.Address, Math.Min(bgra.Length, locked.RowBytes * height));
            return new BitmapSource(bitmap);
        }

        public static BitmapSource Create(int width, int height, double dpiX, double dpiY, System.Windows.Media.PixelFormat format, object? palette, byte[] pixels, int stride) =>
            FromBgra(pixels, width, height);

        public void Dispose() => _bitmap.Dispose();
    }

    /// <summary>A picture a player draws into frame by frame (WPF's WriteableBitmap, BGRA only).</summary>
    public sealed class WriteableBitmap : BitmapSource
    {
        readonly Avalonia.Media.Imaging.WriteableBitmap _writable;

        public WriteableBitmap(int width, int height, double dpiX, double dpiY, System.Windows.Media.PixelFormat format, object? palette)
            : this(new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(dpiX, dpiY), Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul))
        {
        }

        WriteableBitmap(Avalonia.Media.Imaging.WriteableBitmap bitmap) : base(bitmap) => _writable = bitmap;

        /// <summary>Copies <paramref name="rect"/> worth of BGRA rows (<paramref name="stride"/> bytes apart) in.</summary>
        public void WritePixels(System.Windows.Int32Rect rect, byte[] pixels, int stride, int offset)
        {
            using var locked = _writable.Lock();
            for (int y = 0; y < rect.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(pixels, offset + y * stride, locked.Address + (rect.Y + y) * locked.RowBytes + rect.X * 4, rect.Width * 4);
        }
    }
}

namespace System.Windows.Media
{
    public readonly record struct PixelFormat(string Name);

    public static class PixelFormats
    {
        public static PixelFormat Bgra32 { get; } = new("Bgra32");
    }
}

namespace System.Windows.Input
{
    /// <summary>WPF re-asks every command's CanExecute when this is called; Avalonia needs each command told.</summary>
    public static class CommandManager
    {
        public static void InvalidateRequerySuggested() => Kairos.Ui.RelayCommand.RaiseAll();
    }
}

namespace Microsoft.Win32
{
    /// <summary>The folder picker of WPF's common dialogs, over Avalonia's storage provider (blocking, as the original is).</summary>
    public sealed class OpenFolderDialog
    {
        public string InitialDirectory { get; set; } = "";

        public string FolderName { get; private set; } = "";

        public bool? ShowDialog()
        {
            var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                ?.Windows.FirstOrDefault(w => w.IsActive) ?? (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner == null)
                return false;
            var frame = new Avalonia.Threading.DispatcherFrame();
            string? chosen = null;
            _ = PickAsync();
            Avalonia.Threading.Dispatcher.UIThread.PushFrame(frame);
            if (chosen == null)
                return false;
            FolderName = chosen;
            return true;

            async Task PickAsync()
            {
                try
                {
                    var start = InitialDirectory.Length > 0 ? await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(InitialDirectory)) : null;
                    var folders = await owner.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { SuggestedStartLocation = start });
                    chosen = Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folders.FirstOrDefault());
                }
                finally
                {
                    frame.Continue = false;
                }
            }
        }
    }
}
