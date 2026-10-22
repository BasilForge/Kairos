using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Kairos.Ui;

/// <summary>
/// The Camera card's picture: a 16:9 frame with the camera's box (a circle when round), dragged into place. Its place
/// and size are 0..1 of the picture, as <see cref="Engine.CameraMath.Corner"/> lays them out in the recording.
/// </summary>
public sealed class CameraPlacer : FrameworkElement
{
    public static readonly DependencyProperty XProperty = Register(nameof(X));
    public static readonly DependencyProperty YProperty = Register(nameof(Y));
    public static readonly DependencyProperty SizeProperty = Register(nameof(Size));
    public static readonly DependencyProperty RoundProperty = DependencyProperty.Register(nameof(Round), typeof(bool), typeof(CameraPlacer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    static DependencyProperty Register(string name) => DependencyProperty.Register(name, typeof(double), typeof(CameraPlacer),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double X { get => (double)GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => (double)GetValue(YProperty); set => SetValue(YProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool Round { get => (bool)GetValue(RoundProperty); set => SetValue(RoundProperty, value); }

    Point? _grab;

    public CameraPlacer()
    {
        Cursor = Cursors.SizeAll;
        Focusable = true;
        FlowDirection = FlowDirection.LeftToRight; // the picture is never mirrored
    }

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? 320 : available.Width;
        return new Size(width, width * 9 / 16);
    }

    Rect Box()
    {
        var c = new Settings.CameraSettings { X = X, Y = Y, Size = Size, Shape = Round ? Settings.CameraShape.Round : Settings.CameraShape.Square };
        var (x, y, w, h) = Engine.CameraMath.Corner(c, (int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight), 16 / 9.0);
        return new Rect(x, y, w, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var frame = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(Resource("Bg"), new Pen(Resource("Stroke"), 1), frame, 8, 8);
        var box = Box();
        var accent = Resource("Accent");
        var fill = accent.Clone();
        fill.Opacity = 0.28;
        var pen = new Pen(accent, IsKeyboardFocused ? 2.5 : 1.5);
        if (Round)
            dc.DrawEllipse(fill, pen, new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
        else
            dc.DrawRoundedRectangle(fill, pen, box, 4, 4);
    }

    Brush Resource(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var at = e.GetPosition(this);
        var box = Box();
        // Pressed outside the box: it jumps there first, then follows.
        _grab = box.Contains(at) ? (Point)(at - box.TopLeft) : new Point(box.Width / 2, box.Height / 2);
        MoveTo(at);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_grab != null && e.LeftButton == MouseButtonState.Pressed)
            MoveTo(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _grab = null;
        ReleaseMouseCapture();
    }

    void MoveTo(Point at)
    {
        if (_grab is not { } grab || ActualWidth <= 0 || ActualHeight <= 0)
            return;
        // The left and top as shares of the picture; the layout keeps the box inside it.
        var box = Box();
        X = Math.Clamp((at.X - grab.X) / ActualWidth, 0, 1 - box.Width / ActualWidth);
        Y = Math.Clamp((at.Y - grab.Y) / ActualHeight, 0, 1 - box.Height / ActualHeight);
    }

    /// <summary>The arrow keys move the box a little (for keyboard use).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        const double Step = 0.02;
        switch (e.Key)
        {
            case Key.Left: X = Math.Clamp(X - Step, 0, 1); break;
            case Key.Right: X = Math.Clamp(X + Step, 0, 1); break;
            case Key.Up: Y = Math.Clamp(Y - Step, 0, 1); break;
            case Key.Down: Y = Math.Clamp(Y + Step, 0, 1); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        InvalidateVisual();
    }
}
