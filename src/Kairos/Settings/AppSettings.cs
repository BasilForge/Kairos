using Kairos.Engine.Share;

namespace Kairos.Settings;

public enum CaptureMode { Screen, Window, Game }

public enum VideoCodec { Av1, Hevc, H264 }

public enum QualityPreset { Ultra, High, Balanced, Light, Custom }

public enum OutputResolution { Native, P2160, P1440, P1080, P720 }

public enum VoiceSensitivity { Strict, Normal, Easy }

/// <summary>Sound: every source on its own row and track, or (Kairos 1.0's way) all the PC's sound together.</summary>
public enum AudioLayout { Together, Separate }

public enum AudioSourceKind { Game, Discord, Microphone, OtherSounds, App, Input }

public sealed class AppSettings
{
    /// <summary>The settings file's version: 2 since 1.1 (<see cref="Upgrade"/>).</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public CaptureSettings Capture { get; set; } = new();
    public VideoSettings Video { get; set; } = new();
    public ReplaySettings Replay { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public VoiceSettings Voice { get; set; } = new();
    public ShareSettings Share { get; set; } = new();
    public CameraSettings Camera { get; set; } = new();

    /// <summary>
    /// Windows' own language when Kairos speaks it: the culture or one it falls back to (zh-CN is zh-Hans, es-MX is
    /// es), else the same language in another region (Portugal's Portuguese is Brazil's), never across scripts
    /// (Traditional Chinese is not Simplified); otherwise English.
    /// </summary>
    public static string DefaultLanguage(System.Globalization.CultureInfo uiCulture)
    {
        for (var culture = uiCulture; culture.Name.Length > 0; culture = culture.Parent)
            if (App.Text.IsSupported(culture.Name))
                return culture.Name;
        var regional = App.Text.Languages.FirstOrDefault(l =>
            l.Code.Split('-') is [var language, { Length: 2 }] && language == uiCulture.TwoLetterISOLanguageName);
        return regional?.Code ?? App.Text.Fallback;
    }

    /// <summary>
    /// Brings a file an older Kairos wrote up to date, once: 1.0.1's own quality, never changed (AV1, 1440p, Ultra, 60),
    /// turns Automatic, which records what the PC does best; anything the user chose stays. <paramref name="autoInFile"/>:
    /// the file already holds Automatic (a 1.1 build's, before its version was raised), so it is a choice, left as it is.
    /// </summary>
    public void Upgrade(bool autoInFile)
    {
        if (Version < 2 && !autoInFile && Video is { Auto: false, Codec: VideoCodec.Av1, Fps: 60, Resolution: OutputResolution.P1440, Quality: QualityPreset.Ultra,
                CustomCq: 22, CustomMbps: 40, Preset: "p5" })
            Video.Auto = true;
        Version = CurrentVersion;
    }

    /// <summary>Clamps every value into its supported range and replaces missing sections.</summary>
    public void Normalize()
    {
        Capture ??= new();
        Video ??= new();
        Replay ??= new();
        Audio ??= new();
        // A Windows 10 before 2004 gives no app's own sound: all the PC's sound together, no game- or app-only sound.
        if (!Engine.Audio.ProcessAudioSupport.Available)
        {
            Audio.Layout = AudioLayout.Together;
            Audio.SourceAudioOnly = false;
        }
        Hotkeys ??= new();
        Output ??= new();
        General ??= new();
        Voice ??= new();
        Share ??= new();
        Camera ??= new();
        Camera.Device ??= "";
        Camera.X = double.IsFinite(Camera.X) ? Math.Clamp(Camera.X, 0, 1) : 1;
        Camera.Y = double.IsFinite(Camera.Y) ? Math.Clamp(Camera.Y, 0, 1) : 1;
        Camera.Size = double.IsFinite(Camera.Size) ? Math.Clamp(Camera.Size, CameraSettings.MinSize, CameraSettings.MaxSize) : 0.24;
        if (!Enum.IsDefined(Camera.Layout)) Camera.Layout = CameraLayout.Corner;
        if (!Enum.IsDefined(Camera.Shape)) Camera.Shape = CameraShape.Square;

        Capture.MonitorId ??= "";
        Capture.Window ??= "";
        Capture.Apps = [.. (Capture.Apps ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .DistinctBy(App.WindowTarget.AppKey, StringComparer.OrdinalIgnoreCase)
            .Take(CaptureSettings.MaxApps)];
        Capture.Window = Capture.Apps.FirstOrDefault() ?? "";

        Video.Fps = Math.Clamp(Video.Fps, 10, 240);
        Video.CustomCq = Math.Clamp(Video.CustomCq, 1, 63);
        Video.CustomMbps = Math.Clamp(Video.CustomMbps, 2, 300);
        if (string.IsNullOrWhiteSpace(Video.Preset)) Video.Preset = "p5";

        Replay.Seconds = Math.Clamp(Replay.Seconds, 15, 1200);
        Replay.QuickSeconds = Math.Clamp(Replay.QuickSeconds, 5, 120);

        Audio.SystemVolume = Math.Clamp(Audio.SystemVolume, 0, 2);
        Audio.MicVolume = Math.Clamp(Audio.MicVolume, 0, 2);
        Audio.BitrateKbps = Math.Clamp(Audio.BitrateKbps, 64, 320);
        if (string.IsNullOrWhiteSpace(Audio.SystemDevice)) Audio.SystemDevice = "default";
        if (string.IsNullOrWhiteSpace(Audio.MicDevice)) Audio.MicDevice = "default";
        if (!Enum.IsDefined(Audio.Layout)) Audio.Layout = AudioLayout.Together;
        Audio.MixersSetUp ??= [];
        Audio.Sources = (Audio.Sources ?? []).Where(r => r != null && Enum.IsDefined(r.Kind) && !string.IsNullOrWhiteSpace(r.Id))
            .DistinctBy(r => r.Id).ToList();
        foreach (var row in Audio.Sources)
        {
            row.Target ??= "";
            row.Label ??= "";
            row.Volume = double.IsFinite(row.Volume) ? Math.Clamp(row.Volume, 0, 2) : 1;
        }
        if (Audio.Layout == AudioLayout.Separate)
        {
            // The built-in rows can't be removed: one missing (an old or edited file) comes back, in its place.
            var builtIn = AudioSettings.BuiltInSources();
            for (int i = 0; i < builtIn.Count; i++)
                if (!Audio.Sources.Any(r => r.Kind == builtIn[i].Kind))
                    Audio.Sources.Insert(Math.Min(i, Audio.Sources.Count), builtIn[i]);
        }

        Hotkeys.Save ??= "";
        Hotkeys.Quick ??= "";
        Hotkeys.ToggleBuffer ??= "";
        Hotkeys.Record ??= "";
        Hotkeys.PauseRecord ??= App.HotkeyParser.WithShift(Hotkeys.Record);

        // A relative folder would resolve inside the program folder and be lost when the app is replaced.
        if (string.IsNullOrWhiteSpace(Output.Folder) || !Path.IsPathFullyQualified(Output.Folder)) Output.Folder = OutputSettings.DefaultFolder;
        Output.MaxFolderGb = Math.Clamp(Output.MaxFolderGb, 0, 100_000);

        if (!App.Text.IsSupported(General.Language)) General.Language = App.Text.Fallback;
        if (!Enum.IsDefined(Voice.Sensitivity)) Voice.Sensitivity = VoiceSensitivity.Normal;

        Share.CropPosition = double.IsFinite(Share.CropPosition) ? Math.Clamp(Share.CropPosition, 0, 1) : 0.5;
        Share.Tags ??= "";
        Share.PreviewVolume = double.IsFinite(Share.PreviewVolume) && Share.PreviewVolume is >= 0 and <= 1 ? Share.PreviewVolume : 1;
        Share.VerticalFor = (Share.VerticalFor ?? []).Where(p => Enum.IsDefined(p)).Distinct().ToList();
        if (!SharePlanner.CopySizes.Contains(Share.CopyMb)) Share.CopyMb = 0;
    }
}

public sealed class CaptureSettings
{
    public CaptureMode Mode { get; set; } = CaptureMode.Screen;
    /// <summary>libobs monitor id; empty means the primary monitor.</summary>
    public string MonitorId { get; set; } = "";
    /// <summary>
    /// Before 1.1, Window mode's one app: read once from such a file into <see cref="Apps"/> (SettingsStore), and written
    /// as the first app so an older Kairos still finds one. Nothing else reads it.
    /// </summary>
    public string Window { get; set; } = "";
    /// <summary>Window mode's apps, libobs window strings "title:class:exe", five at most: the one in front is recorded.</summary>
    public List<string> Apps { get; set; } = [];
    public const int MaxApps = 5;
    public bool GameFallbackToScreen { get; set; } = true;
    /// <summary>
    /// Never hook Riot's games (VALORANT, League): the screen is recorded for them instead. No longer on the Capture page
    /// (a game that gives no picture through the hook falls back to the screen by itself); kept for whoever turned it on.
    /// </summary>
    public bool ScreenForRiotGames { get; set; }
    public bool CaptureCursor { get; set; } = true;
}

public sealed record VideoSettings
{
    /// <summary>Kairos picks the encoder, size, frame rate and quality from the hardware (<see cref="Engine.SmartSetup"/>).</summary>
    public bool Auto { get; set; }
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public int Fps { get; set; } = 60;
    public OutputResolution Resolution { get; set; } = OutputResolution.P1440;
    public QualityPreset Quality { get; set; } = QualityPreset.Ultra;
    public int CustomCq { get; set; } = 22;
    public int CustomMbps { get; set; } = 40;
    public string Preset { get; set; } = "p5";
}

public sealed class ReplaySettings
{
    public bool Enabled { get; set; } = true;
    public int Seconds { get; set; } = 300;
    public bool QuickEnabled { get; set; }
    public int QuickSeconds { get; set; } = 30;
    /// <summary>After each save the buffers start empty, so the next clip never repeats this one.</summary>
    public bool FreshAfterSave { get; set; }
    /// <summary>
    /// The replay is kept in a ring file on a drive instead of RAM (<see cref="Engine.Replay.ReplayRing"/>). On by default,
    /// for new installs and for settings from before 1.1 (they have no such key).
    /// </summary>
    public bool OnDisk { get; set; } = true;
    /// <summary>The drive for <see cref="OnDisk"/> ("D:"); null: the clips folder's drive.</summary>
    public string? DiskDrive { get; set; }
}

public sealed class AudioSettings
{
    public bool System { get; set; } = true;
    public string SystemDevice { get; set; } = "default";
    public double SystemVolume { get; set; } = 1.0;
    public bool Mic { get; set; } = true;
    public string MicDevice { get; set; } = "default";
    public double MicVolume { get; set; } = 1.0;
    public bool SeparateTracks { get; set; } = true;
    /// <summary>Window mode: only the captured app's audio. Game mode: only the game's audio.</summary>
    public bool SourceAudioOnly { get; set; }
    public int BitrateKbps { get; set; } = 192;
    /// <summary>Missing in a settings file from before 1.1: Together, as it was. A new install starts Separate (SettingsStore).</summary>
    public AudioLayout Layout { get; set; } = AudioLayout.Together;
    /// <summary>Separate sources: one row each (the built-in ones always there, added apps and devices after them).</summary>
    public List<AudioSource> Sources { get; set; } = [];
    /// <summary>The sound mixers (SteelSeries Sonar, GoXLR) whose sound Kairos has set up, once each (MixerSetup).</summary>
    public List<string> MixersSetUp { get; set; } = [];

    /// <summary>The built-in rows, in their order: each on its own track.</summary>
    public static List<AudioSource> BuiltInSources() =>
    [
        new() { Id = "game", Kind = AudioSourceKind.Game },
        new() { Id = "discord", Kind = AudioSourceKind.Discord },
        new() { Id = "mic", Kind = AudioSourceKind.Microphone },
        new() { Id = "other", Kind = AudioSourceKind.OtherSounds },
    ];

    /// <summary>Picking a microphone means the user wants it recorded.</summary>
    public void SelectMic(string deviceId)
    {
        MicDevice = deviceId;
        Mic = true;
    }
}

public sealed class HotkeySettings
{
    public string Save { get; set; } = "Alt+F10";
    public string Quick { get; set; } = "";
    public string ToggleBuffer { get; set; } = "Alt+Shift+F10";
    public string Record { get; set; } = "Alt+F9";
    /// <summary>Pauses and resumes a full recording. Missing (settings from before it existed): the recording keys plus Shift.</summary>
    public string? PauseRecord { get; set; }
    /// <summary>Takes a screenshot. Alt+F12: free of OBS's F9–F11 and RTSS's Ctrl+F12.</summary>
    public string Screenshot { get; set; } = "Alt+F12";
}

public enum CameraLayout { Corner, Vertical }
public enum CameraShape { Square, Round }

/// <summary>
/// A camera (a webcam, a phone acting as one) over the recording: off until turned on. Corner: a box over the game, its
/// place and size applied live. Vertical: a 9:16 picture, the camera on top and the game below (a rebuild).
/// </summary>
public sealed record CameraSettings
{
    public const double MinSize = 0.1, MaxSize = 0.5;

    public bool Enabled { get; set; }
    /// <summary>libobs's id for the camera (win-dshow's "name:path"); empty: the first one.</summary>
    public string Device { get; set; } = "";
    public CameraLayout Layout { get; set; } = CameraLayout.Corner;
    /// <summary>Corner only: the camera's own shape, or a circle.</summary>
    public CameraShape Shape { get; set; } = CameraShape.Square;
    /// <summary>Corner: the box's left and top, 0..1 of the picture (kept inside it), and its width as a share of the picture's.</summary>
    public double X { get; set; } = 1;
    public double Y { get; set; } = 1;
    public double Size { get; set; } = 0.24;
    /// <summary>The camera also saved on its own, beside each clip ("…camera.mp4").</summary>
    public bool SeparateFile { get; set; }
}

public sealed class OutputSettings
{
    public static readonly string DefaultFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), AppInfo.Name);

    public string Folder { get; set; } = DefaultFolder;
    public bool PerAppFolders { get; set; } = true;
    /// <summary>0 disables the clean-up of old clips.</summary>
    public int MaxFolderGb { get; set; }
    /// <summary>In a OneDrive or Dropbox folder: each saved file is kept online only once uploaded (new installs: on).</summary>
    public bool FreeLocalCopies { get; set; }
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool SoundOnSave { get; set; } = true;
    public bool ToastOnSave { get; set; } = true;
    /// <summary>Full screen only: <see cref="Engine.SceneRules.PausesWithoutGame"/>.</summary>
    public bool OnlyWhenGaming { get; set; }
    public string Language { get; set; } = "ar";
    /// <summary>Look for a newer Kairos once a day (off: Kairos never connects for it).</summary>
    public bool CheckUpdates { get; set; } = true;
}

/// <summary>One row of the Separate sources layout.</summary>
public sealed class AudioSource
{
    public string Id { get; set; } = "";
    public AudioSourceKind Kind { get; set; }
    /// <summary>An added app's exe name (no ".exe"), or an added input device's id.</summary>
    public string Target { get; set; } = "";
    /// <summary>What an added app or device is called on its row and its track (the built-in rows have their own names).</summary>
    public string Label { get; set; } = "";
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; }
    public bool OwnTrack { get; set; } = true;
}

/// <summary>Smart Save: save the replay when the user says their recorded phrase.</summary>
public sealed class VoiceSettings
{
    public bool Enabled { get; set; }
    public VoiceSensitivity Sensitivity { get; set; } = VoiceSensitivity.Normal;
    /// <summary>The status-page card that introduces Smart Save was closed.</summary>
    public bool HintDismissed { get; set; }

    /// <summary>
    /// Normal talk scored 0.114 and up against a real user's phrase while their attempts scored 0.05-0.08,
    /// so no threshold goes past 0.100, keeping a margin against saving on ordinary conversation.
    /// </summary>
    public const double MaxThreshold = 0.100;

    /// <summary>
    /// Highest phrase-matching cost that still counts as the phrase. <paramref name="spread"/> is how far the
    /// user's own samples are from each other (<see cref="Engine.Voice.VoicePhrase.Spread"/>): a real voice varies
    /// more than the synthetic voices the floors came from, so the threshold grows with it.
    /// </summary>
    public static double Threshold(VoiceSensitivity s, double spread = 0)
    {
        (double floor, double factor) = s switch
        {
            VoiceSensitivity.Strict => (0.052, 1.5),
            VoiceSensitivity.Easy => (0.066, 2.1),
            _ => (0.060, 1.8),
        };
        return Math.Min(MaxThreshold, Math.Max(floor, factor * spread));
    }
}

/// <summary>The share window's last choices, so the next share starts where the user left off.</summary>
public sealed class ShareSettings
{
    /// <summary>The signed-in X account has Premium (seen on its page while posting); null until known.</summary>
    public bool? XPremium { get; set; }
    /// <summary>Platforms the user turned the 9:16 cut on for (off for every platform at first).</summary>
    public List<SharePlatform> VerticalFor { get; set; } = [];
    /// <summary>Where the 9:16 window sits: 0 = left edge, 1 = right edge.</summary>
    public double CropPosition { get; set; } = 0.5;
    /// <summary>The user's own hashtags, added after the game's tag.</summary>
    public string Tags { get; set; } = "#gaming";
    /// <summary>Loudness of the share panel's preview (0..1); the shared clip keeps its own sound.</summary>
    public double PreviewVolume { get; set; } = 1;
    public bool PreviewMuted { get; set; }
    /// <summary>Copy made to fit this many MB (Discord's 10, 50 or 500); 0 copies the clip at its own size.</summary>
    public int CopyMb { get; set; }

    public ShareSettings Copy()
    {
        var copy = (ShareSettings)MemberwiseClone();
        copy.VerticalFor = [.. VerticalFor];
        return copy;
    }
}
