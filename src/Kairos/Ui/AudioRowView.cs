using System.ComponentModel;
using Kairos.App;
using Kairos.Engine.Audio;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>
/// One row of the Audio page's separate sources: its name and state, a live meter (its id), the volume, mute, and
/// "Own track" as the tracks will be recorded (the switch is greyed when no track is left for it).
/// </summary>
public sealed class AudioRowView : INotifyPropertyChanged
{
    readonly AudioSource _row;
    readonly Action<Action, bool> _change;
    readonly Func<AudioSource, bool> _owns, _canOwn;

    /// <param name="change">Runs a change on the settings: true when it is a live one (volume, mute).</param>
    internal AudioRowView(AudioSource row, Action<Action, bool> change, Func<AudioSource, bool> owns, Func<AudioSource, bool> canOwn)
    {
        _row = row;
        _change = change;
        _owns = owns;
        _canOwn = canOwn;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal AudioSource Row => _row;
    public string Id => _row.Id;

    /// <summary>Its name; an added device's reads left to right in an Arabic window ("(Speakers (RODECaster Duo Chat" was shown).</summary>
    public string Name => _row.Kind is AudioSourceKind.Input or AudioSourceKind.Output
        ? App.Text.Ltr(TrackPlanner.Name(_row))
        : SoundNames.Name(TrackPlanner.Role(_row.Kind), TrackPlanner.Name(_row));

    /// <summary>The icon of its track (an input device: a microphone's).</summary>
    public string Glyph => SoundNames.Glyph(_row.Kind switch
    {
        AudioSourceKind.Input => TrackRole.Microphone,
        AudioSourceKind.Output => TrackRole.Other,
        _ => TrackPlanner.Role(_row.Kind),
    });

    public bool Removable => _row.Kind == AudioSourceKind.App || _row.Kind.IsDevice() && _row.Kind != AudioSourceKind.Microphone;

    /// <summary>The microphone row also picks its device.</summary>
    public bool IsMicrophone => _row.Kind == AudioSourceKind.Microphone;

    /// <summary>The game row also says which app it follows: the full-screen game ("") or one pinned by its exe.</summary>
    public bool IsGame => _row.Kind == AudioSourceKind.Game;

    public string Follows
    {
        get => _row.Target;
        set
        {
            if ((value ?? "") != _row.Target)
                _change(() => _row.Target = value ?? "", false);
        }
    }

    public double Volume
    {
        get => Math.Round(_row.Volume * 100);
        set
        {
            _change(() => _row.Volume = Math.Clamp(value, 0, 200) / 100.0, true);
            Changed(nameof(Volume));
        }
    }

    public bool Muted
    {
        get => _row.Muted;
        set
        {
            _change(() => _row.Muted = value, true);
            Changed(nameof(Muted));
        }
    }

    /// <summary>As it will be recorded: on its own track, or (off) in the shared one.</summary>
    public bool OwnTrack
    {
        get => _owns(_row);
        set => _change(() => _row.OwnTrack = value, false);
    }

    /// <summary>Off can always go on unless no track is left for it.</summary>
    public bool CanOwnTrack => OwnTrack || _canOwn(_row);

    /// <summary>Why the switch is greyed (none while it can go on).</summary>
    public string? OwnTrackTip => CanOwnTrack ? null : Text.Get("audio.noTrackLeft");

    /// <summary>What it hears now: "" when it hears something; why not, otherwise (no game yet, the app not running).</summary>
    public string State { get; private set; } = "";

    internal void SetState(string state)
    {
        if (State == state)
            return;
        State = state;
        Changed(nameof(State));
    }

    internal void Refresh() => Changed(null);

    void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
