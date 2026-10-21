using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Captions;
using Kairos.Settings;
using CaptureMode = Kairos.Settings.CaptureMode;

namespace Kairos.Ui;

/// <summary>
/// Settings being edited plus live engine status. Edits refresh every binding; option lists are cached so
/// dropdowns are not rebuilt, and the 1 s status poll only refreshes the status properties.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    static readonly int[] SecondsSteps = [15, 30, 60, 90, 120, 180, 240, 300, 420, 600, 900, 1200];
    static readonly string[] StatusProperties =
    [
        nameof(EngineOnline), nameof(State), nameof(StateTitle), nameof(StateDetail), nameof(BufferProgress), nameof(BufferText),
        nameof(MemoryText), nameof(QualityText), nameof(EncoderText), nameof(LastClipPath), nameof(LastShotPath),
        nameof(LastClipName), nameof(LastShotName), nameof(HasLastClip), nameof(HasLastShot), nameof(FolderWarning), nameof(EffectiveFolder),
        nameof(SaveVisible), nameof(ReplayPauseVisible), nameof(ReplayPauseTip), nameof(RecordState), nameof(RecordTitle), nameof(RecordDetail),
        nameof(IsRecording), nameof(IsRecordingLive), nameof(StartPrimaryVisible), nameof(StartGhostVisible), nameof(RecordPauseLabel),
        nameof(VoiceView), nameof(ShotView), nameof(VoiceStep), nameof(VoiceDone1), nameof(VoiceDone2), nameof(VoiceDone3), nameof(VoiceMessage),
        nameof(VoiceResult), nameof(VoiceLastMatch), nameof(VoiceStrength), nameof(VoiceNote),
    ];

    AppSettings _s = new();
    string _savedJson = "";
    CapsDto? _caps;
    StatusDto? _status;
    string _message = "";
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _polling;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;
    /// <summary>Asks the window to show a page ("voice" from the Status card).</summary>
    public event Action<string>? NavigateRequested;

    public SettingsViewModel()
    {
        Load();
        _poll.Tick += (_, _) => Poll();
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); _message = ""; Raise(nameof(Message), nameof(ShowBar)); };
        SaveNowCommand = new RelayCommand(_ => SendAsync("save"));
        PauseCommand = new RelayCommand(_ => SendAsync("toggle"));
        RecordCommand = new RelayCommand(_ => SendAsync("record"));
        PauseRecordCommand = new RelayCommand(_ => SendAsync("pause-record"));
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(EffectiveFolder));
        OpenLastClipCommand = new RelayCommand(_ => Shell(LastClipPath), _ => File.Exists(LastClipPath));
        StartEngineCommand = new RelayCommand(_ => StartEngine());
        ApplyCommand = new RelayCommand(_ => Apply(), _ => IsDirty);
        RevertCommand = new RelayCommand(_ => Load(), _ => IsDirty);
        RefreshCapsCommand = new RelayCommand(_ => LoadCapsAsync());
        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());
        VoiceRecordCommand = new RelayCommand(_ => VoiceCommand("voice-enroll"));
        VoiceTestCommand = new RelayCommand(_ => { TestCard = "clip"; VoiceCommand("voice-test"); });
        VoiceTestShotCommand = new RelayCommand(_ => { TestCard = "shot"; VoiceCommand("voice-test"); });
        VoiceCancelCommand = new RelayCommand(_ => VoiceCommand("voice-cancel"));
        VoiceDeleteCommand = new RelayCommand(_ => VoiceCommand("voice-delete"));
        VoiceRecordShotCommand = new RelayCommand(_ => VoiceCommand("voice-enroll-shot"));
        VoiceDeleteShotCommand = new RelayCommand(_ => VoiceCommand("voice-delete-shot"));
        ScreenshotCommand = new RelayCommand(_ => SendAsync("screenshot"));
        ResetHotkeysCommand = new RelayCommand(_ => ResetHotkeys());
        foreach (var state in KeyStates)
            state.FixCommand = new RelayCommand(_ => UseFreeKeys([state]), _ => state.CanFix);
        FixAllCommand = new RelayCommand(_ => UseFreeKeys(KeyStates.Where(k => k.CanFix)), _ => ShowsFixAll);
        ShareLastClipCommand = new RelayCommand(_ => { if (HasLastClip) ShareRequested?.Invoke(LastClipPath!); });
        ShareLastShotCommand = new RelayCommand(_ => { if (HasLastShot) ShareRequested?.Invoke(LastShotPath!); });
        ViewLastShotCommand = new RelayCommand(_ => { if (HasLastShot) PictureRequested?.Invoke(LastShotPath!); });
        VoiceSetUpCommand = new RelayCommand(_ => NavigateRequested?.Invoke("voice"));
        VoiceDismissHintCommand = new RelayCommand(_ => DismissVoiceHint());
    }

    public void Start()
    {
        LoadCapsAsync();
        Poll();
        _poll.Start();
        WatchSettingsFile();
    }

    public void Stop()
    {
        if (VoiceView is "enrolling" or "testing")
            PipeClient.Send("voice-cancel", 800);
        _poll.Stop();
        _watcher?.Dispose();
    }

    FileSystemWatcher? _watcher;
    readonly DispatcherTimer _reloadDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };

    /// <summary>
    /// Reloads when settings.json changes outside this window (another window, the tray, a script), so a
    /// window left open can never write an old copy back. Unsaved edits in this window are kept.
    /// </summary>
    void WatchSettingsFile()
    {
        string path = SettingsStore.DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _reloadDelay.Tick += (_, _) => { _reloadDelay.Stop(); ReloadIfChangedOutside(); };
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _reloadDelay.Stop();
            _reloadDelay.Start();
        });
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (s, e) => changed(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    public void ReloadIfChangedOutside()
    {
        if (IsDirty)
            return;
        var onDisk = SettingsStore.Load(SettingsStore.DefaultPath);
        if (SettingsStore.Serialize(onDisk) != _savedJson)
            Load();
    }

    /// <summary>Shows instant replay on or off without saving it (used by --render).</summary>
    public void SeedReplay(bool enabled)
    {
        _s.Replay.Enabled = enabled;
        _savedJson = SettingsStore.Serialize(_s);
        RaiseAll();
    }

    /// <summary>Applies a status/caps snapshot directly (used by --render).</summary>
    public void Seed(StatusDto? status, CapsDto? caps)
    {
        _status = status;
        UpdateLatestPictures();
        RefreshKeyStates();
        SetCaps(caps);
    }

    /// <summary>A status as if the poll had brought it (used by --render livecheck).</summary>
    public void SeedStatus(StatusDto status)
    {
        _status = status;
        UpdateLatestPictures();
        RefreshKeyStates();
        RaiseStatus();
    }

    /// <summary>The engine's status changed; the update bar only when the update did.</summary>
    void RaiseStatus()
    {
        Raise(StatusProperties);
        if (Update == _updateShown)
            return;
        _updateShown = Update;
        Raise(nameof(UpdateBarVisible), nameof(UpdateText), nameof(UpdateActionVisible), nameof(UpdateActionText), nameof(UpdateNotesVisible));
        // WPF asks a button again only after input: a bar that just appeared would keep its Update grey until a click.
        CommandManager.InvalidateRequerySuggested();
    }

    Engine.Updates.UpdateStatus? _updateShown;

    /// <summary>The Status page's Latest card asks the window to share a file or to view a screenshot.</summary>
    public event Action<string>? ShareRequested, PictureRequested;
    public ICommand ShareLastClipCommand { get; }
    public ICommand ShareLastShotCommand { get; }
    public ICommand ViewLastShotCommand { get; }

    public bool HasLastClip => File.Exists(LastClipPath);
    public bool HasLastShot => File.Exists(LastShotPath);
    public string LastShotName => HasLastShot ? Path.GetFileNameWithoutExtension(LastShotPath!) : Text.Get("status.noShot");
    public System.Windows.Media.Imaging.BitmapSource? LastClipThumbnail { get; private set; }
    public System.Windows.Media.Imaging.BitmapSource? LastShotThumbnail { get; private set; }
    string? _clipThumbFor, _shotThumbFor;

    /// <summary>Small pictures of the latest clip and screenshot, fetched again only when they change.</summary>
    void UpdateLatestPictures()
    {
        void Fetch(string? path, ref string? fetchedFor, Action<System.Windows.Media.Imaging.BitmapSource?> set)
        {
            if (string.Equals(path, fetchedFor, StringComparison.OrdinalIgnoreCase))
                return;
            fetchedFor = path;
            set(null);
            if (path == null || !File.Exists(path))
                return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            ShellThumbnail.Request(path, image => dispatcher?.BeginInvoke(() => set(image)));
        }
        Fetch(LastClipPath, ref _clipThumbFor, image => { LastClipThumbnail = image; Raise(nameof(LastClipThumbnail)); });
        Fetch(LastShotPath, ref _shotThumbFor, image => { LastShotThumbnail = image; Raise(nameof(LastShotThumbnail)); });
    }

    void Raise(params string[] names)
    {
        foreach (string n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    void RaiseAll()
    {
        RefreshKeyStates();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    void Set(Action change)
    {
        change();
        _s.Normalize();
        IsDirty = SettingsStore.Serialize(_s) != _savedJson;
        if (IsDirty)
            _message = "";
        RaiseAll();
    }

    public void Load()
    {
        _s = SettingsStore.Load(SettingsStore.DefaultPath);
        _savedJson = SettingsStore.Serialize(_s);
        IsDirty = false;
        BuildTextOptions();
        RebuildOptions();
        ApplyAccent();
        RaiseAll();
    }

    public bool IsDirty { get; private set; }

    // ---------------------------------------------------------------- the update bar

    Engine.Updates.UpdateStatus? Update => _status?.Update;

    public bool UpdateBarVisible => Update is { State: not Engine.Updates.UpdateState.None };

    public string UpdateText => Update switch
    {
        { State: Engine.Updates.UpdateState.Downloading } u => Text.Format("update.downloading", u.Percent),
        { State: Engine.Updates.UpdateState.WaitingForRecording } => Text.Get("update.waiting"),
        { State: Engine.Updates.UpdateState.WaitingForWindow } => Text.Get("update.waitingWindow"),
        { State: Engine.Updates.UpdateState.Failed } => Text.Get("update.failed"),
        { State: Engine.Updates.UpdateState.Ready } => Text.Get("update.restarting"),
        { } u => Text.Format("update.available", AppInfo.Display(u.Version)),
        null => "",
    };

    public bool UpdateActionVisible => Update?.State is Engine.Updates.UpdateState.Available or Engine.Updates.UpdateState.Failed;

    public string UpdateActionText => Text.Get(Update?.State == Engine.Updates.UpdateState.Failed ? "update.retry" : "update.now");

    /// <summary>Downloads and restarts into the update; not while settings changes are unsaved (the restart would lose them).</summary>
    public ICommand UpdateCommand => _updateCommand ??= new RelayCommand(_ => SendAsync("update-download"), _ => !IsDirty && UpdateActionVisible);
    RelayCommand? _updateCommand;

    public bool UpdateNotesVisible => Update?.NotesUrl != null;

    /// <summary>The window in use beyond unsaved settings: the editor or the share panel open, an edit still saving (the window sets it).</summary>
    public Func<bool> InUse { get; set; } = () => false;

    /// <summary>The status question; "busy" while the window is in use, so a downloaded update waits instead of closing it.</summary>
    internal string StatusQuestion => IsDirty || InUse() ? "status busy" : "status";

    public ICommand UpdateNotesCommand => _updateNotesCommand ??= new RelayCommand(_ =>
    {
        if (Update?.NotesUrl is { } url)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    });
    RelayCommand? _updateNotesCommand;
    public string Message => IsDirty ? Text.Get("bar.unsaved") : _message;
    public bool ShowBar => IsDirty || _message.Length > 0;
    public string VersionText => Text.Format("general.version", AppInfo.Display(AppInfo.Version));

    // ---------------------------------------------------------------- auto captions model (General > Storage)

    CaptionModel _captionModel = CaptionModel.Default;
    RelayCommand? _removeCaptionModel;

    /// <summary>The captions engine downloaded with the model (tests give their own).</summary>
    public CaptionRuntime CaptionRuntime { get; set; } = CaptionRuntime.Default;

    public CaptionModel CaptionModel
    {
        get => _captionModel;
        set
        {
            _captionModel = value;
            RefreshCaptionModel();
        }
    }

    /// <summary>The model or the engine is on this PC (a partial model too: it can be removed).</summary>
    public bool CaptionModelOnDisk => File.Exists(_captionModel.Path) || CaptionRuntime.DiskBytes > 0;

    public string CaptionModelText => CaptionModelOnDisk
        ? Text.Format("general.captionsSize", Math.Max(1, (int)Math.Round(CaptionBytes / (1024.0 * 1024))))
        : Text.Get("general.captionsNone");

    long CaptionBytes => (File.Exists(_captionModel.Path) ? new FileInfo(_captionModel.Path).Length : 0) + CaptionRuntime.DiskBytes;

    public ICommand RemoveCaptionModelCommand => _removeCaptionModel ??= new RelayCommand(_ =>
    {
        try
        {
            _captionModel.Remove();
            CaptionRuntime.Remove();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by captions running right now: it stays, and the row says so.
        }
        RefreshCaptionModel();
    }, _ => CaptionModelOnDisk);

    /// <summary>Reads the model's state again (after the editor downloaded it).</summary>
    public void RefreshCaptionModel()
    {
        Raise(nameof(CaptionModelOnDisk), nameof(CaptionModelText));
        CommandManager.InvalidateRequerySuggested();
    }

    // ------------------------------------------------------------------ cached option lists

    public List<Option> Monitors { get; private set; } = [];
    public List<Option> Windows { get; private set; } = [];
    public List<Option> Speakers { get; private set; } = [];
    public List<Option> Mics { get; private set; } = [];
    public List<Option> Resolutions { get; private set; } = [];
    public List<Option> FpsOptions { get; private set; } = [];
    public List<Option> QuickOptions { get; private set; } = [];
    public List<Option> AudioBitrates { get; private set; } = [];
    public List<Option> FolderLimits { get; private set; } = [];
    public List<Option> Languages { get; } = [.. Text.Languages.Select(l => new Option(l.Name, l.Code))];

    /// <summary>The fixed lists, labelled in the current language (built again after a language switch).</summary>
    void BuildTextOptions()
    {
        FpsOptions = [.. new[] { 30, 60, 90, 120, 144, 165, 240 }.Select(f => new Option(Text.Ltr($"{f} fps"), f))];
        QuickOptions = [.. new[] { 10, 15, 20, 30, 45, 60 }.Select(s => new Option(Text.Format("unit.seconds", s), s))];
        AudioBitrates = [.. new[] { 128, 160, 192, 256, 320 }.Select(b => new Option(Text.Ltr($"{b} kbps"), b))];
        FolderLimits =
        [
            new(Text.Get("saving.noLimit"), 0), .. new[] { 25, 50, 100, 200, 500 }.Select(g => new Option(Text.Ltr($"{g} GB"), g)),
            new(Text.Ltr("1 TB"), 1000),
        ];
    }

    void SetCaps(CapsDto? caps)
    {
        _caps = caps;
        RebuildOptions();
        RaiseAll();
    }

    /// <summary>The running apps' windows, the saved one first while it is closed (so the choice still shows).</summary>
    List<Option> WindowOptions(string saved)
    {
        var options = (_caps?.Windows ?? []).Select(w => new Option(w.Name, w.Value)).ToList();
        if (!string.IsNullOrEmpty(saved) && options.All(o => (string)o.Value != saved))
            options.Insert(0, new Option(App.WindowTarget.Title(saved) ?? saved, saved));
        return options;
    }

    void RebuildOptions()
    {
        Monitors = [.. (_caps?.Monitors ?? []).Select(m => new Option(MonitorLabel(m), m.Id))];

        Windows = WindowOptions(_s.Capture.Window);

        Speakers = DeviceOptions(_caps?.Speakers);
        Mics = DeviceOptions(_caps?.Mics);
        RebuildResolutions();
    }

    (int W, int H) _resolutionsFor;

    void RebuildResolutions()
    {
        var size = BaseSize;
        if (size == _resolutionsFor && Resolutions.Count > 0)
            return;
        _resolutionsFor = size;
        Resolutions = [.. ResolutionOptions.For(size.H).Select(r => new Option(ResolutionLabel(r, size), r))];
    }

    static string ResolutionLabel(OutputResolution r, (int W, int H) native) => r switch
    {
        OutputResolution.Native => $"{Text.Get("quality.native")} • {Text.Ltr($"{native.W}×{native.H}")}",
        OutputResolution.P2160 => "4K • 2160p",
        OutputResolution.P1440 => "2K • 1440p",
        OutputResolution.P1080 => "1080p",
        _ => "720p",
    };

    static string MonitorLabel(MonitorInfo m)
    {
        string name = m.Name.Split(':')[0].Trim();
        return $"{name} • {Text.Ltr($"{m.Width}×{m.Height}")}{(m.Primary ? Text.Get("capture.primary") : "")}";
    }

    static List<Option> DeviceOptions(List<NameValue>? devices)
    {
        var list = (devices ?? [])
            .Select(d => new Option(d.Value == "default" ? Text.Get("audio.default") : d.Name, d.Value)).ToList();
        if (list.All(o => (string)o.Value != "default"))
            list.Insert(0, new Option(Text.Get("audio.default"), "default"));
        return list;
    }

    // ------------------------------------------------------------------ capture

    public CaptureMode Mode { get => _s.Capture.Mode; set => Set(() => _s.Capture.Mode = value); }

    /// <summary>Shown selection: the saved monitor, or the one the engine will actually use (primary).</summary>
    public string MonitorId
    {
        get => VideoMath.PickMonitor(_caps?.Monitors ?? [], _s.Capture.MonitorId)?.Id ?? _s.Capture.MonitorId;
        set
        {
            if (value == null || value == MonitorId)
                return;
            Set(() => _s.Capture.MonitorId = value);
            RebuildResolutions();
            Raise(nameof(Resolutions), nameof(Resolution));
        }
    }

    public string WindowTarget { get => _s.Capture.Window; set => Set(() => _s.Capture.Window = value ?? ""); }
    public bool GameFallback { get => _s.Capture.GameFallbackToScreen; set => Set(() => _s.Capture.GameFallbackToScreen = value); }
    public bool CaptureCursor { get => _s.Capture.CaptureCursor; set => Set(() => _s.Capture.CaptureCursor = value); }

    public int SecondsIndex
    {
        get => Array.FindIndex(SecondsSteps, v => v >= _s.Replay.Seconds) is int i and >= 0 ? i : SecondsSteps.Length - 1;
        set => Set(() => _s.Replay.Seconds = SecondsSteps[Math.Clamp(value, 0, SecondsSteps.Length - 1)]);
    }
    public int SecondsMaxIndex => SecondsSteps.Length - 1;
    public string BufferLength => Text.Duration(_s.Replay.Seconds);
    public bool QuickEnabled { get => _s.Replay.QuickEnabled; set => Set(() => _s.Replay.QuickEnabled = value); }
    public int QuickSeconds { get => _s.Replay.QuickSeconds; set => Set(() => _s.Replay.QuickSeconds = value); }

    // ------------------------------------------------------------------ quality

    IReadOnlyCollection<string>? EncoderIds => _caps?.Encoders;

    public VideoCodec Codec { get => _s.Video.Codec; set => Set(() => _s.Video.Codec = value); }
    public bool Av1Available => CodecAvailable(VideoCodec.Av1);
    public bool HevcAvailable => CodecAvailable(VideoCodec.Hevc);
    public bool H264Available => CodecAvailable(VideoCodec.H264);
    bool CodecAvailable(VideoCodec c) => EncoderIds == null || EncoderCatalog.HasHardware(c, EncoderIds);
    VideoCodec Recommended => Av1Available ? VideoCodec.Av1 : HevcAvailable ? VideoCodec.Hevc : VideoCodec.H264;
    public bool Av1Recommended => Recommended == VideoCodec.Av1;
    public bool HevcRecommended => Recommended == VideoCodec.Hevc;
    public bool H264Recommended => Recommended == VideoCodec.H264;
    public bool CpuOnly => EncoderIds != null && !Av1Available && !HevcAvailable && !H264Available;

    /// <summary>Saved resolution, or Native when the saved one is not offered on this monitor.</summary>
    public OutputResolution Resolution
    {
        get => Resolutions.Any(o => (OutputResolution)o.Value == _s.Video.Resolution) ? _s.Video.Resolution : OutputResolution.Native;
        set => Set(() => _s.Video.Resolution = value);
    }

    public int Fps { get => _s.Video.Fps; set => Set(() => _s.Video.Fps = value); }
    public QualityPreset Quality { get => _s.Video.Quality; set => Set(() => _s.Video.Quality = value); }
    public int CustomCq { get => _s.Video.CustomCq; set => Set(() => _s.Video.CustomCq = value); }
    public int CustomMbps { get => _s.Video.CustomMbps; set => Set(() => _s.Video.CustomMbps = value); }
    public int PresetLevel
    {
        get => int.TryParse(_s.Video.Preset.TrimStart('p', 'P'), out int n) ? Math.Clamp(n, 1, 7) : 5;
        set => Set(() => _s.Video.Preset = $"p{Math.Clamp(value, 1, 7)}");
    }

    /// <summary>The encoder speed in words: faster (1–2), balanced (3–5), best quality (6–7).</summary>
    public string PresetWord => Text.Get(PresetLevel switch { <= 2 => "quality.speedFast", <= 5 => "quality.speedBalanced", _ => "quality.speedBest" });

    (int W, int H) BaseSize
    {
        get
        {
            var m = VideoMath.PickMonitor(_caps?.Monitors ?? [], _s.Capture.MonitorId);
            return m == null ? (2560, 1440) : (m.Width, m.Height);
        }
    }

    public string EstimateText
    {
        get
        {
            var (bw, bh) = BaseSize;
            var (w, h) = VideoMath.OutputSize(bw, bh, _s.Video.Resolution);
            var plan = EncoderPresets.Plan(_s.Video.Codec, _s.Video.Quality, w, h, _s.Video.Fps, _s.Video.CustomCq,
                _s.Video.CustomMbps, nvencAvailable: true, _s.Video.Preset);
            int estimate = EncoderPresets.EstimateBufferMb(plan.MaxBitrateKbps, _s.Replay.Seconds);
            int mb = Math.Min(estimate, BufferBudget.CapMb(estimate, BufferBudget.PhysicalMemoryBytes));
            return Text.Format("quality.estimate", Text.Ltr(FormatSize(mb * 1024L * 1024)));
        }
    }

    // ------------------------------------------------------------------ audio

    public bool AudioSystem { get => _s.Audio.System; set => Set(() => _s.Audio.System = value); }
    public string SystemDevice { get => _s.Audio.SystemDevice; set => Set(() => _s.Audio.SystemDevice = value ?? "default"); }
    public double SystemVolume { get => Math.Round(_s.Audio.SystemVolume * 100); set => Set(() => _s.Audio.SystemVolume = value / 100.0); }
    public bool Mic { get => _s.Audio.Mic; set => Set(() => _s.Audio.Mic = value); }
    public string MicDevice
    {
        get => _s.Audio.MicDevice;
        set
        {
            if (value != null && value != _s.Audio.MicDevice)
                Set(() => _s.Audio.SelectMic(value));
        }
    }
    public double MicVolume { get => Math.Round(_s.Audio.MicVolume * 100); set => Set(() => _s.Audio.MicVolume = value / 100.0); }
    public bool SeparateTracks { get => _s.Audio.SeparateTracks; set => Set(() => _s.Audio.SeparateTracks = value); }
    public bool SourceAudioOnly { get => _s.Audio.SourceAudioOnly; set => Set(() => _s.Audio.SourceAudioOnly = value); }

    public bool SourceAudioPossible => _s.Capture.Mode != CaptureMode.Screen;
    public int AudioBitrate { get => _s.Audio.BitrateKbps; set => Set(() => _s.Audio.BitrateKbps = value); }


    // ------------------------------------------------------------------ hotkeys

    public string HkSave { get => _s.Hotkeys.Save; set => Set(() => _s.Hotkeys.Save = value ?? ""); }
    public string HkQuick { get => _s.Hotkeys.Quick; set => Set(() => _s.Hotkeys.Quick = value ?? ""); }
    public string HkToggle { get => _s.Hotkeys.ToggleBuffer; set => Set(() => _s.Hotkeys.ToggleBuffer = value ?? ""); }
    public string HkRecord { get => _s.Hotkeys.Record; set => Set(() => _s.Hotkeys.Record = value ?? ""); }
    public string HkPauseRecord { get => _s.Hotkeys.PauseRecord ?? ""; set => Set(() => _s.Hotkeys.PauseRecord = value ?? ""); }
    public string HkScreenshot { get => _s.Hotkeys.Screenshot; set => Set(() => _s.Hotkeys.Screenshot = value ?? ""); }

    public ICommand ResetHotkeysCommand { get; }

    // ---------------------------------------------------------------- keys another app holds

    /// <summary>Where the keys are saved and how the engine is told (tests: a temp file and no engine).</summary>
    internal string SettingsPath { get; set; } = SettingsStore.DefaultPath;
    internal Func<string, int, string?> Send { get; set; } = (command, timeout) => PipeClient.Send(command, timeout);
    internal Func<Hotkey, bool> IsKeyFree { get; set; } = HotkeyManager.IsFree;
    internal Func<IReadOnlyList<Hotkey>> NvidiaKeys { get; set; } = NvidiaHotkeys.Read;
    internal Func<IReadOnlySet<System.Windows.Forms.Keys>> ObsKeys { get; set; } = ObsHotkeys.Read;

    /// <summary>
    /// A key Kairos may offer: no app holds it now, it is not one of NVIDIA's shortcuts (NVIDIA takes them back after a
    /// restart) and OBS does not use its key (OBS reacts to it with any modifiers held too).
    /// </summary>
    Func<Hotkey, bool> Available()
    {
        var nvidia = NvidiaKeys();
        var obs = ObsKeys();
        return h => !nvidia.Contains(h) && !obs.Contains(h.Key) && IsKeyFree(h);
    }

    public KeyState SaveKey { get; } = new("save");
    public KeyState ToggleKey { get; } = new("toggle");
    public KeyState RecordKey { get; } = new("record");
    public KeyState PauseRecordKey { get; } = new("pauseRecord");
    public KeyState ScreenshotKey { get; } = new("screenshot");
    IEnumerable<KeyState> KeyStates => [SaveKey, ToggleKey, RecordKey, PauseRecordKey, ScreenshotKey];

    /// <summary>Two or more keys held by other apps: one button takes free keys for all of them.</summary>
    public bool ShowsFixAll => KeyStates.Count(k => k.CanFix) >= 2;
    public ICommand FixAllCommand { get; }

    /// <summary>"3 hotkeys are held by NVIDIA" (or by other apps).</summary>
    public string TakenSummary
    {
        get
        {
            var taken = KeyStates.Where(k => k.Taken).ToList();
            var owners = taken.Select(k => k.Owner).Distinct().ToList();
            return taken.Count == 0 ? "" : Text.Format("hotkeys.takenMany", taken.Count, owners.Count == 1 ? owners[0] : Text.Get("hotkeys.otherApps"));
        }
    }

    string? _keysSeen;

    static string KeyOf(HotkeySettings keys, string action) => action switch
    {
        "save" => keys.Save,
        "toggle" => keys.ToggleBuffer,
        "record" => keys.Record,
        "pauseRecord" => keys.PauseRecord ?? "",
        "screenshot" => keys.Screenshot,
        _ => "",
    };

    static void SetKey(HotkeySettings keys, string action, string value)
    {
        switch (action)
        {
            case "save": keys.Save = value; break;
            case "toggle": keys.ToggleBuffer = value; break;
            case "record": keys.Record = value; break;
            case "pauseRecord": keys.PauseRecord = value; break;
            case "screenshot": keys.Screenshot = value; break;
        }
    }

    /// <summary>Every key Kairos uses (a free key on offer is none of these).</summary>
    IEnumerable<string> KeysInUse() => [HkSave, HkQuick, HkToggle, HkRecord, HkPauseRecord, HkScreenshot];

    /// <summary>
    /// Which keys another app holds, from the engine's report (only when that report or the keys changed, not on every
    /// poll). A key being changed in the page is not marked: the report is about the saved one.
    /// </summary>
    void RefreshKeyStates()
    {
        var held = _status?.Hotkeys;
        string seen = (held == null ? "" : string.Join(",", held.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")))
            + "|" + string.Join(",", KeysInUse()) + "|" + _savedJson;
        if (seen == _keysSeen)
            return;
        _keysSeen = seen;
        var saved = _savedJson == null ? null : SettingsStore.Deserialize(_savedJson).Hotkeys;
        IReadOnlyList<Hotkey>? nvidia = null;
        Func<Hotkey, bool>? available = null;
        foreach (var state in KeyStates)
        {
            string key = KeyOf(_s.Hotkeys, state.Action);
            bool taken = held != null && held.TryGetValue(state.Action, out bool reserved) && !reserved
                && HotkeyParser.TryParse(key, out _) && (saved == null || KeyOf(saved, state.Action) == key);
            if (!taken)
            {
                state.Update(false, "", null);
                continue;
            }
            nvidia ??= NvidiaKeys();
            HotkeyParser.TryParse(key, out var hotkey);
            string owner = nvidia.Contains(hotkey) ? "NVIDIA" : Text.Get("hotkeys.otherApp");
            available ??= Available();
            state.Update(true, owner, HotkeyDefaults.FreeChoice(state.Action, KeysInUse(), available));
        }
    }

    /// <summary>
    /// Takes a free key for each of these actions and saves only the keys at once (other unsaved changes stay pending),
    /// then the engine reserves them.
    /// </summary>
    void UseFreeKeys(IEnumerable<KeyState> states)
    {
        var disk = SettingsStore.Load(SettingsPath);
        var inUse = KeysInUse().ToList();
        var available = Available();
        bool changed = false;
        foreach (var state in states.ToList())
        {
            if (HotkeyDefaults.FreeChoice(state.Action, inUse, available) is not { } free)
                continue;
            SetKey(disk.Hotkeys, state.Action, free);
            SetKey(_s.Hotkeys, state.Action, free);
            inUse.Add(free);
            changed = true;
        }
        if (!changed)
            return;
        disk.Normalize();
        SettingsStore.Save(SettingsPath, disk);
        _savedJson = SettingsStore.Serialize(disk);
        IsDirty = SettingsStore.Serialize(_s) != _savedJson;
        RaiseAll();
        var send = Send;
        Task.Run(() => send("apply", 4000));
    }

    /// <summary>Kairos's own keys back, as unsaved changes (Save applies them, Revert takes them back).</summary>
    public void ResetHotkeys() => Set(() => _s.Hotkeys = new HotkeySettings());

    // ------------------------------------------------------------------ general

    public string Folder { get => _s.Output.Folder; set => Set(() => _s.Output.Folder = value ?? ""); }
    public bool PerAppFolders { get => _s.Output.PerAppFolders; set => Set(() => _s.Output.PerAppFolders = value); }
    public int MaxFolderGb { get => _s.Output.MaxFolderGb; set => Set(() => _s.Output.MaxFolderGb = value); }
    public bool StartWithWindows { get => _s.General.StartWithWindows; set => Set(() => _s.General.StartWithWindows = value); }
    public bool CheckUpdates { get => _s.General.CheckUpdates; set => Set(() => _s.General.CheckUpdates = value); }
    public bool OnlyWhenGaming { get => _s.General.OnlyWhenGaming; set => Set(() => _s.General.OnlyWhenGaming = value); }
    public bool SoundOnSave { get => _s.General.SoundOnSave; set => Set(() => _s.General.SoundOnSave = value); }
    public bool ToastOnSave { get => _s.General.ToastOnSave; set => Set(() => _s.General.ToastOnSave = value); }
    public string Language { get => _s.General.Language; set => Set(() => _s.General.Language = value ?? Text.Fallback); }

    // ------------------------------------------------------------------ Smart Save

    public bool VoiceEnabled { get => _s.Voice.Enabled; set => Set(() => _s.Voice.Enabled = value); }
    public VoiceSensitivity VoiceSensitivity { get => _s.Voice.Sensitivity; set => Set(() => _s.Voice.Sensitivity = value); }
    public bool ShowVoiceHint => !_s.Voice.Enabled && !_s.Voice.HintDismissed;

    Engine.Voice.VoiceStatus? Voice => _status?.Voice;
    string _voiceNote = "";

    /// <summary>
    /// The card a test was started from ("clip" or "shot"): it shows the test. A test listens for both phrases and
    /// says which one it heard.
    /// </summary>
    public string TestCard
    {
        get => _testCard;
        set
        {
            _testCard = value;
            Raise(nameof(VoiceView), nameof(ShotView));
        }
    }
    string _testCard = "clip";

    /// <summary>The clip phrase's card: "none", "ready", "enrolling" or "testing".</summary>
    public string VoiceView => Voice?.Mode switch
    {
        "enrolling" when Voice.EnrollingFor == "clip" => "enrolling",
        "testing" when _testCard == "clip" => "testing",
        _ => Voice?.HasPhrase == true ? "ready" : "none",
    };

    /// <summary>The screenshot phrase's card: "none", "ready", "enrolling" or "testing".</summary>
    public string ShotView => Voice?.Mode switch
    {
        "enrolling" when Voice.EnrollingFor == "shot" => "enrolling",
        "testing" when _testCard == "shot" => "testing",
        _ => Voice?.HasShotPhrase == true ? "ready" : "none",
    };
    public string VoiceStep => Text.Format("voice.step", Math.Min(3, (Voice?.Samples ?? 0) + 1));
    public bool VoiceDone1 => Voice?.Samples >= 1;
    public bool VoiceDone2 => Voice?.Samples >= 2;
    public bool VoiceDone3 => Voice?.Samples >= 3;
    public string VoiceMessage => Voice?.Message is { } key && key.StartsWith("voice.err.") ? Text.Get(key) : "";
    public string VoiceResult => Voice?.LastScore == null ? ""
        : Text.Get(!Voice.LastMatch ? "voice.notMatched" : Voice.LastPhrase == "shot" ? "voice.matchedShot" : "voice.matchedClip");
    public bool VoiceLastMatch => Voice?.LastMatch == true;
    public double VoiceStrength => Voice is { LastScore: double score } v ? Engine.Voice.PhraseMatcher.Strength(score, v.Threshold) : 0;
    public string VoiceNote => _voiceNote;

    public ICommand VoiceRecordCommand { get; }
    public ICommand VoiceTestCommand { get; }
    public ICommand VoiceTestShotCommand { get; }
    public ICommand VoiceCancelCommand { get; }
    public ICommand VoiceDeleteCommand { get; }
    public ICommand VoiceRecordShotCommand { get; }
    public ICommand VoiceDeleteShotCommand { get; }
    public ICommand ScreenshotCommand { get; }
    public ICommand VoiceSetUpCommand { get; }
    public ICommand VoiceDismissHintCommand { get; }

    void VoiceCommand(string command)
    {
        _voiceNote = _status == null ? Text.Get("voice.err.engine")
            : Voice?.ModelsFound == false && command is "voice-enroll" or "voice-enroll-shot" or "voice-test" ? Text.Get("voice.err.files")
            : Voice?.Available != true && command is "voice-enroll" or "voice-enroll-shot" or "voice-test" ? Text.Get("voice.err.noMic")
            : "";
        Raise(nameof(VoiceNote));
        if (_voiceNote.Length == 0)
            SendAsync(command);
    }

    /// <summary>The share panel's last choices.</summary>
    public ShareSettings SharePrefs => _s.Share;

    /// <summary>Saves only the share choices, leaving any other unsaved change pending.</summary>
    public void RememberShare(ShareSettings share)
    {
        var disk = SettingsStore.Load(SettingsStore.DefaultPath);
        disk.Share = share;
        disk.Normalize();
        SettingsStore.Save(SettingsStore.DefaultPath, disk);
        _s.Share = SettingsStore.Deserialize(SettingsStore.Serialize(disk)).Share;
        _savedJson = SettingsStore.Serialize(disk);
        IsDirty = SettingsStore.Serialize(_s) != _savedJson;
        RaiseAll();
    }

    /// <summary>Saves only the "card closed" flag, leaving any other unsaved change pending.</summary>
    void DismissVoiceHint()
    {
        var disk = SettingsStore.Load(SettingsStore.DefaultPath);
        disk.Voice.HintDismissed = true;
        SettingsStore.Save(SettingsStore.DefaultPath, disk);
        _s.Voice.HintDismissed = true;
        _savedJson = SettingsStore.Serialize(disk);
        IsDirty = SettingsStore.Serialize(_s) != _savedJson;
        RaiseAll();
    }

    /// <summary>Takes effect in this window at once (a preview until Apply); Revert puts the saved color back.</summary>
    void ApplyAccent()
    {
        if (System.Windows.Application.Current is { } app)
            ThemeAccent.Apply(app.Resources);
    }

    public string EffectiveFolder => _status?.Engine?.ClipsFolder is { Length: > 0 } f ? f : _s.Output.Folder;

    // ------------------------------------------------------------------ live status

    public bool EngineOnline => _status != null;

    /// <summary>Instant replay (the buffer kept in the background); off, Kairos is a plain recorder.</summary>
    public bool ReplayEnabled { get => _s.Replay.Enabled; set => Set(() => _s.Replay.Enabled = value); }
    /// <summary>Save clip and the instant replay pause need the buffer.</summary>
    public bool SaveVisible => EngineOnline && _s.Replay.Enabled;
    public bool ReplayPauseVisible => SaveVisible;
    public string ReplayPauseTip => Text.Get(PausedByUser ? "status.resume" : "status.pause") + (HkToggle.Length > 0 ? $" ({HkToggle})" : "");

    // The recording card: start here; while it runs, stop and pause sit side by side.
    public string RecordState => StatusLogic.RecordState(_status);
    public string RecordTitle => Text.Get(RecordState switch { "recording" => "status.recording", "paused" => "status.recordPaused", _ => "status.recordIdle" });
    public string RecordDetail => _status?.Engine is { Recording: true } r
        ? StatusLogic.RecordingDetail(r.RecordingSeconds, r.RecordingBytes)
        : "";
    public bool IsRecording => Recording;
    public bool IsRecordingLive => RecordState == "recording";
    /// <summary>Instant replay off: recording is the page's main action.</summary>
    public bool StartPrimaryVisible => !Recording && !_s.Replay.Enabled;
    public bool StartGhostVisible => !Recording && _s.Replay.Enabled;
    public string RecordPauseLabel => Text.Get(RecordingPaused ? "status.resumeRecord" : "status.pauseRecord");
    bool PausedByUser => _status?.PausedByUser == true;
    bool Recording => _status?.Engine?.Recording == true;

    /// <summary>active | paused | waiting | recording | error | offline</summary>
    bool RecordingPaused => _status?.Engine?.RecordingPaused == true;

    /// <summary>The instant replay card's state (the recording has its own card).</summary>
    public string State => StatusLogic.ReplayState(_status, _s.Replay.Enabled);

    public string StateTitle => Text.Get("status." + State);

    public string StateDetail => StatusLogic.ReplayDetail(State, _status?.Error);

    public double BufferProgress => _s.Replay.Enabled && _status?.Engine is { BufferTargetSeconds: > 0 } e ? (double)e.BufferSeconds / e.BufferTargetSeconds : 0;
    public string BufferText => !_s.Replay.Enabled ? Text.Get("status.replayOff")
        : _status?.Engine is { } e ? $"{Text.Duration(e.BufferSeconds)} / {Text.Duration(e.BufferTargetSeconds)}" : "–";
    public string MemoryText => _status?.Engine is { } e ? Text.Ltr($"{e.WorkingSetMb} MB") : "–";

    public string QualityText => _status?.Engine is { } e ? $"{CodecName(e.Encoder)} • {e.Height}p{Math.Round(e.Fps)}" : "–";

    public string EncoderText => _status?.Engine?.Encoder switch
    {
        null or "" => "",
        var id when id.Contains("nvenc") => "NVIDIA NVENC",
        var id when id.Contains("amf") => "AMD AMF",
        var id when id.Contains("qsv") => "Intel Quick Sync",
        _ => "CPU (x264)",
    };

    static string CodecName(string encoderId) =>
        encoderId.Contains("av1") ? "AV1" : encoderId.Contains("hevc") || encoderId.Contains("h265") ? "HEVC" : "H.264";


    public string? LastClipPath => _status?.LastClip;
    /// <summary>The newest screenshot the engine took (the Clips page refreshes when it changes).</summary>
    public string? LastShotPath => _status?.LastShot;
    public string LastClipName => LastClipPath is { } p ? Path.GetFileNameWithoutExtension(p) : Text.Get("status.noClip");
    public string FolderWarning => _status?.Engine is { FolderFellBack: true } e ? Text.Format("status.folderFallback", e.ClipsFolder) : "";

    // ------------------------------------------------------------------ commands

    public ICommand SaveNowCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand PauseRecordCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenLastClipCommand { get; }
    public ICommand StartEngineCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand RefreshCapsCommand { get; }
    public ICommand BrowseFolderCommand { get; }

    void Apply()
    {
        bool languageChanged = _s.General.Language != SettingsStore.Deserialize(_savedJson).General.Language;
        SettingsStore.Save(SettingsStore.DefaultPath, _s);
        _savedJson = SettingsStore.Serialize(_s);
        IsDirty = false;
        _message = "…";
        RaiseAll();
        Task.Run(() =>
        {
            string? reply = PipeClient.Send("apply", 4000);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _message = Text.Get(reply != null ? "bar.applied" : "bar.savedOnly");
                if (reply != null)
                    TryParseStatus(reply);
                RaiseAll();
                _messageTimer.Stop();
                _messageTimer.Start();
            });
        });
        if (languageChanged)
        {
            ApplyLanguage(_s.General.Language);
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>
    /// Switches the text language and relabels every list in it (they were built in the old one, and the window that
    /// reopens in the new language keeps this view model).
    /// </summary>
    public void ApplyLanguage(string language)
    {
        Text.Language = language;
        BuildTextOptions();
        _resolutionsFor = default; // "native" is a word too
        RebuildOptions();
        RaiseAll();
    }

    Task _typingSent = Task.CompletedTask;

    /// <summary>
    /// Tells the engine a hotkey box has focus (or not), so a key typed there is not run. Sent one after another, so a
    /// quick focus and blur arrive in order.
    /// </summary>
    public void SetTypingHotkey(bool typing)
    {
        string command = typing ? "hotkeys-typing" : "hotkeys-typed";
        _typingSent = _typingSent.ContinueWith(_ => PipeClient.Send(command, 800), TaskScheduler.Default);
    }

    void SendAsync(string command) => Task.Run(() =>
    {
        string? reply = PipeClient.Send(command);
        if (reply != null)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { TryParseStatus(reply); RaiseStatus(); });
    });

    async void Poll()
    {
        if (_polling)
            return;
        _polling = true;
        // Asked without a thread waiting on the answer. The answer is handed to the window's thread explicitly: the first
        // poll runs before the window's message loop does, where an await has no context to come back to.
        var dispatcher = _poll.Dispatcher;
        string? reply = await PipeClient.SendAsync(StatusQuestion, 800).ConfigureAwait(false);
        _ = dispatcher.BeginInvoke(() => TakeStatus(reply));
    }

    void TakeStatus(string? reply)
    {
        _polling = false;
        bool wasOnline = _status != null;
        if (reply == null)
            _status = null;
        else
            TryParseStatus(reply);
        if (!wasOnline && _status != null && _caps == null)
            LoadCapsAsync();
        RefreshKeyStates();
        RaiseStatus();
        _poll.Interval = TimeSpan.FromMilliseconds(VoiceView is "enrolling" or "testing" ? 200 : 500);
    }

    void TryParseStatus(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<StatusDto>(json, Ipc.Json);
            if (s != null && s.Hotkeys != null)
            {
                _status = s;
                UpdateLatestPictures();
            }
        }
        catch (JsonException) { }
    }

    void LoadCapsAsync() => Task.Run(() =>
    {
        string? reply = PipeClient.Send("caps", 3000);
        if (reply == null)
            return;
        try
        {
            var caps = JsonSerializer.Deserialize<CapsDto>(reply, Ipc.Json);
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => SetCaps(caps));
        }
        catch (JsonException) { }
    });

    static void StartEngine() =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--background") { UseShellExecute = false });

    void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(Folder) ? Folder : "" };
        if (dialog.ShowDialog() == true)
            Folder = dialog.FolderName;
    }

    static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(App.ShellPaths.OpenFolder(folder));
    }

    static void Shell(string? path)
    {
        if (path != null && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
