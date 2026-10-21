using System.Windows.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Kairos.App;
using Kairos.Engine.Share;

namespace Kairos.Ui.Edit;

/// <summary>
/// A clip in Kairos's own player: play/pause, seek, mute, and Edit at the top. The player and the frame reader run only
/// while it is open; closing frees both, so the clip can be deleted next.
/// </summary>
public partial class VideoViewer : UserControl
{
    const int PictureWidth = 1280;
    readonly PlayerSession _session = new();

    /// <summary>Pictures the player has shown (the measuring harness).</summary>
    internal int FramesShown => _session.FramesShown;
    ClipItem? _item;
    FrameGrabber? _grabber;
    double _duration, _aspect = 16 / 9.0, _position;
    bool _dragging, _resumeAfterDrag, _settingSeek, _muted;

    public VideoViewer()
    {
        InitializeComponent();
        ShowPlaying(false);
        ShowMuted(false);
        _session.Frame += () => Video.InvalidateVisual();
        Scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, Scrim))
                Hide();
        };
        VideoFrame.PointerPressed += (_, e) =>
        {
            e.Handled = true; // a click on the clip plays or pauses it, it does not close the viewer
            TogglePlay();
        };
        Seek.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
                OnSeekChanged(Seek.Value);
        };
        Seek.AddHandler(PointerPressedEvent, (_, _) => OnSeekStart(), RoutingStrategies.Tunnel);
        Seek.AddHandler(PointerReleasedEvent, (_, _) => OnSeekEnd(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public event Action<ClipItem>? ShareRequested, ShowRequested, DeleteRequested, EditRequested;

    public bool IsOpen => _item != null;

    public void Show(ClipItem item)
    {
        Hide();
        _item = item;
        Heading.Text = item.App;
        Meta.Text = item.Meta;
        _position = 0;
        _duration = 0;
        try
        {
            var info = ClipTranscoder.Probe(item.Path);
            _duration = info.Duration;
            _aspect = info.Height > 0 ? info.Width / (double)info.Height : _aspect;
            _grabber = new FrameGrabber(item.Path);
        }
        catch (Exception e) when (e is ShareException or IOException or InvalidOperationException)
        {
            // FFmpeg or the file is missing: the viewer still opens, with its buttons and the card's picture.
        }
        Video.Source = Still(0) ?? item.Thumbnail;
        _settingSeek = true;
        Seek.Maximum = Math.Max(0.01, _duration);
        Seek.Value = 0;
        _settingSeek = false;
        Seek.IsEnabled = PlayButton.IsEnabled = _duration > 0;
        ShowTime();
        IsVisible = true;
        Focus();
    }

    public void Hide()
    {
        _session.Stop();
        _grabber?.Dispose();
        _grabber = null;
        _item = null;
        Video.Source = null; // lets go of the clip (it may be deleted next)
        ShowPlaying(false);
        IsVisible = false;
    }

    BitmapSource? Still(double seconds)
    {
        if (_grabber?.Grab(seconds, PictureWidth, exact: false) is not { } frame)
            return null;
        return BitmapSource.FromBgra(frame.Bgra, frame.Width, frame.Height);
    }

    void Play()
    {
        if (_item == null || _duration <= 0)
            return;
        double from = _position >= _duration - 0.05 ? 0 : _position;
        int height = (int)Math.Round(PictureWidth / _aspect) & ~1;
        var picture = _session.Start(_item.Path, from, _duration, PictureWidth, height, t =>
        {
            _position = t;
            _settingSeek = true;
            Seek.Value = Math.Min(t, Seek.Maximum);
            _settingSeek = false;
            ShowTime();
        }, _ =>
        {
            ShowPlaying(false);
            if (_position >= _duration - 0.1)
                _position = _duration;
        });
        if (picture != null)
        {
            Video.Source = picture;
            ShowPlaying(true);
        }
    }

    void Pause()
    {
        _session.Stop();
        ShowPlaying(false);
    }

    void TogglePlay()
    {
        if (_session.IsPlaying)
            Pause();
        else
            Play();
    }

    /// <summary>The play button's action (the measuring harness).</summary>
    internal void TogglePlayNow() => TogglePlay();

    void ShowPlaying(bool playing)
    {
        PlayIcon.Glyph = playing ? "E769" : "E768";
        string label = Text.Get(playing ? "viewer.pause" : "viewer.play");
        ToolTip.SetTip(PlayButton, label);
        Avalonia.Automation.AutomationProperties.SetName(PlayButton, label);
        PlayMark.IsVisible = !playing;
    }

    void ShowMuted(bool muted)
    {
        _muted = muted;
        _session.Volume = muted ? 0 : 1;
        MuteIcon.Glyph = muted ? "E74F" : "E767";
        string label = Text.Get(muted ? "viewer.unmute" : "viewer.mute");
        ToolTip.SetTip(MuteButton, label);
        Avalonia.Automation.AutomationProperties.SetName(MuteButton, label);
    }

    void ShowTime() => Time.Text = $"{Clock(_position)} / {Clock(_duration)}";

    static string Clock(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    void SeekTo(double seconds)
    {
        _position = Math.Clamp(seconds, 0, _duration);
        _settingSeek = true;
        Seek.Value = _position;
        _settingSeek = false;
        ShowTime();
        bool playing = _session.IsPlaying;
        if (playing)
            Play();
        else if (Still(_position) is { } still)
            Video.Source = still;
    }

    void OnSeekStart()
    {
        _dragging = true;
        _resumeAfterDrag = _session.IsPlaying;
        Pause();
    }

    void OnSeekChanged(double value)
    {
        if (_settingSeek)
            return;
        _position = value;
        ShowTime();
        if (Still(_position) is { } still)
            Video.Source = still;
    }

    void OnSeekEnd()
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (_resumeAfterDrag)
            Play();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
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
            // The arrow that points right goes later, in either reading direction (the timeline is never mirrored).
            case Key.Right:
                SeekTo(_position + 5);
                break;
            case Key.Left:
                SeekTo(_position - 5);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    void OnPlay(object? sender, RoutedEventArgs e) => TogglePlay();

    void OnMute(object? sender, RoutedEventArgs e) => ShowMuted(!_muted);

    void OnClose(object? sender, RoutedEventArgs e) => Hide();

    void OnEdit(object? sender, RoutedEventArgs e) => Act(EditRequested);

    void OnShare(object? sender, RoutedEventArgs e) => Act(ShareRequested);

    void OnShow(object? sender, RoutedEventArgs e)
    {
        if (_item is { } item)
            ShowRequested?.Invoke(item);
    }

    void OnDelete(object? sender, RoutedEventArgs e) => Act(DeleteRequested);

    /// <summary>Closes first (the player and the reader let go of the file), then hands the clip on.</summary>
    void Act(Action<ClipItem>? handler)
    {
        if (_item is not { } item)
            return;
        Hide();
        handler?.Invoke(item);
    }
}
