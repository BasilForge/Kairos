using System.Windows.Media;

namespace Kairos.Ui.Edit;

/// <summary>
/// The Library player's playing and seeking; <see cref="VideoViewer"/> only shows what this says. A click on the bar
/// and the arrow keys move what plays (it plays on from there); while the bar's handle or an arrow is held, only the
/// picture moves, and it plays on once when let go.
/// </summary>
internal sealed class ViewerPlayback
{
    readonly IPlayerSession _session;
    readonly string _clip;
    readonly Func<(int Width, int Height)> _size;
    readonly Func<double, ImageSource?> _still;
    /// <summary>Counts the runs: a run's late callbacks after a seek started the next one are ignored.</summary>
    int _run;
    bool _resumeAfterHold;

    /// <param name="size">The size to decode at (<see cref="PictureSize"/>), asked at each play: the window may have changed.</param>
    /// <param name="still">The picture at a time, shown while paused or held (a key frame: quick).</param>
    public ViewerPlayback(IPlayerSession session, string clip, double duration, Func<(int Width, int Height)> size, Func<double, ImageSource?> still)
    {
        _session = session;
        _clip = clip;
        Duration = duration;
        _size = size;
        _still = still;
    }

    /// <summary>The narrowest picture decoded (a small window still gets a clear one).</summary>
    const int MinWidth = 640;
    /// <summary>The widest (1440p): a maximized window on a 4K screen would otherwise copy 30 MB a picture.</summary>
    const int MaxWidth = 2560;

    /// <summary>
    /// The size the picture is shown at in screen pixels, never above the clip's own: decoded at that size it is as
    /// sharp as the screen allows, up to 1440p. It was 1280 wide whatever the window, so a 1440p clip in a large window
    /// was enlarged about twice and looked soft next to a real player.
    /// </summary>
    internal static (int Width, int Height) PictureSize(int clipWidth, int clipHeight, double areaWidth, double areaHeight)
    {
        double aspect = clipWidth / (double)Math.Max(1, clipHeight);
        double width = Math.Min(Math.Max(Math.Min(areaWidth, areaHeight * aspect), MinWidth), Math.Min(clipWidth, MaxWidth));
        int w = Math.Max(2, (int)Math.Round(width) & ~1);
        return (w, Math.Max(2, (int)Math.Round(w / aspect) & ~1));
    }

    public double Duration { get; }
    public double Position { get; private set; }
    public bool Playing { get; private set; }
    /// <summary>The bar's handle or an arrow is held (<see cref="Hold"/> until <see cref="Release"/>): stills may be lighter.</summary>
    public bool Held { get; private set; }

    /// <summary>The time moved (played on, or a seek): the bar and the clock follow.</summary>
    public event Action? Moved;
    /// <summary>What to show: the player's picture while playing, a still where it stopped or was moved to.</summary>
    public event Action<ImageSource>? Picture;
    public event Action<bool>? PlayingChanged;

    public void Toggle()
    {
        if (Playing)
            Pause();
        else
            Play();
    }

    public void Play()
    {
        if (Duration <= 0)
            return;
        int run = ++_run;
        double from = Position >= Duration - 0.05 ? 0 : Position;
        var (width, height) = _size();
        bool started = _session.Start(_clip, from, Duration, width, height, picture =>
        {
            if (run == _run)
                Picture?.Invoke(picture); // with its first frame: the picture before stays until then
        }, t =>
        {
            if (run != _run)
                return;
            Position = t;
            Moved?.Invoke();
        }, _ =>
        {
            if (run != _run)
                return;
            if (Position >= Duration - 0.1)
                Position = Duration;
            SetPlaying(false);
            Moved?.Invoke();
        });
        if (!started)
            return;
        Position = from;
        Moved?.Invoke();
        SetPlaying(true);
    }

    /// <summary>Stops playing (also when the viewer closes: the player lets go of the clip).</summary>
    public void Pause()
    {
        _run++;
        _session.Stop();
        SetPlaying(false);
    }

    /// <summary>
    /// A click on the bar, an arrow, or the bar moved while held: plays on from there while playing, shows the picture
    /// there while paused.
    /// </summary>
    public void SeekTo(double seconds)
    {
        Position = Math.Clamp(seconds, 0, Duration);
        Moved?.Invoke();
        if (Playing && Position < Duration - 0.05)
            Play();
        else
        {
            if (Playing)
                Pause(); // stepped to the end: it stops there instead of starting over
            ShowStill();
        }
    }

    public void SeekBy(double seconds) => SeekTo(Position + seconds);

    /// <summary>
    /// The bar's handle taken, or an arrow held down (its repeats): playing waits, so the player is not started again
    /// at every step (30 times a second for a held key).
    /// </summary>
    public void Hold()
    {
        if (Held)
            return;
        Held = true;
        _resumeAfterHold = Playing;
        if (Playing)
            Pause();
    }

    /// <summary>Let go: plays on from there if it was playing, else the picture there at full size.</summary>
    public void Release()
    {
        if (!Held)
            return;
        Held = false;
        if (_resumeAfterHold)
            Play();
        else
            ShowStill();
    }

    void ShowStill()
    {
        if (_still(Position) is { } still)
            Picture?.Invoke(still);
    }

    void SetPlaying(bool playing)
    {
        if (Playing == playing)
            return;
        Playing = playing;
        PlayingChanged?.Invoke(playing);
    }
}
