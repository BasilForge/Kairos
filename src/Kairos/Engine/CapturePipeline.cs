using Kairos.Native;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Scene, capture and audio sources, encoders and outputs built from <see cref="AppSettings"/>.</summary>
public sealed class CapturePipeline : IDisposable
{
    const uint ChannelVideo = 0, ChannelDesktop = 1, ChannelApp = 2, ChannelMic = 3;
    static readonly TimeSpan MinBufferBeforeSave = TimeSpan.FromSeconds(2);

    readonly AppSettings _s;
    readonly List<IntPtr> _sources = [];
    readonly List<IntPtr> _audioEncoders = [];
    readonly List<(IntPtr Meter, ObsVolmeterCallback Callback)> _meters = [];
    readonly LevelMeter _systemLevel = new();
    readonly LevelMeter _micLevel = new();
    IntPtr _systemSource;
    IntPtr _scene, _videoEncoder, _replay, _quick, _record;
    ObsSignalCallback? _onReplaySaved, _onQuickSaved, _onRecordStopped;
    DateTime _bufferStartedUtc;
    DateTime _saveRequestedUtc;
    volatile bool _saving;
    string? _recordPath;

    readonly Func<IReadOnlyList<string>, IReadOnlyList<string>>? _encoderFilter;
    readonly Voice.VoiceTrigger? _voice;
    IntPtr _micSource;
    ObsAudioCaptureCallback? _micTap;

    /// <param name="encoderFilter">Self-test hook that narrows the encoder list, e.g. to force Intel QSV.</param>
    /// <param name="voice">Smart Save listener that hears the microphone source.</param>
    public CapturePipeline(AppSettings settings, Func<IReadOnlyList<string>, IReadOnlyList<string>>? encoderFilter = null,
        Voice.VoiceTrigger? voice = null)
    {
        _s = settings;
        _encoderFilter = encoderFilter;
        _voice = voice;
    }

    public string ClipsFolder { get; private set; } = "";

    /// <summary>The Game mode source, for a backend that points it at another window as focus moves (none: IntPtr.Zero).</summary>
    public IntPtr GameSource { get; private set; }

    /// <summary>Changes the Game mode source's settings in place.</summary>
    public void UpdateGameSource(ObsData settings)
    {
        if (GameSource != IntPtr.Zero)
            Obs.obs_source_update(GameSource, settings.Handle);
    }
    public bool FolderFellBack { get; private set; }
    public EncoderPlan? Plan { get; private set; }
    public EncoderChoice? Choice { get; private set; }
    public int Fps { get; private set; }
    public (int W, int H) Output { get; private set; }
    /// <summary>The canvas: the captured monitor's own size, which screenshots keep.</summary>
    public (int W, int H) Base { get; private set; }
    public string? LastError { get; private set; }
    public bool BufferActive => _replay != IntPtr.Zero && Obs.obs_output_active(_replay);
    public bool Recording => _record != IntPtr.Zero && Obs.obs_output_active(_record);
    public bool RecordingPaused => Recording && Obs.obs_output_paused(_record);
    readonly RecordingClock _recordClock = new();

    /// <summary>Raised on a libobs thread with the raw file path and whether it was the quick buffer.</summary>
    public event Action<string, bool>? ReplaySaved;

    /// <summary>Raised on a libobs thread with the finished recording's path and its length without pauses.</summary>
    public event Action<string, double>? RecordingStopped;

    public void Build()
    {
        var monitor = VideoMath.PickMonitor(ObsHost.Monitors(), _s.Capture.MonitorId);
        int baseW = monitor?.Width ?? 1920, baseH = monitor?.Height ?? 1080;
        Base = (baseW, baseH);
        var ids = ObsHost.EncoderIds();
        Choice = EncoderCatalog.Pick(_s.Video.Codec, _encoderFilter?.Invoke(ids) ?? ids);
        Output = VideoMath.OutputSize(baseW, baseH, _s.Video.Resolution);
        Fps = _s.Video.Fps;
        if (Choice.Vendor == EncoderVendor.Software)
            (Output, Fps) = EncoderCatalog.LimitForSoftware(Output, Fps);
        if (!ObsHost.ResetVideo(new VideoConfig(baseW, baseH, Output.W, Output.H, Fps)))
            throw new InvalidOperationException("Could not set up video");

        ClipsFolder = ClipOrganizer.ResolveFolder(_s.Output.Folder, f => ClipOrganizer.Usable(f, App.InstallPaths.Root), out bool fellBack);
        FolderFellBack = fellBack;

        BuildScene(monitor?.Id ?? "", baseW, baseH);
        BuildAudio();
        BuildEncoders();
        _replay = CreateReplayOutput("Kairos replay", "Kairos %CCYY-%MM-%DD %hh-%mm-%ss", _s.Replay.Seconds);
        _onReplaySaved = (_, _) => OnSaved(_replay, quick: false);
        Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(_replay), "saved", _onReplaySaved, IntPtr.Zero);

        if (_s.Replay.QuickEnabled)
        {
            _quick = CreateReplayOutput("Kairos quick", "Kairos quick %CCYY-%MM-%DD %hh-%mm-%ss", _s.Replay.QuickSeconds);
            _onQuickSaved = (_, _) => OnSaved(_quick, quick: true);
            Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(_quick), "saved", _onQuickSaved, IntPtr.Zero);
        }

        ObsLog.Info($"pipeline built: mode={_s.Capture.Mode} {baseW}x{baseH} -> {Output.W}x{Output.H}@{Fps} " +
                    $"{Choice.Id} cq={Plan!.Cq} max={Plan.MaxBitrateKbps}kbps folder={ClipsFolder}");
    }

    void BuildScene(string monitorId, int baseW, int baseH)
    {
        _scene = Obs.obs_scene_create("Kairos");
        switch (_s.Capture.Mode)
        {
            case CaptureMode.Window:
                AddToScene(CreateSource(CaptureBackend.Window(_s.Capture.Window, _s.Capture.CaptureCursor), "Window"), baseW, baseH);
                break;

            case CaptureMode.Game:
                if (CaptureBackend.ScreenBehindGame(_s.Capture.GameFallbackToScreen))
                    AddToScene(CreateMonitorSource(monitorId), baseW, baseH);
                if (CaptureBackend.HasGameCapture)
                {
                    IntPtr game = CreateSource(CaptureBackend.Game(_s.Capture.CaptureCursor, _s.Audio.SourceAudioOnly), "Game");
                    GameSource = game;
                    AddToScene(game, baseW, baseH);
                    if (_s.Audio.SourceAudioOnly)
                    {
                        ConfigureAudio(game, SystemMixers(), _s.Audio.SystemVolume);
                        AttachMeter(game, _systemLevel);
                        _systemSource = game;
                    }
                }
                break;

            default:
                AddToScene(CreateMonitorSource(monitorId), baseW, baseH);
                break;
        }
        Obs.obs_set_output_source(ChannelVideo, Obs.obs_scene_get_source(_scene));
    }

    IntPtr CreateMonitorSource(string monitorId)
    {
        return CreateSource(CaptureBackend.Screen(monitorId, _s.Capture.CaptureCursor), "Screen");
    }

    void BuildAudio()
    {
        var a = _s.Audio;
        bool appOnly = a.SourceAudioOnly && _s.Capture.Mode == CaptureMode.Window;
        bool gameOnly = a.SourceAudioOnly && _s.Capture.Mode == CaptureMode.Game;

        if (appOnly)
        {
            IntPtr app = CreateAppAudio(_s.Capture.Window, "App audio");
            _systemSource = app;
            ConfigureAudio(app, SystemMixers(), a.SystemVolume);
            AttachMeter(app, _systemLevel);
            Obs.obs_set_output_source(ChannelApp, app);
        }
        else if (a.System && !gameOnly)
        {
            IntPtr desktop = CreateSource(CaptureBackend.DesktopAudio(a.SystemDevice), "Desktop audio");
            _systemSource = desktop;
            ConfigureAudio(desktop, SystemMixers(), a.SystemVolume);
            AttachMeter(desktop, _systemLevel);
            Obs.obs_set_output_source(ChannelDesktop, desktop);
        }

        // Smart Save needs the microphone even when it is not recorded; then it joins no audio track.
        if (a.Mic || (_voice != null && _s.Voice.Enabled))
        {
            IntPtr mic = CreateSource(CaptureBackend.Microphone(a.MicDevice), "Microphone");
            ConfigureAudio(mic, a.Mic ? 1u | (a.SeparateTracks ? VoiceTrack : 0u) : 0u, a.MicVolume);
            AttachMeter(mic, _micLevel);
            Obs.obs_set_output_source(ChannelMic, mic);
            AttachVoice(mic);
        }
    }

    void AttachVoice(IntPtr mic)
    {
        if (_voice == null)
            return;
        var voice = _voice;
        _micTap = (_, _, audioData, muted) =>
        {
            if (!muted && voice.Listening)
                unsafe
                {
                    var data = (ObsAudioData*)audioData;
                    voice.Feed(new ReadOnlySpan<float>((void*)data->Data0, (int)data->Frames));
                }
        };
        Obs.obs_source_add_audio_capture_callback(mic, _micTap, IntPtr.Zero);
        _micSource = mic;
        voice.SetMicConnected(true);
    }

    /// <summary>Feeds the loudest channel of each libobs meter update into <paramref name="level"/>.</summary>
    void AttachMeter(IntPtr source, LevelMeter level)
    {
        IntPtr meter = Obs.obs_volmeter_create(1 /* OBS_FADER_IEC */);
        if (meter == IntPtr.Zero)
            return;
        ObsVolmeterCallback callback = (_, _, peak, _) => level.Add(LoudestChannel(peak));
        Obs.obs_volmeter_add_callback(meter, callback, IntPtr.Zero);
        Obs.obs_volmeter_attach_source(meter, source);
        _meters.Add((meter, callback));
    }

    static unsafe float LoudestChannel(IntPtr peak)
    {
        float* p = (float*)peak;
        return Math.Max(p[0], p[1]);
    }

    uint SystemMixers() => 1u | (_s.Audio.SeparateTracks ? 2u : 0u);

    /// <summary>Track 3's mixer: your voice.</summary>
    const uint VoiceTrack = 4u;

    IntPtr CreateAppAudio(string window, string name) => CreateSource(CaptureBackend.AppAudio(window), name);

    static void ConfigureAudio(IntPtr source, uint mixers, double volume)
    {
        Obs.obs_source_set_audio_mixers(source, mixers);
        Obs.obs_source_set_volume(source, (float)volume);
    }

    /// <summary>MP4 codec tag "hvc1" (big-endian, as obs-ffmpeg-mux expects) so Apple players accept HEVC clips.</summary>
    const long Hvc1Tag = 0x68766331;

    void BuildEncoders()
    {
        var choice = Choice!;
        Plan = EncoderPresets.Plan(choice.Codec, _s.Video.Quality, Output.W, Output.H, Fps,
            _s.Video.CustomCq, _s.Video.CustomMbps, choice.Vendor != EncoderVendor.Software, _s.Video.Preset);

        using (var d = new ObsData())
        {
            foreach (var (key, value) in EncoderCatalog.Settings(Plan, choice))
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
            _videoEncoder = Obs.obs_video_encoder_create(choice.Id, "Kairos video", d.Handle, IntPtr.Zero);
        }
        if (_videoEncoder == IntPtr.Zero)
            throw new InvalidOperationException($"Could not create encoder {choice.Id}");
        Obs.obs_encoder_set_video(_videoEncoder, Obs.obs_get_video());

        foreach (int mixer in AudioMixerIndexes())
        {
            using var d = new ObsData().Set("bitrate", (long)_s.Audio.BitrateKbps);
            IntPtr enc = Obs.obs_audio_encoder_create("ffmpeg_aac", $"Kairos audio {mixer + 1}", d.Handle, (nuint)mixer, IntPtr.Zero);
            if (enc == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the AAC encoder");
            Obs.obs_encoder_set_audio(enc, Obs.obs_get_audio());
            _audioEncoders.Add(enc);
        }
    }

    /// <summary>Track 1 is the full mix; with separate tracks, 2 is system/app audio and 3 is your voice (the mic).</summary>
    IEnumerable<int> AudioMixerIndexes()
    {
        yield return 0;
        if (!_s.Audio.SeparateTracks)
            yield break;
        if (_s.Audio.System || _s.Audio.SourceAudioOnly)
            yield return 1;
        if (_s.Audio.Mic)
            yield return 2;
    }

    IntPtr CreateReplayOutput(string name, string format, int seconds)
    {
        int sizeMb = BufferBudget.CapMb(EncoderPresets.EstimateBufferMb(Plan!.MaxBitrateKbps, seconds), BufferBudget.PhysicalMemoryBytes);
        using var d = new ObsData()
            .Set("directory", ClipsFolder).Set("format", format).Set("extension", "mp4").Set("allow_spaces", true)
            .Set("max_time_sec", (long)seconds).Set("max_size_mb", (long)sizeMb);
        IntPtr output = Obs.obs_output_create("replay_buffer", name, d.Handle, IntPtr.Zero);
        if (output == IntPtr.Zero)
            throw new InvalidOperationException("Could not create the replay buffer");
        AttachEncoders(output);
        return output;
    }

    void AttachEncoders(IntPtr output)
    {
        Obs.obs_output_set_video_encoder(output, _videoEncoder);
        for (int i = 0; i < _audioEncoders.Count; i++)
            Obs.obs_output_set_audio_encoder(output, _audioEncoders[i], (nuint)i);
    }

    IntPtr CreateSource((string Id, ObsData Settings) backend, string name)
    {
        using var settings = backend.Settings;
        return CreateSource(backend.Id, name, settings);
    }

    IntPtr CreateSource(string id, string name, ObsData settings)
    {
        IntPtr source = Obs.obs_source_create(id, name, settings.Handle, IntPtr.Zero);
        if (source == IntPtr.Zero)
            throw new InvalidOperationException($"Could not create source {id}");
        _sources.Add(source);
        return source;
    }

    void AddToScene(IntPtr source, int w, int h)
    {
        IntPtr item = Obs.obs_scene_add(_scene, source);
        Obs.obs_sceneitem_set_bounds_type(item, ObsConst.BoundsScaleInner);
        Obs.obs_sceneitem_set_bounds_alignment(item, ObsConst.AlignCenter);
        var bounds = new Vec2(w, h);
        Obs.obs_sceneitem_set_bounds(item, ref bounds);
        var pos = new Vec2(0, 0);
        Obs.obs_sceneitem_set_pos(item, ref pos);
    }

    int _feedHolds;
    volatile bool _bufferStarting, _recordStarting;

    /// <summary>The canvas is fed by the scene while something needs it: a buffer, a recording, or a held feed (a screenshot).</summary>
    void ApplyFeed()
    {
        if (_scene == IntPtr.Zero)
            return;
        bool on = !CaptureBackend.DetachVideoWhenIdle || BufferActive || Recording || _bufferStarting || _recordStarting || Volatile.Read(ref _feedHolds) > 0;
        Obs.obs_set_output_source(ChannelVideo, on ? Obs.obs_scene_get_source(_scene) : IntPtr.Zero);
    }

    /// <summary>Keeps the scene on the canvas (a screenshot of a paused pipeline needs a picture); pair with <see cref="ReleaseFeed"/>.</summary>
    public void HoldFeed()
    {
        Interlocked.Increment(ref _feedHolds);
        ApplyFeed();
    }

    public void ReleaseFeed()
    {
        Interlocked.Decrement(ref _feedHolds);
        ApplyFeed();
    }

    /// <summary>Starts or stops the replay buffer(s). Returns whether the buffer is active afterwards.</summary>
    public bool SetBufferActive(bool on)
    {
        if (on == BufferActive)
        {
            ApplyFeed();
            return on;
        }
        if (!on)
        {
            Obs.obs_output_stop(_replay);
            if (_quick != IntPtr.Zero)
                Obs.obs_output_stop(_quick);
            // The buffers stop on libobs's thread; the feed goes once they have (see OnBufferStopped).
            Task.Run(async () =>
            {
                for (int i = 0; i < 40 && BufferActive; i++)
                    await Task.Delay(50);
                ApplyFeed();
            });
            return false;
        }

        _bufferStarting = true; // the scene is on the canvas before an output starts
        ApplyFeed();
        try
        {
            if (!Obs.obs_output_start(_replay))
            {
                LastError = Obs.Utf8(Obs.obs_output_get_last_error(_replay)) ?? "The replay buffer could not start";
                ObsLog.Error($"replay start failed: {LastError}");
                return false;
            }
            if (_quick != IntPtr.Zero && !Obs.obs_output_start(_quick))
                ObsLog.Warn("quick buffer could not start");
        }
        finally
        {
            _bufferStarting = false;
            ApplyFeed();
        }
        LastError = null;
        _bufferStartedUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>Requests a save. False when inactive, too early, or a save is already running.</summary>
    public bool SaveReplay(bool quick) => RequestSave(quick) == SaveWatch.Request.Started;

    /// <summary>What a buffer holds now, about (its bitrate times the seconds filled): how long writing it may take.</summary>
    public long BufferBytes => Plan == null || !BufferActive ? 0
        : (long)EncoderPresets.EstimateBufferMb(Plan.MaxBitrateKbps,
            (int)Math.Min(_s.Replay.Seconds, (DateTime.UtcNow - _bufferStartedUtc).TotalSeconds)) * 1024 * 1024;

    /// <summary>Requests a save and says what happened: started, off, just starting, or one is already being written.</summary>
    public SaveWatch.Request RequestSave(bool quick)
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
        var cd = new CallData();
        bool ok = Obs.proc_handler_call(Obs.obs_output_get_proc_handler(output), "save", ref cd);
        Obs.FreeCallData(ref cd);
        if (!ok)
            _saving = false;
        return ok ? SaveWatch.Request.Started : SaveWatch.Request.Off;
    }

    void OnSaved(IntPtr output, bool quick)
    {
        string? path = null;
        var cd = new CallData();
        if (Obs.proc_handler_call(Obs.obs_output_get_proc_handler(output), "get_last_replay", ref cd) &&
            Obs.calldata_get_string(ref cd, "path", out IntPtr p))
            path = Obs.Utf8(p);
        Obs.FreeCallData(ref cd);
        _saving = false;
        if (path != null)
            ReplaySaved?.Invoke(path.Replace('/', Path.DirectorySeparatorChar), quick);
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
            return false;
        }

        ReleaseRecordOutput();
        _recordPath = target ?? Path.Combine(ClipsFolder, "Recordings", $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
        using (var d = new ObsData().Set("path", _recordPath).Set("muxer_settings", "movflags=frag_keyframe+empty_moov+delay_moov"))
            _record = Obs.obs_output_create("ffmpeg_muxer", "Kairos record", d.Handle, IntPtr.Zero);
        AttachEncoders(_record);
        string path = _recordPath;
        _onRecordStopped = (_, _) =>
        {
            ApplyFeed(); // nothing records any more: the scene may come off the canvas
            RecordingStopped?.Invoke(path, _recordClock.Seconds(DateTime.UtcNow));
        };
        Obs.signal_handler_connect(Obs.obs_output_get_signal_handler(_record), "stop", _onRecordStopped, IntPtr.Zero);

        _recordStarting = true;
        ApplyFeed();
        bool started = Obs.obs_output_start(_record);
        _recordStarting = false;
        ApplyFeed();
        if (started)
        {
            _recordClock.Start(DateTime.UtcNow);
            return true;
        }
        LastError = Obs.Utf8(Obs.obs_output_get_last_error(_record)) ?? "Recording could not start";
        ObsLog.Error($"record start failed: {LastError}");
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
        if (_systemSource != IntPtr.Zero)
            Obs.obs_source_set_muted(_systemSource, muted);
    }

    /// <summary>Loudest system and mic peaks (dBFS) since the previous call; read by the live meter stream.</summary>
    public (double System, double Mic) TakeLevels() => (_systemLevel.Take(), _micLevel.Take());

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
            Recording ? (long)Obs.obs_output_get_total_bytes(_record) : 0);
    }

    void ReleaseRecordOutput()
    {
        if (_record == IntPtr.Zero)
            return;
        if (Obs.obs_output_active(_record))
        {
            Obs.obs_output_stop(_record);
            for (int i = 0; i < 50 && Obs.obs_output_active(_record); i++)
                Thread.Sleep(100);
        }
        if (_onRecordStopped != null)
            Obs.signal_handler_disconnect(Obs.obs_output_get_signal_handler(_record), "stop", _onRecordStopped, IntPtr.Zero);
        Obs.obs_output_release(_record);
        _record = IntPtr.Zero;
    }

    static void ReleaseReplay(ref IntPtr output, ObsSignalCallback? callback)
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
        ReleaseRecordOutput();
        ReleaseReplay(ref _quick, _onQuickSaved);
        ReleaseReplay(ref _replay, _onReplaySaved);

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

        foreach (var (meter, callback) in _meters)
        {
            Obs.obs_volmeter_remove_callback(meter, callback, IntPtr.Zero);
            Obs.obs_volmeter_detach_source(meter);
            Obs.obs_volmeter_destroy(meter);
        }
        _meters.Clear();

        foreach (uint channel in new[] { ChannelVideo, ChannelDesktop, ChannelApp, ChannelMic })
            Obs.obs_set_output_source(channel, IntPtr.Zero);
        _systemSource = IntPtr.Zero;
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
