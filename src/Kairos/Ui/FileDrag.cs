using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Kairos.Ui;

/// <summary>
/// Files dragged out of Kairos (into Discord, a chat or a folder): a press that moves past the system's drag distance
/// starts the drag; a click, or a smaller move, stays a click.
/// </summary>
public static class FileDrag
{
    /// <summary>Far enough from the press to be a drag, by Windows's own drag distance.</summary>
    public static bool Passed(Vector moved) =>
        Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance;

    /// <summary>
    /// Lets <paramref name="element"/> be dragged out as <paramref name="file"/> (read when the drag starts; null or gone: no
    /// drag). <paramref name="notReady"/>: called when there is nothing to drag yet (a copy still to be made).
    /// </summary>
    public static void Attach(UIElement element, Func<string?> file, Action? notReady = null)
    {
        Point? pressedAt = null;
        element.PreviewMouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(element);
        element.PreviewMouseLeftButtonUp += (_, _) => pressedAt = null;
        element.PreviewMouseMove += (_, e) =>
        {
            if (pressedAt is not { } from || e.LeftButton != MouseButtonState.Pressed || !Passed(e.GetPosition(element) - from))
                return;
            pressedAt = null;
            if (file() is not { } path)
            {
                notReady?.Invoke();
                return;
            }
            if (!File.Exists(path))
                return;
            // A button holds the mouse while pressed: let go, so the drag (not a click) has it.
            Mouse.Capture(null);
            DragDrop.DoDragDrop(element, new DataObject(DataFormats.FileDrop, new[] { path }), DragDropEffects.Copy);
        };
    }
}
