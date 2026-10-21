using Kairos.Native;

namespace Kairos.Engine;

/// <summary>What differs between platforms in the libobs set-up: plugins, graphics module, and the capture sources.</summary>
public static class CaptureBackend
{
    public static readonly string[] Modules = ["win-capture", "win-wasapi", "obs-nvenc", "obs-ffmpeg", "obs-x264"];

    /// <summary>
    /// Intel's encoder plugin takes over half a second to start (it probes the Intel driver), while the recording
    /// usually runs on NVIDIA or AMD. It loads once the replay buffer is up, unless the wanted codec has no other
    /// hardware encoder (then it is loaded with the rest, since it could be the pick). It has no post-load step.
    /// </summary>
    public static readonly string[] Later = ["obs-qsv11"];

    public const string GraphicsModule = "libobs-d3d11";

    /// <summary>Whether a game can be hooked directly; where it cannot, Game mode records the screen.</summary>
    public const bool HasGameCapture = true;

    /// <summary>Where the FFmpeg 8 libraries are: beside the program.</summary>
    public static string FfmpegFolder => AppContext.BaseDirectory;

    /// <summary>The file that tells FFmpeg 8 is there.</summary>
    public const string FfmpegMainLibrary = "avcodec-62.dll";

    public static string FfmpegLibraryName(string library, int version) => $"{library}-{version}.dll";

    /// <summary>Whether the video feed is cut while nothing records (the screen is then not captured at all).</summary>
    public const bool DetachVideoWhenIdle = false;

    /// <summary>Whether Game mode also records the screen behind the game source (the setting: shown when no game is running).</summary>
    public static bool ScreenBehindGame(bool fallbackSetting) => fallbackSetting || !HasGameCapture;

    /// <summary>
    /// Where libobs starts: it looks for its own files only as ../../data/libobs/ from the working directory, so in the
    /// one-folder program that is data\libobs itself.
    /// </summary>
    public static string LibobsWorkingDirectory(string appFolder) => Path.Combine(appFolder, "data", "libobs");

    /// <summary>A plugin's DLL and its data folder in the program folder.</summary>
    public static (string Bin, string Data) ModulePaths(string appFolder, string name) =>
        (Path.Combine(appFolder, "obs-plugins", "64bit", name + ".dll"),
         Path.Combine(appFolder, "data", "obs-plugins", name) + Path.DirectorySeparatorChar);

    /// <summary>Whether a source type lists its devices only through an instance (libobs's X11 capture crashes without one).</summary>
    public static bool PropertiesNeedInstance(string sourceId) => false;

    /// <summary>Called before libobs starts: where the platform needs telling about its windowing system (not Windows).</summary>
    public static void BeforeStartup()
    {
    }

    /// <summary>The source type that lists monitors, and the property that holds their ids.</summary>
    public static (string Source, string Property) MonitorList => ("monitor_capture", "monitor_id");

    public static (string Source, string Property) WindowList => ("window_capture", "window");

    public static (string Source, string Property) MicList => ("wasapi_input_capture", "device_id");

    public static (string Source, string Property) SpeakerList => ("wasapi_output_capture", "device_id");

    // Kairos records SDR (NV12, Rec. 709): with Force SDR Windows hands an HDR screen over already in SDR, where the
    // HDR copy recorded black on some HDR monitors (a 4K HDR screen, DXGI, 2026-10-18).
    public static (string Id, ObsData Settings) Screen(string monitorId, bool cursor) =>
        ("monitor_capture", new ObsData().Set("monitor_id", monitorId).Set("method", 0L).Set("capture_cursor", cursor).Set("force_sdr", true));

    public static (string Id, ObsData Settings) Window(string window, bool cursor) =>
        ("window_capture", new ObsData().Set("window", window).Set("priority", 2L).Set("method", 2L).Set("cursor", cursor).Set("client_area", true));

    public static (string Id, ObsData Settings) Game(bool cursor, bool captureAudio) =>
        ("game_capture", new ObsData().Set("capture_mode", "any_fullscreen").Set("capture_cursor", cursor)
            .Set("anti_cheat_hook", true).Set("capture_overlays", false).Set("capture_audio", captureAudio));

    public static (string Id, ObsData Settings) DesktopAudio(string device) =>
        ("wasapi_output_capture", new ObsData().Set("device_id", device));

    public static (string Id, ObsData Settings) Microphone(string device) =>
        ("wasapi_input_capture", new ObsData().Set("device_id", device));

    /// <summary>One app's own sound (its child processes' too), found by its exe whatever its window's title says.</summary>
    public static (string Id, ObsData Settings) AppAudio(string window) =>
        ("wasapi_process_output_capture", new ObsData().Set("window", window).Set("priority", 2L));
}
