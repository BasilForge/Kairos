using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Kairos.Platform;

namespace Kairos.App;

/// <summary>
/// The "clip saved" card in the top-right corner. A click-through window that never takes focus; it fades in, holds,
/// and fades out. The X11 server cannot keep a window out of captures, so the card can show up in a clip taken at that
/// moment (the Windows app excludes it).
/// </summary>
public sealed class SaveToast : IDisposable
{
    public const double FadeIn = 0.18, Hold = 2.2, FadeOut = 0.35;
    public const double Total = FadeIn + Hold + FadeOut;
    const int Width = 300, Height = 64, Margin = 16, SlidePx = 14;

    readonly UiTimer _timer = new(15);
    Window? _window;
    PixelPoint _pos;
    DateTime _shownAt;

    public SaveToast() => _timer.Tick += Animate;

    /// <summary>Opacity 0..1 at <paramref name="t"/> seconds after showing.</summary>
    public static double Opacity(double t)
    {
        if (t <= 0 || t >= Total)
            return 0;
        if (t < FadeIn)
            return 1 - Math.Pow(1 - t / FadeIn, 3);
        if (t < FadeIn + Hold)
            return 1;
        double k = (t - FadeIn - Hold) / FadeOut;
        return 1 - k * k * k;
    }

    /// <param name="warning">Something did not work: an amber "!" instead of the green check.</param>
    public void Show(string title, string subtitle, bool rtl, bool warning = false)
    {
        _window?.Close();
        _window = Build(title, subtitle, rtl, warning);
        var screen = _window.Screens.Primary;
        double scale = screen?.Scaling ?? 1;
        var work = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        _pos = new PixelPoint(work.Right - (int)((Width + Margin) * scale), work.Y + (int)(Margin * scale));
        _window.Position = _pos;
        _window.Opacity = 0;
        _window.Show();
        MakeClickThrough(_window);
        _shownAt = DateTime.UtcNow;
        _timer.Start();
    }

    void Animate()
    {
        double t = (DateTime.UtcNow - _shownAt).TotalSeconds;
        if (t >= Total || _window == null)
        {
            _timer.Stop();
            _window?.Hide();
            return;
        }
        double opacity = Opacity(t);
        _window.Opacity = opacity;
        int slide = t < FadeIn ? (int)((1 - opacity) * SlidePx * (_window.Screens.Primary?.Scaling ?? 1)) : 0;
        _window.Position = new PixelPoint(_pos.X + slide, _pos.Y);
    }

    static Window Build(string title, string subtitle, bool rtl, bool warning)
    {
        var disc = new Grid { Width = 30, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        disc.Children.Add(new Ellipse { Fill = new SolidColorBrush(warning ? Color.Parse("#FFB020") : Color.Parse("#2ED47A")) });
        var mark = new Shape
        {
            Stroke = new SolidColorBrush(Color.Parse("#2E2726")), StrokeThickness = 2.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse(warning ? "M15,8 L15,17 M15,22 L15,22.2" : "M8.4,15.6 L13.2,20.1 L21.9,10.8"),
        };
        disc.Children.Add(mark);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#F5ECEA")), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#BBAAA7")), TextTrimming = TextTrimming.CharacterEllipsis });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(16, 0), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(disc);
        row.Children.Add(text);

        return new Window
        {
            Width = Width, Height = Height, CanResize = false, ShowInTaskbar = false, Topmost = true, ShowActivated = false, Focusable = false,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            Content = new Border
            {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(245, 58, 49, 48)), BorderBrush = new SolidColorBrush(Color.Parse("#625453")),
                Child = row,
            },
        };
    }

    /// <summary>An empty input region (the X Shape extension) lets every click fall through the card to what is under it.</summary>
    static void MakeClickThrough(Window window)
    {
        if (window.TryGetPlatformHandle() is not { Handle: not 0 } handle || handle.HandleDescriptor != "XID")
            return;
        IntPtr display = Xlib.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
            return;
        XShapeCombineRectangles(display, (nuint)handle.Handle, 2 /* ShapeInput */, 0, 0, IntPtr.Zero, 0, 0 /* ShapeSet */, 0);
        Xlib.XCloseDisplay(display);
    }

    [DllImport("libXext.so.6")]
    static extern void XShapeCombineRectangles(IntPtr display, nuint window, int kind, int xOff, int yOff, IntPtr rectangles, int count, int op, int ordering);

    public void Dispose()
    {
        _timer.Dispose();
        _window?.Close();
    }
}
