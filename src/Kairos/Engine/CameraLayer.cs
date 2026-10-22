using Kairos.Native;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>
/// The camera in the pipeline: its source (win-dshow, loaded on first use, its own sound left out), its place on the
/// scene (a corner box, a circle through obs-filters' mask, or the top band of a 9:16 picture), and, when asked, its own
/// file: a view of the camera alone, its own encoder and replay buffer (and recording), saved with each clip.
/// </summary>
sealed class CameraLayer : IDisposable
{
    /// <summary>The camera's own file: 1080p, the camera fitted in.</summary>
    internal const int FileWidth = 1920, FileHeight = 1080;

    readonly IntPtr _scene;
    readonly IntPtr _source;
    /// <summary>The camera's item on the scene: the camera itself, or (round) its circle scene.</summary>
    IntPtr _item;
    readonly int _canvasW, _canvasH;
    readonly bool _vertical;
    /// <summary>A clip that plays as the camera (self-tests on a PC without one), or null.</summary>
    readonly string? _standIn;
    /// <summary>Round: a scene of the canvas's size with the camera covering its middle square, under the circle mask.</summary>
    IntPtr _circle, _mask;
    bool _active;
    /// <summary>The size asked of the camera (<see cref="CameraMath.BestSize"/>), or null: its own default.</summary>
    string? _asked;
    /// <summary>Whether that size gave a picture was looked at (once); the camera let go (no check after that).</summary>
    bool _checked, _disposed;
    readonly Lock _gate = new();

    IntPtr _fileScene, _view, _viewVideo, _encoder, _replay, _record;
    ObsSignalCallback? _onSaved;

    /// <summary>The camera's own clip was written (the raw path, renamed beside the main clip by the engine).</summary>
    public event Action<string>? FileSaved;

    /// <param name="standIn">A clip that plays as the camera (self-tests on a PC without one); null: the camera.</param>
    public CameraLayer(IntPtr scene, CameraSettings s, int canvasW, int canvasH, string? standIn = null)
    {
        _scene = scene;
        _canvasW = canvasW;
        _canvasH = canvasH;
        _vertical = s.Layout == CameraLayout.Vertical;
        _standIn = standIn;
        ObsHost.LoadCamera(filters: false); // a round camera loads its mask's plugin itself (SetRound)
        string device;
        if (standIn != null)
        {
            device = standIn;
            using var d = new ObsData().Set("local_file", standIn).Set("looping", true).Set("hw_decode", false);
            _source = Obs.obs_source_create("ffmpeg_source", "Kairos camera", d.Handle, IntPtr.Zero);
        }
        else
        {
            var camera = ObsHost.Camera(s.Device);
            device = camera.Device;
            // Made closed: it opens when the buffer or a recording starts (SetActive).
            using var d = new ObsData().Set("video_device_id", device).Set("active", false);
            // Its biggest size up to 1080p (its own default can be 640x480: many webcams), at the picture's frame rate
            // (win-dshow's default: the closest the camera gives).
            if (CameraMath.BestSize(camera.Sizes) is { } size)
            {
                d.Set("res_type", ObsConst.DshowResTypeCustom).Set("resolution", size);
                _asked = size;
                ObsLog.Info($"camera: asked for {size}");
            }
            _source = Obs.obs_source_create("dshow_input", "Kairos camera", d.Handle, IntPtr.Zero);
        }
        if (_source == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the camera source");
        Obs.obs_source_set_muted(_source, true); // the microphone rows carry the voice; a camera's own sound is left out
        _item = AddCovering(scene, _source);
        Place(s);
        ObsLog.Info($"camera: {device} ({s.Layout}{(_vertical ? "" : ", " + s.Shape)})");
    }

    /// <summary>
    /// The camera open (its light on) only while it is recorded: the buffer runs or a recording does. win-dshow opens and
    /// lets go of the device through its "activate" call (its settings' "active" is read only when it is made).
    /// </summary>
    public void SetActive(bool on)
    {
        if (on == _active)
            return;
        _active = on;
        if (_standIn != null)
            return;
        var cd = new CallData();
        Obs.CallDataSetBool(ref cd, "active", on);
        bool called = Obs.proc_handler_call(Obs.obs_source_get_proc_handler(_source), "activate", ref cd);
        Obs.FreeCallData(ref cd);
        if (!called)
            ObsLog.Warn($"camera could not be {(on ? "opened" : "closed")}");
        if (on && _asked != null && !_checked)
        {
            _checked = true;
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => DefaultSizeIfNoPicture());
        }
    }

    /// <summary>
    /// The size asked for gave no picture (the camera busy in another app, a crowded USB hub; win-dshow does not fall
    /// back by itself): the camera's own default instead.
    /// </summary>
    void DefaultSizeIfNoPicture()
    {
        lock (_gate)
        {
            if (_disposed || !_active || HasPicture)
                return;
            using var d = new ObsData().Set("res_type", ObsConst.DshowResTypePreferred);
            Obs.obs_source_update(_source, d.Handle);
            ObsLog.Warn($"camera: {_asked} gave no picture: back to its own default size");
        }
    }

    /// <summary>The camera is open now (its light on).</summary>
    public bool Active => _active;

    /// <summary>The camera gives a picture now.</summary>
    public bool HasPicture => Obs.obs_source_get_width(_source) > 0;

    /// <summary>The camera's place, size and shape (Corner), as the settings window changes them: no rebuild.</summary>
    public void Place(CameraSettings s)
    {
        double x, y, w, h;
        if (_vertical)
        {
            var band = CameraMath.Bands(_canvasW, _canvasH).Camera;
            (x, y, w, h) = (band.X, band.Y, band.W, band.H);
        }
        else
        {
            uint cw = Obs.obs_source_get_width(_source), ch = Obs.obs_source_get_height(_source);
            // Before its first picture: the shape of the size asked for (a phone held upright), else 16:9.
            double aspect = cw > 0 && ch > 0 ? cw / (double)ch
                : _asked != null && CameraMath.TryParseSize(_asked, out int aw, out int ah) ? aw / (double)ah : 16 / 9.0;
            (x, y, w, h) = CameraMath.Corner(s, _canvasW, _canvasH, aspect);
            SetRound(s.Shape == CameraShape.Round);
        }
        var bounds = new Vec2((float)w, (float)h);
        Obs.obs_sceneitem_set_bounds(_item, ref bounds);
        var pos = new Vec2((float)x, (float)y);
        Obs.obs_sceneitem_set_pos(_item, ref pos);
    }

    /// <summary>An item that covers its bounds, cut to them, its middle kept (the camera, or the game in a 9:16 band).</summary>
    internal static IntPtr AddCovering(IntPtr scene, IntPtr source)
    {
        IntPtr item = Obs.obs_scene_add(scene, source);
        Obs.obs_sceneitem_set_bounds_type(item, ObsConst.BoundsScaleOuter);
        Obs.obs_sceneitem_set_bounds_crop(item, true);
        Obs.obs_sceneitem_set_bounds_alignment(item, ObsConst.AlignCenter);
        return item;
    }

    /// <summary>
    /// A circle: the camera covers the middle square of a scene the canvas's size, and obs-filters' alpha mask, its shape
    /// kept (fitted to the height, centred), cuts that square round; the corner box shows that middle. The camera itself
    /// stays whole for its own file.
    /// </summary>
    void SetRound(bool round)
    {
        if (round == (_circle != IntPtr.Zero))
            return;
        Obs.obs_sceneitem_remove(_item);
        if (!round)
        {
            ReleaseCircle();
            _item = AddCovering(_scene, _source);
            return;
        }
        ObsHost.LoadCamera(filters: true);
        _circle = Obs.obs_scene_create_private("Kairos camera circle");
        IntPtr inner = AddCovering(_circle, _source);
        int side = Math.Min(_canvasW, _canvasH);
        var bounds = new Vec2(side, side);
        Obs.obs_sceneitem_set_bounds(inner, ref bounds);
        var pos = new Vec2((_canvasW - side) / 2f, (_canvasH - side) / 2f);
        Obs.obs_sceneitem_set_pos(inner, ref pos);
        using (var d = new ObsData().Set("type", "mask_alpha_filter.effect").Set("image_path", CircleImage()).Set("stretch", false))
            _mask = Obs.obs_source_create_private("mask_filter_v2", "Kairos camera mask", d.Handle);
        if (_mask != IntPtr.Zero)
            Obs.obs_source_filter_add(Obs.obs_scene_get_source(_circle), _mask);
        _item = AddCovering(_scene, Obs.obs_scene_get_source(_circle));
    }

    void ReleaseCircle()
    {
        if (_mask != IntPtr.Zero)
        {
            Obs.obs_source_filter_remove(Obs.obs_scene_get_source(_circle), _mask);
            Obs.obs_source_release(_mask);
        }
        _mask = IntPtr.Zero;
        if (_circle != IntPtr.Zero)
            Obs.obs_scene_release(_circle);
        _circle = IntPtr.Zero;
    }

    /// <summary>A white circle on transparent, a little inside its square (the mask's edges stay clear), made once.</summary>
    static string CircleImage()
    {
        string path = Path.Combine(SettingsStore.AppDataDir, "camera-circle.png");
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(SettingsStore.AppDataDir);
        using var bitmap = new System.Drawing.Bitmap(512, 512, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(System.Drawing.Brushes.White, 4, 4, 504, 504);
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    /// <summary>
    /// The camera's own file: a view of the camera alone at 1080p, its own encoder (from <paramref name="encoder"/>, the
    /// main one's family, given the size and frame rate) feeding <paramref name="replay"/> (a replay buffer as long as the
    /// main one) with the main mix's sound, at 60 fps or less.
    /// </summary>
    public void BuildFile(Func<int, int, int, IntPtr> encoder, IntPtr replay, IntPtr soundEncoder, int fps)
    {
        _replay = replay;
        _fileScene = Obs.obs_scene_create_private("Kairos camera file");
        IntPtr item = Obs.obs_scene_add(_fileScene, _source);
        Obs.obs_sceneitem_set_bounds_type(item, ObsConst.BoundsScaleInner);
        Obs.obs_sceneitem_set_bounds_alignment(item, ObsConst.AlignCenter);
        var bounds = new Vec2(FileWidth, FileHeight);
        Obs.obs_sceneitem_set_bounds(item, ref bounds);
        _view = Obs.obs_view_create();
        Obs.obs_view_set_source(_view, 0, Obs.obs_scene_get_source(_fileScene));
        var ovi = ObsHost.VideoInfo(new VideoConfig(FileWidth, FileHeight, FileWidth, FileHeight, fps));
        _viewVideo = Obs.obs_view_add2(_view, ref ovi);
        if (_viewVideo == IntPtr.Zero)
            throw new InvalidOperationException("Could not make the camera's own picture");
        uint every = CameraMath.FileFpsDivisor(fps);
        _encoder = encoder(FileWidth, FileHeight, fps / (int)every);
        Obs.obs_encoder_set_video(_encoder, _viewVideo);
        if (every > 1 && !Obs.obs_encoder_set_frame_rate_divisor(_encoder, every))
            ObsLog.Warn("camera file: could not lower its frame rate");
        Obs.obs_output_set_video_encoder(_replay, _encoder);
        Obs.obs_output_set_audio_encoder(_replay, soundEncoder, 0);
        _onSaved = (_, _) =>
        {
            if (CapturePipeline.LastReplayPath(_replay) is { } path)
                FileSaved?.Invoke(path);
        };
        Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(_replay), "saved", _onSaved, IntPtr.Zero);
        ObsLog.Info($"camera file: {FileWidth}x{FileHeight} at {fps / (int)every} fps, its own replay");
    }

    public bool HasFile => _replay != IntPtr.Zero;

    public void SetBufferActive(bool on)
    {
        if (_replay == IntPtr.Zero || on == Obs.obs_output_active(_replay))
            return;
        if (on && !Obs.obs_output_start(_replay))
            ObsLog.Warn($"camera replay could not start: {Obs.Utf8(Obs.obs_output_get_last_error(_replay))}");
        else if (!on)
            Obs.obs_output_stop(_replay);
    }

    /// <summary>The camera's buffer starts empty again (after its clip is written).</summary>
    public void StartFresh()
    {
        if (_replay != IntPtr.Zero && Obs.obs_output_active(_replay))
            CapturePipeline.Restart(_replay, "camera replay");
    }

    /// <summary>Saves the camera's replay with the main one.</summary>
    public void Save()
    {
        if (_replay != IntPtr.Zero && Obs.obs_output_active(_replay))
            CapturePipeline.CallSave(_replay);
    }

    /// <summary>A full recording of the camera beside the main one (<c>…camera.mp4</c>).</summary>
    public void StartRecord(string mainPath, IntPtr soundEncoder)
    {
        if (_encoder == IntPtr.Zero)
            return;
        CapturePipeline.ReleaseRecord(ref _record, null);
        using (var d = new ObsData().Set("path", ClipLibrary.CameraOf(mainPath)).Set("muxer_settings", CapturePipeline.MuxerSettings))
            _record = Obs.obs_output_create("ffmpeg_muxer", "Kairos camera record", d.Handle, IntPtr.Zero);
        Obs.obs_output_set_video_encoder(_record, _encoder);
        Obs.obs_output_set_audio_encoder(_record, soundEncoder, 0);
        if (!Obs.obs_output_start(_record))
            ObsLog.Warn($"camera recording could not start: {Obs.Utf8(Obs.obs_output_get_last_error(_record))}");
    }

    /// <summary>Stops the camera's recording (its file closes on its own; let go at the next start or at teardown).</summary>
    public void StopRecord()
    {
        if (_record != IntPtr.Zero && Obs.obs_output_active(_record))
            Obs.obs_output_stop(_record);
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        ReleaseFile();
        Obs.obs_sceneitem_remove(_item);
        ReleaseCircle();
        Obs.obs_source_remove(_source);
        Obs.obs_source_release(_source);
    }

    /// <summary>Lets the camera's own file go (its outputs, encoder, view and scene); the camera stays in the picture.</summary>
    public void ReleaseFile()
    {
        CapturePipeline.ReleaseRecord(ref _record, null);
        CapturePipeline.ReleaseReplay(ref _replay, _onSaved);
        if (_encoder != IntPtr.Zero)
            Obs.obs_encoder_release(_encoder);
        _encoder = IntPtr.Zero;
        if (_view != IntPtr.Zero)
        {
            Obs.obs_view_remove(_view);
            Obs.obs_view_set_source(_view, 0, IntPtr.Zero);
            Obs.obs_view_destroy(_view);
            _view = IntPtr.Zero;
        }
        if (_fileScene != IntPtr.Zero)
            Obs.obs_scene_release(_fileScene);
        _fileScene = IntPtr.Zero;
    }
}
