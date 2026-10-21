using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Kairos.Engine.Share;
using System.Windows.Media.Imaging;

namespace Kairos.Ui.Edit;

/// <summary>What the editor's preview plays through (a stand-in in tests: no decoding, no sound).</summary>
internal interface IPlayerSession
{
    WriteableBitmap? Start(string clip, double from, double to, int width, int height, Action<double> position, Action<string?> ended,
        int audioTrack = 0, double rate = 1, Action<byte[], double>? filter = null);

    void Stop();

    double Volume { get; set; }

    /// <summary>Pictures shown since the last start (the measuring harness).</summary>
    int FramesShown => 0;

    /// <summary>How the last play went: pictures shown and skipped, the longest wait between two (the measuring harness).</summary>
    string Report => "";
}

/// <summary>
/// Plays part of a clip on screen: a <see cref="PreviewPlayer"/> driven by each screen refresh (the window's animation
/// frames; WPF has CompositionTarget.Rendering), stopped and freed by <see cref="Stop"/>. One clip at a time; nothing runs
/// while nothing plays.
/// </summary>
public sealed class PlayerSession : IDisposable, IPlayerSession
{
    /// <summary>Plays silence instead of the sound (the --perf harness: measured, never heard).</summary>
    internal static bool Muted;

    PreviewPlayer? _player;
    double _volume = 1;
    string _report = "";
    bool _frames;

    /// <summary>A picture may have been written (the image showing it redraws): raised after every tick, on the UI thread.</summary>
    public event Action? Frame;

    public bool IsPlaying => _player != null;

    public int FramesShown => _player?.Shown ?? 0;

    public string Report => _player is { } p ? Describe(p) : _report;

    static string Describe(PreviewPlayer p) =>
        FormattableString.Invariant($"shown={p.Shown} skipped={p.Skipped} ({p.SkipReasons}) longestGap={p.LongestGapMs:F0}ms longGaps={p.LongGaps} waits={p.Waits} sound={p.SoundSeconds:F1}s open={p.SoundOpen} peak={p.SoundPeak} volume={p.Volume:0.##}");

    /// <summary>Starts at <paramref name="from"/>; the picture fills in as frames arrive. Null when FFmpeg is missing.</summary>
    public WriteableBitmap? Start(string clip, double from, double to, int width, int height, Action<double> position, Action<string?> ended,
        int audioTrack = 0, double rate = 1, Action<byte[], double>? filter = null)
    {
        Stop();
        try
        {
            ClipTranscoder.RequireLoaded();
        }
        catch (ShareException e)
        {
            ended(e.Message);
            return null;
        }
        PreviewPlayer? player = null;
        player = new PreviewPlayer(clip, from, to, width, height, mute: Muted, position, failure =>
        {
            if (_player == player)
                Stop();
            ended(failure);
        }, audioTrack, rate, filter);
        player.Volume = _volume;
        _player = player;
        _frames = true;
        NextFrame();
        return player.Picture;
    }

    static TopLevel? Window => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault();

    void NextFrame() => Window?.RequestAnimationFrame(OnFrame);

    void OnFrame(TimeSpan _)
    {
        if (!_frames)
            return;
        _player?.Tick();
        Frame?.Invoke();
        if (_frames && _player != null)
            NextFrame();
    }

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
        _frames = false;
        var player = _player;
        _player = null;
        if (player != null)
            _report = Describe(player);
        player?.Dispose();
    }

    public void Dispose() => Stop();
}
