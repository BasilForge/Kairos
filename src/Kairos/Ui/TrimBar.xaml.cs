using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kairos.Ui;

/// <summary>
/// The trim bar: frames along the whole clip, the kept part between two handles, and the playhead. Drag a handle to
/// move one end, drag the middle to move the whole cut, or use the arrow keys on a handle (tenths of a second, Shift
/// for seconds). A click or drag anywhere else (or a plain click on the cut) moves the playhead.
/// The view model clamps what comes back (clip bounds, a second at least, the platform's limit, the playhead in the cut).
/// </summary>
public partial class TrimBar : UserControl
{
    const double Edge = 12;
    /// <summary>Pointer travel that turns a press on the cut into a drag (less is a click that moves the playhead).</summary>
    const double DragStart = 3;

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(double), typeof(TrimBar),
        new PropertyMetadata(0.0, (d, _) => ((TrimBar)d).Arrange()));
    public static readonly DependencyProperty StartProperty = DependencyProperty.Register(nameof(Start), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TrimBar)d).Arrange()));
    public static readonly DependencyProperty EndProperty = DependencyProperty.Register(nameof(End), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TrimBar)d).Arrange()));
    /// <summary>Written while the middle of the cut is dragged: the new start, keeping the length.</summary>
    public static readonly DependencyProperty MoveStartProperty = DependencyProperty.Register(nameof(MoveStart), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(nameof(Playhead), typeof(double), typeof(TrimBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TrimBar)d).Arrange()));
    /// <summary>A handle, the cut or the playhead is being dragged (the preview then shows fast key frames).</summary>
    public static readonly DependencyProperty IsScrubbingProperty = DependencyProperty.Register(nameof(IsScrubbing), typeof(bool), typeof(TrimBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public bool IsScrubbing { get => (bool)GetValue(IsScrubbingProperty); set => SetValue(IsScrubbingProperty, value); }
    public static readonly DependencyProperty ShowPlayheadProperty = DependencyProperty.Register(nameof(ShowPlayhead), typeof(bool), typeof(TrimBar),
        new PropertyMetadata(false, (d, _) => ((TrimBar)d).Arrange()));
    public static readonly DependencyProperty FramesProperty = DependencyProperty.Register(nameof(Frames), typeof(IEnumerable), typeof(TrimBar),
        new PropertyMetadata(null, (d, e) => ((TrimBar)d).Strip.ItemsSource = (IEnumerable?)e.NewValue));

    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => (double)GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double MoveStart { get => (double)GetValue(MoveStartProperty); set => SetValue(MoveStartProperty, value); }
    public double Playhead { get => (double)GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public bool ShowPlayhead { get => (bool)GetValue(ShowPlayheadProperty); set => SetValue(ShowPlayheadProperty, value); }
    public IEnumerable? Frames { get => (IEnumerable?)GetValue(FramesProperty); set => SetValue(FramesProperty, value); }

    /// <summary>RangePress: pressed on the cut, not moved yet (a click or the start of a drag).</summary>
    enum Drag { None, Start, End, RangePress, Range, Playhead }
    Drag _drag;
    double _grabOffset, _pressX;

    public TrimBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Arrange();
        MouseMove += OnMove;
        MouseLeftButtonUp += (_, e) => EndDrag(e.GetPosition(this).X);
        LostMouseCapture += (_, _) =>
        {
            _drag = Drag.None;
            IsScrubbing = false;
        };
    }

    double TrackWidth => Math.Max(1, ActualWidth - 2 * Edge);
    double X(double seconds) => Edge + (Duration > 0 ? seconds / Duration : 0) * TrackWidth;
    double Time(double x) => Math.Clamp((x - Edge) / TrackWidth, 0, 1) * Duration;

    void Arrange()
    {
        if (ActualWidth <= 0 || Duration <= 0)
            return;
        double h = ActualHeight, top = 4, inner = h - 8;
        double xs = X(Start), xe = X(End);
        Place(ShadeLeft, Edge, top, xs - Edge, inner);
        Place(ShadeRight, xe, top, Edge + TrackWidth - xe, inner);
        Place(Range, xs, top, xe - xs, inner);
        Place(StartHandle, xs - Edge, top, Edge, inner);
        Place(EndHandle, xe, top, Edge, inner);
        PlayheadMark.Visibility = ShowPlayhead ? Visibility.Visible : Visibility.Collapsed;
        Place(PlayheadMark, X(Playhead) - 6, 0, 12, h);
    }

    static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }

    void OnStartDown(object sender, MouseButtonEventArgs e) => BeginDrag(Drag.Start, StartHandle, e);
    void OnEndDown(object sender, MouseButtonEventArgs e) => BeginDrag(Drag.End, EndHandle, e);

    void OnRangeDown(object sender, MouseButtonEventArgs e)
    {
        _pressX = e.GetPosition(this).X;
        _grabOffset = Time(_pressX) - Start;
        BeginDrag(Drag.RangePress, Range, e);
    }

    void OnStripDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled)
            return;
        Playhead = Time(e.GetPosition(this).X);
        BeginDrag(Drag.Playhead, this, e);
    }

    void BeginDrag(Drag drag, IInputElement focus, MouseButtonEventArgs e)
    {
        _drag = drag;
        IsScrubbing = true;
        Keyboard.Focus(focus);
        CaptureMouse();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_drag == Drag.None || e.LeftButton != MouseButtonState.Pressed)
            return;
        double x = e.GetPosition(this).X;
        if (_drag == Drag.RangePress && Math.Abs(x - _pressX) >= DragStart)
            _drag = Drag.Range;
        double t = Time(x);
        switch (_drag)
        {
            case Drag.Start: Start = t; break;
            case Drag.End: End = t; break;
            case Drag.Range: MoveStart = t - _grabOffset; break;
            case Drag.Playhead: Playhead = t; break;
        }
    }

    void EndDrag(double x)
    {
        if (_drag == Drag.RangePress)
            Playhead = Time(x); // a click on the cut
        _drag = Drag.None;
        ReleaseMouseCapture();
        IsScrubbing = false;
    }

    static double Step() => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1 : 0.1;

    void OnStartKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            Start += e.Key == Key.Left ? -Step() : Step();
            e.Handled = true;
        }
    }

    void OnEndKey(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            End += e.Key == Key.Left ? -Step() : Step();
            e.Handled = true;
        }
    }
}
