using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using Kairos.Platform;
using Kairos.Engine;
using Kairos.Engine.Updates;
using Kairos.Engine.Voice;
using Kairos.Settings;

namespace Kairos.App;

/// <summary>The always-on background process: libobs pipeline, tray, hotkeys and the IPC server.</summary>
public sealed class EngineApp
{
    static readonly string ExePath = Environment.ProcessPath!;

    readonly TrayController _tray = new();
    readonly HotkeyManager _hotkeys = new();
    readonly SaveToast _toast = new();
    readonly ClipIndex _clipIndex = new(ClipIndex.DefaultPath);
    readonly VoiceTrigger _voice = new(SpeechModels.DefaultDirectory, Path.Combine(SettingsStore.AppDataDir, "voice"));
    int _pendingSeconds;
    bool _pendingByVoice;
    /// <summary>The newest clip saved in this session, until it is deleted from the tray.</summary>
    string? _undoClip;
    readonly UiTimer _trayTimer = new(5000);
    readonly PipeServer _pipe;
    AppSettings _settings;
    CapturePipeline? _pipeline;
    GameWatcher? _watcher;
    string? _pendingApp;
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

        bool firstRun = !File.Exists(SettingsStore.DefaultPath);
        _settings = SettingsStore.Load(SettingsStore.DefaultPath);
        // The installer's language page does this on Windows; here the first run follows the system language.
        if (firstRun)
            _settings.General.Language = AppSettings.DefaultLanguage(System.Globalization.CultureInfo.CurrentUICulture);
        // Whole-screen capture makes the X server copy every frame (it ran at 87% of a core beside a game); the window in front does not.
        if (firstRun)
            _settings.Capture.Mode = CaptureMode.Game;
        // The install wizard's language and start-with-Windows choice, once after it ran.
        bool chosen = InstallChoices.ConsumeInto(_settings, SettingsStore.AppDataDir);
        // A first install takes the usual keys, or for each one another app holds (NVIDIA's are the same), a free one.
        bool pickedKeys = firstRun && HotkeyDefaults.PickFree(_settings.Hotkeys, HotkeyManager.IsFree);
        if (firstRun || chosen)
            SettingsStore.Save(SettingsStore.DefaultPath, _settings);
        Text.Language = _settings.General.Language;
        if (pickedKeys)
            _toast.Show(Text.Get("toast.keysPicked"), Text.Format("toast.keysPickedSave", _settings.Hotkeys.Save), Text.IsRightToLeft);

        _tray.SaveClicked += () => SaveClip(quick: false);
        _tray.QuickClicked += () => SaveClip(quick: true);
        _tray.ScreenshotClicked += () => TakeScreenshot();
        _tray.PauseClicked += TogglePause;
        _tray.RecordClicked += ToggleRecording;
        _tray.PauseRecordClicked += TogglePauseRecording;
        _tray.FolderClicked += OpenClipsFolder;
        _tray.DeleteLastClicked += DeleteLastClip;
        _tray.ShareClicked += ShareLastClip;
        _voice.Triggered += action => UiThread.Post(() =>
        {
            if (action == VoiceAction.Shot)
                TakeScreenshot(byVoice: true);
            else
                SaveClip(quick: false, byVoice: true);
        });
        _tray.SettingsClicked += OpenSettings;
        _tray.ExitClicked += () => Shutdown("the tray's Exit");
        _hotkeys.Pressed += OnHotkey;
        _trayTimer.Tick += () =>
        {
            // A settings window that closed while a hotkey box had focus never said typing ended.
            if (_hotkeys.Suspended && !ForegroundApp.IsKairos())
                _hotkeys.Resume();
            if (_saveWatch.Tick(DateTime.UtcNow) is { } never)
                NotSaved(never);
            if (UpdateRules.ShouldCheck(_settings.General.CheckUpdates && _updates.Available, _bufferActiveSince, _updates.LastCheck, DateTime.UtcNow))
                CheckForUpdate();
            TryApplyUpdate();
            if (_stopped)
                return;
            RefreshTray();
        };
        _updates.NewVersion += version => UiThread.Post(() =>
            _toast.Show(Text.Format("toast.update", AppInfo.Display(version)), Text.Get("toast.updateDetail"), Text.IsRightToLeft));
        _trayTimer.Start();

        ObsLog.Open(Path.Combine(SettingsStore.AppDataDir, "logs"));

        // Right after sign-in, let Windows finish booting before libobs and the GPU encoder start.
        if (background && Environment.TickCount64 < 120_000)
        {
            RefreshTray();
            UiTimer.After(15_000, StartEngine);
        }
        else
        {
            StartEngine();
        }

        _pipe = new PipeServer(HandleCommand,
            () => _pipeline is { } p ? p.TakeLevels() : (LevelMeter.Silent, LevelMeter.Silent),
            onRequest: (request, client) =>
            {
                if (request == "exit")
                    ObsLog.Info($"exit asked over the pipe by {ProcessName(client)}");
            });
        PowerEvents.Resumed += () => UiThread.Post(() => UiTimer.After(5000, RebuildPipeline));
        PowerEvents.SessionEnding += () => UiThread.Post(() => Shutdown("the session ending"));

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
            UiThread.Post(ObsHost.LoadLater); // the slow encoder plugin, now that the replay buffer is up
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
        // Once per key, ever: one small message that goes away by itself (the pages carry no notes).
        if (_newlyTaken.Count > 0)
        {
            var (title, detail) = TakenToast(_newlyTaken);
            _toast.Show(title, detail, Text.IsRightToLeft, warning: true);
        }

        try
        {
            Autostart.Sync(_settings.General.StartWithWindows, InstallPaths.LaunchExe);
        }
        catch (Exception e)
        {
            ObsLog.Warn($"autostart: {e.Message}");
        }

        if (!rebuild)
        {
            UpdateBufferState(); // instant replay may have been turned on or off
            RefreshTray();
            return;
        }

        _watcher?.Dispose();
        _watcher = null;
        if (_settings.General.OnlyWhenGaming)
        {
            _watcher = new GameWatcher();
            _watcher.Changed += _ => UpdateBufferState();
        }

        RebuildPipeline();
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

    /// <summary>The message about keys another app holds: one key and what to do, or how many and which.</summary>
    public static (string Title, string Detail) TakenToast(IReadOnlyList<string> keys) => keys.Count == 1
        ? (Text.Format("toast.keyTaken", keys[0]), Text.Get("toast.keyTakenFix"))
        : (Text.Format("toast.keysTaken", Text.Count("count.hotkeys", keys.Count)), string.Join(Text.Get("list.separator"), keys));

    GameFollower? _gameFollower;
    bool _builtOnce;

    void RebuildPipeline()
    {
        if (_builtOnce)
            ObsHost.LoadLater(); // every encoder plugin is in before a pick after the first
        _builtOnce = true;
        _pipeline?.Dispose();
        _pipeline = null;
        _error = null;
        try
        {
            var p = new CapturePipeline(_settings, voice: _voice);
            p.ReplaySaved += (path, _) => UiThread.Post(() => OnClipSaved(path));
            p.RecordingStopped += (path, seconds) => UiThread.Post(() => OnRecordingStopped(path, seconds));
            p.Build();
            _pipeline = p;
            _gameFollower?.Dispose();
            _gameFollower = _settings.Capture.Mode == CaptureMode.Game ? new GameFollower(p) : null;
            _lastClip ??= NewestClip(p.ClipsFolder);
            _lastShot ??= ClipLibrary.NewestPicture(p.ClipsFolder);
            _retried = false;
            UpdateBufferState();
        }
        catch (Exception e)
        {
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
        UiTimer.After(3000, RebuildPipeline);
    }

    bool WantBuffer => _settings.Replay.Enabled && !_pausedByUser && (_watcher == null || _watcher.GameActive);

    void UpdateBufferState()
    {
        if (_pipeline == null)
            return;
        bool active = _pipeline.SetBufferActive(WantBuffer);
        if (active)
            _bufferActiveSince ??= DateTime.UtcNow;
        if (active && !_readyLogged)
        {
            _readyLogged = true;
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            ObsLog.Info($"replay buffer active {(DateTime.Now - self.StartTime).TotalMilliseconds:F0} ms after the process started");
        }
        if (_pipeline.LastError != null)
            _error = _pipeline.LastError;
        RefreshTray();
    }

    bool _readyLogged;

    // ---------------------------------------------------------------- actions

    void OnHotkey(string id)
    {
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
        string offReason = SaveWatch.OffReason(_settings.Replay.Enabled, _pausedByUser, waiting);
        if (_pipeline == null)
        {
            NotSaved(offReason);
            return;
        }
        _pendingByVoice = byVoice;
        _pendingSeconds = quick ? _settings.Replay.QuickSeconds : _pipeline.GetStatus().BufferSeconds;
        _pendingApp = CapturedApp();
        var request = _pipeline.RequestSave(quick);
        if (request != SaveWatch.Request.Started)
            ObsLog.Info($"save not started: {request} (active={_pipeline.BufferActive})");
        if (_saveWatch.Requested(request, DateTime.UtcNow, _pipeline.BufferBytes, offReason) is { } reason)
            NotSaved(reason);
    }

    /// <summary>A save that did not happen, said with the reason (no sound: only a saved clip chimes).</summary>
    void NotSaved(string reason)
    {
        ObsLog.Warn($"clip not saved: {reason}");
        _toast.Show(Text.Get("toast.notSaved"), Text.Get(reason), Text.IsRightToLeft, warning: true);
    }

    /// <summary>The app being captured now (its folder name): the chosen window's app, or what is in front.</summary>
    string? CapturedApp() => _settings.Capture.Mode == CaptureMode.Window && WindowTarget.ExeName(_settings.Capture.Window) is { } exe
        ? ForegroundApp.ForExe(exe)
        : ForegroundApp.Current();

    void OnClipSaved(string rawPath)
    {
        string app = _pendingApp ?? ClipOrganizer.AppFolderName(null, null, null);
        string final = rawPath;
        try
        {
            string target = ClipOrganizer.TargetPath(_pipeline?.ClipsFolder ?? Path.GetDirectoryName(rawPath)!, app,
                DateTime.Now, _settings.Output.PerAppFolders);
            final = ClipOrganizer.Move(rawPath, target);
        }
        catch (Exception e)
        {
            ObsLog.Warn($"could not move clip: {e.Message}");
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
    }

    /// <summary>"Low disk space", a moment after the saved card (so both are read).</summary>
    void WarnLowDisk(string clip, long free)
    {
        string drive = MountPoint(clip);
        ObsLog.Warn($"low disk space: {free / (1024 * 1024)} MB left on {drive}");
        UiTimer.After(3500, () => _toast.Show(Text.Get("toast.lowDisk"),
            Text.Format("toast.lowDiskBody", Text.Ltr(FormatSize(free)), Text.Ltr(drive)), Text.IsRightToLeft, warning: true));
    }

    void EnforceFolderLimit()
    {
        if (_settings.Output.MaxFolderGb > 0 && _pipeline != null)
        {
            string folder = _pipeline.ClipsFolder;
            int limit = _settings.Output.MaxFolderGb;
            Task.Run(() => StorageJanitor.Enforce(folder, limit, _clipIndex));
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
        pipeline.HoldFeed(); // a paused pipeline is not capturing: bring the picture back for the grab
        Task.Run(async () =>
        {
            string? path = null;
            try
            {
                await Task.Delay(300); // the source needs a frame or two once the scene is back
                if (await ScreenshotGrabber.Grab(w, h, TimeSpan.FromSeconds(2)) is { } frame)
                    path = ScreenshotWriter.Write(target, frame);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
            {
                ObsLog.Warn($"screenshot not saved: {e.Message}");
            }
            pipeline.ReleaseFeed();
            UiThread.Post(() => OnShotTaken(path, app, byVoice));
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
                ClipDeleter.Recycle(clip);
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
        UiTimer.After(milliseconds, () =>
        {
            if (ReferenceEquals(pipeline, _pipeline))
                pipeline?.SetSystemMuted(false);
        });
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
            Notify(Text.Get("toast.recordFailed"), p.LastError ?? "");
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
    void OnRecordingStopped(string path, double seconds)
    {
        // An update pressed during the recording goes ahead now (after this clip's toast and last-clip note).
        UiThread.Post(TryApplyUpdate);
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (bytes > 0)
        {
            _lastClip = path;
            ObsLog.Info($"recording saved: {path} ({seconds:F1} s)");
            Notify(Text.Get("toast.recordSaved"), $"{_recordingApp} • " + Ui.StatusLogic.RecordingDetail(seconds, bytes));
        }
        RefreshTray();
    }

    /// <summary>The small card (never in captures); no sound, which a recording would pick up.</summary>
    void Notify(string title, string detail)
    {
        if (_settings.General.ToastOnSave)
            _toast.Show(title, detail, Text.IsRightToLeft);
    }

    void OpenClipsFolder()
    {
        string folder = _pipeline?.ClipsFolder ?? _settings.Output.Folder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("xdg-open", [folder]) { UseShellExecute = false });
    }

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
        _ = Task.Run(() => _updates.Check(now));
    }

    /// <summary>
    /// On this thread (where recordings start and stop): a downloaded update goes in unless a recording is under way or the
    /// settings window is in use; then it waits, tried again on the tray's tick and when a recording ends. Kairos comes back
    /// with its window only when a Kairos window was in front (the user was in it), never over a game.
    /// </summary>
    void TryApplyUpdate()
    {
        if (!_stopped && _updates.Settle(_pipeline?.Recording == true, UpdateRules.WindowBusy(_windowSaidBusy, _windowAsked, DateTime.UtcNow)))
            Shutdown($"an update to {_updates.Status?.Version}", applyUpdate: true, openWindow: ForegroundApp.IsKairos());
    }

    /// <summary>"D:\…\Kairos.exe (pid 123)" for the log.</summary>
    static string ProcessName(int id) => $"{ForegroundApp.ImagePath(id) ?? "?"} (pid {id})";

    /// <summary>Leaves Kairos; with <paramref name="applyUpdate"/>, Velopack's updater then puts the new version in and starts it.</summary>
    void Shutdown(string why, bool applyUpdate = false, bool openWindow = false)
    {
        if (_stopped)
            return;
        ObsLog.Info($"leaving: {why}");
        _stopped = true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        CloseSettingsWindows();
        _trayTimer.Stop();
        _pipe.Dispose();
        _watcher?.Dispose();
        _hotkeys.Dispose();
        long before = clock.ElapsedMilliseconds;
        _gameFollower?.Dispose();
        _pipeline?.Dispose();
        _pipeline = null;
        _voice.Dispose();
        ObsLog.Info($"leaving: windows and pipe closed in {before} ms, pipeline freed in {clock.ElapsedMilliseconds - before} ms");
        ObsHost.Shutdown();
        _tray.Dispose();
        _toast.Dispose();
        if (applyUpdate)
            _updates.Apply(openWindow);
        KairosApp.Quit();
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
                    // The settings window closes itself on SIGTERM, as its own X would.
                    if (process.Id != Environment.ProcessId && ForegroundApp.CommandLine(process.Id).Contains("--settings"))
                        Process.Start(new ProcessStartInfo("kill", ["-TERM", process.Id.ToString()]) { UseShellExecute = false })?.Dispose();
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It closed meanwhile.
                }
            }
        }
    }

    static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    /// <summary>The mount point of the volume holding <paramref name="path"/>, for "N GB left on …".</summary>
    static string MountPoint(string path)
    {
        string full = Path.GetFullPath(path);
        return DriveInfo.GetDrives().Where(d => full.StartsWith(d.Name, StringComparison.Ordinal)).OrderByDescending(d => d.Name.Length).FirstOrDefault()?.Name ?? "/";
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
            lastIsShot: LastSaved is { } last && ClipOrganizer.IsPicture(last));
    }

    string HandleCommand(string command)
    {
        try
        {
            return Dispatcher.UIThread.Invoke(() => Execute(command));
        }
        catch (Exception e)
        {
            ObsLog.Warn($"command {command}: {e.Message}");
            return JsonSerializer.Serialize(new { error = e.Message }, Ipc.Json);
        }
    }

    string Execute(string command)
    {
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
            case "hotkeys-typing": _hotkeys.Suspend(); break; // a hotkey box has focus: it gets the keys
            case "hotkeys-typed": _hotkeys.Resume(); break;
            case "update-check": CheckForUpdate(); break;
            case "update-download": _ = DownloadUpdate(); break;
            case "exit":
                UiThread.Post(() => Shutdown("the exit command"));
                return JsonSerializer.Serialize(new { exiting = true }, Ipc.Json);
            case "apply":
                var before = _settings;
                _settings = SettingsStore.Load(SettingsStore.DefaultPath);
                if (ObsHost.Started)
                    ApplySettings(rebuild: _pipeline == null || SettingsDiff.NeedsRebuild(before, _settings));
                break;
            case "caps":
                ObsHost.LoadLater(); // the encoder list is complete
                return JsonSerializer.Serialize(Caps(), Ipc.Json);
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
        _pipeline?.GetStatus(),
        _hotkeys.Status.ToDictionary(k => k.Key, k => k.Value),
        _lastClip, _error, _pausedByUser,
        _watcher != null && !_watcher.GameActive,
        AppInfo.Version,
        _voice.Status(),
        _lastShot,
        _updates.Status);

    static CapsDto Caps()
    {
        static List<NameValue> List(string source, string prop) =>
            ObsHost.Started ? ObsHost.ListProperty(source, prop).Select(i => new NameValue(i.Name, i.Value)).ToList() : [];
        string versionFile = Path.Combine(AppContext.BaseDirectory, "libobs-version.txt");
        return new CapsDto(
            ObsHost.Started ? ObsHost.Monitors() : [],
            List(CaptureBackend.WindowList.Source, CaptureBackend.WindowList.Property),
            List(CaptureBackend.MicList.Source, CaptureBackend.MicList.Property),
            List(CaptureBackend.SpeakerList.Source, CaptureBackend.SpeakerList.Property),
            ObsHost.Started ? [.. ObsHost.EncoderIds()] : [],
            File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "");
    }
}
