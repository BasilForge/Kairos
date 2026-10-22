using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Kairos.Native;
using Kairos.Settings;

namespace Kairos.Engine;

public sealed record VideoConfig(int BaseW, int BaseH, int OutW, int OutH, int Fps);

public sealed record MonitorInfo(string Id, string Name, int Width, int Height, bool Primary);

/// <summary>Owns the process-wide libobs instance.</summary>
public static partial class ObsHost
{
    static readonly string[] Modules = ["win-capture", "win-wasapi", "obs-nvenc", "obs-ffmpeg", "obs-x264"];

    /// <summary>
    /// Intel's encoder plugin takes over half a second to start (it probes the Intel driver), while the recording
    /// usually runs on NVIDIA or AMD. It loads once the replay buffer is up, unless the wanted codec has no other
    /// hardware encoder (then it is loaded with the rest, since it could be the pick). It has no post-load step.
    /// </summary>
    static readonly string[] Later = ["obs-qsv11"];
    static bool _laterLoaded;
    static readonly IntPtr GraphicsModule = Marshal.StringToCoTaskMemUTF8("libobs-d3d11");

    public static bool Started { get; private set; }
    public static List<string> FailedModules { get; } = [];

    /// <summary>
    /// Where libobs starts: it looks for its own files only as ../../data/libobs/ from the working directory, so in the
    /// one-folder program that is data\libobs itself.
    /// </summary>
    internal static string LibobsWorkingDirectory(string appFolder) => Path.Combine(appFolder, "data", "libobs");

    /// <summary>A plugin's DLL and its data folder in the program folder.</summary>
    internal static (string Bin, string Data) ModulePaths(string appFolder, string name) =>
        (Path.Combine(appFolder, "obs-plugins", "64bit", name + ".dll"),
         Path.Combine(appFolder, "data", "obs-plugins", name) + Path.DirectorySeparatorChar);

    /// <summary>Starts libobs. <paramref name="appFolder"/> is the program folder (obs.dll, data\, obs-plugins\).</summary>
    /// <param name="wanted">The codec the settings ask for (which encoder plugins must be in before the first pick).</param>
    public static void Start(string appFolder, string configDir, string logDir, VideoConfig video, VideoCodec wanted = VideoCodec.Av1)
    {
        if (Started)
            throw new InvalidOperationException("libobs already started");

        Directory.SetCurrentDirectory(LibobsWorkingDirectory(appFolder));
        ObsLog.Open(logDir);
        ObsLog.HookLibobs();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        string moduleConfig = Path.Combine(configDir, "plugin_config") + Path.DirectorySeparatorChar;
        if (!Obs.obs_startup("en-US", moduleConfig, IntPtr.Zero))
            throw new InvalidOperationException("obs_startup failed");
        Started = true;
        long core = clock.ElapsedMilliseconds;

        var audio = new ObsAudioInfo { SamplesPerSec = 48000, Speakers = ObsConst.SpeakersStereo };
        if (!Obs.obs_reset_audio(ref audio))
            throw new InvalidOperationException("obs_reset_audio failed");
        if (!ResetVideo(video))
            throw new InvalidOperationException("obs_reset_video failed");
        ObsLog.Info($"gpu priority: {GpuPriority.Raise()}");
        long graphics = clock.ElapsedMilliseconds;

        _root = appFolder;
        FailedModules.Clear();
        _laterLoaded = false;
        OnDemand.Clear();
        var took = new List<string>();
        foreach (string name in Modules)
            took.Add(LoadModule(name));
        if (!EncoderCatalog.HasHardware(wanted, EncoderIds()))
            LoadLater(took);
        long modules = clock.ElapsedMilliseconds;
        Obs.obs_post_load_modules();
        Audio.ProcessAudioSource.Register(); // Kairos's own source: an app's sound by process id
        Replay.DiskReplayOutput.Register(); // Kairos's own output: the replay on the disk
        Obs.obs_log_loaded_modules();
        ObsLog.Info($"libobs up in {clock.ElapsedMilliseconds} ms: core {core}, graphics {graphics - core}, modules {modules - graphics} " +
            $"({string.Join(", ", took)}), post-load {clock.ElapsedMilliseconds - modules}");
    }

    static string _root = "";

    /// <summary>Opens and starts one plugin; "name ms" for the log.</summary>
    static string LoadModule(string name)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (bin, data) = ModulePaths(_root, name);
        int rc = Obs.obs_open_module(out IntPtr module, bin, data);
        if (rc != ObsConst.ModuleSuccess || !Obs.obs_init_module(module))
        {
            FailedModules.Add(name);
            ObsLog.Warn($"module {name} failed to load (code {rc})");
        }
        return $"{name} {clock.ElapsedMilliseconds}";
    }

    /// <summary>The plugins left for later, if not in yet: before encoders are listed or picked again.</summary>
    public static void LoadLater()
    {
        if (!Started || _laterLoaded)
            return;
        var took = new List<string>();
        LoadLater(took);
        ObsLog.Info($"later modules in: {string.Join(", ", took)} ms");
    }

    static void LoadLater(List<string> took)
    {
        _laterLoaded = true;
        foreach (string name in Later)
            took.Add(LoadModule(name));
    }

    /// <summary>The plugins loaded on first use (the camera's), since libobs started.</summary>
    static readonly HashSet<string> OnDemand = [];

    /// <summary>win-dshow (cameras) and, for a round camera, obs-filters (its mask): loaded on first use, never with the camera off.</summary>
    public static void LoadCamera(bool filters)
    {
        if (!Started)
            return;
        foreach (string name in filters ? new[] { "win-dshow", "obs-filters" } : ["win-dshow"])
            if (OnDemand.Add(name))
                ObsLog.Info($"camera module in: {LoadModule(name)} ms");
    }

    /// <summary>The cameras Windows has (webcams, phones acting as one): their names and libobs ids.</summary>
    public static List<(string Name, string Value)> Cameras()
    {
        LoadCamera(filters: false);
        lock (CameraSizesSeen)
            CameraSizesSeen.Clear(); // listed again (the camera page): their sizes are read afresh
        return [.. ListProperty("dshow_input", "video_device_id").Where(c => c.Value.Length > 0)];
    }

    /// <summary>Each camera's sizes as read: a scan of the devices takes tens of ms, seconds with some virtual cameras.</summary>
    static readonly Dictionary<string, List<string>> CameraSizesSeen = [];

    /// <summary>
    /// The camera to open (<paramref name="wanted"/>, else the first) and the sizes it gives ("1920x1080"), read without
    /// opening it in one scan of the devices, and kept until the cameras are listed again. An empty device: none.
    /// </summary>
    public static (string Device, List<string> Sizes) Camera(string wanted)
    {
        lock (CameraSizesSeen)
            if (wanted.Length > 0 && CameraSizesSeen.TryGetValue(wanted, out var seen))
                return (wanted, seen);
        LoadCamera(filters: false);
        IntPtr props = Obs.obs_get_source_properties("dshow_input");
        if (props == IntPtr.Zero)
            return (wanted, []);
        try
        {
            string device = wanted.Length > 0 ? wanted
                : Items(props, "video_device_id").Select(c => c.Value).FirstOrDefault(v => v.Length > 0) ?? "";
            if (device.Length == 0)
                return ("", []);
            using var settings = new ObsData().Set("video_device_id", device);
            Obs.obs_properties_apply_settings(props, settings.Handle); // win-dshow fills in the device's sizes
            List<string> sizes = [.. Items(props, "resolution").Select(r => r.Value).Where(v => v.Length > 0)];
            lock (CameraSizesSeen)
                CameraSizesSeen[device] = sizes;
            return (device, sizes);
        }
        finally
        {
            Obs.obs_properties_destroy(props);
        }
    }

    /// <summary>Resets the video pipeline. Every output must be stopped first.</summary>
    public static bool ResetVideo(VideoConfig v)
    {
        var ovi = VideoInfo(v);
        int rc = Obs.obs_reset_video(ref ovi);
        if (rc != ObsConst.VideoSuccess)
            ObsLog.Error($"obs_reset_video returned {rc} for {v}");
        return rc == ObsConst.VideoSuccess;
    }

    /// <summary>libobs's video settings for a canvas and output size (the main picture's, or a view's).</summary>
    public static ObsVideoInfo VideoInfo(VideoConfig v)
    {
        bool downscale = v.OutW < v.BaseW || v.OutH < v.BaseH;
        return new ObsVideoInfo
        {
            GraphicsModule = GraphicsModule,
            FpsNum = (uint)v.Fps,
            FpsDen = 1,
            BaseWidth = (uint)v.BaseW,
            BaseHeight = (uint)v.BaseH,
            OutputWidth = (uint)v.OutW,
            OutputHeight = (uint)v.OutH,
            OutputFormat = ObsConst.VideoFormatNv12,
            Adapter = 0,
            GpuConversion = 1,
            Colorspace = ObsConst.VideoCs709,
            Range = ObsConst.VideoRangePartial,
            ScaleType = downscale ? ObsConst.ScaleArea : ObsConst.ScaleBicubic,
        };
    }

    /// <summary>
    /// libobs frees released sources on a background queue. Draining it (as OBS Studio does) before a rebuild
    /// or shutdown keeps obs_shutdown from freeing the same sources a second time.
    /// </summary>
    public static void DrainDestroyQueue()
    {
        if (!Started)
            return;
        for (int i = 0; i < 10 && Obs.obs_wait_for_destroy_queue(); i++)
        {
        }
    }

    public static void Shutdown()
    {
        if (!Started)
            return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        DrainDestroyQueue();
        Obs.obs_shutdown();
        Started = false;
        ObsLog.Info($"libobs shut down in {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>Public sources and scenes libobs still holds, as "name (type)". Empty after a clean teardown.</summary>
    public static List<string> AliveSources()
    {
        var alive = new List<string>();
        Obs.ObsEnumProc collect = (_, s) =>
        {
            alive.Add($"{Obs.Utf8(Obs.obs_source_get_name(s))} ({Obs.Utf8(Obs.obs_source_get_id(s))})");
            return true;
        };
        Obs.obs_enum_sources(collect, IntPtr.Zero);
        Obs.obs_enum_scenes(collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return alive;
    }

    public static IReadOnlyList<string> EncoderIds()
    {
        var ids = new List<string>();
        for (nuint i = 0; Obs.obs_enum_encoder_types(i, out IntPtr id); i++)
            if (Obs.Utf8(id) is { } s)
                ids.Add(s);
        return ids;
    }

    /// <summary>Items of a list property of a source type, e.g. ("monitor_capture", "monitor_id").</summary>
    public static List<(string Name, string Value)> ListProperty(string sourceId, string property)
    {
        IntPtr props = Obs.obs_get_source_properties(sourceId);
        if (props == IntPtr.Zero)
            return [];
        try
        {
            return Items(props, property);
        }
        finally
        {
            Obs.obs_properties_destroy(props);
        }
    }

    static List<(string Name, string Value)> Items(IntPtr props, string property)
    {
        var items = new List<(string, string)>();
        IntPtr p = Obs.obs_properties_get(props, property);
        if (p == IntPtr.Zero)
            return items;
        nuint count = Obs.obs_property_list_item_count(p);
        for (nuint i = 0; i < count; i++)
            items.Add((Obs.Utf8(Obs.obs_property_list_item_name(p, i)) ?? "",
                       Obs.Utf8(Obs.obs_property_list_item_string(p, i)) ?? ""));
        return items;
    }

    /// <summary>Monitors as libobs names them ("Name: 2560x1440 @ 0,0 (Primary Monitor)").</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        foreach (var (name, id) in ListProperty("monitor_capture", "monitor_id"))
        {
            var m = SizePattern().Match(name);
            if (!m.Success || string.IsNullOrEmpty(id))
                continue;
            list.Add(new MonitorInfo(id, name, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                name.Contains("Primary", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }

    [GeneratedRegex(@"(\d{3,5})x(\d{3,5})")]
    private static partial Regex SizePattern();
}
