using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Kairos.Ui;

/// <summary>Keeps the end of a long read-only path visible (the folder name), instead of the start.</summary>
public static class PathBox
{
    public static readonly AttachedProperty<bool> ShowEndProperty = AvaloniaProperty.RegisterAttached<TextBox, TextBox, bool>("ShowEnd");

    public static bool GetShowEnd(TextBox box) => box.GetValue(ShowEndProperty);
    public static void SetShowEnd(TextBox box, bool value) => box.SetValue(ShowEndProperty, value);

    static PathBox() => ShowEndProperty.Changed.AddClassHandler<TextBox>((box, e) =>
    {
        if (e.NewValue is not true)
            return;
        box.AttachedToVisualTree += (_, _) => ScrollToEnd(box);
        box.PropertyChanged += (_, p) =>
        {
            if (p.Property == TextBox.TextProperty || p.Property == Visual.BoundsProperty)
                ScrollToEnd(box);
        };
        box.LostFocus += (_, _) => ScrollToEnd(box);
    });

    static void ScrollToEnd(TextBox box) => Dispatcher.UIThread.Post(() => box.CaretIndex = box.Text?.Length ?? 0, DispatcherPriority.Loaded);
}
