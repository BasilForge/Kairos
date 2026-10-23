using System.Windows;
using System.Windows.Controls;

namespace Kairos.Ui.Edit;

/// <summary>
/// A column that never scrolls: its children one under another, and when they do not all fit, the ones marked
/// <c>Optional</c> (a hint, a label) are left out until the rest fits, the lowest <c>Priority</c> first and among equal
/// ones the last first; one followed by a child marked <c>StandIn</c> (a smaller form of it) gives way to that instead.
/// The inspector's pages use it so nothing is cut off and nothing scrolls at the window's smallest size.
/// </summary>
public sealed class FitPanel : Panel
{
    public static readonly DependencyProperty OptionalProperty = DependencyProperty.RegisterAttached("Optional", typeof(bool), typeof(FitPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetOptional(UIElement element) => (bool)element.GetValue(OptionalProperty);

    public static void SetOptional(UIElement element, bool value) => element.SetValue(OptionalProperty, value);

    /// <summary>When an optional child goes: the lowest first (default 0).</summary>
    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached("Priority", typeof(int), typeof(FitPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static int GetPriority(UIElement element) => (int)element.GetValue(PriorityProperty);

    public static void SetPriority(UIElement element, int value) => element.SetValue(PriorityProperty, value);

    /// <summary>A smaller form of the optional child before it, shown only when that one is left out.</summary>
    public static readonly DependencyProperty StandInProperty = DependencyProperty.RegisterAttached("StandIn", typeof(bool), typeof(FitPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetStandIn(UIElement element) => (bool)element.GetValue(StandInProperty);

    public static void SetStandIn(UIElement element, bool value) => element.SetValue(StandInProperty, value);

    readonly HashSet<UIElement> _left = [];

    public FitPanel() => ClipToBounds = true;

    /// <summary>The children left out at the last layout (tests read it).</summary>
    public IReadOnlyCollection<UIElement> LeftOut => _left;

    protected override Size MeasureOverride(Size available)
    {
        double width = 0, total = 0;
        var children = InternalChildren.Cast<UIElement>().ToList();
        _left.Clear();
        foreach (var child in children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            if (GetStandIn(child))
                _left.Add(child); // until what it stands in for goes
            else
                total += child.DesiredSize.Height;
        }
        var optional = Enumerable.Range(0, children.Count).Where(i => GetOptional(children[i]))
            .OrderBy(i => GetPriority(children[i])).ThenByDescending(i => i);
        foreach (int i in optional)
        {
            if (total <= available.Height)
                break;
            if (children[i].DesiredSize.Height <= 0)
                continue;
            _left.Add(children[i]);
            total -= children[i].DesiredSize.Height;
            if (i + 1 < children.Count && GetStandIn(children[i + 1]))
            {
                _left.Remove(children[i + 1]);
                total += children[i + 1].DesiredSize.Height;
            }
        }
        return new Size(width, double.IsInfinity(available.Height) ? total : Math.Min(total, available.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (UIElement child in InternalChildren)
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
