using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Kairos.Ui;

/// <summary>A ring that fills clockwise from the top as <see cref="Value"/> goes from 0 to 1 (a save's progress).</summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 8.0);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Fill), Brushes.HotPink);
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Track), Brushes.DimGray);

    static ProgressRing() => AffectsRender<ProgressRing>(ValueProperty, ThicknessProperty, FillProperty, TrackProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public override void Render(DrawingContext dc)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height), r = (size - Thickness) / 2;
        if (r <= 0)
            return;
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        dc.DrawEllipse(null, new Pen(Track, Thickness), centre, r, r);
        double value = Math.Clamp(Value, 0, 1);
        var pen = new Pen(Fill, Thickness, lineCap: PenLineCap.Round);
        if (value >= 0.999)
        {
            dc.DrawEllipse(null, pen, centre, r, r);
            return;
        }
        if (value <= 0.001)
            return;
        double angle = value * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - r);
        var end = new Point(centre.X + r * Math.Sin(angle), centre.Y - r * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(r, r), 0, value > 0.5, SweepDirection.Clockwise);
            g.EndFigure(false);
        }
        dc.DrawGeometry(null, pen, arc);
    }
}
