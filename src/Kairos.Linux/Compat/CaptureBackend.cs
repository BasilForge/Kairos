using Kairos.Native;

namespace Kairos.Engine;

/// <summary>What differs between platforms in the libobs set-up: plugins, graphics module, and the capture sources.</summary>
public static class CaptureBackend
{
    public static readonly string[] Modules = ["linux-capture", "linux-pulseaudio", "linux-pipewire", "obs-nvenc", "obs-ffmpeg", "obs-x264"];

    /// <summary>Nothing is held back on Linux: Intel and AMD encode through obs-ffmpeg's VAAPI.</summary>
    public static readonly string[] Later = [];

    public const string GraphicsModule = "libobs-opengl";

    /// <summary>
    /// Games mode records the window in front through the compositor (xcomposite): the window's pixmap becomes a GPU texture with
    /// no copy through the CPU. Whole-screen capture (xshm) makes the X server copy every frame, which cost about 40% of a core
    /// together at 1080p60 on a 3440x1440 screen, against about 8% for a window.
    /// </summary>
    public const bool HasGameCapture = true;

    /// <summary>FFmpeg 8 (Mint and Ubuntu ship 6): beside the program in ffmpeg/lib, else the development copy tools/get-ffmpeg.sh makes.</summary>
    public static string FfmpegFolder
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "lib");
            return Directory.Exists(bundled) ? bundled : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ffmpeg8", "lib");
        }
    }

    public const string FfmpegMainLibrary = "libavcodec.so.62";

    public static string FfmpegLibraryName(string library, int version) => $"lib{library}.so.{version}";

    /// <summary>
    /// Cut the video feed while the buffer is paused and nothing records. libobs keeps capturing a source that is on the output
    /// channel whether or not an encoder listens, and on X11 that is the X server copying every frame (measured: Xorg at 80-90%
    /// of a core with the buffer paused, against 7% without Kairos).
    /// </summary>
    public const bool DetachVideoWhenIdle = true;

    /// <summary>The window source follows focus (see GameFollower), so no screen is recorded behind it.</summary>
    public static bool ScreenBehindGame(bool fallbackSetting) => false;

    /// <summary>
    /// The libobs install: KAIROS_OBS_PREFIX, else obs\ beside the program (what the package bundles), else a prefix
    /// in the home folder (a development build, see tools/build-libobs.sh). It holds lib\ and share\obs\.
    /// </summary>
    public static string Prefix(string appFolder)
    {
        if (Environment.GetEnvironmentVariable("KAIROS_OBS_PREFIX") is { Length: > 0 } env)
            return env;
        string bundled = Path.Combine(appFolder, "obs");
        return Directory.Exists(bundled) ? bundled : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "obs-prefix");
    }

    /// <summary>libobs finds its effects through the data path compiled into it, so any folder that exists will do.</summary>
    public static string LibobsWorkingDirectory(string appFolder) => Path.Combine(Prefix(appFolder), "share", "obs", "libobs");

    /// <summary>A plugin's shared object and its data folder.</summary>
    public static (string Bin, string Data) ModulePaths(string appFolder, string name) =>
        (Path.Combine(Prefix(appFolder), "lib", "obs-plugins", name + ".so"),
         Path.Combine(Prefix(appFolder), "share", "obs", "obs-plugins", name) + Path.DirectorySeparatorChar);

    /// <summary>Whether a source type lists its devices only through an instance (libobs's X11 capture crashes without one). Not the audio sources: an instance of one opens a recording stream.</summary>
    public static bool PropertiesNeedInstance(string sourceId) => sourceId is "xshm_input" or "xcomposite_input";

    /// <summary>
    /// libobs on Linux is told the windowing system and its X connection by the program (OBS Studio does the same):
    /// the X11 capture sources and the OpenGL context use it. Under Wayland that is XWayland.
    /// </summary>
    public static void BeforeStartup() => NixPlatform.Prepare();

    public static (string Source, string Property) MonitorList => ("xshm_input", "screen");

    public static (string Source, string Property) WindowList => ("xcomposite_input", "capture_window");

    public static (string Source, string Property) MicList => ("pulse_input_capture", "device_id");

    public static (string Source, string Property) SpeakerList => ("pulse_output_capture", "device_id");

    public static (string Id, ObsData Settings) Screen(string monitorId, bool cursor) =>
        ("xshm_input", new ObsData().Set("screen", long.TryParse(monitorId, out long screen) ? screen : 0L).Set("show_cursor", cursor));

    public static (string Id, ObsData Settings) Window(string window, bool cursor) =>
        ("xcomposite_input", new ObsData().Set("capture_window", window).Set("show_cursor", cursor));

    public static (string Id, ObsData Settings) Game(bool cursor, bool captureAudio) =>
        ("xcomposite_input", new ObsData().Set("capture_window", Kairos.App.GameFollower.Target() ?? "").Set("show_cursor", cursor));

    public static (string Id, ObsData Settings) DesktopAudio(string device) =>
        ("pulse_output_capture", new ObsData().Set("device_id", device));

    public static (string Id, ObsData Settings) Microphone(string device) =>
        ("pulse_input_capture", new ObsData().Set("device_id", device));

    /// <summary>PipeWire can capture one app's sound; until that is wired up the desktop mix stands in.</summary>
    public static (string Id, ObsData Settings) AppAudio(string window) => DesktopAudio("default");
}
