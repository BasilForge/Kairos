using System.Collections.ObjectModel;
using System.Windows.Input;
using Kairos.App;
using Kairos.Engine.Audio;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>The Audio page's two layouts and, with separate sources, its rows.</summary>
public sealed partial class SettingsViewModel
{
    public AudioLayout AudioLayout
    {
        get => _s.Audio.Layout;
        set => Set(() =>
        {
            _s.Audio.Layout = value;
            _s.Normalize(); // the built-in rows, the first time
            RebuildAudioRows();
        });
    }

    public bool AudioSeparate => _s.Audio.Layout == AudioLayout.Separate;

    /// <summary>
    /// All PC sound together, recorded from a mixer app's chat or media device: the game is not on it. Says which device
    /// the game plays to, or to use separate sources (the game from its own process). Any one device of a mixer that
    /// splits the sound by kind has only that kind: SteelSeries Sonar (Default too, which Sonar makes its Gaming) plays
    /// all of it on the headphones you listen on, so those are named (when there is one real device to name); GoXLR mixes
    /// it in the hardware and gives it back only as a recording device, so separate sources (not that device as the
    /// microphone: Smart Save would hear the game, and the voice track would hold everything).
    /// </summary>
    public string SystemDeviceWarning
    {
        get
        {
            if (AudioSeparate || !_s.Audio.System || _caps?.Speakers is not { } speakers
                || VirtualAudio.Chosen(speakers, _s.Audio.SystemDevice, _caps.DefaultSpeaker)?.Name is not { } name || VirtualAudio.Classify(name) is not { } device)
                return "";
            if (device.MixOnYourDevice)
                return VirtualAudio.OnlyRealDevice(speakers.Where(d => d.Value != "default").Select(d => d.Name)) is { } real
                    ? Text.Format("audio.mixPartPick", Shown(name), Text.Ltr(device.Brand), Shown(real))
                    : Text.Format("audio.mixPart", Shown(name), Text.Ltr(device.Brand));
            if (device.MixOnARecordingDevice)
                return Text.Format("audio.mixSplit", Shown(name), Text.Ltr(device.Brand));
            if (!device.MissesTheGame)
                return "";
            return VirtualAudio.GameDevice(speakers.Select(d => d.Name)) is { } game
                ? Text.Format("audio.virtualChatPick", Shown(name), Shown(game)) : Text.Format("audio.virtualChat", Shown(name));
        }
    }

    /// <summary>
    /// The microphone is a mixer's whole mix (GoXLR's Broadcast Stream Mix: the game, chat, music and the voice), beside
    /// what is in it already (the other rows, or the System sound): says to pick the voice alone.
    /// </summary>
    public string MicDeviceWarning
    {
        get
        {
            if ((AudioTogether && !(_s.Audio.Mic && _s.Audio.System)) || _caps?.Mics is not { } mics
                || VirtualAudio.MicOnAWholeMix(mics, _s.Audio.MicDevice, _caps.DefaultMic) is not { } whole)
                return "";
            return whole.Voice is { } voice
                ? Text.Format("audio.micWholeMixPick", Shown(whole.Mic.Name), Text.Ltr(whole.Mixer), Shown(voice.Name))
                : Text.Format("audio.micWholeMix", Shown(whole.Mic.Name), Text.Ltr(whole.Mixer));
        }
    }

    /// <summary>
    /// A device's name in a line of text: short, left to right inside an Arabic line (its brackets stay where they are), and
    /// there kept on one line ("Chat Mic (TC-" / "Helicon GoXLR)" was shown on two).
    /// </summary>
    static string Shown(string device) => Text.IsRightToLeft
        ? Text.Ltr(VirtualAudio.ShortName(device).Replace('-', '‑').Replace(' ', ' '))
        : VirtualAudio.ShortName(device);

    public bool AudioTogether => !AudioSeparate;

    /// <summary>Separate sources: one row per source (none in the together layout).</summary>
    public ObservableCollection<AudioRowView> AudioRows { get; } = [];

    /// <summary>What "+ Add source" offers: the apps open when it is pressed (<see cref="OpenApps"/>), minus the rows already there.</summary>
    public IReadOnlyList<Option> AddAudioApps =>
        [.. OpenApps().Where(a => _s.Audio.Sources.All(r => !string.Equals(r.Target, a.Value, StringComparison.OrdinalIgnoreCase)))
            .Select(a => new Option(a.Name, a))];

    /// <summary>The apps open now (<see cref="App.RunningApps.Now"/>), read each time; tests set their own.</summary>
    internal static Func<IReadOnlyList<NameValue>> OpenApps = App.RunningApps.Now;

    /// <summary>What the Game row can follow: the full-screen game, or an app with sound now (and the one pinned, if gone).</summary>
    /// <summary>
    /// The game row's choices: automatic, the apps with sound, and a pinned app that is not running. The same list while
    /// what it shows is the same: every change raises it, and a new list would empty the list's choice (which then wrote
    /// itself back, over and over).
    /// </summary>
    public IReadOnlyList<Option> GameFollowChoices
    {
        get
        {
            string pinned = _s.Audio.Sources.FirstOrDefault(r => r.Kind == AudioSourceKind.Game)?.Target ?? "";
            var running = _caps?.AudioApps ?? [];
            string extra = pinned.Length > 0 && !running.Any(a => string.Equals(a.Value, pinned, StringComparison.OrdinalIgnoreCase)) ? pinned : "";
            if (_followChoices is { } made && ReferenceEquals(made.Apps, running) && made.Extra == extra && made.Language == Text.Language)
                return made.List;
            var apps = running.ToList();
            if (extra.Length > 0)
                apps.Insert(0, new NameValue(App.ForegroundApp.ForProcess(0, extra), extra));
            IReadOnlyList<Option> list = [new Option(Text.Get("audio.gameAuto"), ""), .. apps.Select(a => new Option(a.Name, a.Value))];
            _followChoices = (running, extra, Text.Language, list);
            return list;
        }
    }

    (List<NameValue> Apps, string Extra, string Language, IReadOnlyList<Option> List)? _followChoices;

    public IReadOnlyList<Option> AddAudioDevices =>
        [.. (_caps?.Mics ?? []).Where(m => m.Value != "default" && m.Value != _s.Audio.MicDevice && _s.Audio.Sources.All(r => r.Target != m.Value)
                                          && VirtualAudio.WholeMixOf(m.Name) == null) // a whole mix would hold every other row again
            .Select(m => new Option(Text.Ltr(m.Name), m))];

    /// <summary>
    /// What "+ Add source" offers among the playback devices: a mixer's channel (GoXLR's Music, RØDECaster's Chat). Not
    /// the one Windows plays every app on: as a row it takes each app's sound off its own row (a user's game, Discord and
    /// Other sounds all went silent, everything on that one track).
    /// </summary>
    public IReadOnlyList<Option> AddAudioOutputs =>
        [.. (_caps?.Speakers ?? []).Where(d => d.Value != "default" && d.Value != _caps!.DefaultSpeaker && _s.Audio.Sources.All(r => r.Target != d.Value))
            .Select(d => new Option(Text.Ltr(d.Name), d))];

    ICommand? _addAppCommand, _addDeviceCommand, _addOutputCommand, _removeRowCommand;
    public ICommand AddAudioAppCommand => _addAppCommand ??= new RelayCommand(p => AddAudioApp((NameValue)p!));
    public ICommand AddAudioDeviceCommand => _addDeviceCommand ??= new RelayCommand(p => AddAudioDevice((NameValue)p!));
    public ICommand AddAudioOutputCommand => _addOutputCommand ??= new RelayCommand(p => AddAudioOutput((NameValue)p!));
    public ICommand RemoveAudioRowCommand => _removeRowCommand ??= new RelayCommand(p => RemoveAudioRow((AudioRowView)p!));

    public void AddAudioApp(NameValue app) => AddRow(new AudioSource
    {
        Id = "app:" + app.Value.ToLowerInvariant(), Kind = AudioSourceKind.App, Target = app.Value, Label = app.Name,
    });

    public void AddAudioDevice(NameValue device) => AddDeviceRow(device, AudioSourceKind.Input, "input:");

    public void AddAudioOutput(NameValue device) => AddDeviceRow(device, AudioSourceKind.Output, "output:");

    void AddDeviceRow(NameValue device, AudioSourceKind kind, string prefix) => AddRow(new AudioSource
    {
        Id = prefix + Guid.NewGuid().ToString("N")[..8], Kind = kind, Target = device.Value, Label = device.Name,
    });

    void AddRow(AudioSource row)
    {
        if (_s.Audio.Sources.Any(r => r.Id == row.Id))
            return;
        // Its own track while one is left; else it shares the last.
        row.OwnTrack = CanOwn(row, [.. _s.Audio.Sources, row]);
        Set(() => _s.Audio.Sources.Add(row));
        RebuildAudioRows();
    }

    public void RemoveAudioRow(AudioRowView row)
    {
        Set(() => _s.Audio.Sources.Remove(row.Row));
        RebuildAudioRows();
    }

    void RebuildAudioRows()
    {
        AudioRows.Clear();
        if (_s.Audio.Layout == AudioLayout.Separate)
            foreach (var row in _s.Audio.Sources)
                AudioRows.Add(new AudioRowView(row, ChangeRow, Owns, r => CanOwn(r, _s.Audio.Sources)));
        RefreshAudioRowStates();
        Raise(nameof(AddAudioApps), nameof(AddAudioDevices), nameof(GameFollowChoices), nameof(SystemDeviceWarning), nameof(MicDeviceWarning), nameof(CaptureWarning));
    }

    /// <summary>
    /// A row's change: unsaved like any. A volume or mute is heard at once and moves no track (the row shows it itself);
    /// "Own track" may move every row's.
    /// </summary>
    void ChangeRow(Action change, bool live)
    {
        if (live)
        {
            SetLive(change);
            return;
        }
        Set(change);
        foreach (var row in AudioRows)
            row.Refresh();
    }

    bool Owns(AudioSource row) => TrackPlanner.Plan(_s.Audio).OwnsTrack(row.Id);

    /// <summary>Whether <paramref name="row"/> would have a track of its own with its switch on.</summary>
    bool CanOwn(AudioSource row, List<AudioSource> rows)
    {
        var trial = new AudioSettings
        {
            Layout = AudioLayout.Separate,
            Sources = rows.Select(r => new AudioSource { Id = r.Id, Kind = r.Kind, Target = r.Target, Label = r.Label, OwnTrack = r == row || r.OwnTrack }).ToList(),
        };
        return TrackPlanner.Plan(trial).OwnsTrack(row.Id);
    }

    /// <summary>
    /// Each row the engine captures says why it hears nothing (no game yet, the app closed, nothing playing, or its app
    /// heard on a device row: which one). A row the engine does not have (not saved yet, sound recorded together, the
    /// engine off) says nothing. A row of the device Windows plays every app on says to remove it (it takes their sound).
    /// </summary>
    void RefreshAudioRowStates()
    {
        var quiet = _status?.Engine?.QuietAudioRows;
        var heardOn = _status?.Engine?.AudioRowsHeardOn;
        var saved = SavedRowIds();
        string? DeviceRowName(string device) => _s.Audio.Sources.FirstOrDefault(r => r.Kind == AudioSourceKind.Output && r.Target == device)?.Label;
        foreach (var view in AudioRows)
            view.SetState(!saved.Contains(view.Id) ? Text.Get("audio.startsAfterSave") // the engine has no such source until Save
                : view.Row.Kind == AudioSourceKind.Output && _caps?.DefaultSpeaker is { } main && view.Row.Target == main ? Text.Get("audio.outputIsDefault")
                : quiet == null || !quiet.Contains(view.Id) ? ""
                : heardOn?.GetValueOrDefault(view.Id) is { } device && DeviceRowName(device) is { } name ? Text.Format("audio.heardOnRow", Shown(name))
                : view.Row.Kind switch
                {
                    AudioSourceKind.Game => Text.Get("audio.waitingGame"),
                    AudioSourceKind.OtherSounds => Text.Get("audio.nothingPlaying"),
                    _ when view.Row.Kind.IsDevice() => "",
                    _ => Text.Get("audio.notRunning"),
                });
    }

    /// <summary>The rows of the saved settings (what the engine records), read again only when they were saved again.</summary>
    HashSet<string> SavedRowIds()
    {
        if (_savedRows is not { } known || !ReferenceEquals(known.Json, _savedJson))
            _savedRows = known = (_savedJson, _savedJson.Length == 0 ? [] : Settings.SettingsStore.Deserialize(_savedJson).Audio.Sources.Select(r => r.Id).ToHashSet());
        return known.Ids;
    }

    (string Json, HashSet<string> Ids)? _savedRows;

    /// <summary>These settings as if loaded from disk (tests and renders), the rows with them.</summary>
    internal void SeedSettings(AppSettings settings)
    {
        _s = settings;
        _savedJson = Settings.SettingsStore.Serialize(_s);
        IsDirty = false;
        RebuildAudioRows();
        RaiseAll();
    }
}
