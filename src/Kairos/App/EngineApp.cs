using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using Kairos.Engine;
using Kairos.Engine.Updates;
using Kairos.Engine.Voice;
using Kairos.Settings;
using Microsoft.Win32;

namespace Kairos.App;

/// <summary>The always-on background process: libobs pipeline, tray, hotkeys and the IPC server.</summary>
public sealed class EngineApp : ApplicationContext
{
    static readonly string ExePath = Environment.ProcessPath!;

    readonly Control _ui = new();
    readonly TrayController _tray = new();
    readonly HotkeyManager _hotkeys = new();
    readonly SaveToast _toast = new();
    readonly ClipIndex _clipIndex = new(ClipIndex.DefaultPath);
    readonly VoiceTrigger _voice = new(SpeechModels.DefaultDirectory, Path.Combine(SettingsStore.AppDataDir, "voice"));
    int _pendingSeconds;
    bool _pendingByVoice;
    /// <summary>The newest clip saved in this session, until it is deleted from the tray.</summary>
    string? _undoClip;
    readonly System.Windows.Forms.Timer _trayTimer = new() { Interval = 5000 };
    readonly BorderlessTips _tips;
    /// <summary>One app and Games modes only: the pipeline's once-a-second look at its scene.</summary>
    System.Windows.Forms.Timer? _sceneTimer;
    /// <summary>Separate sources only: which processes each sound row captures, every 2 s.</summary>
    AudioWatcher? _audioWatcher;
    /// <summary>The encoders that could not start on this PC (left out of the next pipeline), kept in hardware.json.</summary>
    EncoderFallback _encoders = new();
    HardwareProfile? _hardware;
    HardwareState _hardwareState = new();
    /// <summary>NVENC called the NVIDIA driver outdated: the Status page's note ("status.nvidiaDriver" or "...Restart").</summary>
    string? _nvidiaNote;
    /// <summary>A ring could not be written this run: the replay stays in RAM until the replay's disk settings change.</summary>
    bool _diskOff;

    /// <summary>The ring files' folder <paramref name="s"/> asks for (the clips folder's drive unless one is chosen), or null.</summary>
    string? RingFolder(AppSettings s) => s.Replay.OnDisk
        ? Engine.Replay.DiskReplayPlace.FolderFor(s.Replay.DiskDrive, _pipeline?.ClipsFolder is { Length: > 0 } clips ? clips : s.Output.Folder)
        : null;
    static string HardwarePath => Path.Combine(SettingsStore.AppDataDir, HardwareState.FileName);
    /// <summary>Automatic: over 2 % of the frames lagged or skipped in a minute, sampled each 5 s while the buffer runs.</summary>
    readonly LagGuard _skipping = new(0.02, 60);
    readonly PipeServer _pipe;
    AppSettings _settings;
    CapturePipeline? _pipeline;
    GameWatcher? _watcher;
    string? _pendingApp;
    /// <summary>The clip's place as the save was asked for (the disk writes it there itself).</summary>
    string? _pendingTarget;
    /// <summary>The folder name of what the running recording records.</summary>
    string _recordingApp = "";
    string? _lastClip;
    /// <summary>The newest screenshot saved in this session.</summary>
    string? _lastShot;
    /// <summary>The newest clip or screenshot saved in this session: what the tray shares.</summary>
    string? _lastSaved;
    string? _error;
    bool _pausedByUser;
    readonly SaveWatch _saveWatch = new();
    bool _retried;
    bool _stopped;
    /// <summary>The screen check saw a picture this session (<see cref="CheckScreenPicture"/>).</summary>
    bool _screenSeen;
    bool _starting = true;
    /// <summary>Keys taken this time settings were applied (two actions on the same keys: the second is not another app's fault).</summary>
    readonly HashSet<Hotkey> _keysInUse = [];
    readonly TakenKeyNotice _takenNotice = new(TakenKeyNotice.DefaultPath);
    /// <summary>Keys found taken while applying settings, not told about before: one message for all of them.</summary>
    readonly List<string> _newlyTaken = [];
    readonly UpdateService _updates = UpdateService.ForThisInstall(SettingsStore.AppDataDir);
    /// <summary>When the replay buffer first ran (the first update check waits a minute after it).</summary>
    DateTime? _bufferActiveSince;
    /// <summary>What the settings window last said with its status question: in use or not, and when (a downloaded update waits for it).</summary>
    bool _windowSaidBusy;
    DateTime _windowAsked;

    public EngineApp(bool openSettings, bool background)
    {
        _ui.CreateControl();
        PowerThrottling.OptOut();
        // The log first: the start's own messages (the keys picked, "Updated to …") are in it too.
        ObsLog.Open(Path.Combine(SettingsStore.AppDataDir, "logs"));
        ObsLog.Info($"kairos {AppInfo.Version}{(_updates.Available ? "" : " (not installed: a development run)")}");

        bool firstRun = !File.Exists(SettingsStore.DefaultPath);
        _settings = SettingsStore.Load(SettingsStore.DefaultPath);
        _tips = new BorderlessTips(() => _settings.Capture.BorderlessTips);
        // The install wizard's language and start-with-Windows choice, once after it ran.
        bool chosen = InstallChoices.ConsumeInto(_settings, SettingsStore.AppDataDir);
        // A first install takes the usual keys, or for each one another app holds (NVIDIA's are the same), a free one.
        bool pickedKeys = firstRun && HotkeyDefaults.PickFree(_settings.Hotkeys, HotkeyManager.IsFree);
        if (firstRun || chosen)
            SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        Text.Language = _settings.General.Language;
        if (pickedKeys)
            _toast.Show(Text.Get("toast.keysPicked"), Text.Format("toast.keysPickedSave", _settings.Hotkeys.Save), Text.IsRightToLeft);

        _tray.SaveClicked += FromTray("save", () => SaveClip(quick: false));
        _tray.QuickClicked += FromTray("quick", () => SaveClip(quick: true));
        _tray.ScreenshotClicked += FromTray("screenshot", () => TakeScreenshot());
        _tray.PauseClicked += FromTray("pause", TogglePause);
        _tray.RecordClicked += FromTray("record", ToggleRecording);
        _tray.PauseRecordClicked += TogglePauseRecording;
        _tray.FolderClicked += OpenClipsFolder;
        _tray.DeleteLastClicked += DeleteLastClip;
        _tray.ShareClicked += ShareLastClip;
        _voice.Triggered += action => _ui.BeginInvoke(() =>
        {
            if (action == VoiceAction.Shot)
                TakeScreenshot(byVoice: true);
            else
                SaveClip(quick: false, byVoice: true);
        });
        _tray.SettingsClicked += OpenSettings;
        _tray.ExitClicked += () => Shutdown("the tray's Exit");
        _hotkeys.Pressed += OnHotkey;
        _trayTimer.Tick += (_, _) =>
        {
            // A settings window that closed while a hotkey box had focus never said typing ended.
            if (_hotkeys.Suspended && !ForegroundApp.IsKairos())
                ResumeHotkeys("the settings window is not in front");
            if (DateTime.UtcNow - _health.At >= HealthEvery)
                LogHealth();
            _pipeline?.ReleaseIdleMeters();
            var now = DateTime.UtcNow;
            var frames = _pipeline is { BufferActive: true } running ? running.FrameCounts() : default;
            // The app in front, looked at once a tick (a browser or a player is no game): a full-screen game names clips
            // saved from elsewhere; any game, windowed too, keeps the replay from a lighter step's rebuild.
            var (front, frontExe) = GameWatcher.FrontApp();
            if (front != null && GameWatcher.IsGame(front.Exe))
                _lastGame = (front.Pid, front.Exe, now);
            if (frontExe != null && GameWatcher.IsGame(frontExe))
                _lastPlayed = now;
            StepDownWhenSkipping(now, frames);
            RelieveWhenBehind(now, frames, GameInReplay(now));
            if (_saveWatch.Tick(now, _lag.Behind) is { } never)
                NotSaved(never);
            WatchUnenteredGame();
            if (UpdateRules.ShouldCheck(_settings.General.CheckUpdates && _updates.Available, _bufferActiveSince, _updates.LastCheck, DateTime.UtcNow))
                CheckForUpdate();
            RemindUpdate();
            TryApplyUpdate();
            if (_stopped)
                return;
            RefreshTray();
        };
        _tray.UpdateClicked += () => _ = DownloadUpdate();
        SayWhenUpdated(hadSettings: !firstRun);
        _trayTimer.Start();

        ObsLog.Info(LogLines.Settings(_settings, FolderKind));
        LogHealth(quiet: true); // the CPU's starting point for the first health line
        Resilience.RegisterRestart("--background");

        // Right after sign-in, let Windows finish booting before libobs and the GPU encoder start.
        if (background && Environment.TickCount64 < 120_000)
        {
            RefreshTray();
            var delay = new System.Windows.Forms.Timer { Interval = 15_000 };
            delay.Tick += (_, _) => { delay.Dispose(); StartEngine(); };
            delay.Start();
        }
        else
        {
            StartEngine();
        }

        _pipe = new PipeServer(HandleCommand,
            () =>
            {
                // On the engine's own thread: the meters are made and freed there, beside the sources they measure.
                try
                {
                    return (IReadOnlyList<(string, double)>)_ui.Invoke(() => _pipeline is { } p ? p.TakeLevels() : []);
                }
                catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
                {
                    return [];
                }
            },
            onRequest: (request, client) =>
            {
                if (request == "exit")
                    ObsLog.Info($"exit asked over the pipe by {ProcessName(client)}");
            });
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += (_, _) => _ui.Invoke(() => Shutdown("Windows ending the session"));

        if (openSettings)
            OpenSettings();
    }

    // ---------------------------------------------------------------- pipeline

    void StartEngine()
    {
        _starting = false;
        try
        {
            ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, Path.Combine(SettingsStore.AppDataDir, "logs"),
                new VideoConfig(1920, 1080, 1920, 1080, 60), _settings.Video.Codec);
            ApplySettings();
            _ui.BeginInvoke(ObsHost.LoadLater); // the slow encoder plugin, now that the replay buffer is up
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    /// <summary>Applies everything; the pipeline (and with it the replay buffer) is rebuilt only when <paramref name="rebuild"/>.</summary>
    void ApplySettings(bool rebuild = true)
    {
        Text.Language = _settings.General.Language;
        _voice.Configure(_settings.Voice.Enabled, _settings.Voice.Sensitivity);
        _hotkeys.UnregisterAll();
        _keysInUse.Clear();
        _newlyTaken.Clear();
        Register("save", _settings.Hotkeys.Save);
        if (_settings.Replay.QuickEnabled)
            Register("quick", _settings.Hotkeys.Quick);
        Register("toggle", _settings.Hotkeys.ToggleBuffer);
        Register("record", _settings.Hotkeys.Record);
        Register("pauseRecord", _settings.Hotkeys.PauseRecord ?? "");
        Register("screenshot", _settings.Hotkeys.Screenshot);
        LogHotkeys();
        // Once per key, ever: one small message that goes away by itself (the pages carry no notes).
        if (_newlyTaken.Count > 0)
        {
            Warn(TakenToast(_newlyTaken));
        }

        // A test run (its own data folder) leaves the installed Kairos's start-up entry and language alone.
        if (!SettingsStore.OwnDataFolder)
        {
            Logged("autostart", () => Autostart.Sync(_settings.General.StartWithWindows, InstallPaths.LaunchExe));
            Logged("wizard language", () => WizardLanguage.Remember(_settings.General.Language));
        }

        if (!rebuild)
        {
            _pipeline?.ApplyLive(LiveAudio.Of(_settings.Audio));
            _pipeline?.ApplyCamera(_settings.Camera);
            if (_pipeline != null)
                _pipeline.FreshAfterSave = _settings.Replay.FreshAfterSave;
            UpdateBufferState(); // instant replay may have been turned on or off
            RefreshTray();
            return;
        }

        _watcher?.Dispose();
        _watcher = null;
        if (Engine.SceneRules.PausesWithoutGame(_settings))
        {
            _watcher = new GameWatcher();
            _watcher.Changed += _ => UpdateBufferState();
        }

        RebuildPipeline();
    }

    /// <summary>Runs a step whose failure is only worth a line in the log.</summary>
    static void Logged(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            ObsLog.Warn($"{what}: {e.Message}");
        }
    }

    void Register(string id, string hotkey)
    {
        if (!HotkeyParser.TryParse(hotkey, out var h))
            return;
        if (!_keysInUse.Add(h))
        {
            ObsLog.Warn($"hotkey {id} ({hotkey}) is already another Kairos action's");
            return;
        }
        if (_hotkeys.Register(id, h))
            return;
        ObsLog.Warn($"hotkey {id} ({hotkey}) is taken by another app");
        if (_takenNotice.ShouldTell(hotkey))
            _newlyTaken.Add(hotkey);
    }

    /// <summary>A recording that could not start: what to try, in the user's language (OBS's own error goes to the log).</summary>
    public static (string Title, string Detail) RecordFailedToast() => (Text.Get("toast.recordFailed"), Text.Get("fix.closeRecorders"));

    /// <summary>
    /// A game Kairos cannot capture from inside is recorded from the screen, which a game in exclusive full screen leaves
    /// black: the tip to set its display mode to Borderless (nothing else changes), or to run Kairos as administrator (then
    /// Windows lets no clip be dragged out of the Library into a normal app).
    /// </summary>
    public static (string Title, string Detail) BorderlessToast(string game) =>
        // Right to left, the name's spaces kept from breaking: GDI+ widened the one in "Wuthering Waves" (left to right,
        // the same widened the space before it).
        (Text.Get("toast.borderless"), Text.Format("toast.borderlessDetail", Text.IsRightToLeft ? Text.Ltr(game.Replace(' ', ' ')) : game));

    /// <summary>The message about keys another app holds: one key and what to do, or how many and which.</summary>
    public static (string Title, string Detail) TakenToast(IReadOnlyList<string> keys) => keys.Count == 1
        ? (Text.Format("toast.keyTaken", keys[0]), Text.Get("toast.keyTakenFix"))
        : (Text.Format("toast.keysTaken", Text.Count("count.hotkeys", keys.Count)), string.Join(Text.Get("list.separator"), keys));

    bool _builtOnce;

    void RebuildPipeline()
    {
        SetUpMixers();
        _stepPending = false; // any rebuild takes Automatic's step (and the lighter recording, kept in _hardwareState)
        _lag.Reset();
        if (_saveWatch.Pending)
        {
            _saveWatch.Cancel(); // the clip being written goes with the old pipeline: not said later as "could not write"
            ObsLog.Warn("save: the clip being written was dropped by the rebuild");
        }
        _replayLogged = null; // the new pipeline's replay is said again
        if (_builtOnce)
            ObsHost.LoadLater(); // every encoder plugin is in before a pick after the first
        _builtOnce = true;
        _sceneTimer?.Dispose();
        _sceneTimer = null;
        _audioWatcher?.Dispose();
        _audioWatcher = null;
        _pipeline?.Dispose();
        _pipeline = null;
        _error = null;
        CapturePipeline? p = null;
        try
        {
            _hardware = HardwareProfile.Read(_settings.Capture.MonitorId);
            if (_hardware.Fingerprint != _hardwareState.Fingerprint)
            {
                _hardwareState = HardwareState.Load(HardwarePath, _hardware.Fingerprint);
                bool stepped = _hardwareState.StepUpIfQuiet(DateTime.UtcNow);
                if (stepped)
                    ObsLog.Info($"automatic: a week without skipped frames: one step back (step {_hardwareState.AutoStep})");
                bool eased = _hardwareState.EaseIfQuiet(DateTime.UtcNow);
                if (eased)
                    ObsLog.Info($"lighter recording: a week without falling behind: one step back (relief {_hardwareState.Relief})");
                if (stepped || eased)
                    _hardwareState.Save(HardwarePath);
                _encoders = new EncoderFallback(_hardwareState.Failed, _hardwareState.NoBFrames);
                ObsLog.Info($"hardware: {_hardware.CaptureGpu} | {_hardware.Cpu} ({_hardware.CpuThreads} threads) | {_hardware.ScreenWidth}x{_hardware.ScreenHeight}@{_hardware.RefreshHz} " +
                            $"| known to fail: {string.Join(", ", _hardwareState.Failed)}");
            }
            string? noted = _hardwareState.NvidiaOutdatedDriver;
            _nvidiaNote = ObsLog.NvencOutdated ? _hardwareState.NvidiaNote(_hardware.NvidiaDriver) : null;
            if (_hardwareState.NvidiaOutdatedDriver != noted)
                _hardwareState.Save(HardwarePath); // kept, so a driver update without a restart says "restart the PC"
            p = new CapturePipeline(_settings, voice: _voice, encoders: _encoders, screenWgc: _hardwareState.ScreenWgc)
            {
                DiskOff = _diskOff,
                Hardware = _hardware,
                AutoStep = _hardwareState.AutoStep,
                Relief = _hardwareState.Relief,
            };
            _skipping.Reset();
            p.ReplaySaved += (path, quick, names) => _ui.BeginInvoke(() => OnClipSaved(path, names, quick));
            p.CannotEnterGame += (exe, title) => _unentered = (exe, title); // on the scene timer's tick: the UI thread
            p.CameraSaved += path => _ui.BeginInvoke(() => OnCameraSaved(path));
            p.SaveFailed += () => _ui.BeginInvoke(() =>
            {
                if (_saveWatch.Saved(0, -1) is { } reason) // as a clip that came out empty: "not saved", now
                    NotSaved(reason);
            });
            p.DiskFailed += () => _ui.BeginInvoke(() =>
            {
                if (_diskOff || _stopped)
                    return;
                _diskOff = true; // a write failed: the replay goes on in RAM, never stopped
                ObsLog.Warn("replay on the disk: a write failed: the replay is rebuilt in RAM");
                RebuildPipeline();
            });
            p.RecordingStopped += (path, seconds, names) => _ui.BeginInvoke(() => OnRecordingStopped(path, seconds, names));
            p.Build();
            _pipeline = p;
            if (!_hardwareState.ScreenWgc && !_screenSeen && p.ShowsScreen)
                _ = CheckScreenPicture(p);
            // Screen and One app: the app in front, each second (a game filling the screen is captured from the game). Games:
            // whether the game is hooked, each second (the screen under it goes off), and the process list every 2 s when
            // Riot's games are left to the screen (one snapshot, no process opened).
            bool guard = _settings.Capture.ScreenForRiotGames, odd = false;
            EventHandler? look = p.BuiltMode switch
            {
                // Games with no screen under the game (no fallback, no guard): nothing to look at, no timer.
                CaptureMode.Game when guard || _settings.Capture.GameFallbackToScreen => (_, _) =>
                {
                    if (guard && (odd = !odd))
                        _pipeline?.SetGuard(AntiCheatGuard.AnyProtectedRunning());
                    _pipeline?.WatchGame(GameWatcher.FullscreenForeground());
                },
                CaptureMode.Game => null,
                CaptureMode.Window => (_, _) =>
                {
                    var (fullscreen, exe) = GameWatcher.FrontApp();
                    _pipeline?.WatchWindow(fullscreen, exe);
                },
                _ => (_, _) => _pipeline?.WatchScreenGame(GameWatcher.FullscreenForeground()),
            };
            if (look != null)
            {
                _sceneTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _sceneTimer.Tick += look;
                _sceneTimer.Start();
            }
            if (_settings.Audio.Layout == AudioLayout.Separate)
            {
                _audioWatcher = new AudioWatcher(() => _settings, (assignment, heardOn) => _pipeline?.UpdateAudioTargets(assignment, heardOn), () => ChosenWindow);
                _audioWatcher.Start();
            }
            _lastClip ??= NewestClip(p.ClipsFolder);
            _lastShot ??= ClipLibrary.NewestPicture(p.ClipsFolder);
            _retried = false;
            UpdateBufferState();
        }
        catch (Exception e)
        {
            if (_pipeline == null)
                p?.Dispose(); // a half-built pipeline: its sources and encoders go, not left for the next try
            Fail(e);
        }
        RefreshTray();
    }

    void Fail(Exception e)
    {
        _error = e.Message;
        ObsLog.Error(e.ToString());
        RefreshTray();
        if (_retried || !ObsHost.Started)
            return;
        _retried = true;
        var retry = new System.Windows.Forms.Timer { Interval = 3000 };
        retry.Tick += (_, _) => { retry.Dispose(); RebuildPipeline(); };
        retry.Start();
    }

    /// <summary>A step down waits for the replay to stop (a rebuild now would throw away what it holds).</summary>
    bool _stepPending;

    /// <summary>
    /// Automatic, each 5 s: when over 2 % of the frames of the last minute were lagged or skipped, one step lighter
    /// (quality, then frame rate, then size; three at most). It takes effect when the replay next stops (the game ends,
    /// a pause) or the settings are saved: never by throwing away the replay or during a recording.
    /// </summary>
    void StepDownWhenSkipping(DateTime now, FrameCount frames)
    {
        if (!_settings.Video.Auto || _pipeline is not { BufferActive: true, Recording: false } p || _hardwareState.AutoStep >= 3 || _stepPending
            || _lag.Behind || p.Relief < _hardwareState.Relief) // falling behind: the lighter recording acts first (RelieveWhenBehind)
        {
            _skipping.Reset();
            return;
        }
        if (_skipping.Add(now, frames) is not { } bad)
            return;
        _hardwareState.StepDown(now);
        _hardwareState.Save(HardwarePath);
        _stepPending = true;
        _skipping.Reset();
        ObsLog.Warn($"automatic: {bad:P1} of the frames skipped over a minute: one step lighter (step {_hardwareState.AutoStep}) when the replay stops");
    }

    /// <summary>Whether recording keeps up (<see cref="LagGuard"/>), sampled on the engine's tick.</summary>
    readonly LagGuard _lag = new();

    /// <summary>Kairos's share of the graphics card's memory, with the health line and when recording falls behind.</summary>
    static void LogGpuMemory()
    {
        if (GpuMemory.Read() is { } memory)
            ObsLog.Info(LogLines.GpuMemory(memory.Used, memory.Budget, memory.InSystem));
    }

    /// <summary>"Already as light as it goes" is logged once a run.</summary>
    bool _saidBehind;

    /// <summary>How long recording stays behind (<see cref="LagGuard.BehindFor"/>) before it is made lighter for the week.</summary>
    static readonly TimeSpan SustainedBehind = TimeSpan.FromMinutes(1);

    /// <summary>A clip was saved since the last lighter step: building the pipeline again throws away nothing the player wants.</summary>
    bool _savedSinceStep;

    /// <summary>When a game (windowed too) was last in front, seen on the tray's tick.</summary>
    DateTime _lastPlayed = DateTime.MinValue;

    /// <summary>
    /// The replay may hold a game the player still wants: one was in front within the replay's length (an alt-tab to
    /// Kairos to press Save is no end of play), or One app mode records its app whatever is in front.
    /// </summary>
    bool GameInReplay(DateTime now) =>
        _settings.Capture.Mode == CaptureMode.Window
        || now - _lastPlayed <= TimeSpan.FromSeconds(Math.Max(_settings.Replay.Seconds, _settings.Replay.QuickEnabled ? _settings.Replay.QuickSeconds : 0));

    /// <summary>
    /// Whether building <paramref name="p"/> again throws away nothing the player may still save (<see cref="LagRelief.RebuildNow"/>):
    /// a game's picture is captured, or the replay holds a game played (<see cref="GameInReplay"/>).
    /// </summary>
    bool RebuildNow(CapturePipeline p, bool gameInReplay) =>
        LagRelief.RebuildNow(p.Recording, _saveWatch.Pending, gameInReplay || p.GameGivesPicture, _savedSinceStep);

    /// <summary>
    /// Each 5 s, for everyone (the user's own settings or Automatic): when over 10 % of the frames lagged or were skipped
    /// in each 20 s for a minute, a game holds the graphics card and the clips stutter and come late, so recording gets
    /// one step lighter (<see cref="LagRelief"/>), kept for this PC and said once. The REALTIME step is set as the recording
    /// runs; any other builds the pipeline again, which starts the replay empty, so with a game in front it waits for the
    /// next saved clip (<see cref="LagRelief.RebuildNow"/>), and never comes during a recording or while a clip is written.
    /// </summary>
    void RelieveWhenBehind(DateTime now, FrameCount frames, bool gameInReplay)
    {
        if (_pipeline is not { BufferActive: true } p)
        {
            _lag.Reset();
            return;
        }
        if (p.Relief < _hardwareState.Relief)
        {
            // Lighter already: built again once that throws away nothing the player may still save; still watched
            // meanwhile (a slow save says whether the recording is behind now).
            _lag.Add(now, frames);
            if (RebuildNow(p, gameInReplay))
                RebuildPipeline();
            return;
        }
        if (_lag.Add(now, frames) is not { } share || !_lag.BehindFor(now, SustainedBehind))
            return;
        if (p.Lightest)
        {
            if (!_saidBehind)
            {
                ObsLog.Warn($"recording falls behind: {share:P0} of the frames lagged or skipped for a minute, already as light as it goes");
                LogGpuMemory();
            }
            _saidBehind = true;
            return;
        }
        _hardwareState.Relieve(now, p.NextRelief);
        _hardwareState.Save(HardwarePath);
        _savedSinceStep = false;
        bool live = p.StepLive(_hardwareState.Relief);
        bool rebuild = !live && RebuildNow(p, gameInReplay);
        ObsLog.Warn($"recording falls behind: {share:P0} of the frames lagged or skipped for a minute: lighter (relief {_hardwareState.Relief})"
                    + (live ? ", taken as it runs: the replay is kept" : rebuild ? "" : ", built once a clip is saved or the game is left: the replay is kept"));
        LogGpuMemory();
        Warn((Text.Get("toast.relief"), Text.Get(LagRelief.TextKey(_hardwareState.Relief))));
        if (live)
            _lag.Reset(); // a minute behind again before the next step
        else if (rebuild)
            RebuildPipeline();
    }

    bool WantBuffer => _settings.Replay.Enabled && !_pausedByUser && (_watcher == null || _watcher.GameActive);

    void UpdateBufferState()
    {
        if (_pipeline == null)
            return;
        bool active = _pipeline.SetBufferActive(WantBuffer);
        if (active)
            _bufferActiveSince ??= DateTime.UtcNow;
        if (active != _replayLogged)
        {
            _replayLogged = active;
            ObsLog.Info(active ? "replay: running" : $"replay: stopped ({ReplayOffReason()})");
        }
        if (active && !_readyLogged)
        {
            _readyLogged = true;
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            ObsLog.Info($"replay buffer active {(DateTime.Now - self.StartTime).TotalMilliseconds:F0} ms after the process started");
        }
        // The replay's own error (a recording that could not start is said by its toast): cleared once it starts.
        _error = _pipeline.BufferError;
        RefreshTray();
        if (active && _pipeline.Choice is { } working && _hardwareState.NoteWorked(working.Id))
            _hardwareState.Save(HardwarePath); // it failed once before: that passed
        if (!active && _pipeline.BufferError != null)
            TryNextEncoder("the replay buffer");
        else if (!active && _stepPending && !_pipeline.Recording)
            _ui.BeginInvoke(RebuildPipeline); // Automatic's step down, now that the replay holds nothing
    }

    /// <summary>
    /// An output that could not start: its encoder is left out and the pipeline is built again with the next one (an
    /// AMD driver without OpenCL fails every AMF encoder; x264 runs anywhere). Once per encoder, so a failure that is
    /// not the encoder's ends with x264's own message.
    /// </summary>
    void TryNextEncoder(string what)
    {
        if (_pipeline?.Choice is not { } choice
            || !_encoders.Failed(choice.Id, withBFrames: choice.Vendor == EncoderVendor.Nvidia && !choice.NoBFrames && _pipeline.Plan?.BFrames > 0))
            return;
        ObsLog.Warn($"{what} could not start with {choice.Id}: trying again (excluded: {string.Join(", ", _encoders.Excluded)}; " +
                    $"without B-frames: {string.Join(", ", _encoders.WithoutBFrames)})");
        if (_encoders.Excluded.Contains(choice.Id))
            _hardwareState.NoteFailure(choice.Id); // kept for good only when it fails in another run too
        _hardwareState.Save(HardwarePath);
        _ui.BeginInvoke(RebuildPipeline);
    }

    bool _readyLogged;

    // ------------------------------------------------------------------ the log's own lines (LogLines)

    /// <summary>The replay's state the log last said (null: not said since the pipeline was built).</summary>
    bool? _replayLogged;
    string? _hotkeysLogged;
    int _clipsSaved, _clipsNotSaved, _shotsSaved, _hotkeyPresses;
    static readonly DateTime StartedUtc = DateTime.UtcNow;

    /// <summary>A health line this often (<see cref="LogLines.Health"/>): rare enough to cost nothing.</summary>
    static readonly TimeSpan HealthEvery = TimeSpan.FromMinutes(10);
    (DateTime At, TimeSpan Cpu, EngineStatus? Status) _health;

    /// <summary>The clips folder's place for the log: its cloud's name or its drive, never the path.</summary>
    static string FolderKind(string folder) => CloudFolders.Under(folder, CloudFolders.OnThisPc()) is { } cloud
        ? cloud.Kind.ToString()
        : (Path.GetPathRoot(folder) ?? "?").TrimEnd('\\');

    /// <summary>Why the replay is not running, as the log says it.</summary>
    string ReplayOffReason() =>
        !_settings.Replay.Enabled ? "turned off in the settings"
        : _pausedByUser ? "paused by the user"
        : _watcher is { GameActive: false } ? (_settings.Capture.Mode == CaptureMode.Game ? "waiting for a game: games only, no screen" : "waiting for a game: only while gaming is on")
        : _pipeline?.BufferError is { } error ? "error: " + error
        : "not started";

    /// <summary>A tray action, said in the log first (so a log shows where a save or a pause came from).</summary>
    static Action FromTray(string what, Action action) => () =>
    {
        ObsLog.Info("tray: " + what);
        action();
    };

    /// <summary>The hotkeys and whether Kairos holds each, when that changed since the log last said it.</summary>
    void LogHotkeys()
    {
        var k = _settings.Hotkeys;
        var keys = new[] { ("save", k.Save), ("quick", _settings.Replay.QuickEnabled ? k.Quick : ""), ("toggle", k.ToggleBuffer),
            ("record", k.Record), ("pauseRecord", k.PauseRecord ?? ""), ("screenshot", k.Screenshot) };
        string line = LogLines.Hotkeys(keys.Select(key => (key.Item1, key.Item2, _hotkeys.Status.TryGetValue(key.Item1, out bool held) && held)));
        if (line == _hotkeysLogged)
            return;
        _hotkeysLogged = line;
        ObsLog.Info(line);
    }

    void ResumeHotkeys(string why)
    {
        if (!_hotkeys.Suspended)
            return;
        _hotkeys.Resume();
        ObsLog.Info($"hotkeys: taken again ({why})");
    }

    /// <summary>The health line (<see cref="LogLines.Health"/>); <paramref name="quiet"/>: only the starting point.</summary>
    void LogHealth(bool quiet = false)
    {
        var now = DateTime.UtcNow;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var cpu = self.TotalProcessorTime;
        var status = _pipeline?.GetStatus();
        if (!quiet)
        {
            double cpuPercent = (cpu - _health.Cpu).TotalMilliseconds / Math.Max(1, (now - _health.At).TotalMilliseconds) / Environment.ProcessorCount * 100;
            string drive = "?";
            long freeGb = 0;
            try
            {
                if (Path.GetPathRoot(_settings.Output.Folder) is { Length: > 0 } root)
                {
                    drive = root.TrimEnd('\\');
                    freeGb = new DriveInfo(root).AvailableFreeSpace >> 30;
                }
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                drive = "network";
            }
            ObsLog.Info(LogLines.Health(status, _health.Status, cpuPercent, self.WorkingSet64 >> 20, drive, freeGb));
            LogGpuMemory();
        }
        _health = (now, cpu, status);
    }

    // ---------------------------------------------------------------- actions

    void OnHotkey(string id)
    {
        _hotkeyPresses++;
        ObsLog.Info("hotkey: " + id);
        switch (id)
        {
            case "save": SaveClip(quick: false); break;
            case "quick": SaveClip(quick: true); break;
            case "toggle": TogglePause(); break;
            case "record": ToggleRecording(); break;
            case "pauseRecord": TogglePauseRecording(); break;
            case "screenshot": TakeScreenshot(); break;
        }
    }

    void SaveClip(bool quick, bool byVoice = false)
    {
        bool waiting = _watcher != null && !_watcher.GameActive && !_pausedByUser;
        bool failed = _error != null; // the pipeline could not be built, or its replay could not start
        string offReason = SaveWatch.OffReason(_settings.Replay.Enabled, _pausedByUser, waiting, failed);
        if (_pipeline == null)
        {
            NotSaved(offReason);
            return;
        }
        _pendingByVoice = byVoice;
        _pendingSeconds = quick ? _settings.Replay.QuickSeconds : _pipeline.GetStatus().BufferSeconds;
        _pendingApp = CapturedApp(_pendingSeconds);
        // Where the clip goes: on the disk it is written straight there (no move after), in RAM it is moved there.
        _pendingTarget = ClipOrganizer.TargetPath(_pipeline.ClipsFolder, _pendingApp ?? ClipOrganizer.AppFolderName(null, null, null),
            DateTime.Now, _settings.Output.PerAppFolders);
        var request = _pipeline.RequestSave(quick, _pendingTarget);
        if (request != SaveWatch.Request.Started)
            ObsLog.Info($"save not started: {request} (active={_pipeline.BufferActive})");
        if (_saveWatch.Requested(request, DateTime.UtcNow, _pipeline.BufferBytes, offReason) is { } reason)
            NotSaved(reason);
    }

    /// <summary>The clip's audio tracks named as they were recorded (Mix, Game, Microphone…), in place.</summary>
    /// <summary>The file's sound tracks named; a recording's file still held by its muxer is tried again on a timer.</summary>
    static void NameTracks(string clip, IReadOnlyList<string> names) =>
        Engine.Audio.TrackNaming.Name(clip, names, (delay, retry) =>
        {
            var timer = new System.Windows.Forms.Timer { Interval = (int)delay.TotalMilliseconds };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                retry();
            };
            timer.Start();
        });

    /// <summary>A save that did not happen, said with the reason (no sound: only a saved clip chimes).</summary>
    void NotSaved(string reason)
    {
        _clipsNotSaved++;
        ObsLog.Warn($"clip not saved: {reason}");
        Warn(NotSavedToast(reason));
    }

    /// <summary>"Clip not saved" and the reason; when instant replay could not start, that, and what to try.</summary>
    public static (string Title, string Detail) NotSavedToast(string reason) => reason == "toast.notSaved.failed"
        ? (Text.Get(reason), Text.Get("fix.closeRecorders"))
        : (Text.Get("toast.notSaved"), Text.Get(reason));

    /// <summary>
    /// The app being captured now (its folder name): the chosen window's app while it is recorded, or what is in front;
    /// for a clip of <paramref name="clipSeconds"/>, the game played just before when that is the desktop or Kairos
    /// (<see cref="ClipOrganizer.SaveFolderName"/>).
    /// </summary>
    string? CapturedApp(double? clipSeconds = null)
    {
        if (SceneRules.RecordsChosenWindow(_settings.Capture, _pipeline?.CaptureNotice) && WindowTarget.ExeName(ChosenWindow) is { } exe)
            return ForegroundApp.ForExe(exe);
        if (clipSeconds is not { } seconds || _lastGame is not { } last)
            return ForegroundApp.Current();
        return ClipOrganizer.SaveFolderName(ForegroundApp.Current(), ForegroundApp.IsKairos, () => ForegroundApp.ForProcess(last.Pid, last.Exe),
            DateTime.UtcNow - last.At, seconds);
    }

    /// <summary>The last full-screen app in front (a game, usually) and when, seen on the tray's tick.</summary>
    (uint Pid, string Exe, DateTime At)? _lastGame;

    /// <summary>The camera's own clip and the clip it goes with, each kept (with when it came) until the other one comes.</summary>
    (string Path, DateTime At)? _cameraHalf, _clipHalf;

    /// <summary>The two halves of one save come within a minute of each other (a long buffer takes a while to write).</summary>
    static readonly TimeSpan PairWindow = TimeSpan.FromMinutes(1);

    /// <summary>Apps: the chosen app recorded now (the first one while there is no pipeline).</summary>
    string? ChosenWindow => _pipeline?.CapturedWindow ?? _settings.Capture.Apps.FirstOrDefault();

    void OnCameraSaved(string raw)
    {
        _cameraHalf = (raw, DateTime.UtcNow);
        PlaceCameraIfPaired();
    }

    /// <summary>
    /// The camera's clip beside its clip, named after it ("….camera.mp4"): the Library shows them as one. A half left
    /// over from a save whose other half failed never pairs with a later one: the newer half waits on.
    /// </summary>
    void PlaceCameraIfPaired()
    {
        if (_cameraHalf is not { } camera || _clipHalf is not { } clip)
            return;
        if ((camera.At - clip.At).Duration() > PairWindow)
        {
            ObsLog.Warn($"camera clip left unpaired: {(camera.At < clip.At ? camera.Path : clip.Path)}");
            if (camera.At < clip.At)
                _cameraHalf = null;
            else
                _clipHalf = null;
            return;
        }
        _cameraHalf = _clipHalf = null;
        string target = ClipLibrary.CameraOf(clip.Path);
        try
        {
            HeldFile.Retry(() => File.Move(camera.Path, target, overwrite: false)); // OBS's file, just written
            CloudFolders.Saved(target, _settings.Output.FreeLocalCopies, shown: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn($"camera clip not placed: {e.Message}");
        }
    }

    void OnClipSaved(string rawPath, IReadOnlyList<string> names, bool quick)
    {
        string app = _pendingApp ?? ClipOrganizer.AppFolderName(null, null, null);
        string final = rawPath;
        try
        {
            if (!ClipOrganizer.IsAt(rawPath, _pendingTarget)) // written there already (the replay on the disk)
            {
                string target = ClipOrganizer.TargetPath(_pipeline?.ClipsFolder ?? Path.GetDirectoryName(rawPath)!, app,
                    DateTime.Now, _settings.Output.PerAppFolders);
                final = ClipOrganizer.Move(rawPath, target);
            }
        }
        catch (Exception e)
        {
            ObsLog.Warn($"could not move clip: {e.Message}");
        }
        NameTracks(final, names);
        if (!quick && _pipeline?.HasCameraFile == true)
        {
            _clipHalf = (final, DateTime.UtcNow);
            PlaceCameraIfPaired();
        }
        _lastClip = final;
        _lastSaved = final;
        _undoClip = final;
        Index(final);
        long free = SaveWatch.FreeBytes(final);
        long bytes = File.Exists(final) ? new FileInfo(final).Length : 0;
        if (_saveWatch.Saved(bytes, free) is { } problem)
        {
            NotSaved(problem);
            return;
        }
        _clipsSaved++;
        _savedSinceStep = true; // a lighter step waiting for it is taken on the next tick
        ObsLog.Info($"clip saved{(_pendingByVoice ? " by voice" : "")}: {final}");

        _tray.FlashSaved();
        if (_settings.General.SoundOnSave)
            PlayChime();
        if (_settings.General.ToastOnSave)
            _toast.Show(Text.Get("toast.saved"),
                $"{app} • {Text.Duration(_pendingSeconds)}" + (_pendingByVoice ? " • " + Text.Get("toast.byVoice") : ""), Text.IsRightToLeft);
        RefreshTray();
        EnforceFolderLimit();
        if (_saveWatch.ShouldWarnLowDisk(free, DateTime.UtcNow))
            WarnLowDisk(final, free);
    }

    /// <summary>A saved clip or screenshot joins the list of files Kairos made (the folder limit only removes those).</summary>
    void Index(string path)
    {
        try
        {
            _clipIndex.Add(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn($"clip index: {e.Message}");
        }
        CloudFolders.Saved(path, _settings.Output.FreeLocalCopies);
    }

    /// <summary>"Low disk space", a moment after the saved card (so both are read).</summary>
    void WarnLowDisk(string clip, long free)
    {
        string drive = (Path.GetPathRoot(clip) ?? "").TrimEnd('\\');
        ObsLog.Warn($"low disk space: {free / (1024 * 1024)} MB left on {drive}");
        var later = new System.Windows.Forms.Timer { Interval = 3500 };
        later.Tick += (_, _) =>
        {
            later.Dispose();
            _toast.Show(Text.Get("toast.lowDisk"),
                Text.Format("toast.lowDiskBody", Text.Ltr(Ui.SettingsViewModel.FormatSize(free)), Text.Ltr(drive)), Text.IsRightToLeft, ToastIcon.Warning);
        };
        later.Start();
    }

    void EnforceFolderLimit()
    {
        if (_settings.Output.MaxFolderGb > 0 && _pipeline != null)
        {
            string folder = _pipeline.ClipsFolder;
            int limit = _settings.Output.MaxFolderGb;
            Task.Run(() => StorageJanitor.Enforce(folder, limit, _clipIndex, CloudFolders.Under(folder, CloudFolders.OnThisPc())));
        }
    }

    /// <summary>
    /// Saves a picture of what is being captured where a clip would go. The GPU copy and the PNG happen off the UI
    /// thread; a press while one is under way is ignored.
    /// </summary>
    void TakeScreenshot(bool byVoice = false)
    {
        var pipeline = _pipeline;
        if (pipeline == null)
        {
            _toast.Show(Text.Get("toast.shotFailed"), "", Text.IsRightToLeft);
            return;
        }
        if (ScreenshotGrabber.Busy)
            return;
        string app = CapturedApp() ?? ClipOrganizer.AppFolderName(null, null, null);
        string target = ClipOrganizer.TargetPath(pipeline.ClipsFolder, app, DateTime.Now, _settings.Output.PerAppFolders, ".png");
        var (w, h) = pipeline.Base;
        Task.Run(async () =>
        {
            string? path = null;
            try
            {
                if (await ScreenshotGrabber.Grab(w, h, TimeSpan.FromSeconds(2)) is { } frame)
                    path = ScreenshotWriter.Write(target, frame);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                ObsLog.Warn($"screenshot not saved: {e.Message}");
            }
            _ui.BeginInvoke(() => OnShotTaken(path, app, byVoice));
        });
    }

    void OnShotTaken(string? path, string app, bool byVoice)
    {
        if (path == null)
        {
            _toast.Show(Text.Get("toast.shotFailed"), "", Text.IsRightToLeft);
            return;
        }
        Index(path);
        _shotsSaved++;
        ObsLog.Info($"screenshot saved{(byVoice ? " by voice" : "")}: {path}");
        _lastShot = path;
        _lastSaved = path;
        _undoClip = path;
        _tray.FlashSaved();
        if (_settings.General.SoundOnSave)
            PlayMuted(ShotSound.Play, 400);
        if (_settings.General.ToastOnSave)
            _toast.Show(Text.Get("toast.shot"), app + (byVoice ? " • " + Text.Get("toast.byVoice") : ""), Text.IsRightToLeft);
        RefreshTray();
        EnforceFolderLimit();
    }

    /// <summary>Undo for an accidental save: sends the clip saved last in this session to the Recycle Bin.</summary>
    void DeleteLastClip()
    {
        string? clip = _undoClip;
        _undoClip = null;
        if (clip != null && File.Exists(clip))
        {
            try
            {
                foreach (string file in ClipLibrary.FilesOf(clip).ToList()) // its camera file goes with it
                    ClipDeleter.Recycle(file);
                ObsLog.Info($"deleted from the tray: {clip}");
                if (_lastClip == clip)
                    _lastClip = _pipeline != null ? NewestClip(_pipeline.ClipsFolder) : null;
                if (_lastShot == clip)
                    _lastShot = null;
                if (_lastSaved == clip)
                    _lastSaved = null;
            }
            catch (Exception e)
            {
                ObsLog.Warn($"could not delete clip: {e.Message}");
            }
        }
        RefreshTray();
    }

    /// <summary>The newest video in the clips folder, as the Clips page lists them (no hidden folders or caches).</summary>
    static string? NewestClip(string folder) => ClipLibrary.NewestVideo(folder);

    /// <summary>Plays the save chime with system capture muted for a moment, so the chime stays out of future clips.</summary>
    void PlayChime() => PlayMuted(Chime.Play, 650);

    /// <summary>Plays a feedback sound with the captured system audio muted meanwhile, so it never lands in a clip.</summary>
    void PlayMuted(Action play, int milliseconds)
    {
        var pipeline = _pipeline;
        pipeline?.SetSystemMuted(true);
        play();
        var unmute = new System.Windows.Forms.Timer { Interval = milliseconds };
        unmute.Tick += (_, _) =>
        {
            unmute.Dispose();
            if (ReferenceEquals(pipeline, _pipeline))
                pipeline?.SetSystemMuted(false);
        };
        unmute.Start();
    }

    void TogglePause()
    {
        _pausedByUser = !_pausedByUser;
        UpdateBufferState();
    }

    void ToggleRecording()
    {
        if (_pipeline is not { } p)
            return;
        bool was = p.Recording;
        string? target = null;
        if (!was)
        {
            // Like a clip: in the folder of what is being recorded (Desktop, or the game), named with the time it starts.
            _recordingApp = CapturedApp() ?? ClipOrganizer.AppFolderName(null, null, null);
            target = ClipOrganizer.FreePath(ClipOrganizer.TargetPath(p.ClipsFolder, _recordingApp, DateTime.Now, _settings.Output.PerAppFolders));
        }
        bool now = p.ToggleRecording(target);
        if (!was && now)
            Notify(Text.Get("toast.recordStarted"),
                _settings.Hotkeys.Record.Length > 0 ? Text.Format("toast.recordHint", _settings.Hotkeys.Record) : "");
        else if (!was)
        {
            Warn(RecordFailedToast());
            if (!p.BufferActive) // the buffer runs on the same encoder: the encoder works, the recording's file does not
                TryNextEncoder("a recording");
        }
        RefreshTray();
    }

    void TogglePauseRecording()
    {
        if (_pipeline is not { } p || !p.TogglePauseRecording())
            return;
        var st = p.GetStatus();
        Notify(Text.Get(st.RecordingPaused ? "toast.recordPaused" : "toast.recordResumed"),
            Ui.StatusLogic.RecordingDetail(st.RecordingSeconds, st.RecordingBytes));
        RefreshTray();
    }

    /// <summary>The recording file is closed: it becomes the last clip (to play or share), with a note of its length and size.</summary>
    void OnRecordingStopped(string path, double seconds, IReadOnlyList<string> names)
    {
        // An update pressed during the recording goes ahead now (after this clip's toast and last-clip note).
        _ui.BeginInvoke(TryApplyUpdate);
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (bytes > 0)
        {
            NameTracks(path, names);
            _lastClip = path;
            CloudFolders.Saved(path, _settings.Output.FreeLocalCopies);
            CloudFolders.Saved(ClipLibrary.CameraOf(path), _settings.Output.FreeLocalCopies, shown: false); // its camera file, if any
            ObsLog.Info($"recording saved: {path} ({seconds:F1} s)");
            Notify(Text.Get("toast.recordSaved"), $"{_recordingApp} • " + Ui.StatusLogic.RecordingDetail(seconds, bytes));
        }
        RefreshTray();
    }

    /// <summary>A problem: the amber card, shown even with the save notes turned off.</summary>
    void Warn((string Title, string Detail) toast) => _toast.Show(toast.Title, toast.Detail, Text.IsRightToLeft, ToastIcon.Warning);

    /// <summary>The last game in front that Kairos could not open (<see cref="CapturePipeline.CannotEnterGame"/>).</summary>
    (string Exe, string Title)? _unentered;

    /// <summary>
    /// On the tray's tick: a game Kairos cannot open, seen in exclusive full screen (the screen copy can come out black;
    /// in Borderless it records as it is, so no tip), gets the Borderless tip at once, over the game, so no clip is lost
    /// before the player knows (<see cref="BorderlessTips"/>).
    /// </summary>
    void WatchUnenteredGame()
    {
        if (_unentered is { } game && SaveToast.FullScreenGame
            && string.Equals(GameWatcher.FullscreenForeground()?.Exe, game.Exe, StringComparison.OrdinalIgnoreCase))
            _tips.Seen(game.Exe, game.Title);
        if (_tips.Pending)
            ShowTip();
    }

    /// <summary>
    /// The Borderless tip waiting, shown even with the save notes turned off and over the game: the calm card and its
    /// chime (with the save sound on). Never over presentation mode.
    /// </summary>
    void ShowTip()
    {
        // Asked first: the card's own "not shown" line would be logged at every tick of a presentation.
        if ((SaveToast.FullScreenBusy && !SaveToast.FullScreenGame) || !_tips.TryShow(game =>
            {
                var (title, detail) = BorderlessToast(game);
                return _toast.Show(title, detail, Text.IsRightToLeft, ToastIcon.Tip, ToastTiming.Calm, overGame: true);
            }))
            return;
        // Not with all the sound recorded together: the chime would be in the next clip (separate sources leave Kairos out).
        if (_settings.General.SoundOnSave && !(_settings.Audio.Layout == AudioLayout.Together && _settings.Audio.System))
            Chime.PlayTip();
        // Told once a game, across starts: only the list goes onto the file (this copy can be older than it).
        var told = _settings.Capture.BorderlessTips.ToList();
        SettingsStore.Update(SettingsStore.DefaultPath, s => s.Capture.BorderlessTips = [.. s.Capture.BorderlessTips.Union(told, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>The small card (never in captures); no sound, which a recording would pick up.</summary>
    void Notify(string title, string detail)
    {
        if (_settings.General.ToastOnSave)
            _toast.Show(title, detail, Text.IsRightToLeft);
    }

    void OpenClipsFolder() => ShellPaths.OpenFolder(_pipeline?.ClipsFolder ?? _settings.Output.Folder);

    /// <summary>What the tray shares: the newest clip or screenshot saved in this session, else the newest clip.</summary>
    string? LastSaved => new[] { _lastSaved, _lastClip }.FirstOrDefault(p => p != null && File.Exists(p));

    /// <summary>Opens the settings window on the share panel for the newest clip.</summary>
    void ShareLastClip()
    {
        string? last = LastSaved;
        if (last == null)
            return;
        ShareRequest.Write(ShareRequest.DefaultPath, last, DateTime.UtcNow);
        OpenSettings();
    }

    void OpenSettings()
    {
        Process.Start(new ProcessStartInfo(ExePath, "--settings") { UseShellExecute = false });
    }

    /// <summary>Downloads the update found (the settings window's Update), then restarts into it when nothing is under way.</summary>
    async Task DownloadUpdate()
    {
        await Task.Run(_updates.Download).ConfigureAwait(true);
        TryApplyUpdate();
    }

    /// <summary>Looks for an update off this thread (Velopack's first steps would hold up hotkeys and the tray).</summary>
    void CheckForUpdate()
    {
        var now = DateTime.UtcNow;
        string language = Text.Language;
        _ = Task.Run(() => _updates.Check(now, language));
    }

    /// <summary>
    /// A newer Kairos is told (a usual one once, an important one each day until it is in) when no full-screen app (a
    /// game) is in front, so it never covers one; otherwise asked again on the next tick.
    /// </summary>
    void RemindUpdate()
    {
        var now = DateTime.UtcNow;
        if (!_updates.RemindDue(now) || _updates.Status is not { } status || Gaming())
            return;
        string version = AppInfo.Display(status.Version);
        bool shown = status.Important
            ? _toast.Show(Text.Format("toast.updateImportant", version), Text.Get("toast.updateImportantDetail"), Text.IsRightToLeft, ToastIcon.Update)
            : _toast.Show(Text.Format("toast.update", version), Text.Get("toast.updateDetail"), Text.IsRightToLeft, ToastIcon.Update);
        if (shown)
            _updates.Reminded(now);
    }

    /// <summary>
    /// A few seconds after a build, the screen's picture: black with Automatic (some PCs with two GPUs copy black, without
    /// an error) tries Windows Graphics Capture, kept for this PC when it shows the screen; set back when it is black too
    /// (a screen switched off). Read off the canvas small (<see cref="ScreenCheck"/>), never over a screenshot the user takes.
    /// </summary>
    async Task CheckScreenPicture(CapturePipeline p)
    {
        bool Gone() => p != _pipeline || _stopped;
        try
        {
            await Task.Delay(6000);
            if (Gone())
                return;
            // While a game gives the picture the screen is not even copied under it: the canvas tells nothing about it.
            while (p.GameGivesPicture)
            {
                await Task.Delay(5000);
                if (Gone())
                    return;
            }
            if (!await ScreenIsBlack(p, "DXGI"))
            {
                _screenSeen = true; // this session's later builds need no look
                return;
            }
            ObsLog.Warn("screen capture: the picture is black with Automatic (DXGI): trying Windows Graphics Capture");
            p.UseWgcForScreen(true);
            await Task.Delay(2500);
            if (Gone())
                return;
            bool keep = !await ScreenIsBlack(p, "Windows Graphics Capture");
            if (Gone())
                return;
            if (keep)
            {
                ObsLog.Info("screen capture: Windows Graphics Capture shows the screen: kept for this PC");
                _hardwareState.ScreenWgc = true;
                _hardwareState.Save(HardwarePath);
            }
            else
            {
                ObsLog.Info("screen capture: black either way (the screen off or asleep?): Automatic kept");
                p.UseWgcForScreen(false);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ObsLog.Warn("screen check: " + e.Message);
        }
    }

    /// <summary>
    /// Three looks a little apart, all black (a look that cannot be taken counts as a picture: nothing changes). The log
    /// says each look's lit share, so a check that went wrong (a screen dark for a moment) shows.
    /// </summary>
    static async Task<bool> ScreenIsBlack(CapturePipeline p, string method)
    {
        var (w, h) = p.Base;
        var looks = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            if (i > 0)
                await Task.Delay(700);
            double? lit = await ScreenCheck.LookLit(w, h);
            looks.Add(lit is { } share ? $"{share:P1}" : "no look");
            if (lit is not { } seen || !ScreenCheck.IsBlack(seen))
            {
                ObsLog.Info($"screen check ({method}): lit {string.Join(", ", looks)}: a picture");
                return false;
            }
        }
        ObsLog.Info($"screen check ({method}): lit {string.Join(", ", looks)}: black");
        return true;
    }

    /// <summary>
    /// A game (an app filling its screen, not Kairos) is in front: the game watcher's look when it runs (its grace keeps a
    /// minimised game counted), else one look at the window in front.
    /// </summary>
    bool Gaming() => !ForegroundApp.IsKairos() && (_watcher?.GameActive ?? GameWatcher.ForegroundIsGame());

    /// <summary>The first start of a newer Kairos says so once; the version that ran is kept in last-version.txt.</summary>
    void SayWhenUpdated(bool hadSettings)
    {
        string file = Path.Combine(SettingsStore.AppDataDir, "last-version.txt");
        try
        {
            string? last = File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            if (last != AppInfo.Version)
                File.WriteAllText(file, AppInfo.Version);
            if (!UpdateRules.JustUpdated(last, AppInfo.Version, hadSettings))
                return;
            UpdateService.ForgetImportant(SettingsStore.AppDataDir);
            _toast.Show(Text.Format("toast.updated", AppInfo.Display(AppInfo.Version)), Text.Get("toast.updatedDetail"), Text.IsRightToLeft);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn("last version: " + e.Message);
        }
    }

    /// <summary>
    /// On this thread (where recordings start and stop): a downloaded update goes in unless a recording is under way or the
    /// settings window is in use; then it waits, tried again on the tray's tick and when a recording ends. Kairos comes back
    /// with its window only when a Kairos window was in front (the user was in it), never over a game.
    /// </summary>
    void TryApplyUpdate()
    {
        if (_stopped || !_updates.Settle(_pipeline is { } p && (p.Recording || p.Saving),
                UpdateRules.WindowBusy(_windowSaidBusy, _windowAsked, DateTime.UtcNow), Gaming()))
            return;
        // Velopack's updater first (it waits for Kairos to leave): if it cannot start, Kairos keeps running and says so.
        if (_updates.Apply(openWindow: ForegroundApp.IsKairos()))
            Shutdown($"an update to {_updates.Status?.Version}");
    }

    /// <summary>"D:\…\Kairos.exe (pid 123)" for the log.</summary>
    static string ProcessName(int id) => $"{ForegroundApp.ImagePath(id) ?? "?"} (pid {id})";

    /// <summary>Leaves Kairos (after an update's hand-over, Velopack's updater then puts the new version in and starts it).</summary>
    void Shutdown(string why)
    {
        if (_stopped)
            return;
        ObsLog.Info(LogLines.Session(DateTime.UtcNow - StartedUtc, _clipsSaved, _clipsNotSaved, _shotsSaved, _hotkeyPresses));
        ObsLog.Info($"leaving: {why}");
        _stopped = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CloseSettingsWindows();
        _trayTimer.Stop();
        _sceneTimer?.Dispose();
        _audioWatcher?.Dispose();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _pipe.Dispose();
        _watcher?.Dispose();
        _hotkeys.Dispose();
        long before = clock.ElapsedMilliseconds;
        _pipeline?.Dispose();
        _pipeline = null;
        _voice.Dispose();
        ObsLog.Info($"leaving: windows and pipe closed in {before} ms, pipeline freed in {clock.ElapsedMilliseconds - before} ms");
        ObsHost.Shutdown();
        _tray.Dispose();
        _toast.Dispose();
        ExitThread();
    }

    /// <summary>
    /// The settings window runs as its own Kairos process; leaving Kairos from the tray closes it as its own X would
    /// (the window's own close: no kill).
    /// </summary>
    static void CloseSettingsWindows()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath)))
        {
            using (process)
            {
                try
                {
                    // Its own copy's windows only: a test or development run never closes the installed Kairos's window.
                    if (process.Id != Environment.ProcessId && process.MainWindowHandle != IntPtr.Zero
                        && string.Equals(process.MainModule?.FileName, ExePath, StringComparison.OrdinalIgnoreCase))
                        process.CloseMainWindow();
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It closed meanwhile, or it is not ours to read.
                }
            }
        }
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;
        _ui.BeginInvoke(() =>
        {
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += (_, _) => { t.Dispose(); RebuildPipeline(); };
            t.Start();
        });
    }

    // ---------------------------------------------------------------- tray + IPC

    void RefreshTray()
    {
        var st = _pipeline?.GetStatus();
        bool recording = st?.Recording == true;
        bool recordingPaused = st?.RecordingPaused == true;
        bool waiting = _watcher != null && !_watcher.GameActive && !_pausedByUser;
        TrayState state = _starting ? TrayState.Starting
            : _error != null && st?.BufferActive != true ? TrayState.Error
            : recording ? TrayState.Recording
            : _pausedByUser || !_settings.Replay.Enabled ? TrayState.Paused
            : waiting ? TrayState.Waiting
            : st?.BufferActive == true ? TrayState.Active
            : TrayState.Starting;
        _tray.Update(state, _settings.Replay.QuickEnabled, _settings.Replay.QuickSeconds, _pausedByUser, recording,
            canUndo: _undoClip != null && File.Exists(_undoClip), canShare: LastSaved != null, recordingPaused,
            lastIsShot: LastSaved is { } last && ClipOrganizer.IsPicture(last),
            update: _updates.Status is { Offered: true } update ? AppInfo.Display(update.Version) : null);
    }

    string HandleCommand(string command)
    {
        try
        {
            return (string)_ui.Invoke(() => Execute(command));
        }
        catch (Exception e)
        {
            ObsLog.Warn($"command {command}: {e.Message}");
            return JsonSerializer.Serialize(new { error = e.Message }, Ipc.Json);
        }
    }

    string Execute(string command)
    {
        if (command.StartsWith("live ", StringComparison.Ordinal))
        {
            // A slider moving in the settings window: on the sources now, the saved settings untouched.
            _pipeline?.ApplyLive(Ipc.ParseLive(command["live ".Length..]));
            return JsonSerializer.Serialize(new { ok = true }, Ipc.Json);
        }
        if (command is "save" or "quick" or "toggle" or "record" or "pause-record" or "screenshot" or "delete-last")
            ObsLog.Info("command: " + command); // the settings window's buttons, or Kairos.exe --send (a Stream Deck, a macro)
        switch (command)
        {
            case "save": SaveClip(false); break;
            case "quick": SaveClip(true); break;
            case "toggle": TogglePause(); break;
            case "record": ToggleRecording(); break;
            case "pause-record": TogglePauseRecording(); break;
            case "screenshot": TakeScreenshot(); break;
            case "open-settings": OpenSettings(); break;
            case "voice-enroll": _voice.StartEnroll(); break;
            case "voice-test": _voice.StartTest(); break;
            case "voice-cancel": _voice.Cancel(); break;
            case "voice-delete": _voice.DeletePhrase(); break;
            case "voice-enroll-shot": _voice.StartEnroll(VoiceAction.Shot); break;
            case "voice-delete-shot": _voice.DeletePhrase(VoiceAction.Shot); break;
            case "delete-last": DeleteLastClip(); break;
            case "hotkeys-typing": // a hotkey box has focus: it gets the keys
                if (!_hotkeys.Suspended)
                {
                    _hotkeys.Suspend();
                    ObsLog.Info("hotkeys: let go while one is typed in the settings");
                }
                break;
            case "hotkeys-typed": ResumeHotkeys("typing ended"); break;
            case "update-check": CheckForUpdate(); break;
            case "update-download": _ = DownloadUpdate(); break;
            case "exit":
                _ui.BeginInvoke(() => Shutdown("the exit command"));
                return JsonSerializer.Serialize(new { exiting = true }, Ipc.Json);
            case "apply":
                var before = _settings;
                string? oldRings = RingFolder(before);
                _settings = SettingsStore.Load(SettingsStore.DefaultPath);
                if (before.Replay.OnDisk != _settings.Replay.OnDisk || before.Replay.DiskDrive != _settings.Replay.DiskDrive)
                    _diskOff = false; // the drive (or the choice) changed: try the disk again
                bool rebuild = _pipeline == null || SettingsDiff.NeedsRebuild(before, _settings);
                if (_hardwareState.Relief > 0 && before.Video != _settings.Video)
                {
                    _hardwareState.ChoseVideo(); // the user's new choice is recorded as chosen (the rebuild above takes it)
                    _hardwareState.Save(HardwarePath);
                }
                if (LogLines.Changes(before, _settings, FolderKind) is { } changed)
                    ObsLog.Info($"settings changed: {changed} ({(rebuild ? "built again" : "applied live")})");
                if (ObsHost.Started)
                    ApplySettings(rebuild: rebuild);
                // The replay back in RAM or in another folder: its old ring files give their space back (the rebuild
                // closed them); the camera's own when its file was turned off.
                string? newRings = RingFolder(_settings);
                if (oldRings != null && oldRings != newRings)
                    Task.Run(() => Engine.Replay.DiskReplayPlace.Remove(oldRings));
                else if (newRings != null && before.Camera.SeparateFile && !_settings.Camera.SeparateFile)
                    Task.Run(() => Engine.Replay.DiskReplayPlace.RemoveCamera(newRings));
                break;
            case "caps":
                ObsHost.LoadLater(); // the encoder list is complete
                return JsonSerializer.Serialize(Caps(), Ipc.Json);
            case "cameras":
                // Asked only once the camera is turned on in the settings: win-dshow loads then, never before.
                return JsonSerializer.Serialize(ObsHost.Started ? ObsHost.Cameras().Select(c => new NameValue(c.Name, c.Value)).ToList() : [], Ipc.Json);
            case "status" or "status busy":
                // Only the settings window asks twice a second, saying whether it is in use.
                _windowSaidBusy = command == "status busy";
                _windowAsked = DateTime.UtcNow;
                _hotkeys.RetryPending();
                break;
            default:
                return JsonSerializer.Serialize(new { error = $"unknown command {command}" }, Ipc.Json);
        }
        return JsonSerializer.Serialize(Status(), Ipc.Json);
    }

    StatusDto Status() => new(
        _pipeline is { } p ? p.GetStatus() with { NvidiaNote = _nvidiaNote, Behind = _lag.Behind && p.Lightest } : null,
        _hotkeys.Status.ToDictionary(k => k.Key, k => k.Value),
        _lastClip, _error, _pausedByUser,
        _watcher != null && !_watcher.GameActive,
        AppInfo.Version,
        _voice.Status(),
        _lastShot,
        _updates.Status,
        _updates.Look);

    /// <summary>The apps with sound now (for the Audio page's "Add source"): their name, and their exe to follow.</summary>
    List<NameValue> AudioAppsNow()
    {
        try
        {
            uint game = _audioWatcher?.GamePid ?? 0;
            return RunningApps.Pick(ProcessList.All().Where(p => p.Pid != game), new HashSet<uint>(), Engine.Audio.CoreAudio.ProcessesWithSound(),
                ForegroundApp.ForProcess);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return [];
        }
    }

    /// <summary>
    /// A sound mixer (SteelSeries Sonar, GoXLR) here for the first time: the sound set up for it once, before this build
    /// records. Asked at every build (the start, a wake from sleep, a save), so a mixer turned on while Kairos runs is too.
    /// </summary>
    void SetUpMixers()
    {
        var (speakers, mics) = (List("wasapi_output_capture", "device_id"), List("wasapi_input_capture", "device_id"));
        string? defaultMic = Engine.Audio.CoreAudio.DefaultCaptureId();
        bool separate = Engine.Audio.ProcessAudioSupport.Available;
        var done = Engine.Audio.MixerSetup.FirstTime(_settings, speakers, mics, defaultMic, separate);
        if (done.Count == 0)
            return;
        // The same setup onto the file as saved, not this copy written whole (it can be older than the file).
        SettingsStore.Update(SettingsStore.DefaultPath, s => Engine.Audio.MixerSetup.FirstTime(s, speakers, mics, defaultMic, separate));
        foreach (string line in done)
            ObsLog.Info($"audio: {line}");
    }

    /// <summary>A libobs source's list (devices, windows) as names and values; none before libobs starts.</summary>
    static List<NameValue> List(string source, string prop) =>
        ObsHost.Started ? ObsHost.ListProperty(source, prop).Select(i => new NameValue(i.Name, i.Value)).ToList() : [];

    CapsDto Caps()
    {
        string versionFile = Path.Combine(AppContext.BaseDirectory, "libobs-version.txt");
        return new CapsDto(
            ObsHost.Started ? ObsHost.Monitors() : [],
            List("window_capture", "window"),
            List("wasapi_input_capture", "device_id"),
            List("wasapi_output_capture", "device_id"),
            ObsHost.Started ? [.. UsableEncoders()] : [],
            File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "",
            AudioAppsNow(),
            Engine.Audio.CoreAudio.DefaultRenderId(),
            _hardware?.RefreshHz ?? 0,
            Engine.Audio.CoreAudio.DefaultCaptureId());
    }

    /// <summary>The encoders a build may pick from (none that failed here, QSV only for an Intel screen): the page's formats.</summary>
    IReadOnlyList<string> UsableEncoders()
    {
        var ids = _encoders.Usable(ObsHost.EncoderIds());
        return _hardware is { } hw ? SmartSetup.Usable(ids, hw) : ids;
    }
}
