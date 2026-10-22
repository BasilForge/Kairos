using System.Windows;
using System.Windows.Media;
using Kairos.App;
using Kairos.Engine;

namespace Kairos.Ui;

/// <summary>
/// Live audio meter: redrawn every display frame with OBS-style ballistics from the engine's level stream. The stream
/// runs only while a meter is on screen, and the frame callback only while its level moves: once the bar and its peak
/// mark have settled (silence, or a steady sound) it rests, and the next new level wakes it.
/// </summary>
public sealed class LevelBar : FrameworkElement
{
    public static readonly DependencyProperty ChannelProperty =
        DependencyProperty.Register(nameof(Channel), typeof(string), typeof(LevelBar),
            new PropertyMetadata("system", (d, e) => ((LevelBar)d)._channel = (string)e.NewValue));

    /// <summary>The channel, as a plain field: the stream's thread reads it (a dependency property is the window's thread's only).</summary>
    volatile string _channel = "system";

    static readonly Brush Fill = Frozen(new LinearGradientBrush(
        [
            new GradientStop(Color.FromRgb(0x2E, 0xD4, 0x7A), 0.0),
            new GradientStop(Color.FromRgb(0x2E, 0xD4, 0x7A), 0.70),
            new GradientStop(Color.FromRgb(0xFF, 0xC9, 0x3C), 0.85),
            new GradientStop(Color.FromRgb(0xFF, 0x4A, 0x5E), 1.0),
        ], 0));
    static readonly Brush Track = Frozen(new SolidColorBrush(Color.FromRgb(0x4E, 0x42, 0x41)));
    static readonly Brush Marker = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xEC, 0xEA)));

    static LevelStream? _stream;
    static int _users;

    /// <summary>Where meters read the levels (tests give one that is fed by hand).</summary>
    internal static Func<LevelStream> StreamFactory = () => new LevelStream();

    int _rendering; // 1 while the frame callback is on
    bool _attached;

    readonly MeterBallistics _ballistics = new();
    TimeSpan _lastFrame;

    public string Channel
    {
        get => (string)GetValue(ChannelProperty);
        set => SetValue(ChannelProperty, value);
    }

    public LevelBar()
    {
        Height = 6;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    internal bool Rendering => Volatile.Read(ref _rendering) == 1;
    internal double BarDb => _ballistics.BarDb;
    internal double HoldDb => _ballistics.HoldDb;

    internal void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        if (_users++ == 0)
            _stream = StreamFactory();
        _stream!.Changed += OnLevels;
        Resume();
    }

    internal void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        Rest();
        if (_stream != null)
            _stream.Changed -= OnLevels;
        if (--_users == 0)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    /// <summary>A new level (on the stream's thread): a resting meter starts drawing again when its own level moved.</summary>
    void OnLevels()
    {
        if (!Rendering && Target != _ballistics.BarDb)
            Dispatcher.BeginInvoke(Resume);
    }

    void Resume()
    {
        if (!_attached || Interlocked.Exchange(ref _rendering, 1) == 1)
            return;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void Rest()
    {
        if (Interlocked.Exchange(ref _rendering, 0) == 1)
            CompositionTarget.Rendering -= OnFrame;
    }

    double Target => _stream is { } stream ? stream.Level(_channel) : LevelMeter.Silent;

    void OnFrame(object? sender, EventArgs e) => Frame(((RenderingEventArgs)e).RenderingTime);

    internal void Frame(TimeSpan time)
    {
        if (time == _lastFrame)
            return;
        double dt = _lastFrame == TimeSpan.Zero ? 0.016 : (time - _lastFrame).TotalSeconds;
        _lastFrame = time;
        double target = Target;
        double before = _ballistics.BarDb, holdBefore = _ballistics.HoldDb;
        _ballistics.Update(target, dt);
        if (_ballistics.BarDb != before || _ballistics.HoldDb != holdBefore)
            InvalidateVisual();
        // Settled: the bar is on its level and the peak mark on the bar, so the next frames would change nothing.
        if (_ballistics.BarDb == target && _ballistics.HoldDb == _ballistics.BarDb)
        {
            Rest();
            if (Target != target)
                Resume(); // a level that came in just now (its wake-up saw this meter still drawing)
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, r = h / 2;
        if (w <= 0 || h <= 0)
            return;
        var full = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Track, null, full, r, r);

        double fill = LevelMeter.ToBar(_ballistics.BarDb) * w;
        if (fill > 0.5)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, fill, h), r, r));
            dc.DrawRoundedRectangle(Fill, null, full, r, r);
            dc.Pop();
        }

        double hold = LevelMeter.ToBar(_ballistics.HoldDb) * w;
        if (hold > 2)
            dc.DrawRectangle(Marker, null, new Rect(Math.Min(hold, w) - 2, 0, 2, h));
    }

    static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
