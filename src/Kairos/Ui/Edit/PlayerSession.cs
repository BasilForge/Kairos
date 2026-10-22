using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairos.Engine.Share;

namespace Kairos.Ui.Edit;

/// <summary>What the editor's preview and the Library's player play through (a stand-in in tests: no decoding, no sound).</summary>
internal interface IPlayerSession
{
    /// <param name="show">Given the picture the player draws into once its first frame is in it (blank until the clip is
    /// opened and read up to <paramref name="from"/>: the picture before stays meanwhile).</param>
    /// <param name="mix">Several sound tracks mixed at their gains (null: the first track, the full mix).</param>
    /// <returns>False when it cannot play (FFmpeg is missing): <paramref name="ended"/> was told why.</returns>
    bool Start(string clip, double from, double to, int width, int height, Action<WriteableBitmap> show, Action<double> position,
        Action<string?> ended, double rate = 1, Action<byte[], double>? filter = null, TrackMix? mix = null);

    /// <summary>
    /// Reads the part to play next ahead, held until the playing one ends, then plays it at once (before the playing one's
    /// <c>ended</c>). Replaces one read ahead before; <see cref="Start"/> and <see cref="Stop"/> drop it. False if nothing plays.
    /// </summary>
    bool Queue(string clip, double from, double to, int width, int height, Action<WriteableBitmap> show, Action<double> position,
        Action<string?> ended, double rate, Action<byte[], double>? filter, TrackMix? mix, double volume);

    void Stop();

    double Volume { get; set; }

    /// <summary>Pictures shown since the last start (the measuring harness).</summary>
    int FramesShown => 0;

    /// <summary>How the last play went: pictures shown and skipped, the longest wait between two (the measuring harness).</summary>
    string Report => "";
}

/// <summary>
/// Plays part of a clip on screen: a <see cref="PreviewPlayer"/> driven by each screen refresh, stopped and freed by
/// <see cref="Stop"/>. One part at a time, and the next one read ahead (<see cref="Queue"/>); nothing runs while nothing plays.
/// </summary>
public sealed class PlayerSession : IDisposable, IPlayerSession
{
    /// <summary>Plays silence instead of the sound (the --perf harness: measured, never heard).</summary>
    internal static bool Muted;

    PreviewPlayer? _player, _next;
    double _volume = 1;
    string _report = "";

    public bool IsPlaying => _player != null;

    public int FramesShown => _player?.Shown ?? 0;

    public string Report => _player is { } p ? Describe(p) : _report;

    static string Describe(PreviewPlayer p) =>
        FormattableString.Invariant($"shown={p.Shown} skipped={p.Skipped} ({p.SkipReasons}) longestGap={p.LongestGapMs:F0}ms longGaps={p.LongGaps} waits={p.Waits} sound={p.SoundSeconds:F1}s open={p.SoundOpen} peak={p.SoundPeak} volume={p.Volume:0.##}");

    public bool Start(string clip, double from, double to, int width, int height, Action<WriteableBitmap> show, Action<double> position,
        Action<string?> ended, double rate = 1, Action<byte[], double>? filter = null, TrackMix? mix = null)
    {
        Stop();
        try
        {
            ClipTranscoder.RequireLoaded();
        }
        catch (ShareException e)
        {
            ended(e.Message);
            return false;
        }
        Play(Open(clip, from, to, width, height, show, position, ended, rate, filter, mix, _volume, held: false));
        return true;
    }

    public bool Queue(string clip, double from, double to, int width, int height, Action<WriteableBitmap> show, Action<double> position,
        Action<string?> ended, double rate, Action<byte[], double>? filter, TrackMix? mix, double volume)
    {
        DropNext();
        if (_player == null)
            return false; // nothing plays (FFmpeg is missing, or it stopped): nothing to go on from
        _next = Open(clip, from, to, width, height, show, position, ended, rate, filter, mix, volume, held: true);
        return true;
    }

    PreviewPlayer Open(string clip, double from, double to, int width, int height, Action<WriteableBitmap> show, Action<double> position,
        Action<string?> ended, double rate, Action<byte[], double>? filter, TrackMix? mix, double volume, bool held)
    {
        PreviewPlayer? player = null;
        bool shown = false;
        player = new PreviewPlayer(clip, from, to, width, height, mute: Muted, t =>
        {
            if (!shown)
            {
                shown = true;
                show(player!.Picture); // its first frame is in it (a position comes only after one is shown)
            }
            position(t);
        }, failure =>
        {
            if (_player == player)
                Ended(failure);
            ended(failure);
        }, rate, filter, mix, held) { Volume = volume };
        return player;
    }

    void DropNext()
    {
        _next?.Dispose();
        _next = null;
    }

    void Play(PreviewPlayer player)
    {
        _player = player;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>The playing part has ended: the part read ahead goes on at once, unless it ended with a failure.</summary>
    void Ended(string? failure)
    {
        var next = _next;
        _next = null;
        Stop();
        if (failure != null)
            next?.Dispose(); // nothing plays on from a failure
        else if (next != null)
        {
            next.Release();
            Play(next);
        }
    }

    void OnRendering(object? sender, EventArgs e) => _player?.Tick();

    /// <summary>0..1, kept for the next clip too.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (_player != null)
                _player.Volume = value;
        }
    }

    public void Stop()
    {
        CompositionTarget.Rendering -= OnRendering;
        var player = _player;
        _player = null;
        if (player != null)
            _report = Describe(player);
        player?.Dispose();
        DropNext();
    }

    public void Dispose() => Stop();
}
