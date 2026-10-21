using Avalonia;
using Avalonia.Controls;

namespace Kairos.Ui;

/// <summary>Takes the width it is given and the height that keeps <see cref="Ratio"/> (width / height).</summary>
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty = AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 16.0 / 9);

    static AspectBox() => AffectsMeasure<AspectBox>(RatioProperty);

    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    Size Fit(Size available)
    {
        double ratio = Ratio > 0 && double.IsFinite(Ratio) ? Ratio : 16.0 / 9;
        double width = double.IsInfinity(available.Width) ? 400 : available.Width;
        return new Size(width, Math.Round(width / ratio));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var size = Fit(constraint);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = Fit(arrangeSize);
        Child?.Arrange(new Rect(size));
        return size;
    }
}
