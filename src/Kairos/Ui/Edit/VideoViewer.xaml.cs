using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kairos.App;
using Kairos.Engine.Share;

namespace Kairos.Ui.Edit;

/// <summary>
/// A clip in Kairos's own player (it opened the Windows player before): play/pause, seek, mute, and Edit at the top.
/// The player and the frame reader run only while it is open; closing frees both, so the clip can be deleted next.
/// </summary>
public partial class VideoViewer : UserControl
{
    readonly PlayerSession _session = new();

    /// <summary>Pictures the player has shown (the measuring harness).</summary>
    internal int FramesShown => _session.FramesShown;
    ClipItem? _item;
    FrameGrabber? _grabber;
    ViewerPlayback? _playback;
    int _clipWidth, _clipHeight;
    bool _settingSeek, _muted;

    public VideoViewer()
    {
        InitializeComponent();
        ShowPlaying(false);
        ShowMuted(false);
    }

    public event Action<ClipItem>? ShareRequested, ShowRequested, DeleteRequested, EditRequested;

    public bool IsOpen => _item != null;

    public void Show(ClipItem item)
    {
        Hide();
        _item = item;
        Heading.Text = item.App;
        Meta.Text = item.Meta;
        double duration = 0;
        _clipWidth = _clipHeight = 0;
        try
        {
            var info = ClipTranscoder.Probe(item.Path);
            duration = info.Duration;
            (_clipWidth, _clipHeight) = (info.Width, info.Height);
            _grabber = new FrameGrabber(item.Path);
        }
        catch (Exception e) when (e is ShareException or IOException or InvalidOperationException)
        {
            // FFmpeg or the file is missing: the viewer still opens, with its buttons and the card's picture.
        }
        // Laid out first: the first picture is read at the size it is shown at.
        Visibility = Visibility.Visible;
        UpdateLayout();
        Video.Source = Still(0) ?? item.Thumbnail;
        _playback = new ViewerPlayback(_session, item.Path, duration, PictureSize, Still);
        _playback.Moved += ShowPosition;
        _playback.Picture += picture => Video.Source = picture;
        _playback.PlayingChanged += ShowPlaying;
        _settingSeek = true;
        Seek.Maximum = Math.Max(0.01, duration);
        _settingSeek = false;
        Seek.IsEnabled = PlayButton.IsEnabled = duration > 0;
        ShowPosition();
        Dispatcher.BeginInvoke(() => CloseButton.Focus(), DispatcherPriority.Input);
    }

    public void Hide()
    {
        _playback?.Pause();
        _playback = null;
        _grabber?.Dispose();
        _grabber = null;
        _item = null;
        Video.Source = null; // lets go of the clip (it may be deleted next)
        Visibility = Visibility.Collapsed;
    }

    /// <summary>The picture's size on screen in pixels (the viewer's middle row, inside the frame's margins).</summary>
    (int Width, int Height) PictureSize()
    {
        if (_clipWidth <= 0 || _clipHeight <= 0)
            return (1280, 720); // not probed: nothing plays
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        double across = Scrim.ActualWidth - Frame.Margin.Left - Frame.Margin.Right;
        return ViewerPlayback.PictureSize(_clipWidth, _clipHeight, Math.Max(0, across) * dpi.DpiScaleX,
            Scrim.RowDefinitions[1].ActualHeight * dpi.DpiScaleY);
    }

    /// <summary>The widest still while the bar or an arrow is held: one is read at every step, on this thread.</summary>
    const int HeldStillWidth = 1280;

    BitmapSource? Still(double seconds)
    {
        int width = PictureSize().Width;
        if (_playback?.Held == true)
            width = Math.Min(width, HeldStillWidth);
        if (_grabber?.Grab(seconds, width, exact: false) is not { } frame)
            return null;
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, frame.Bgra, frame.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    void TogglePlay() => _playback?.Toggle();

    /// <summary>The play button's action (the measuring harness).</summary>
    internal void TogglePlayNow() => TogglePlay();

    void ShowPlaying(bool playing)
    {
        PlayIcon.Text = playing ? "" : "";
        string label = Text.Get(playing ? "viewer.pause" : "viewer.play");
        PlayButton.ToolTip = label;
        AutomationProperties.SetName(PlayButton, label);
        PlayMark.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowMuted(bool muted)
    {
        _muted = muted;
        _session.Volume = muted ? 0 : 1;
        MuteIcon.Text = muted ? "" : "";
        string label = Text.Get(muted ? "viewer.unmute" : "viewer.mute");
        MuteButton.ToolTip = label;
        AutomationProperties.SetName(MuteButton, label);
    }

    void ShowTime() => Time.Text = $"{Clock(_playback?.Position ?? 0)} / {Clock(_playback?.Duration ?? 0)}";

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    /// <summary>The bar and the clock show where the player is.</summary>
    void ShowPosition()
    {
        _settingSeek = true;
        Seek.Value = Math.Min(_playback?.Position ?? 0, Seek.Maximum);
        _settingSeek = false;
        ShowTime();
    }

    // The bar's handle held: playing waits; let go, it plays on from there.
    void OnSeekDragStarted(object sender, DragStartedEventArgs e) => _playback?.Hold();

    void OnSeekDragCompleted(object sender, DragCompletedEventArgs e) => _playback?.Release();

    /// <summary>The bar moved by the user: a click (anywhere on it), a drag, or its own keys.</summary>
    void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingSeek)
            _playback?.SeekTo(e.NewValue);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!IsOpen)
            return;
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                break;
            case Key.Space:
                TogglePlay();
                break;
            // The arrow that points right goes later, in either reading direction (the timeline is never mirrored). Held
            // down, its repeats only move the picture; it plays on when let go.
            case Key.Right or Key.Left:
                if (e.IsRepeat)
                    _playback?.Hold();
                _playback?.SeekBy(e.Key == Key.Right ? 5 : -5);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (IsOpen && e.Key is Key.Right or Key.Left)
            _playback?.Release();
    }

    /// <summary>An arrow held while the window went to the back never comes up here: let go then, so playing goes on.</summary>
    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        if (e.NewValue is false)
            _playback?.Release();
    }

    void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == Scrim)
            Hide();
    }

    void OnVideoClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // a click on the clip plays or pauses it, it does not close the viewer
        TogglePlay();
    }

    void OnPlay(object sender, RoutedEventArgs e) => TogglePlay();

    void OnMute(object sender, RoutedEventArgs e) => ShowMuted(!_muted);

    void OnClose(object sender, RoutedEventArgs e) => Hide();

    void OnEdit(object sender, RoutedEventArgs e) => Act(EditRequested);

    void OnShare(object sender, RoutedEventArgs e) => Act(ShareRequested);

    void OnShow(object sender, RoutedEventArgs e)
    {
        if (_item is { } item)
            ShowRequested?.Invoke(item);
    }

    void OnDelete(object sender, RoutedEventArgs e) => Act(DeleteRequested);

    /// <summary>Closes first (the player and the reader let go of the file), then hands the clip on.</summary>
    void Act(Action<ClipItem>? handler)
    {
        if (_item is not { } item)
            return;
        Hide();
        handler?.Invoke(item);
    }
}
