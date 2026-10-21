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
    static string[] Modules => CaptureBackend.Modules;
    static string[] Later => CaptureBackend.Later;
    static bool _laterLoaded;
    static readonly IntPtr GraphicsModule = Marshal.StringToCoTaskMemUTF8(CaptureBackend.GraphicsModule);

    public static bool Started { get; private set; }
    public static List<string> FailedModules { get; } = [];

    internal static string LibobsWorkingDirectory(string appFolder) => CaptureBackend.LibobsWorkingDirectory(appFolder);

    internal static (string Bin, string Data) ModulePaths(string appFolder, string name) => CaptureBackend.ModulePaths(appFolder, name);

    /// <summary>Starts libobs. <paramref name="appFolder"/> is the program folder (obs.dll, data\, obs-plugins\).</summary>
    /// <param name="wanted">The codec the settings ask for (which encoder plugins must be in before the first pick).</param>
    public static void Start(string appFolder, string configDir, string logDir, VideoConfig video, VideoCodec wanted = VideoCodec.Av1)
    {
        if (Started)
            throw new InvalidOperationException("libobs already started");

        Directory.SetCurrentDirectory(LibobsWorkingDirectory(appFolder));
        ObsLog.Open(logDir);
        ObsLog.HookLibobs();
        CaptureBackend.BeforeStartup();

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
        long graphics = clock.ElapsedMilliseconds;

        _root = appFolder;
        FailedModules.Clear();
        _laterLoaded = false;
        var took = new List<string>();
        foreach (string name in Modules)
            took.Add(LoadModule(name));
        if (!EncoderCatalog.HasHardware(wanted, EncoderIds()))
            LoadLater(took);
        long modules = clock.ElapsedMilliseconds;
        Obs.obs_post_load_modules();
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

    /// <summary>Resets the video pipeline. Every output must be stopped first.</summary>
    public static bool ResetVideo(VideoConfig v)
    {
        bool downscale = v.OutW < v.BaseW || v.OutH < v.BaseH;
        var ovi = new ObsVideoInfo
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
        int rc = Obs.obs_reset_video(ref ovi);
        if (rc != ObsConst.VideoSuccess)
            ObsLog.Error($"obs_reset_video returned {rc} for {v}");
        return rc == ObsConst.VideoSuccess;
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
        var items = new List<(string, string)>();
        // Some sources (X11 screen capture) cannot describe themselves without an instance.
        IntPtr instance = CaptureBackend.PropertiesNeedInstance(sourceId) ? Obs.obs_source_create(sourceId, "properties", IntPtr.Zero, IntPtr.Zero) : IntPtr.Zero;
        IntPtr props = CaptureBackend.PropertiesNeedInstance(sourceId)
            ? instance == IntPtr.Zero ? IntPtr.Zero : Obs.obs_source_properties(instance)
            : Obs.obs_get_source_properties(sourceId);
        if (props == IntPtr.Zero)
        {
            if (instance != IntPtr.Zero)
                Obs.obs_source_release(instance);
            return items;
        }
        try
        {
            IntPtr p = Obs.obs_properties_get(props, property);
            if (p == IntPtr.Zero)
                return items;
            nuint count = Obs.obs_property_list_item_count(p);
            bool numbers = Obs.obs_property_list_format(p) == 1; // X11 screens are an int list; reading one as a string crashes
            for (nuint i = 0; i < count; i++)
                items.Add((Obs.Utf8(Obs.obs_property_list_item_name(p, i)) ?? "",
                           numbers ? Obs.obs_property_list_item_int(p, i).ToString() : Obs.Utf8(Obs.obs_property_list_item_string(p, i)) ?? ""));
        }
        finally
        {
            Obs.obs_properties_destroy(props);
            if (instance != IntPtr.Zero)
                Obs.obs_source_release(instance);
        }
        return items;
    }

    /// <summary>Monitors as libobs names them ("Name: 2560x1440 @ 0,0 (Primary Monitor)").</summary>
    public static List<MonitorInfo> Monitors()
    {
        var list = new List<MonitorInfo>();
        foreach (var (name, id) in ListProperty(CaptureBackend.MonitorList.Source, CaptureBackend.MonitorList.Property))
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
