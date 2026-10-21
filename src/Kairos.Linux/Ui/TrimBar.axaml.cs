using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

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

    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<TrimBar, double>(nameof(Duration));
    public static readonly StyledProperty<double> StartProperty = AvaloniaProperty.Register<TrimBar, double>(nameof(Start), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> EndProperty = AvaloniaProperty.Register<TrimBar, double>(nameof(End), defaultBindingMode: BindingMode.TwoWay);
    /// <summary>Written while the middle of the cut is dragged: the new start, keeping the length.</summary>
    public static readonly StyledProperty<double> MoveStartProperty = AvaloniaProperty.Register<TrimBar, double>(nameof(MoveStart), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> PlayheadProperty = AvaloniaProperty.Register<TrimBar, double>(nameof(Playhead), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> ShowPlayheadProperty = AvaloniaProperty.Register<TrimBar, bool>(nameof(ShowPlayhead));
    public static readonly StyledProperty<IEnumerable?> FramesProperty = AvaloniaProperty.Register<TrimBar, IEnumerable?>(nameof(Frames));

    static TrimBar()
    {
        foreach (var property in new AvaloniaProperty[] { DurationProperty, StartProperty, EndProperty, PlayheadProperty, ShowPlayheadProperty })
            property.Changed.AddClassHandler<TrimBar>((bar, _) => bar.Place());
        FramesProperty.Changed.AddClassHandler<TrimBar>((bar, e) => bar.Strip.ItemsSource = (IEnumerable?)e.NewValue);
    }

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double MoveStart { get => GetValue(MoveStartProperty); set => SetValue(MoveStartProperty, value); }
    public double Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public bool ShowPlayhead { get => GetValue(ShowPlayheadProperty); set => SetValue(ShowPlayheadProperty, value); }
    public IEnumerable? Frames { get => GetValue(FramesProperty); set => SetValue(FramesProperty, value); }

    /// <summary>RangePress: pressed on the cut, not moved yet (a click or the start of a drag).</summary>
    enum Drag { None, Start, End, RangePress, Range, Playhead }
    Drag _drag;
    double _grabOffset, _pressX;

    public TrimBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Place();
        StartHandle.PointerPressed += (_, e) => BeginDrag(Drag.Start, StartHandle, e);
        EndHandle.PointerPressed += (_, e) => BeginDrag(Drag.End, EndHandle, e);
        Range.PointerPressed += (_, e) =>
        {
            _pressX = e.GetPosition(this).X;
            _grabOffset = Time(_pressX) - Start;
            BeginDrag(Drag.RangePress, Range, e);
        };
        Hit.PointerPressed += (_, e) =>
        {
            if (e.Handled)
                return;
            Playhead = Time(e.GetPosition(this).X);
            BeginDrag(Drag.Playhead, this, e);
        };
        PointerMoved += OnMove;
        PointerReleased += (_, e) => EndDrag(e.GetPosition(this).X, e);
        PointerCaptureLost += (_, _) => _drag = Drag.None;
        StartHandle.KeyDown += (_, e) => Nudge(e, () => Start += e.Key == Key.Left ? -Step(e) : Step(e));
        EndHandle.KeyDown += (_, e) => Nudge(e, () => End += e.Key == Key.Left ? -Step(e) : Step(e));
    }

    double TrackWidth => Math.Max(1, Bounds.Width - 2 * Edge);
    double X(double seconds) => Edge + (Duration > 0 ? seconds / Duration : 0) * TrackWidth;
    double Time(double x) => Math.Clamp((x - Edge) / TrackWidth, 0, 1) * Duration;

    void Place()
    {
        if (Bounds.Width <= 0 || Duration <= 0 || Overlay == null)
            return;
        double h = Bounds.Height, top = 4, inner = h - 8;
        double xs = X(Start), xe = X(End);
        Put(ShadeLeft, Edge, top, xs - Edge, inner);
        Put(ShadeRight, xe, top, Edge + TrackWidth - xe, inner);
        Put(Range, xs, top, xe - xs, inner);
        Put(StartHandle, xs - Edge, top, Edge, inner);
        Put(EndHandle, xe, top, Edge, inner);
        PlayheadMark.IsVisible = ShowPlayhead;
        Put(PlayheadMark, X(Playhead) - 6, 0, 12, h);
    }

    static void Put(Control e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }

    void BeginDrag(Drag drag, InputElement focus, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _drag = drag;
        focus.Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        if (_drag == Drag.None || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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

    void EndDrag(double x, PointerReleasedEventArgs e)
    {
        if (_drag == Drag.RangePress)
            Playhead = Time(x); // a click on the cut
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    static double Step(KeyEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0.1;

    static void Nudge(KeyEventArgs e, Action move)
    {
        if (e.Key is Key.Left or Key.Right)
        {
            move();
            e.Handled = true;
        }
    }
}
