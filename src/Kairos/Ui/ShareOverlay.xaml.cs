using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Kairos.Ui;

/// <summary>The share panel over the settings window. Logic lives in <see cref="ShareViewModel"/>.</summary>
public partial class ShareOverlay : UserControl
{
    public ShareOverlay()
    {
        InitializeComponent();
        // Copy dragged out: the copy made to fit the size, or the clip (into Discord, a chat, a folder).
        FileDrag.Attach(CopyTile, () => Vm?.DragFile, () => Vm?.PrepareDrag());
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(() => Panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
                    System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    ShareViewModel? Vm => DataContext as ShareViewModel;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape && Vm is { } vm)
        {
            if (vm.IsWorking)
                vm.CancelCommand.Execute(null);
            else
                vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
        // Space plays and pauses the cut, except while typing or on a button (where Space presses it).
        else if (e.Key == Key.Space && Vm is { } v && Keyboard.FocusedElement is not (TextBox or System.Windows.Controls.Primitives.ButtonBase)
                 && v.PlayCommand.CanExecute(null))
        {
            v.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Emoji in the caption
    DateTime _emojiClosed;

    /// <summary>The picker out of its popup (for --render share-emoji).</summary>
    internal FrameworkElement TakeEmojiPicker()
    {
        var picker = (FrameworkElement)EmojiPopup.Child;
        EmojiPopup.Child = null;
        return picker;
    }

    /// <summary>Copy pressed on a video: its sizes under the tile, Original or one of Discord's (no tick: a small, plain list).</summary>
    internal void ShowCopySizes() => CopySizes().IsOpen = true;

    internal ContextMenu CopySizes()
    {
        var vm = Vm!;
        var menu = new ContextMenu { PlacementTarget = CopyTile, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = CopyTile.ActualWidth };
        foreach (var size in vm.CopySizeChoices)
            menu.Items.Add(new MenuItem
            {
                Header = size.Label,
                InputGestureText = size.For,
                Command = vm.CopyAtCommand,
                CommandParameter = size.Mb,
            });
        return menu;
    }

    void OnEmojiOpen(object sender, RoutedEventArgs e)
    {
        // A click on the button while the picker is open closes it (the picker lets go first): leave it closed.
        if ((DateTime.UtcNow - _emojiClosed).TotalMilliseconds > 250)
            EmojiPopup.IsOpen = true;
    }

    void OnEmojiClosed(object? sender, EventArgs e) => _emojiClosed = DateTime.UtcNow;

    /// <summary>Puts the emoji where the caret is (over a selection); the picker stays open for more.</summary>
    void OnEmojiPick(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as FrameworkElement)?.DataContext is not EmojiPick pick)
            return;
        int caret = vm.InsertEmoji(pick.Emoji, CaptionBox.SelectionStart, CaptionBox.SelectionLength);
        CaptionBox.CaretIndex = caret;
    }

    /// <summary>Pressed on the picture: a click (play or pause) or, with 9:16 on, a drag of the frame.</summary>
    Point? _press;
    bool _cropping;
    /// <summary>Where the frame was taken, in pixels from its centre (that spot stays under the pointer).</summary>
    double _grab;

    void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { CanEdit: true } vm || OnVolumePill(e.OriginalSource))
            return;
        var at = e.GetPosition(Preview);
        _press = at;
        _cropping = false;
        _grab = vm.ShowCrop ? GrabOffset(vm, at.X) : 0;
        Preview.CaptureMouse();
        e.Handled = true;
    }

    void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (_press == null)
            return;
        bool click = !_cropping;
        _press = null;
        _cropping = false;
        Preview.ReleaseMouseCapture();
        // Like any player: a click on the picture plays or pauses.
        if (click && Vm is { } vm && vm.PlayCommand.CanExecute(null))
            vm.PlayCommand.Execute(null);
    }

    /// <summary>The loudness pill sits on the picture: a press there is for it, not for play or the 9:16 frame.</summary>
    bool OnVolumePill(object source) =>
        source is System.Windows.Media.Visual v && (v == VolumePill || VolumePill.IsAncestorOf(v));

    /// <summary>The wheel on the pill turns the preview up or down a step.</summary>
    void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.PreviewVolume += Math.Sign(e.Delta) * 0.05;
        e.Handled = true;
    }

    double FrameCentre(ShareViewModel vm) => ((1 - vm.CropFraction) * vm.CropPosition + vm.CropFraction / 2) * Preview.ActualWidth;

    /// <summary>Taken inside the frame: keep that spot under the pointer. Outside it: the frame centres on the pointer.</summary>
    double GrabOffset(ShareViewModel vm, double x)
    {
        double offset = x - FrameCentre(vm);
        return Math.Abs(offset) <= vm.CropFraction * Preview.ActualWidth / 2 ? offset : 0;
    }

    void MoveCrop(double centre)
    {
        if (Vm is not { } vm || Preview.ActualWidth <= 0)
            return;
        double frame = vm.CropFraction;
        if (frame < 1)
            vm.CropPosition = (centre / Preview.ActualWidth - frame / 2) / (1 - frame);
    }

    void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (_press is not { } press)
            return;
        double x = e.GetPosition(Preview).X;
        if (e.LeftButton == MouseButtonState.Pressed && Vm is { ShowCrop: true } &&
            (_cropping || Math.Abs(x - press.X) >= SystemParameters.MinimumHorizontalDragDistance))
        {
            _cropping = true;
            MoveCrop(x - _grab);
        }
    }
}
