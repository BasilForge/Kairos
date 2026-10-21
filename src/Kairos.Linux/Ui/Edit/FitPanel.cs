using Avalonia;
using Avalonia.Controls;

namespace Kairos.Ui.Edit;

/// <summary>
/// A column that never scrolls: its children one under another, and when they do not all fit, the ones marked
/// <c>Optional</c> (a hint, a label) are left out, the last first, until the rest fits. The inspector's pages use it so
/// nothing is cut off and nothing scrolls at the window's smallest size.
/// </summary>
public sealed class FitPanel : Panel
{
    public static readonly AttachedProperty<bool> OptionalProperty = AvaloniaProperty.RegisterAttached<FitPanel, Control, bool>("Optional");

    public static bool GetOptional(Control element) => element.GetValue(OptionalProperty);

    public static void SetOptional(Control element, bool value) => element.SetValue(OptionalProperty, value);

    static FitPanel() => OptionalProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as FitPanel)?.InvalidateMeasure());

    readonly HashSet<Control> _left = [];

    public FitPanel() => ClipToBounds = true;

    /// <summary>The children left out at the last layout (tests read it).</summary>
    public IReadOnlyCollection<Control> LeftOut => _left;

    protected override Size MeasureOverride(Size available)
    {
        double width = 0, total = 0;
        var children = Children.ToList();
        foreach (var child in children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            total += child.DesiredSize.Height;
        }
        _left.Clear();
        for (int i = children.Count - 1; i >= 0 && total > available.Height; i--)
        {
            if (!GetOptional(children[i]) || children[i].DesiredSize.Height <= 0)
                continue;
            _left.Add(children[i]);
            total -= children[i].DesiredSize.Height;
        }
        return new Size(width, double.IsInfinity(available.Height) ? total : Math.Min(total, available.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (var child in Children)
        {
            double h = child.DesiredSize.Height;
            if (_left.Contains(child))
            {
                child.Arrange(new Rect(0, -10_000, final.Width, h)); // out of sight (the panel clips)
                continue;
            }
            child.Arrange(new Rect(0, y, final.Width, h));
            y += h;
        }
        return final;
    }
}
