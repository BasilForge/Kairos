using Kairos.App;
using Kairos.Engine.Audio;
using Kairos.Engine.Replay;
using Kairos.Native;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Scene, capture and audio sources, encoders and outputs built from <see cref="AppSettings"/>.</summary>
public sealed class CapturePipeline : IDisposable
{
    const uint ChannelVideo = 0, ChannelDesktop = 1, ChannelMic = 3;
    /// <summary>The first of the channels for the protected games' own sound (one each, <see cref="AntiCheatGuard.GameSoundWindows"/>).</summary>
    /// <summary>Extra sound sources from here on: the chosen apps' (Apps) or the protected games' (Games), never both.</summary>
    const uint ChannelExtraSound = 4;
    static readonly TimeSpan MinBufferBeforeSave = TimeSpan.FromSeconds(2);

    readonly AppSettings _s;
    readonly List<IntPtr> _sources = [];
    readonly List<IntPtr> _audioEncoders = [];
    readonly List<(IntPtr Meter, ObsVolmeterCallback Callback)> _meters = [];
    readonly LevelMeter _systemLevel = new();
    readonly LevelMeter _micLevel = new();
    /// <summary>The sources of the system-sound track: the desktop, the chosen app, or the game (and the protected games).</summary>
    readonly List<IntPtr> _systemSources = [];
    /// <summary>The tracks this pipeline records, and which each source feeds (<see cref="TrackPlanner"/>).</summary>
    TrackPlan _plan = null!;
    /// <summary>The volumes and mutes as set last (a slider, or Save): a source made later takes them too.</summary>
    LiveAudio _live = new(1, 1, []);
    /// <summary>Separate sources: each row's source (an app row's follows its process), and the process each follows now.</summary>
    readonly Dictionary<string, IntPtr> _rowSources = [];
    readonly Dictionary<string, uint> _rowPids = [];
    /// <summary>Separate sources: "Other sounds", one source per process (<see cref="AudioPartition"/>).</summary>
    readonly Dictionary<uint, IntPtr> _otherSources = [];
    /// <summary>Separate sources: the output channel each source plays on (libobs has 64; 0 is the picture).</summary>
    readonly Dictionary<IntPtr, uint> _channels = [];
    /// <summary>The mode the scene was built in (<see cref="SceneRules.BuildMode"/>).</summary>
    CaptureMode _mode;
    /// <summary>
    /// One app: its window capture; Games: the game capture. Under either (and Screen's game layer), the screen: One app
    /// shows it while the window gives no picture; Screen and Games while the game gives none (<see cref="PlaceScreen"/>).
    /// One app's window itself is not captured while its game gives the picture over it (<see cref="PlaceWindow"/>).
    /// </summary>
    IntPtr _window, _game, _screenLayer, _windowItem;
    /// <summary>The game capture whose "unhooked" signal brings back, at once, the capture under it.</summary>
    IntPtr _hookSource;
    ObsSignalCallback? _onUnhooked;
    /// <summary>
    /// The captures under a game (the screen, One app's window) change on the engine's tick and on libobs's graphics
    /// thread (the signal): one at a time.
    /// </summary>
    readonly object _underGate = new();
    WindowPresence _presence = new();
    /// <summary>Screen and One app: a game capture over the capture, for a game that fills the screen (<see cref="GameOverlay"/>).</summary>
    IntPtr _gameLayerSource, _gameLayer;
    readonly GameOverlay _overlay = new();
    /// <summary>The captured screen's device ("\\.\DISPLAY1"), or null when it cannot be told apart (one screen: any).</summary>
    string? _captureDevice;
    /// <summary>The camera over the picture (null: off), and in the 9:16 layout the band the game fills.</summary>
    CameraLayer? _camera;
    (int X, int Y, int W, int H)? _gameBand;
    /// <summary>One app: the chosen window's exe (read once when built).</summary>
    /// <summary>Window mode: the chosen app recorded now.</summary>
    string _target = "";
    /// <summary>Games mode: an anti-cheat that forbids hooking games runs, so the screen is recorded (<see cref="AntiCheatGuard"/>).</summary>
    bool _guardOn;
    IntPtr _scene, _videoEncoder, _replay, _quick, _record;
    ObsSignalCallback? _onReplaySaved, _onQuickSaved, _onRecordStopped;
    DateTime _bufferStartedUtc;
    DateTime _saveRequestedUtc;
    volatile bool _saving;
    /// <summary>A clip is being written (an update waits for it).</summary>
    public bool Saving => _saving;
    string? _recordPath;

    readonly Func<IReadOnlyList<string>, IReadOnlyList<string>>? _encoderFilter;
    readonly Voice.VoiceTrigger? _voice;
    IntPtr _micSource;
    ObsAudioCaptureCallback? _micTap;

    /// <param name="encoderFilter">Self-test hook that narrows the encoder list, e.g. to force Intel QSV.</param>
    /// <param name="voice">Smart Save listener that hears the microphone source.</param>
    /// <param name="encoders">The encoders that failed this session: left out, or (B-frames) asked less of.</param>
    /// <param name="screenWgc">This PC's screen capture is black with Automatic: Windows Graphics Capture from the start
    /// (<see cref="ScreenCheck"/>).</param>
    public CapturePipeline(AppSettings settings, Func<IReadOnlyList<string>, IReadOnlyList<string>>? encoderFilter = null,
        Voice.VoiceTrigger? voice = null, EncoderFallback? encoders = null, bool screenWgc = false)
    {
        _s = settings;
        _screenWgc = screenWgc;
        _encoderFilter = encoderFilter;
        _voice = voice;
        _encoders = encoders;
    }

    readonly EncoderFallback? _encoders;
    bool _screenWgc;
    /// <summary>The screen captures in the scene (their method can change while it runs).</summary>
    readonly List<IntPtr> _monitorSources = [];

    /// <summary>The video settings recorded with: the user's own, or Automatic's choice.</summary>
    VideoSettings _video = new();

    /// <summary>The hardware (the engine reads it once; tests and self-tests leave it to the pipeline).</summary>
    public HardwareProfile? Hardware { get; init; }

    /// <summary>How far Automatic stepped down for skipped frames (0: not at all).</summary>
    public int AutoStep { get; init; }

    /// <summary>How much lighter to record because recording fell behind a game (<see cref="LagRelief"/>; 0: as chosen).</summary>
    public int Relief { get => _relief; init => _relief = value; }

    int _relief;

    /// <summary>What <see cref="NextRelief"/> was found with, for a step taken as the recording runs.</summary>
    (VideoSettings Video, int Max, bool Nvidia) _reliefFrom;

    /// <summary>
    /// Takes <paramref name="step"/> as the recording runs when it changes only the GPU class (<see cref="LagRelief.Live"/>):
    /// the replay is kept. False when it needs the pipeline anew.
    /// </summary>
    public bool StepLive(int step)
    {
        if (!LagRelief.Live(_relief, step, _reliefFrom.Nvidia))
            return false;
        _relief = step;
        SetGpuClass();
        FindNextRelief();
        return true;
    }

    /// <summary>The next lighter step from <see cref="Relief"/> (<see cref="LagRelief.Next"/>).</summary>
    void FindNextRelief() =>
        NextRelief = LagRelief.Next(_reliefFrom.Video, _relief, _reliefFrom.Max, _reliefFrom.Nvidia, realtime: !GpuPriority.Intel);

    /// <summary>The GPU class for <see cref="Relief"/>: REALTIME at its own step only, libobs's own otherwise.</summary>
    void SetGpuClass()
    {
        if (GpuPriority.SetClass(realtime: _relief == LagRelief.Realtime) is { } priority)
            ObsLog.Info($"gpu priority: {priority}");
    }

    /// <summary>
    /// What Automatic records with: its pick, at its own encoder speed (never a speed set by hand while it was off, which
    /// the Quality page no longer shows), the user's custom numbers kept for when Automatic is turned off.
    /// </summary>
    internal static VideoSettings AutoVideo(SmartChoice smart, VideoSettings user) => new()
    {
        Auto = true, Codec = smart.Encoder.Codec, Resolution = smart.Resolution, Fps = smart.Fps, Quality = smart.Quality,
        CustomCq = user.CustomCq, CustomMbps = user.CustomMbps, Preset = VideoSettings.DefaultPreset,
    };

    /// <summary>The next lighter step (<see cref="LagRelief.Next"/>); <see cref="Relief"/> itself when none is left.</summary>
    public int NextRelief { get; private set; }

    /// <summary>
    /// Nothing lighter left (<see cref="LagRelief.Max"/>): the CPU records already, this CPU has no room for it, or what is
    /// left would record the same.
    /// </summary>
    public bool Lightest => NextRelief == Relief;

    /// <summary>Automatic: what it records with; null when the user chose.</summary>
    public AutoPick? Auto { get; private set; }

    public string ClipsFolder { get; private set; } = "";
    public bool FolderFellBack { get; private set; }
    public EncoderPlan? Plan { get; private set; }
    public EncoderChoice? Choice { get; private set; }
    public int Fps { get; private set; }
    public (int W, int H) Output { get; private set; }
    /// <summary>The canvas: the captured monitor's own size, which screenshots keep.</summary>
    public (int W, int H) Base { get; private set; }
    /// <summary>Why the replay buffer could not start, until it starts.</summary>
    public string? BufferError { get; private set; }
    /// <summary>Why the last recording could not start, until one starts.</summary>
    public string? RecordError { get; private set; }
    public string? LastError => RecordError ?? BufferError;
    /// <summary>The mode the scene was built in: One app wants <see cref="WatchWindow"/> each second, Screen <see cref="WatchScreenGame"/>, Games <see cref="WatchGame"/> (and <see cref="SetGuard"/> every 2 s).</summary>
    public CaptureMode BuiltMode => _mode;
    /// <summary>Why the full screen is recorded instead of the chosen capture (a text key), or null.</summary>
    public string? CaptureNotice { get; private set; }
    public bool BufferActive => _replay != IntPtr.Zero && Obs.obs_output_active(_replay);
    public bool Recording => _record != IntPtr.Zero && Obs.obs_output_active(_record);
    public bool RecordingPaused => Recording && Obs.obs_output_paused(_record);
    readonly RecordingClock _recordClock = new();

    /// <summary>Raised on a libobs thread with the raw file path, whether it was the quick buffer, and its track names.</summary>
    public event Action<string, bool, IReadOnlyList<string>>? ReplaySaved;

    /// <summary>Raised on a libobs thread with the finished recording's path, its length without pauses, and its track names.</summary>
    public event Action<string, double, IReadOnlyList<string>>? RecordingStopped;

    public void Build()
    {
        var monitor = VideoMath.PickMonitor(ObsHost.Monitors(), _s.Capture.MonitorId);
        int baseW = monitor?.Width ?? 1920, baseH = monitor?.Height ?? 1080;
        Base = (baseW, baseH);
        var ids = ObsHost.EncoderIds();
        var hw = Hardware ?? HardwareProfile.Read(monitor?.Id);
        var usable = _encoders?.Usable(ids) ?? ids;
        usable = _encoderFilter?.Invoke(usable) ?? usable;
        if (LagRelief.OnCpu(Relief) && usable.Contains(EncoderCatalog.X264))
            usable = [EncoderCatalog.X264]; // the CPU: no time on the busy graphics card (Automatic's own CPU choice)
        if (_s.Video.Auto)
        {
            var smart = SmartSetup.Choose(hw, usable, AutoStep);
            Choice = smart.Encoder;
            _video = AutoVideo(smart, _s.Video);
        }
        else
        {
            Choice = SmartSetup.PickChosen(_s.Video.Codec, usable, hw);
            _video = _s.Video;
        }
        _reliefFrom = (_video, LagRelief.Max(hw.CpuThreads, Choice.Vendor == EncoderVendor.Software && !LagRelief.OnCpu(Relief)),
            Choice.Vendor == EncoderVendor.Nvidia);
        FindNextRelief();
        SetGpuClass();
        _video = LagRelief.Video(_video, Relief);
        if (_encoders?.NoBFrames(Choice.Id) == true)
            Choice = Choice with { NoBFrames = true }; // a start with B-frames failed on this encoder
        Output = VideoMath.OutputSize(baseW, baseH, _video.Resolution);
        Fps = _video.Fps;
        if (Choice.Vendor == EncoderVendor.Software && !_s.Video.Auto)
            (Output, Fps) = EncoderCatalog.LimitForSoftware(Output, Fps);
        _gameBand = null;
        if (_s.Camera is { Enabled: true, Layout: CameraLayout.Vertical })
        {
            // 9:16: the picture is made at its own size (no scaling); the camera on top, the game below it.
            Output = CameraMath.VerticalSize(_video.Resolution, baseH);
            (baseW, baseH) = Output;
            Base = Output;
            _gameBand = CameraMath.Bands(baseW, baseH).Game;
        }
        Auto = _s.Video.Auto ? new AutoPick(Choice.Id, Choice.Vendor == EncoderVendor.Software ? hw.Cpu : hw.CaptureGpu ?? "", Output.H, Fps, _video.Quality) : null;
        _captureDevice = hw.CaptureDevice;
        if (!ObsHost.ResetVideo(new VideoConfig(baseW, baseH, Output.W, Output.H, Fps)))
            throw new InvalidOperationException("Could not set up video");

        ClipsFolder = ClipOrganizer.ResolveFolder(_s.Output.Folder, f => ClipOrganizer.Usable(f, App.InstallPaths.Root), out bool fellBack);
        FolderFellBack = fellBack;

        _mode = SceneRules.BuildMode(_s.Capture);
        if (_mode == CaptureMode.Window)
            _target = AppTargets.Pick(_s.Capture.Apps, null, Open(App.WindowTarget.OpenApps(_s.Capture.Apps)), null) ?? "";
        FreshAfterSave = _s.Replay.FreshAfterSave;
        if (_mode != _s.Capture.Mode)
        {
            CaptureNotice = "status.captureNoApp";
            ObsLog.Info("capture: no window chosen, recording the screen");
        }
        _plan = TrackPlanner.Plan(_s.Audio);
        _live = LiveAudio.Of(_s.Audio);
        BuildScene(monitor?.Id ?? "", baseW, baseH);
        if (_s.Audio.Layout == AudioLayout.Separate)
            BuildSeparateAudio();
        else
            BuildAudio();
        BuildEncoders();
        bool cameraFile = _camera != null && _s.Camera.SeparateFile;
        PlanDisk(cameraFile);
        if (_camera != null && cameraFile)
        {
            try
            {
                _camera.BuildFile((w, h, fps) => CreateVideoEncoder(CameraPlan(w, h, fps), "Kairos camera video"),
                    NewReplayHere("Kairos camera replay", "Kairos camera %CCYY-%MM-%DD %hh-%mm-%ss", _s.Replay.Seconds,
                        _diskFolder == null ? null : new DiskRing(Path.Combine(_diskFolder, DiskReplayPlace.CameraFile), _cameraRingBytes, _keepSeconds)),
                    _audioEncoders[0], Fps);
            }
            catch (InvalidOperationException e)
            {
                // The replay matters more: it runs, with the camera in the picture and no file of its own.
                ObsLog.Warn($"camera file left out: {e.Message}");
                _camera.ReleaseFile();
            }
        }
        _replay = CreateReplayOutput("Kairos replay", "Kairos %CCYY-%MM-%DD %hh-%mm-%ss", _s.Replay.Seconds,
            _diskFolder == null ? null : new DiskRing(Path.Combine(_diskFolder, DiskReplayPlace.FileName), _mainRingBytes, _keepSeconds), main: true);
        _onReplaySaved = (_, _) => OnSaved(_replay, quick: false);
        Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(_replay), "saved", _onReplaySaved, IntPtr.Zero);

        if (_s.Replay.QuickEnabled)
        {
            // On the disk the quick clip comes from the main ring: nothing written twice.
            _quick = CreateReplayOutput("Kairos quick", "Kairos quick %CCYY-%MM-%DD %hh-%mm-%ss", _s.Replay.QuickSeconds,
                ReplayOnDisk ? new DiskRing(null, 0, _keepSeconds, Share: "Kairos replay") : null);
            _onQuickSaved = (_, _) => OnSaved(_quick, quick: true);
            Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(_quick), "saved", _onQuickSaved, IntPtr.Zero);
        }

        ObsLog.Info($"pipeline built: mode={_s.Capture.Mode} {baseW}x{baseH} -> {Output.W}x{Output.H}@{Fps} " +
                    $"{Choice.Id} cq={Plan!.Cq} max={Plan.MaxBitrateKbps}kbps folder={ClipsFolder}" +
                    (Relief > 0 ? $" lighter (relief {Relief}): it fell behind a game" : "") +
                    (ReplayOnDisk ? $" replay on the disk: {_diskFolder} ({(_mainRingBytes + _cameraRingBytes) >> 20} MB)" : ""));
    }

    void BuildScene(string monitorId, int baseW, int baseH)
    {
        _scene = Obs.obs_scene_create("Kairos");
        switch (_mode)
        {
            case CaptureMode.Window:
                // The game layer's source first, as in Screen: libobs ticks it before the window's when its game goes.
                var gameOver = CreateGameLayerSource();
                using (var d = new ObsData()
                           .Set("window", _target).Set("priority", 2L).Set("method", 2L)
                           .Set("cursor", _s.Capture.CaptureCursor).Set("client_area", true))
                    _windowItem = AddToScene(_window = CreateSource("window_capture", "Window", d), baseW, baseH);
                AddGameLayer(baseW, baseH, gameOver); // the chosen window from its game, when it is fullscreen or gives no picture
                WatchUnhook(gameOver);
                // Over the window, shown only when it is needed (it then covers any picture the capture kept); not open
                // now: the screen from the first frame, not black until the first check.
                _presence = new WindowPresence(showScreen: !App.WindowTarget.IsOpen(_target));
                _screenLayer = AddToScene(CreateMonitorSource(monitorId), baseW, baseH);
                Obs.obs_sceneitem_set_visible(_screenLayer, _presence.ShowScreen);
                if (_presence.ShowScreen)
                {
                    CaptureNotice = "status.captureGone";
                    ObsLog.Info("capture: the chosen app isn't open, recording the screen");
                }
                break;

            case CaptureMode.Game:
                _guardOn = _s.Capture.ScreenForRiotGames && AntiCheatGuard.AnyProtectedRunning();
                // The game capture is made before the screen under it, as Screen's game layer (the screen starts copying
                // in the frame its game goes).
                using (var d = new ObsData()
                           .Set("capture_mode", GameCaptureMode(_guardOn)).Set("window", "").Set("capture_cursor", _s.Capture.CaptureCursor)
                           .Set("anti_cheat_hook", true).Set("capture_overlays", false)
                           .Set("capture_audio", GameSoundOnly))
                    _game = CreateSource("game_capture", "Game", d);
                _screenLayer = AddToScene(CreateMonitorSource(monitorId), baseW, baseH);
                AddToScene(_game, baseW, baseH);
                if (GameSoundOnly)
                    AddSystemSound(_game);
                WatchUnhook(_game);
                ShowGuard();
                break;

            default:
                // The game layer's source is made first: libobs ticks sources in the order they were made, so when its
                // game goes (the "unhooked" signal comes in its tick) the screen's tick that same frame starts copying.
                var layer = CreateGameLayerSource();
                _screenLayer = AddToScene(CreateMonitorSource(monitorId), baseW, baseH);
                AddGameLayer(baseW, baseH, layer);
                WatchUnhook(layer);
                break;
        }
        if (_s.Camera.Enabled)
        {
            try
            {
                _camera = new CameraLayer(_scene, _s.Camera, baseW, baseH, CameraClip);
                _camera.FileSaved += path =>
                {
                    CameraSaved?.Invoke(path);
                    if (FreshAfterSave)
                        Task.Run(() =>
                        {
                            lock (_outputsGate)
                                _camera?.StartFresh();
                        });
                };
            }
            catch (InvalidOperationException e)
            {
                ObsLog.Warn($"camera: {e.Message}"); // recording goes on without it
            }
        }
        Obs.obs_set_output_source(ChannelVideo, Obs.obs_scene_get_source(_scene));
    }

    /// <summary>Self-tests on a PC without a camera: a clip that plays as the camera.</summary>
    public string? CameraClip { get; init; }

    /// <summary>The camera's own clip was written (its raw path; the engine puts it beside the main clip).</summary>
    public event Action<string>? CameraSaved;

    /// <summary>The camera's own clip is made with each save.</summary>
    public bool HasCameraFile => _camera?.HasFile == true;

    /// <summary>The camera gives a picture (self-test).</summary>
    public bool CameraHasPicture => _camera?.HasPicture == true;

    /// <summary>The camera is open now (the Status page's mark): while the buffer or a recording runs.</summary>
    public bool CameraOn => _camera?.Active == true;

    /// <summary>The camera's place, size and shape changed in the settings: set on the running scene (no rebuild).</summary>
    public void ApplyCamera(CameraSettings camera) => _camera?.Place(camera);

    /// <summary>
    /// A game capture with no target yet, over the capture and stretched to the canvas as the screen shows the game; it
    /// is targeted only when a game fills the screen (nothing is hooked before).
    /// </summary>
    void AddGameLayer(int baseW, int baseH, IntPtr source)
    {
        _gameLayerSource = source;
        _gameLayer = AddToScene(_gameLayerSource, baseW, baseH);
        if (_gameBand == null)
            Obs.obs_sceneitem_set_bounds_type(_gameLayer, ObsConst.BoundsStretch);
        Obs.obs_sceneitem_set_visible(_gameLayer, false);
    }

    IntPtr CreateGameLayerSource()
    {
        using var d = new ObsData().Set("capture_mode", "window").Set("window", "").Set("priority", 2L)
            .Set("capture_cursor", _s.Capture.CaptureCursor).Set("anti_cheat_hook", true).Set("capture_overlays", false);
        return CreateSource("game_capture", "Game over capture", d);
    }

    /// <summary>Riot's games are left to the screen when the user asked so.</summary>
    bool LeftToScreen(string exe) =>
        _s.Capture.ScreenForRiotGames && AntiCheatGuard.Games.Contains(exe, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Screen and One app, once a second: the game layer takes the game that fills the captured screen (Screen) or the
    /// chosen app's window when it is fullscreen or its capture has no picture (One app), and shows its picture.
    /// </summary>
    void UpdateGameLayer(string? front)
    {
        if (_gameLayerSource == IntPtr.Zero)
            return;
        HoldGameLayer(front != null);
        var (retarget, show) = _overlay.Update(front, Obs.obs_source_get_width(_gameLayerSource) > 0, DateTime.UtcNow);
        if (retarget != null)
        {
            using (var d = new ObsData().Set("window", retarget))
                Obs.obs_source_update(_gameLayerSource, d.Handle);
            if (retarget != _layerLogged)
                ObsLog.Info($"capture: game layer on {App.WindowTarget.ExeName(retarget)}"); // a re-hook every 8 s is not logged
            _layerLogged = retarget;
        }
        bool shown = Obs.obs_sceneitem_visible(_gameLayer);
        if (_mode != CaptureMode.Screen)
        {
            if (show != shown)
                Obs.obs_sceneitem_set_visible(_gameLayer, show);
            return;
        }
        if (show)
            lock (_underGate)
            {
                _layerGeneration++; // a layer still leaving stays
                if (!shown)
                    Obs.obs_sceneitem_set_visible(_gameLayer, true);
            }
        PlaceScreen(gamePicture: show);
        if (!show && shown)
            _ = HideLayerWhenScreenShows(_layerGeneration);
    }

    /// <summary>Screen: bumped each time the game layer shows, which a layer waiting to leave checks.</summary>
    int _layerGeneration;

    /// <summary>
    /// Screen: the game layer leaves once the screen under it gives a picture again (a frame or two after it is copied
    /// again; half a second at most), so an alt-tab out of a game never records a black frame between the two. It looks
    /// on the engine's thread, where the once-a-second look started it.
    /// </summary>
    async Task HideLayerWhenScreenShows(int generation)
    {
        long until = Environment.TickCount64 + 500;
        while (true)
        {
            lock (_underGate)
            {
                if (_screenLayer == IntPtr.Zero || generation != _layerGeneration)
                    return; // torn down, or the game shows again
                if (Environment.TickCount64 >= until || _monitorSources.Any(source => Obs.obs_source_get_width(source) > 0))
                {
                    Obs.obs_sceneitem_set_visible(_gameLayer, false);
                    return;
                }
            }
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The screen under the game capture (<see cref="SceneRules.ScreenUnderGame"/>): hidden, libobs stops copying it (its
    /// Desktop Duplication is let go, not only left out of the picture); shown again, it starts copying on its next tick.
    /// </summary>
    /// <param name="onGraphicsThread">Called by libobs's signal: the log line is written off that thread (a frame waits on it).</param>
    void PlaceScreen(bool gamePicture, bool onGraphicsThread = false)
    {
        bool show = KeepCaptureUnder || SceneRules.ScreenUnderGame(_mode, gamePicture, _s.Capture.GameFallbackToScreen || _guardOn);
        lock (_underGate)
        {
            if (_screenLayer == IntPtr.Zero || Obs.obs_sceneitem_visible(_screenLayer) == show)
                return;
            Obs.obs_sceneitem_set_visible(_screenLayer, show);
        }
        Log(show ? "capture: the screen is copied (no picture from a game)" : "capture: the game gives its picture: the screen is not copied",
            onGraphicsThread);
    }

    /// <summary>
    /// One app: the chosen window's capture (Windows Graphics Capture) goes off while its game, fullscreen in front, gives
    /// its picture through the game layer: hidden, libobs lets the capture go (it copied each frame of the game too).
    /// It comes back when the game is left or its hook stops; the layer stays over it until the window gives a picture
    /// again (the layer is targeted while the window gives none), so leaving the game shows no black frame.
    /// </summary>
    void PlaceWindow(bool gamePicture, bool onGraphicsThread = false)
    {
        bool show = KeepCaptureUnder || !gamePicture;
        lock (_underGate)
        {
            if (_screenLayer == IntPtr.Zero || Obs.obs_sceneitem_visible(_windowItem) == show)
                return;
            Obs.obs_sceneitem_set_visible(_windowItem, show);
        }
        Log(show ? "capture: the window is copied (its game is left or gave no picture)" : "capture: the game gives its picture: the window is not copied",
            onGraphicsThread);
    }

    /// <summary>A line about the captures under a game; from libobs's signal, written off its graphics thread (a frame waits on it).</summary>
    static void Log(string line, bool onGraphicsThread)
    {
        if (onGraphicsThread)
            ThreadPool.QueueUserWorkItem(_ => ObsLog.Info(line));
        else
            ObsLog.Info(line);
    }

    /// <summary>
    /// The game capture lost its game (closed, minimised, its picture made anew): the screen comes back the same moment,
    /// on libobs's graphics thread, not at the next once-a-second look. The game's picture is gone at once and the copy
    /// gives its first picture a frame later: one black frame, at times up to four at 30 fps while starting the copy holds
    /// the graphics thread (measured; OBS's game capture stays black).
    /// </summary>
    void WatchUnhook(IntPtr source)
    {
        _hookSource = source;
        _onUnhooked = (_, _) =>
        {
            if (_mode == CaptureMode.Window)
                PlaceWindow(gamePicture: false, onGraphicsThread: true);
            else
                PlaceScreen(gamePicture: false, onGraphicsThread: true);
        };
        Obs.signal_handler_connect(Obs.obs_source_get_signal_handler(source), "unhooked", _onUnhooked, IntPtr.Zero);
    }

    /// <summary>
    /// Games, once a second: the screen goes off under a hooked game (<see cref="PlaceScreen"/>); a game in front that
    /// gives no picture is asked whether Kairos can open it (<see cref="CannotEnterGame"/>).
    /// </summary>
    public void WatchGame(App.FullscreenApp? front = null)
    {
        if (_mode != CaptureMode.Game)
            return;
        bool hooked = !_guardOn && GameHooked;
        PlaceScreen(gamePicture: hooked);
        if (!hooked && !_guardOn && front != null && GameOverlay.MayHook(front.Exe))
            Hookable(front);
    }

    /// <summary>Games: its capture has a game's picture.</summary>
    public bool GameHooked => _game != IntPtr.Zero && Obs.obs_source_get_width(_game) > 0;

    /// <summary>A game capture gives the picture now (Screen's layer shows, or Games has hooked a game).</summary>
    public bool GameGivesPicture => GameLayerShown || GameHooked;

    /// <summary>
    /// Self-tests: the game layer lets its game go, as when a game makes its picture anew (a new resolution, Alt+Enter):
    /// libobs stops the capture and says "unhooked", the game still open.
    /// </summary>
    internal void DropGameLayerHookForTest()
    {
        using var d = new ObsData().Set("window", "");
        Obs.obs_source_update(_gameLayerSource, d.Handle);
    }

    /// <summary>Self-tests: Games's capture takes this window (a test game parked off-screen is never fullscreen).</summary>
    internal void TargetGameForTest(string window)
    {
        using var d = new ObsData().Set("capture_mode", "window").Set("window", window);
        Obs.obs_source_update(_game, d.Handle);
    }

    /// <summary>Self-tests (--compare-copy): the capture under the game (the screen, One app's window) stays on, as before 1.1.1.</summary>
    internal bool KeepCaptureUnder { get; set; }

    /// <summary>The screen under the game is captured now (self-tests: libobs copies it only while it shows).</summary>
    public bool ScreenCopied => _monitorSources.Any(Obs.obs_source_showing);

    /// <summary>One app: the chosen window is captured now (self-tests).</summary>
    public bool WindowCopied => _window != IntPtr.Zero && Obs.obs_source_showing(_window);

    string? _layerLogged;
    bool _layerHeld;

    /// <summary>
    /// game_capture hooks only while its source is showing, and its item stays hidden until it has a picture: while a
    /// game is targeted the source is held showing, so it hooks; let go when there is none (the hook is dropped).
    /// </summary>
    void HoldGameLayer(bool hold)
    {
        if (hold == _layerHeld)
            return;
        _layerHeld = hold;
        if (hold)
            Obs.obs_source_inc_showing(_gameLayerSource);
        else
            Obs.obs_source_dec_showing(_gameLayerSource);
    }

    /// <summary>The screen captured (Windows's device name), or null for the primary one.</summary>
    public string? CaptureDevice => _captureDevice;

    /// <summary>The game layer shows now.</summary>
    public bool GameLayerShown => _gameLayer != IntPtr.Zero && Obs.obs_sceneitem_visible(_gameLayer);

    /// <summary>Screen, once a second: a game that fills the captured screen is captured from the game (no black).</summary>
    public void WatchScreenGame(App.FullscreenApp? front)
    {
        if (_mode != CaptureMode.Screen)
            return;
        bool here = front != null && (_captureDevice == null || front.Monitor.Equals(_captureDevice, StringComparison.OrdinalIgnoreCase))
                    && !LeftToScreen(front.Exe) && GameOverlay.MayHook(front.Exe) && Hookable(front);
        UpdateGameLayer(here ? front!.Window : null);
    }

    /// <summary>The front game last asked about, and whether libobs can open it (<see cref="Hookable"/>).</summary>
    (uint Pid, bool Ok) _hookable;
    readonly HashSet<string> _toldNotHookable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A game in front that Kairos cannot open (its exe and its window's title), once a game a pipeline.</summary>
    public event Action<string, string>? CannotEnterGame;

    /// <summary>
    /// A game run as administrator (or guarded by its anti-cheat) cannot be hooked by Kairos: libobs tried every 2 s for
    /// ever (a user's log: 6,000 "could not open process" lines for Genshin Impact, Star Rail, Wuthering Waves and
    /// Endfield). It is left to the screen capture, which records it as before; asked once a process, said once a game.
    /// </summary>
    bool Hookable(App.FullscreenApp front)
    {
        if (_hookable.Pid != front.Pid)
            _hookable = (front.Pid, App.ProcessList.CanBeHooked(front.Pid));
        if (!_hookable.Ok && _toldNotHookable.Add(front.Exe))
        {
            ObsLog.Info($"capture: {front.Exe} cannot be hooked (run as administrator, or guarded by its anti-cheat): the screen records it");
            CannotEnterGame?.Invoke(front.Exe, App.WindowTarget.Title(front.Window) ?? "");
        }
        return _hookable.Ok;
    }

    IntPtr CreateMonitorSource(string monitorId)
    {
        // Kairos records SDR (NV12, Rec. 709): with Force SDR Windows hands an HDR screen over already in SDR, where the
        // HDR copy recorded black on some HDR monitors (a 4K HDR screen, DXGI, 2026-10-18).
        using var d = new ObsData().Set("monitor_id", monitorId).Set("method", ScreenMethod).Set("capture_cursor", _s.Capture.CaptureCursor)
            .Set("force_sdr", true);
        IntPtr source = CreateSource("monitor_capture", "Screen", d);
        _monitorSources.Add(source);
        return source;
    }

    /// <summary>libobs's screen capture method: 0 Automatic (DXGI on the screen's own GPU), 2 Windows Graphics Capture.</summary>
    long ScreenMethod => _screenWgc ? 2L : 0L;

    /// <summary>The scene's picture is the screen's (Full screen, or Games with no game hooked): what the black check reads.</summary>
    public bool ShowsScreen => BuiltMode is CaptureMode.Screen or CaptureMode.Game;

    /// <summary>Switches the screen captures to Windows Graphics Capture (or back), with no rebuild: the replay carries on.</summary>
    public void UseWgcForScreen(bool on)
    {
        _screenWgc = on;
        foreach (IntPtr source in _monitorSources)
            using (var d = new ObsData().Set("method", ScreenMethod))
                Obs.obs_source_update(source, d.Handle);
        ObsLog.Info($"screen capture: method {(on ? "Windows Graphics Capture" : "Automatic")}");
    }

    /// <summary>All PC sound together with "game sound only": the game capture's own sound is the system track.</summary>
    bool GameSoundOnly => _s.Audio.Layout == AudioLayout.Together && _s.Audio.SourceAudioOnly;

    /// <summary>The track names written into the saved files (<see cref="Mp4TrackNames"/>).</summary>
    public IReadOnlyList<string> TrackNames => _plan.Names;

    /// <summary>
    /// Separate sources: a source per row (an app row waits for its process: <see cref="UpdateAudioTargets"/>), each in
    /// the mix and its own or the shared track. Other sounds come and go with their processes.
    /// </summary>
    void BuildSeparateAudio()
    {
        foreach (var row in _s.Audio.Sources)
        {
            IntPtr source = row.Kind switch
            {
                AudioSourceKind.Microphone => InputSource(_s.Audio.MicDevice, "Microphone"),
                AudioSourceKind.Input => InputSource(row.Target, TrackPlanner.Name(row)),
                AudioSourceKind.Output => DeviceSource("wasapi_output_capture", row.Target, TrackPlanner.Name(row)), // as Windows sends it, before a mixer's faders
                AudioSourceKind.OtherSounds => IntPtr.Zero,
                _ => ProcessSource(0, TrackPlanner.Name(row)),
            };
            if (source == IntPtr.Zero)
                continue;
            Route(source, row);
            _rowSources[row.Id] = source;
            if (row.Kind == AudioSourceKind.Microphone)
            {
                _micSource = source;
                AttachVoice(source);
            }
        }
    }

    IntPtr InputSource(string device, string name) => DeviceSource("wasapi_input_capture", device, name);

    IntPtr DeviceSource(string kind, string device, string name)
    {
        using var d = new ObsData().Set("device_id", string.IsNullOrEmpty(device) ? "default" : device);
        return CreateSource(kind, name, d);
    }

    IntPtr ProcessSource(uint pid, string name)
    {
        using var d = new ObsData().Set("pid", (long)pid);
        return CreateSource(ProcessAudioSource.Id, name, d);
    }

    /// <summary>A row's source into its tracks at the row's volume and mute as they are now, on a free output channel.</summary>
    void Route(IntPtr source, AudioSource row)
    {
        Obs.obs_source_set_audio_mixers(source, _plan.MixersFor(row.Id));
        var (volume, muted) = _live.For(row.Id);
        Obs.obs_source_set_volume(source, (float)volume);
        Obs.obs_source_set_muted(source, muted);
        for (uint channel = 1; channel < 64; channel++)
            if (!_channels.ContainsValue(channel))
            {
                _channels[source] = channel;
                Obs.obs_set_output_source(channel, source);
                return;
            }
        ObsLog.Warn("audio: no free output channel");
    }

    /// <summary>
    /// Separate sources, every 2 s: the processes each row captures now (<see cref="AudioPartition.Assign"/>). An app row
    /// moves to its new process; Other sounds gains and loses processes. No rebuild: the replay carries on.
    /// </summary>
    /// <param name="heardOn">The named rows heard on a device row instead (<see cref="EngineStatus.AudioRowsHeardOn"/>).</param>
    public void UpdateAudioTargets(IReadOnlyDictionary<string, uint[]> assignment, IReadOnlyDictionary<string, string>? heardOn = null)
    {
        if (_s.Audio.Layout != AudioLayout.Separate)
            return;
        _heardOn = heardOn;
        foreach (var row in _s.Audio.Sources)
        {
            if (row.Kind.IsDevice() || row.Kind == AudioSourceKind.OtherSounds
                || !_rowSources.TryGetValue(row.Id, out IntPtr source))
                continue;
            uint pid = assignment.TryGetValue(row.Id, out uint[]? pids) && pids.Length > 0 ? pids[0] : 0;
            _rowPids[row.Id] = pid;
            // Every tick, the same pid included: a capture that failed to start (a game still loading) tries again.
            using var d = new ObsData().Set("pid", (long)pid);
            Obs.obs_source_update(source, d.Handle);
        }

        var other = _s.Audio.Sources.FirstOrDefault(r => r.Kind == AudioSourceKind.OtherSounds);
        var wanted = other != null && assignment.TryGetValue(other.Id, out uint[]? others) ? others.ToHashSet() : [];
        foreach (uint gone in _otherSources.Keys.Where(pid => !wanted.Contains(pid)).ToList())
        {
            IntPtr source = _otherSources[gone];
            _otherSources.Remove(gone);
            UnmeterSource(source);
            if (_channels.Remove(source, out uint channel))
                Obs.obs_set_output_source(channel, IntPtr.Zero);
            _sources.Remove(source);
            Obs.obs_source_remove(source);
            Obs.obs_source_release(source);
        }
        if (other == null)
            return;
        foreach (uint pid in wanted.Where(pid => !_otherSources.ContainsKey(pid)))
        {
            IntPtr source = ProcessSource(pid, "Other sounds");
            Route(source, other);
            _otherSources[pid] = source;
            MeterSource(source, other.Id); // only while the meters run
        }
    }

    /// <summary>Other sounds' sources as libobs has them: volume and mute (the self-test checks a new one takes the live values).</summary>
    public IReadOnlyList<(float Volume, bool Muted)> OtherSourceStates() =>
        [.. _otherSources.Values.Select(s => (Obs.obs_source_get_volume(s), Obs.obs_source_muted(s)))];

    /// <summary>The rows that hear something now: an app row with its process, a device, Other sounds with any process.</summary>
    public IReadOnlyList<string> CapturingRows => SeparateRows(capturing: true);

    /// <summary>The rows this engine captures that hear nothing now (<see cref="EngineStatus.QuietAudioRows"/>).</summary>
    public IReadOnlyList<string> QuietRows => SeparateRows(capturing: false);

    IReadOnlyDictionary<string, string>? _heardOn;

    List<string> SeparateRows(bool capturing) => _s.Audio.Layout != AudioLayout.Separate ? []
        : _s.Audio.Sources.Where(r => capturing == r.Kind switch
        {
            _ when r.Kind.IsDevice() => _rowSources.ContainsKey(r.Id),
            AudioSourceKind.OtherSounds => _otherSources.Count > 0,
            _ => _rowPids.GetValueOrDefault(r.Id) != 0,
        }).Select(r => r.Id).ToList();

    void BuildAudio()
    {
        var a = _s.Audio;
        bool appOnly = a.SourceAudioOnly && _mode == CaptureMode.Window;
        bool gameOnly = a.SourceAudioOnly && _mode == CaptureMode.Game;

        if (appOnly)
        {
            // Every chosen app's sound together (the protected games' channels are Games mode's alone).
            var apps = _s.Capture.Apps;
            for (int i = 0; i < apps.Count; i++)
            {
                IntPtr app = CreateAppAudio(apps[i], $"App audio {i + 1}");
                AddSystemSound(app);
                Obs.obs_set_output_source(ChannelExtraSound + (uint)i, app);
            }
        }
        else if (a.System && !gameOnly)
        {
            using var d = new ObsData().Set("device_id", a.SystemDevice);
            IntPtr desktop = CreateSource("wasapi_output_capture", "Desktop audio", d);
            AddSystemSound(desktop);
            Obs.obs_set_output_source(ChannelDesktop, desktop);
        }

        // Smart Save needs the microphone even when it is not recorded; then it joins no audio track.
        if (a.Mic || (_voice != null && _s.Voice.Enabled))
        {
            IntPtr mic = InputSource(a.MicDevice, "Microphone");
            _micSource = mic;
            Obs.obs_source_set_audio_mixers(mic, _plan.MixersFor("mic"));
            AttachMeter(mic, _micLevel);
            Obs.obs_set_output_source(ChannelMic, mic);
            AttachVoice(mic);
        }
    }

    /// <summary>How many sources feed the system-sound track (for the self-test).</summary>
    internal int SystemSoundSources => _systemSources.Count;

    /// <summary>The volume libobs has on the system (or app, or game) sound; -1 without one. For the self-test.</summary>
    internal float SystemSourceVolume => _systemSources.Count == 0 ? -1 : Obs.obs_source_get_volume(_systemSources[0]);

    /// <summary>Sets the volumes on the sources, when they are built and on a change (no rebuild: the replay is kept).</summary>
    public void ApplyLive(LiveAudio live)
    {
        _live = live;
        if (_s.Audio.Layout == AudioLayout.Separate)
        {
            foreach (var row in live.Rows ?? [])
                foreach (IntPtr source in SourcesOf(row.Id))
                {
                    Obs.obs_source_set_volume(source, (float)LiveAudio.Limit(row.Volume));
                    Obs.obs_source_set_muted(source, row.Muted);
                }
            return;
        }
        foreach (IntPtr source in _systemSources)
            Obs.obs_source_set_volume(source, (float)LiveAudio.Limit(live.SystemVolume));
        if (_micSource != IntPtr.Zero)
            Obs.obs_source_set_volume(_micSource, (float)LiveAudio.Limit(live.MicVolume));
    }

    /// <summary>Separate sources: a row's sources now (Other sounds: one per process).</summary>
    IEnumerable<IntPtr> SourcesOf(string rowId) =>
        _s.Audio.Sources.FirstOrDefault(r => r.Id == rowId)?.Kind == AudioSourceKind.OtherSounds ? _otherSources.Values
        : _rowSources.TryGetValue(rowId, out IntPtr one) ? [one] : [];

    void AttachVoice(IntPtr mic)
    {
        if (_voice == null)
            return;
        var voice = _voice;
        // Muted from the recording (the Microphone row's switch) is not muted for Smart Save: it still listens.
        _micTap = (_, _, audioData, _) =>
        {
            if (voice.Listening)
                unsafe
                {
                    var data = (ObsAudioData*)audioData;
                    voice.Feed(new ReadOnlySpan<float>((void*)data->Data0, (int)data->Frames));
                }
        };
        Obs.obs_source_add_audio_capture_callback(mic, _micTap, IntPtr.Zero);
        voice.SetMicConnected(true);
    }

    /// <summary>Feeds the loudest channel of each libobs meter update into <paramref name="level"/>.</summary>
    void AttachMeter(IntPtr source, LevelMeter level)
    {
        if (NewMeter(source, level) is { } meter)
            _meters.Add(meter);
    }

    /// <summary>A libobs meter on <paramref name="source"/> feeding <paramref name="level"/> (null when libobs has none).</summary>
    static (IntPtr Meter, ObsVolmeterCallback Callback)? NewMeter(IntPtr source, LevelMeter level)
    {
        IntPtr meter = Obs.obs_volmeter_create(1 /* OBS_FADER_IEC */);
        if (meter == IntPtr.Zero)
            return null;
        ObsVolmeterCallback callback = (_, _, peak, _) => level.Add(LoudestChannel(peak));
        Obs.obs_volmeter_add_callback(meter, callback, IntPtr.Zero);
        Obs.obs_volmeter_attach_source(meter, source);
        return (meter, callback);
    }

    static void FreeMeter((IntPtr Meter, ObsVolmeterCallback Callback) m)
    {
        Obs.obs_volmeter_remove_callback(m.Meter, m.Callback, IntPtr.Zero);
        Obs.obs_volmeter_detach_source(m.Meter);
        Obs.obs_volmeter_destroy(m.Meter);
    }

    static unsafe float LoudestChannel(IntPtr peak)
    {
        float* p = (float*)peak;
        return Math.Max(p[0], p[1]);
    }

    /// <summary>
    /// "Game sound only": the guard keeps the hook, and so its sound, out of the protected games, so their own sound (the
    /// app's sound, no hook) joins the track instead. Made the first time the guard turns on, kept until the next build.
    /// </summary>
    void AddProtectedGameSound()
    {
        if (!GameSoundOnly || _gameSoundAdded)
            return;
        _gameSoundAdded = true;
        for (int i = 0; i < AntiCheatGuard.GameSoundWindows.Length; i++)
        {
            IntPtr sound = CreateAppAudio(AntiCheatGuard.GameSoundWindows[i], "Protected game audio");
            AddSystemSound(sound);
            Obs.obs_source_set_volume(sound, (float)LiveAudio.Limit(_live.SystemVolume));
            Obs.obs_set_output_source(ChannelExtraSound + (uint)i, sound);
        }
    }

    bool _gameSoundAdded;

    /// <summary>A source of the system-sound track: its mixers, the system meter, and the volume and save-sound mute.</summary>
    void AddSystemSound(IntPtr source)
    {
        Obs.obs_source_set_audio_mixers(source, _plan.MixersFor("system"));
        AttachMeter(source, _systemLevel);
        _systemSources.Add(source);
    }

    /// <summary>One app's own sound (its child processes' too), found by its exe whatever its window's title says.</summary>
    IntPtr CreateAppAudio(string window, string name)
    {
        using var d = new ObsData().Set("window", window).Set("priority", 2L);
        return CreateSource("wasapi_process_output_capture", name, d);
    }

    /// <summary>MP4 codec tag "hvc1" (big-endian, as obs-ffmpeg-mux expects) so Apple players accept HEVC clips.</summary>
    const long Hvc1Tag = 0x68766331;

    /// <summary>A video encoder of the chosen kind for <paramref name="plan"/> (its picture is set by the caller).</summary>
    IntPtr CreateVideoEncoder(EncoderPlan plan, string name)
    {
        var choice = Choice!;
        using var d = new ObsData();
        foreach (var (key, value) in EncoderCatalog.Settings(plan, choice, light: Relief >= LagRelief.LightEncoder))
        {
            switch (value)
            {
                case string str: d.Set(key, str); break;
                case long n: d.Set(key, n); break;
                case bool b: d.Set(key, b); break;
            }
        }
        if (choice.Codec == VideoCodec.Hevc)
            d.Set("codec_type", Hvc1Tag);
        IntPtr encoder = Obs.obs_video_encoder_create(choice.Id, name, d.Handle, IntPtr.Zero);
        if (encoder == IntPtr.Zero)
            throw new InvalidOperationException($"Could not create encoder {choice.Id}");
        return encoder;
    }

    void BuildEncoders()
    {
        var choice = Choice!;
        Plan = EncoderPresets.Plan(choice.Codec, _video.Quality, Output.W, Output.H, Fps,
            _video.CustomCq, _video.CustomMbps, choice.Vendor != EncoderVendor.Software, _video.Preset);
        _videoEncoder = CreateVideoEncoder(Plan, "Kairos video");
        Obs.obs_encoder_set_video(_videoEncoder, Obs.obs_get_video());

        foreach (var track in _plan.Tracks)
        {
            using var d = new ObsData().Set("bitrate", (long)_s.Audio.BitrateKbps);
            IntPtr enc = Obs.obs_audio_encoder_create("ffmpeg_aac", track.Name, d.Handle, (nuint)track.Mixer, IntPtr.Zero);
            if (enc == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the AAC encoder");
            Obs.obs_encoder_set_audio(enc, Obs.obs_get_audio());
            _audioEncoders.Add(enc);
        }
    }

    IntPtr CreateReplayOutput(string name, string format, int seconds, DiskRing? disk = null, bool main = false)
    {
        IntPtr output = NewReplayHere(name, format, seconds, disk, main);
        AttachEncoders(output);
        return output;
    }

    // ------------------------------------------------------------------ the replay on the disk

    /// <summary>
    /// A ring file for a replay output on the disk: its path and size, and the longest clip asked of it; or none, and the
    /// output takes its clips from <see cref="Share"/>'s ring.
    /// </summary>
    internal sealed record DiskRing(string? Path, long Bytes, double KeepSeconds, string? Share = null);

    /// <summary>The main replay is kept on the disk this run (asked for, the drive had room, its ring was made).</summary>
    public bool ReplayOnDisk => _diskFolder != null;

    /// <summary>The ring files' folder this run, or null (the replay in RAM).</summary>
    public string? DiskFolder => _diskFolder;

    /// <summary>The replay was asked for on the disk but is in RAM this run because its ring could not be made or written.</summary>
    public bool DiskFellBack { get; private set; }

    /// <summary>A ring could not be written while recording: the engine rebuilds with the replay in RAM.</summary>
    public event Action? DiskFailed;

    /// <summary>A clip on the disk could not be written (said at once, not after the write limit).</summary>
    public event Action? SaveFailed;

    string? _diskFolder;
    long _mainRingBytes, _cameraRingBytes;
    double _keepSeconds;
    Action? _onDiskFailed;
    ObsSignalCallback? _onSaveFailed;

    /// <summary>
    /// The replay on the disk: its folder and ring sizes, when asked for and a fixed drive has room for them and 1 GB more
    /// (the files already there count: they are reused). Otherwise RAM, and <see cref="DiskFellBack"/>.
    /// </summary>
    void PlanDisk(bool cameraFile)
    {
        _diskFolder = null;
        DiskFellBack = false;
        if (!_s.Replay.OnDisk)
            return;
        if (DiskOff)
        {
            DiskFellBack = true;
            return;
        }
        // The quick clip comes from the main ring: it holds the longer of the two.
        _keepSeconds = Math.Max(_s.Replay.Seconds, _s.Replay.QuickEnabled ? _s.Replay.QuickSeconds : 0);
        int seconds = (int)Math.Ceiling(_keepSeconds);
        int soundKbps = _s.Audio.BitrateKbps * Math.Max(1, _audioEncoders.Count);
        _mainRingBytes = DiskReplayPlace.RingBytes(Plan!.MaxBitrateKbps + soundKbps, seconds);
        _cameraRingBytes = cameraFile ? DiskReplayPlace.RingBytes(CameraPlan().MaxBitrateKbps + _s.Audio.BitrateKbps, seconds) : 0;
        string folder = DiskReplayPlace.FolderFor(_s.Replay.DiskDrive, ClipsFolder);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            long reused = DiskReplayPlace.Files.Select(f => Path.Combine(folder, f)).Where(File.Exists).Sum(f => new FileInfo(f).Length);
            long needed = DiskReplayPlace.NeededBytes(_mainRingBytes + _cameraRingBytes);
            if (drive.DriveType != DriveType.Fixed)
                ObsLog.Warn($"replay on the disk: {drive.Name} is not a fixed drive (a USB stick or the network): the replay stays in RAM");
            else if (!drive.IsReady || drive.AvailableFreeSpace + reused < needed)
                ObsLog.Warn($"replay on the disk: {drive.Name} has {drive.AvailableFreeSpace >> 20} MB free, {needed >> 20} MB needed: the replay stays in RAM");
            else
            {
                _diskFolder = folder;
                _onDiskFailed = () => DiskFailed?.Invoke();
                DiskReplayOutput.Failed += _onDiskFailed;
                return;
            }
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            ObsLog.Warn($"replay on the disk: {folder}: {e.Message}: the replay stays in RAM");
        }
        // Too little room or no fixed drive: RAM, said in the log only (the disk is the default: most never chose it).
        // A ring that cannot be made or written is what Status tells (DiskFellBack).
    }

    /// <summary>The camera file's encoder plan (its own size and rate, High).</summary>
    EncoderPlan CameraPlan(int w = CameraLayer.FileWidth, int h = CameraLayer.FileHeight, int fps = 0) =>
        EncoderPresets.Plan(Choice!.Codec, QualityPreset.High, w, h, fps > 0 ? fps : Fps / (int)CameraMath.FileFpsDivisor(Fps),
            _video.CustomCq, _video.CustomMbps, Choice.Vendor != EncoderVendor.Software, _video.Preset);

    /// <summary>This run keeps the replay in RAM whatever the settings say (a ring failed while recording).</summary>
    public bool DiskOff { get; set; }

    /// <summary>
    /// A replay output in the clips folder: on the disk when <paramref name="disk"/> is given and its ring could be made
    /// (or the ring it shares exists), else in RAM. The main replay in RAM takes the camera's and the quick clip's along.
    /// </summary>
    IntPtr NewReplayHere(string name, string format, int seconds, DiskRing? disk, bool main = false)
    {
        if (disk != null)
        {
            using var d = new ObsData().Set("directory", ClipsFolder).Set("format", format).Set("seconds", (long)seconds)
                .Set("ring_path", disk.Path ?? "").Set("ring_bytes", disk.Bytes).Set("keep_seconds", (long)Math.Ceiling(disk.KeepSeconds))
                .Set("share", disk.Share ?? "");
            IntPtr output = Obs.obs_output_create(DiskReplayOutput.Id, name, d.Handle, IntPtr.Zero);
            if (output != IntPtr.Zero && DiskReplayOutput.HasRing(disk.Share ?? name))
            {
                _onSaveFailed ??= (_, _) =>
                {
                    _saving = false;
                    SaveFailed?.Invoke();
                };
                Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(output), "save_failed", _onSaveFailed, IntPtr.Zero);
                return output;
            }
            // libobs makes the output even when its create failed: let it go.
            if (output != IntPtr.Zero)
                Obs.obs_output_release(output);
            ObsLog.Warn($"replay on the disk: {name} could not be made: in RAM");
            if (main)
            {
                _diskFolder = null;
                DiskFellBack = true;
            }
        }
        return NewReplay(name, ClipsFolder, format, seconds, ReplayMb(seconds));
    }

    /// <summary>The memory a replay of <paramref name="seconds"/> may hold at the main encoder's top bitrate.</summary>
    int ReplayMb(int seconds) =>
        BufferBudget.CapMb(EncoderPresets.EstimateBufferMb(Plan!.MaxBitrateKbps, seconds), BufferBudget.PhysicalMemoryBytes);

    /// <summary>A replay buffer writing "<paramref name="format"/>.mp4" files into <paramref name="folder"/> (its encoders set by the caller).</summary>
    internal static IntPtr NewReplay(string name, string folder, string format, int seconds, int maxMb)
    {
        using var d = new ObsData()
            .Set("directory", folder).Set("format", format).Set("extension", "mp4").Set("allow_spaces", true)
            .Set("max_time_sec", (long)seconds).Set("max_size_mb", (long)maxMb);
        IntPtr output = Obs.obs_output_create("replay_buffer", name, d.Handle, IntPtr.Zero);
        if (output == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the replay buffer");
        return output;
    }

    /// <summary>A recording's muxer: written in fragments, so a recording cut short still plays.</summary>
    internal const string MuxerSettings = "movflags=frag_keyframe+empty_moov+delay_moov";

    /// <summary>The file a replay buffer wrote last, with Windows's separators; null when it says none.</summary>
    internal static string? LastReplayPath(IntPtr output)
    {
        string? path = null;
        var cd = new CallData();
        if (Obs.proc_handler_call(Obs.obs_output_get_proc_handler(output), "get_last_replay", ref cd) &&
            Obs.calldata_get_string(ref cd, "path", out IntPtr p))
            path = Obs.Utf8(p);
        Obs.FreeCallData(ref cd);
        return path?.Replace('/', '\\');
    }

    /// <summary>Asks a replay buffer to write what it holds; false when it would not.</summary>
    internal static bool CallSave(IntPtr output)
    {
        var cd = new CallData();
        bool ok = Obs.proc_handler_call(Obs.obs_output_get_proc_handler(output), "save", ref cd);
        Obs.FreeCallData(ref cd);
        return ok;
    }

    void AttachEncoders(IntPtr output)
    {
        Obs.obs_output_set_video_encoder(output, _videoEncoder);
        for (int i = 0; i < _audioEncoders.Count; i++)
            Obs.obs_output_set_audio_encoder(output, _audioEncoders[i], (nuint)i);
    }

    IntPtr CreateSource(string id, string name, ObsData settings)
    {
        IntPtr source = Obs.obs_source_create(id, name, settings.Handle, IntPtr.Zero);
        if (source == IntPtr.Zero)
            throw new InvalidOperationException($"Could not create source {id}");
        _sources.Add(source);
        return source;
    }

    IntPtr AddToScene(IntPtr source, int w, int h)
    {
        IntPtr item = Obs.obs_scene_add(_scene, source);
        Obs.obs_sceneitem_set_bounds_type(item, ObsConst.BoundsScaleInner);
        Obs.obs_sceneitem_set_bounds_alignment(item, ObsConst.AlignCenter);
        var bounds = new Vec2(w, h);
        var pos = new Vec2(0, 0);
        if (_gameBand is { } band)
        {
            // 9:16: the picture covers the band under the camera, its middle kept.
            Obs.obs_sceneitem_remove(item);
            item = CameraLayer.AddCovering(_scene, source);
            bounds = new Vec2(band.W, band.H);
            pos = new Vec2(band.X, band.Y);
        }
        Obs.obs_sceneitem_set_bounds(item, ref bounds);
        Obs.obs_sceneitem_set_pos(item, ref pos);
        return item;
    }

    /// <summary>The chosen app's window recorded now ("title:class:exe"), null in the other modes.</summary>
    public string? CapturedWindow => _mode == CaptureMode.Window ? _target : null;

    /// <summary>Whether a window string's app is among <paramref name="open"/> (<see cref="App.WindowTarget.OpenApps"/>).</summary>
    static Func<string, bool> Open(HashSet<string> open) => w => App.WindowTarget.ExeName(w) is { } exe && open.Contains(exe);

    /// <summary>
    /// Apps, once a second: the chosen app in front (<paramref name="frontExe"/>) is recorded, else the one recorded so far
    /// while it is open; the screen shows while none is open or its window gives no picture. A minimized window keeps its
    /// last picture: showing the desktop instead would record other windows.
    /// </summary>
    public void WatchWindow(App.FullscreenApp? front = null, string? frontExe = null)
    {
        if (_mode != CaptureMode.Window)
            return;
        var isOpen = Open(App.WindowTarget.OpenApps(_s.Capture.Apps)); // one look at the processes and windows a tick
        if (AppTargets.Pick(_s.Capture.Apps, frontExe, isOpen, _target) is { } next && next != _target)
        {
            _target = next;
            using (var d = new ObsData().Set("window", next))
                Obs.obs_source_update(_window, d.Handle);
            ObsLog.Info($"capture: now recording {App.WindowTarget.ExeName(next)}");
        }
        bool open = isOpen(_target);
        bool windowPicture = open && Obs.obs_source_get_width(_window) > 0;
        string? exe = App.WindowTarget.ExeName(_target);
        bool fullscreen = front != null && exe != null && front.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase);
        // As in Screen mode, never a hook into a browser, a player or the shell (window capture shows them), nor into a
        // game that cannot be opened (the window capture records it).
        UpdateGameLayer(open && (fullscreen || !windowPicture) && exe != null && !LeftToScreen(exe) && GameOverlay.MayHook(exe)
                        && (!fullscreen || Hookable(front!)) ? _target : null);
        PlaceWindow(gamePicture: fullscreen && GameLayerShown);
        bool hasPicture = windowPicture || (open && GameLayerShown);
        if (!_presence.Update(hasPicture))
            return;
        Obs.obs_sceneitem_set_visible(_screenLayer, _presence.ShowScreen);
        CaptureNotice = _presence.ShowScreen ? "status.captureGone" : null;
        ObsLog.Info(_presence.ShowScreen ? "capture: the chosen window gives no picture, recording the screen" : "capture: the chosen window is back");
    }

    /// <summary>
    /// Games, every 2 s: whether an anti-cheat that forbids hooking games runs. Only the capture mode changes: no rebuild,
    /// so the replay and a recording carry on.
    /// </summary>
    public void SetGuard(bool on)
    {
        on &= _s.Capture.ScreenForRiotGames; // Riot's games are hooked like any game unless the user asked otherwise
        if (_mode != CaptureMode.Game || on == _guardOn)
            return;
        _guardOn = on;
        using (var d = new ObsData().Set("capture_mode", GameCaptureMode(on)).Set("window", ""))
            Obs.obs_source_update(_game, d.Handle);
        ShowGuard();
        if (!on)
            ObsLog.Info("anti-cheat guard off: games are captured again");
    }

    /// <summary>The screen under the game shows to fall back to it, or while the guard is on, which the Status page says.</summary>
    void ShowGuard()
    {
        if (_guardOn)
            AddProtectedGameSound();
        WatchGame();
        CaptureNotice = _guardOn ? "status.antiCheat" : null;
        if (_guardOn)
            ObsLog.Info("anti-cheat guard on: recording the screen, no game hook");
    }

    /// <summary>The game capture's mode: any full-screen game, or (guarded) a window that is never named, so nothing is hooked.</summary>
    static string GameCaptureMode(bool guarded) => guarded ? "window" : "any_fullscreen";

    /// <summary>Starts or stops the replay buffer(s). Returns whether the buffer is active afterwards.</summary>
    public bool SetBufferActive(bool on)
    {
        lock (_outputsGate)
            return SetBufferActiveLocked(on);
    }

    bool SetBufferActiveLocked(bool on)
    {
        if (!on)
            BufferError = null; // off, paused or waiting for a game (also after a failed start): not an error any more
        if (on == BufferActive)
            return on;
        if (!on)
        {
            Obs.obs_output_stop(_replay);
            if (_quick != IntPtr.Zero)
                Obs.obs_output_stop(_quick);
            _camera?.SetBufferActive(false);
            _camera?.SetActive(Recording); // the buffer is still stopping: BufferActive may say true a moment longer
            return false;
        }

        if (FailsForTest() || !Obs.obs_output_start(_replay))
        {
            BufferError = FailsForTest() ? "Simulated encoder failure (self-test)"
                : Obs.Utf8(Obs.obs_output_get_last_error(_replay)) ?? "The replay buffer could not start";
            ObsLog.Error($"replay start failed: {BufferError}");
            return false;
        }
        if (_quick != IntPtr.Zero && !Obs.obs_output_start(_quick))
            ObsLog.Warn("quick buffer could not start");
        _camera?.SetBufferActive(true);
        _camera?.SetActive(true);
        CpuPriority.RaiseVideoThreadsSoon();
        BufferError = null;
        _bufferStartedUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>Requests a save. False when inactive, too early, or a save is already running.</summary>
    public bool SaveReplay(bool quick) => RequestSave(quick) == SaveWatch.Request.Started;

    /// <summary>What a buffer holds now, about (its bitrate times the seconds filled): how long writing it may take.</summary>
    public long BufferBytes => Plan == null || !BufferActive ? 0
        : (long)EncoderPresets.EstimateBufferMb(Plan.MaxBitrateKbps,
            (int)Math.Min(_s.Replay.Seconds, (DateTime.UtcNow - _bufferStartedUtc).TotalSeconds)) * 1024 * 1024;

    /// <summary>
    /// Requests a save and says what happened: started, off, just starting, or one is already being written. On the
    /// disk the clip is written straight to <paramref name="target"/> (its app's folder and name) when given.
    /// </summary>
    public SaveWatch.Request RequestSave(bool quick, string? target = null)
    {
        IntPtr output = quick ? _quick : _replay;
        if (output == IntPtr.Zero || !Obs.obs_output_active(output))
            return SaveWatch.Request.Off;
        DateTime now = DateTime.UtcNow;
        if (now - _bufferStartedUtc < MinBufferBeforeSave)
            return SaveWatch.Request.Starting;
        if (_saving && now - _saveRequestedUtc < SaveWatch.WriteLimit(BufferBytes))
            return SaveWatch.Request.Busy;

        _saving = true;
        _saveRequestedUtc = now;
        if (target != null)
            DiskReplayOutput.PlaceNext(output, target);
        bool ok = CallSave(output);
        if (!ok)
            _saving = false;
        else if (!quick)
            _camera?.Save(); // the camera's own clip, the same moment
        return ok ? SaveWatch.Request.Started : SaveWatch.Request.Off;
    }

    /// <summary>
    /// "Start fresh after each save" (live: no rebuild): once a clip is written its buffers start empty again, so the next
    /// clip never repeats it; the camera's own buffer once its own clip is written.
    /// </summary>
    public bool FreshAfterSave { get; set; }

    /// <summary>The outputs are started, stopped and let go one at a time: the UI thread, and a fresh start off it.</summary>
    readonly object _outputsGate = new();

    /// <summary>The replay and the quick buffer start empty again, together, off libobs's thread (a stop waits on it).</summary>
    void StartFresh()
    {
        lock (_outputsGate)
        {
            IntPtr[] running = [.. new[] { _replay, _quick }.Where(o => o != IntPtr.Zero && Obs.obs_output_active(o))];
            foreach (IntPtr output in running)
                Obs.obs_output_force_stop(output);
            foreach (IntPtr output in running)
            {
                WaitInactive(output, 1000);
                if (!Obs.obs_output_start(output))
                    ObsLog.Warn($"replay could not start again: {Obs.Utf8(Obs.obs_output_get_last_error(output))}");
            }
            if (running.Length > 0)
            {
                _bufferStartedUtc = DateTime.UtcNow;
                ObsLog.Info("replay: started fresh after the save");
                CpuPriority.RaiseVideoThreadsSoon();
            }
        }
    }

    /// <summary>Self-tests only: encoders made to fail as the buffer starts ("id", or "id:bframes": only with B-frames).</summary>
    internal static IReadOnlySet<string> FailingEncoders { get; set; } = new HashSet<string>();

    bool FailsForTest() => FailingEncoders.Count > 0 && Choice is { } c
        && (FailingEncoders.Contains(c.Id) || (!c.NoBFrames && Plan?.BFrames > 0 && FailingEncoders.Contains(c.Id + ":bframes")));

    /// <summary>A replay buffer emptied: stopped at once (what it held is dropped) and started again.</summary>
    internal static void Restart(IntPtr output, string what)
    {
        Obs.obs_output_force_stop(output);
        WaitInactive(output, 1000);
        if (!Obs.obs_output_start(output))
            ObsLog.Warn($"{what} could not start again: {Obs.Utf8(Obs.obs_output_get_last_error(output))}");
        CpuPriority.RaiseVideoThreadsSoon();
    }

    /// <summary>Waits up to <paramref name="ms"/> for a stopping output to finish.</summary>
    static void WaitInactive(IntPtr output, int ms)
    {
        for (int waited = 0; waited < ms && Obs.obs_output_active(output); waited += 25)
            Thread.Sleep(25);
    }

    void OnSaved(IntPtr output, bool quick)
    {
        string? path = LastReplayPath(output);
        _saving = false;
        if (path != null)
            ReplaySaved?.Invoke(path, quick, TrackNames);
        if (FreshAfterSave)
            Task.Run(StartFresh);
    }

    /// <summary>
    /// Starts a recording into <paramref name="target"/> (Kairos names it like a clip, in the app's folder), or stops the
    /// one running. Returns whether recording is active afterwards.
    /// </summary>
    public bool ToggleRecording(string? target = null)
    {
        if (Recording)
        {
            _recordClock.Pause(DateTime.UtcNow); // its length is what was recorded, not the time the file takes to close
            Obs.obs_output_stop(_record);
            _camera?.StopRecord();
            _camera?.SetActive(BufferActive);
            return false;
        }

        ReleaseRecordOutput();
        _recordPath = target ?? Path.Combine(ClipsFolder, "Recordings", $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
        using (var d = new ObsData().Set("path", _recordPath).Set("muxer_settings", MuxerSettings))
            _record = Obs.obs_output_create("ffmpeg_muxer", "Kairos record", d.Handle, IntPtr.Zero);
        AttachEncoders(_record);
        string path = _recordPath;
        var names = TrackNames; // as this recording was made, whatever pipeline exists when it stops
        _onRecordStopped = (_, _) => RecordingStopped?.Invoke(path, _recordClock.Seconds(DateTime.UtcNow), names);
        Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(_record), "stop", _onRecordStopped, IntPtr.Zero);

        if (Obs.obs_output_start(_record))
        {
            RecordError = null;
            _recordClock.Start(DateTime.UtcNow);
            // The camera's recording only beside a main one that started.
            _camera?.SetActive(true);
            _camera?.StartRecord(path, _audioEncoders[0]);
            CpuPriority.RaiseVideoThreadsSoon();
            return true;
        }
        // For the log only: the user is shown a hint in their language (EngineApp.RecordFailedToast).
        RecordError = Obs.Utf8(Obs.obs_output_get_last_error(_record)) ?? "Recording could not start";
        ObsLog.Error($"record start failed: {RecordError}");
        return false;
    }

    /// <summary>
    /// Pauses or resumes the full recording; the file stays one continuous video. The pause sits on the encoders, which
    /// the replay buffer shares, so the buffer takes nothing in while the recording is paused either.
    /// </summary>
    public bool TogglePauseRecording()
    {
        if (!Recording)
            return false;
        bool pause = !Obs.obs_output_paused(_record);
        if (!Obs.obs_output_pause(_record, pause))
        {
            ObsLog.Warn($"record {(pause ? "pause" : "resume")} refused");
            return false;
        }
        if (pause)
            _recordClock.Pause(DateTime.UtcNow);
        else
            _recordClock.Resume(DateTime.UtcNow);
        return true;
    }

    /// <summary>Mutes system/app/game audio capture, so Kairos's own save sound is not recorded into the next clips.</summary>
    public void SetSystemMuted(bool muted)
    {
        foreach (IntPtr source in _systemSources)
            Obs.obs_source_set_muted(source, muted);
    }

    /// <summary>
    /// Each source's loudest peak (dBFS) since the previous call, for the Audio page's meters: "system" and "mic" in the
    /// together layout, each row's id with separate sources. Separate sources measure only while someone reads: the
    /// meters start on the first read and stop when nobody has read for a while (<see cref="ReleaseIdleMeters"/>).
    /// </summary>
    public IReadOnlyList<(string Id, double Db)> TakeLevels()
    {
        if (_s.Audio.Layout != AudioLayout.Separate)
            return [("system", _systemLevel.Take()), ("mic", _micLevel.Take())];
        _levelsReadUtc = DateTime.UtcNow;
        if (_rowLevels.Count == 0)
            StartRowMeters();
        return _rowLevels.Select(r => (r.Key, r.Value.Take())).ToList();
    }

    /// <summary>Separate sources: each row's meter, while the Audio page reads them (none otherwise).</summary>
    readonly Dictionary<string, LevelMeter> _rowLevels = [];
    readonly Dictionary<IntPtr, (IntPtr Meter, ObsVolmeterCallback Callback)> _sourceMeters = [];
    DateTime _levelsReadUtc;

    void StartRowMeters()
    {
        foreach (var row in _s.Audio.Sources)
            _rowLevels[row.Id] = new LevelMeter();
        foreach (var row in _s.Audio.Sources)
            foreach (IntPtr source in SourcesOf(row.Id))
                MeterSource(source, row.Id);
    }

    void MeterSource(IntPtr source, string rowId)
    {
        if (_rowLevels.TryGetValue(rowId, out var level) && !_sourceMeters.ContainsKey(source) && NewMeter(source, level) is { } meter)
            _sourceMeters[source] = meter;
    }

    void UnmeterSource(IntPtr source)
    {
        if (_sourceMeters.Remove(source, out var meter))
            FreeMeter(meter);
    }

    /// <summary>The engine's 5-second tick: separate sources stop measuring once the Audio page stopped reading.</summary>
    public void ReleaseIdleMeters()
    {
        if (_rowLevels.Count == 0 || DateTime.UtcNow - _levelsReadUtc < TimeSpan.FromSeconds(5))
            return;
        foreach (IntPtr source in _sourceMeters.Keys.ToList())
            UnmeterSource(source);
        _rowLevels.Clear();
    }

    public EngineStatus GetStatus()
    {
        IntPtr video = Obs.obs_get_video();
        int target = _s.Replay.Seconds;
        int filled = BufferActive ? (int)Math.Min(target, (DateTime.UtcNow - _bufferStartedUtc).TotalSeconds) : 0;
        return new EngineStatus(
            BufferActive, Recording, Obs.obs_get_active_fps(), Obs.obs_get_lagged_frames(),
            video == IntPtr.Zero ? 0 : Obs.video_output_get_skipped_frames(video),
            video == IntPtr.Zero ? 0 : Obs.video_output_get_total_frames(video),
            filled, target, Choice?.Id ?? "", Output.W, Output.H, Environment.WorkingSet / (1024 * 1024),
            Plan == null ? 0 : Math.Min(EncoderPresets.EstimateBufferMb(Plan.MaxBitrateKbps, target),
                BufferBudget.CapMb(EncoderPresets.EstimateBufferMb(Plan.MaxBitrateKbps, target), BufferBudget.PhysicalMemoryBytes)),
            ClipsFolder, FolderFellBack, LastError,
            RecordingPaused,
            Recording ? _recordClock.Seconds(DateTime.UtcNow) : 0,
            Recording ? (long)Obs.obs_output_get_total_bytes(_record) : 0,
            CaptureNotice,
            QuietRows,
            Choice != null && Choice.Codec != _video.Codec && _encoders?.Excluded.Count > 0 ? EncoderCatalog.CodecName(_video.Codec) : null,
            _s.Audio.Layout == AudioLayout.Together && _s.Audio.System ? ObsLog.AudioDevices.SystemDevice : null,
            Auto,
            CameraOn: CameraOn,
            DiskFellBack: DiskFellBack,
            Relief: Relief,
            RenderedFrames: Obs.obs_get_total_frames(),
            AudioRowsHeardOn: _heardOn);
    }

    /// <summary>libobs's frame counters now (what the lag guard and Automatic watch).</summary>
    public FrameCount FrameCounts()
    {
        IntPtr video = Obs.obs_get_video();
        return video == IntPtr.Zero ? default
            : new FrameCount(Obs.obs_get_lagged_frames(), Obs.obs_get_total_frames(), Obs.video_output_get_skipped_frames(video),
                Obs.video_output_get_total_frames(video));
    }

    void ReleaseRecordOutput() => ReleaseRecord(ref _record, _onRecordStopped);

    /// <summary>Stops a recording output (waiting up to 5 s for its file to close) and lets it go.</summary>
    internal static void ReleaseRecord(ref IntPtr output, ObsSignalCallback? stopped)
    {
        if (output == IntPtr.Zero)
            return;
        if (Obs.obs_output_active(output))
        {
            Obs.obs_output_stop(output);
            WaitInactive(output, 5000);
        }
        if (stopped != null)
            Obs.signal_handler_disconnect(Obs.obs_output_get_signal_handler(output), "stop", stopped, IntPtr.Zero);
        Obs.obs_output_release(output);
        output = IntPtr.Zero;
    }

    internal static void ReleaseReplay(ref IntPtr output, ObsSignalCallback? callback)
    {
        if (output == IntPtr.Zero)
            return;
        if (Obs.obs_output_active(output))
            Obs.obs_output_force_stop(output);
        if (callback != null)
            Obs.signal_handler_disconnect(Obs.obs_output_get_signal_handler(output), "saved", callback, IntPtr.Zero);
        Obs.obs_output_release(output);
        output = IntPtr.Zero;
    }

    public void Teardown()
    {
        lock (_outputsGate)
            TeardownLocked();
    }

    void TeardownLocked()
    {
        if (_onUnhooked != null)
        {
            // Returns once a signal under way has ended: none comes after the sources go.
            Obs.signal_handler_disconnect(Obs.obs_source_get_signal_handler(_hookSource), "unhooked", _onUnhooked, IntPtr.Zero);
            _onUnhooked = null;
        }
        lock (_underGate)
            _screenLayer = IntPtr.Zero;
        if (_gameLayerSource != IntPtr.Zero)
            HoldGameLayer(false);
        ReleaseRecordOutput();
        _camera?.Dispose(); // its outputs use the main sound encoder: they go first
        _camera = null;
        ReleaseReplay(ref _quick, _onQuickSaved);
        ReleaseReplay(ref _replay, _onReplaySaved);
        if (_onDiskFailed != null)
            DiskReplayOutput.Failed -= _onDiskFailed;

        if (_videoEncoder != IntPtr.Zero)
            Obs.obs_encoder_release(_videoEncoder);
        _videoEncoder = IntPtr.Zero;
        foreach (IntPtr enc in _audioEncoders)
            Obs.obs_encoder_release(enc);
        _audioEncoders.Clear();

        if (_micSource != IntPtr.Zero && _micTap != null)
            Obs.obs_source_remove_audio_capture_callback(_micSource, _micTap, IntPtr.Zero);
        _micSource = IntPtr.Zero;
        _voice?.SetMicConnected(false);

        foreach (var meter in _meters)
            FreeMeter(meter);
        _meters.Clear();

        for (uint channel = ChannelVideo; channel < 64; channel++)
            Obs.obs_set_output_source(channel, IntPtr.Zero);
        _systemSources.Clear();
        foreach (IntPtr source in _sourceMeters.Keys.ToList())
            UnmeterSource(source);
        _rowLevels.Clear();
        _rowSources.Clear();
        _rowPids.Clear();
        _otherSources.Clear();
        _channels.Clear();
        foreach (IntPtr source in _sources)
        {
            Obs.obs_source_remove(source);
            Obs.obs_source_release(source);
        }
        _sources.Clear();
        if (_scene != IntPtr.Zero)
        {
            // libobs 32 registers scenes on the main canvas; only obs_source_remove takes it off the canvas.
            // Without this every rebuild leaked the old scene and its capture source.
            Obs.obs_source_remove(Obs.obs_scene_get_source(_scene));
            Obs.obs_scene_release(_scene);
        }
        _scene = IntPtr.Zero;
        _saving = false;
        ObsHost.DrainDestroyQueue();
    }

    public void Dispose() => Teardown();
}
