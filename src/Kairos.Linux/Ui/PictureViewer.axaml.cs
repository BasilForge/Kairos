using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Kairos.App;

namespace Kairos.Ui;

/// <summary>A screenshot at full size over the settings window, with what can be done with it.</summary>
public partial class PictureViewer : UserControl
{
    readonly DispatcherTimer _copiedFor = new() { Interval = TimeSpan.FromSeconds(1.6) };
    ClipItem? _item;

    public PictureViewer()
    {
        InitializeComponent();
        _copiedFor.Tick += (_, _) => ShowCopied(false);
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Scrim))
                Hide();
        };
        PictureFrame.PointerPressed += (_, e) => e.Handled = true; // a click on the picture keeps it open
    }

    public event Action<ClipItem>? ShareRequested, ShowRequested, DeleteRequested, EditRequested;

    /// <summary>The picture a step (+1 next, -1 before) away on the Library page, or null; set by the window.</summary>
    public Func<ClipItem, int, ClipItem?>? Neighbor { get; set; }

    public bool IsOpen => _item != null;

    public void Show(ClipItem item)
    {
        _item = item;
        Picture.Source = PictureClipboard.TryLoad(item.Path); // gone or unreadable: the viewer still opens, with its buttons
        Heading.Text = item.App;
        Meta.Text = item.Meta;
        ShowCopied(false);
        // Each chevron points the way its button goes: in Arabic "next" sits on the left and points left.
        bool rtl = FlowDirection == FlowDirection.RightToLeft;
        PrevIcon.Glyph = rtl ? "E76C" : "E76B";
        NextIcon.Glyph = rtl ? "E76B" : "E76C";
        PrevButton.IsEnabled = Neighbor?.Invoke(item, -1) != null;
        NextButton.IsEnabled = Neighbor?.Invoke(item, +1) != null;
        IsVisible = true;
        Dispatcher.UIThread.Post(() => CloseButton.Focus(), DispatcherPriority.Input);
    }

    public void Hide()
    {
        _item = null;
        Picture.Source = null; // lets go of the picture (it may be deleted next)
        IsVisible = false;
    }

    void ShowCopied(bool copied)
    {
        _copiedFor.Stop();
        CopyLabel.Text = Text.Get(copied ? "viewer.copied" : "viewer.copy");
        CopyIcon.Glyph = copied ? "E73E" : "E8C8";
        if (copied)
            _copiedFor.Start();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsOpen)
        {
            Hide();
            e.Handled = true;
        }
        // The arrow that points at a button goes that way, in either reading direction.
        else if (e.Key is Key.Left or Key.Right && IsOpen)
        {
            int step = e.Key == Key.Right ? +1 : -1;
            Step(FlowDirection == FlowDirection.RightToLeft ? -step : step);
            e.Handled = true;
        }
    }

    void OnPrev(object? sender, RoutedEventArgs e) => Step(-1);

    void OnNext(object? sender, RoutedEventArgs e) => Step(+1);

    void Step(int step)
    {
        if (_item != null && Neighbor?.Invoke(_item, step) is { } next)
            Show(next);
    }

    void OnClose(object? sender, RoutedEventArgs e) => Hide();

    void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_item != null && File.Exists(_item.Path) && PictureClipboard.Copy(_item.Path))
            ShowCopied(true);
    }

    void OnShare(object? sender, RoutedEventArgs e) => Act(ShareRequested, close: true);

    void OnShow(object? sender, RoutedEventArgs e) => Act(ShowRequested, close: false);

    void OnDelete(object? sender, RoutedEventArgs e) => Act(DeleteRequested, close: true);

    void OnEdit(object? sender, RoutedEventArgs e) => Act(EditRequested, close: true);

    void Act(Action<ClipItem>? handler, bool close)
    {
        if (_item is not { } item)
            return;
        if (close)
            Hide();
        handler?.Invoke(item);
    }
}
