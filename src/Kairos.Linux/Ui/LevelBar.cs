using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kairos.App;
using Kairos.Engine;

namespace Kairos.Ui;

/// <summary>
/// Live audio meter: redrawn every display frame with OBS-style ballistics from the engine's level stream. The stream
/// runs only while a meter is on screen, and the frame callback only while its level moves: once the bar and its peak
/// mark have settled (silence, or a steady sound) it rests, and the next new level wakes it.
/// </summary>
public sealed class LevelBar : Control
{
    public static readonly StyledProperty<string> ChannelProperty = AvaloniaProperty.Register<LevelBar, string>(nameof(Channel), "system");

    static readonly IBrush Fill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromRgb(0x2E, 0xD4, 0x7A), 0.0),
            new GradientStop(Color.FromRgb(0x2E, 0xD4, 0x7A), 0.70),
            new GradientStop(Color.FromRgb(0xFF, 0xC9, 0x3C), 0.85),
            new GradientStop(Color.FromRgb(0xFF, 0x4A, 0x5E), 1.0),
        ],
    }.ToImmutable();
    static readonly IBrush Track = new SolidColorBrush(Color.FromRgb(0x4E, 0x42, 0x41)).ToImmutable();
    static readonly IBrush Marker = new SolidColorBrush(Color.FromRgb(0xF5, 0xEC, 0xEA)).ToImmutable();

    static LevelStream? _stream;
    static int _users;

    /// <summary>Where meters read the levels (tests give one that is fed by hand).</summary>
    internal static Func<LevelStream> StreamFactory = () => new LevelStream();

    int _rendering; // 1 while the frame callback is on
    bool _attached;

    readonly MeterBallistics _ballistics = new();
    TimeSpan _lastFrame;

    public string Channel { get => GetValue(ChannelProperty); set => SetValue(ChannelProperty, value); }

    public LevelBar()
    {
        Height = 6;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Detach();
        base.OnDetachedFromVisualTree(e);
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

    /// <summary>A new level (on the stream's thread): a resting meter starts drawing again.</summary>
    void OnLevels()
    {
        if (!Rendering)
            Avalonia.Threading.Dispatcher.UIThread.Post(Resume);
    }

    void Resume()
    {
        if (!_attached || Interlocked.Exchange(ref _rendering, 1) == 1)
            return;
        _lastFrame = TimeSpan.Zero;
        NextFrame();
    }

    void Rest() => Interlocked.Exchange(ref _rendering, 0);

    void NextFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);

    double Target => _stream == null ? LevelMeter.Silent : Channel == "mic" ? _stream.MicDb : _stream.SystemDb;

    void OnFrame(TimeSpan time)
    {
        if (!Rendering)
            return;
        Frame(time);
        if (Rendering)
            NextFrame();
    }

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

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height, r = h / 2;
        if (w <= 0 || h <= 0)
            return;
        var full = new Rect(0, 0, w, h);
        dc.DrawRectangle(Track, null, full, r, r);

        double fill = LevelMeter.ToBar(_ballistics.BarDb) * w;
        if (fill > 0.5)
        {
            using (dc.PushClip(new RoundedRect(new Rect(0, 0, fill, h), r)))
                dc.DrawRectangle(Fill, null, full, r, r);
        }

        double hold = LevelMeter.ToBar(_ballistics.HoldDb) * w;
        if (hold > 2)
            dc.DrawRectangle(Marker, null, new Rect(Math.Min(hold, w) - 2, 0, 2, h));
    }
}
