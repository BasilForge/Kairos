using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kairos.Ui;

/// <summary>The share panel over the settings window. Logic lives in <see cref="ShareViewModel"/>.</summary>
public partial class ShareOverlay : UserControl
{
    public ShareOverlay()
    {
        InitializeComponent();
        Focusable = true;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
                Dispatcher.UIThread.Post(() => Panel.Focus(), DispatcherPriority.Input);
        };
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    ShareViewModel? Vm => DataContext as ShareViewModel;

    /// <summary>The preview picture (redrawn while the cut plays).</summary>
    public Image PreviewPicture => PreviewImage;

    void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { } vm)
        {
            if (vm.IsWorking)
                vm.CancelCommand.Execute(null);
            else
                vm.CloseCommand.Execute(null);
            e.Handled = true;
        }
        // Space plays and pauses the cut, except while typing or on a button (where Space presses it).
        else if (e.Key == Key.Space && Vm is { } v && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not (TextBox or Button)
                 && v.PlayCommand.CanExecute(null))
        {
            v.PlayCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Emoji in the caption
    DateTime _emojiClosed;

    void OnEmojiOpen(object? sender, RoutedEventArgs e)
    {
        // A click on the button while the picker is open closes it (the picker lets go first): leave it closed.
        if ((DateTime.UtcNow - _emojiClosed).TotalMilliseconds > 250)
            EmojiPopup.IsOpen = true;
    }

    void OnEmojiClosed(object? sender, EventArgs e) => _emojiClosed = DateTime.UtcNow;

    /// <summary>Puts the emoji where the caret is (over a selection); the picker stays open for more.</summary>
    void OnEmojiPick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || (sender as Control)?.DataContext is not EmojiPick pick)
            return;
        int caret = vm.InsertEmoji(pick.Emoji, CaptionBox.SelectionStart, CaptionBox.SelectionEnd - CaptionBox.SelectionStart);
        CaptionBox.CaretIndex = caret;
    }

    /// <summary>Pressed on the picture: a click (play or pause) or, with 9:16 on, a drag of the frame.</summary>
    Point? _press;
    bool _cropping;
    /// <summary>Where the frame was taken, in pixels from its centre (that spot stays under the pointer).</summary>
    double _grab;

    void OnPreviewDown(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { CanEdit: true } vm || OnVolumePill(e.Source) || !e.GetCurrentPoint(Preview).Properties.IsLeftButtonPressed)
            return;
        var at = e.GetPosition(Preview);
        _press = at;
        _cropping = false;
        _grab = vm.ShowCrop ? GrabOffset(vm, at.X) : 0;
        e.Pointer.Capture(Preview);
        e.Handled = true;
    }

    void OnPreviewUp(object? sender, PointerReleasedEventArgs e)
    {
        if (_press == null)
            return;
        bool click = !_cropping;
        _press = null;
        _cropping = false;
        e.Pointer.Capture(null);
        // Like any player: a click on the picture plays or pauses.
        if (click && Vm is { } vm && vm.PlayCommand.CanExecute(null))
            vm.PlayCommand.Execute(null);
    }

    /// <summary>The loudness pill sits on the picture: a press there is for it, not for play or the 9:16 frame.</summary>
    bool OnVolumePill(object? source) =>
        source is Avalonia.Visual v && (v == VolumePill || v.GetVisualAncestors().Contains(VolumePill));

    /// <summary>The wheel on the pill turns the preview up or down a step.</summary>
    void OnVolumeWheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        vm.PreviewVolume += Math.Sign(e.Delta.Y) * 0.05;
        e.Handled = true;
    }

    double FrameCentre(ShareViewModel vm) => ((1 - vm.CropFraction) * vm.CropPosition + vm.CropFraction / 2) * Preview.Bounds.Width;

    /// <summary>Taken inside the frame: keep that spot under the pointer. Outside it: the frame centres on the pointer.</summary>
    double GrabOffset(ShareViewModel vm, double x)
    {
        double offset = x - FrameCentre(vm);
        return Math.Abs(offset) <= vm.CropFraction * Preview.Bounds.Width / 2 ? offset : 0;
    }

    void MoveCrop(double centre)
    {
        if (Vm is not { } vm || Preview.Bounds.Width <= 0)
            return;
        double frame = vm.CropFraction;
        if (frame < 1)
            vm.CropPosition = (centre / Preview.Bounds.Width - frame / 2) / (1 - frame);
    }

    void OnPreviewMove(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press)
            return;
        double x = e.GetPosition(Preview).X;
        if (Vm is { ShowCrop: true } && (_cropping || Math.Abs(x - press.X) >= 4))
        {
            _cropping = true;
            MoveCrop(x - _grab);
        }
    }
}
