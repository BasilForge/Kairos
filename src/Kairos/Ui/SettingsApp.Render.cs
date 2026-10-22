using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kairos.App;
using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>
/// The window drawn and checked without being seen (Kairos.exe --render, --selftest-reload): every page and state as a
/// PNG, on made-up or repository files, never the user's.
/// </summary>
public static partial class SettingsApp
{
    /// <summary>A popup's content drawn on its own, in the flyout's frame.</summary>
    static FrameworkElement Standalone(FrameworkElement content)
    {
        var resources = System.Windows.Application.Current.Resources;
        var flyout = new System.Windows.Controls.Border
        {
            Background = (Brush)resources["FlyoutBackground"],
            BorderBrush = (Brush)resources["FlyoutBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12),
            Child = content,
        };
        flyout.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimary");
        return new System.Windows.Controls.Border
        {
            Padding = new Thickness(24),
            Child = flyout,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>The text tool's fonts as its list shows them: each name in its own font, the current one marked.</summary>
    static FrameworkElement FontList()
    {
        var list = new System.Windows.Controls.StackPanel { Width = 220 };
        foreach (var font in Edit.TextFonts.All)
            list.Children.Add(new System.Windows.Controls.Border
            {
                Background = font.Key == Edit.TextFonts.Default ? (Brush)System.Windows.Application.Current.Resources["ComboBoxItemBackgroundSelected"] : Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Child = new System.Windows.Controls.TextBlock { Text = font.Label, FontFamily = font.Family, FontWeight = font.Weight, FontSize = 14 },
            });
        return list;
    }

    /// <summary>Auto captions for the render states: made-up lines, or still listening, or the model not downloaded yet.</summary>
    sealed class RenderCaptionMaker(bool present, bool listening) : Edit.ICaptionMaker
    {
        public bool Ready => present;

        public long DownloadBytes => present ? 0 : Engine.Captions.CaptionModel.Bytes + Engine.Captions.CaptionRuntime.Bytes;

        public Task Download(IProgress<double> progress, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<Engine.Captions.Caption>> Listen(string clip, IReadOnlyList<int> streams, IProgress<double> progress, CancellationToken ct)
        {
            if (listening)
            {
                progress.Report(0.42);
                return new TaskCompletionSource<IReadOnlyList<Engine.Captions.Caption>>().Task;
            }
            IReadOnlyList<Engine.Captions.Caption> lines = Text.IsArabic
                ? [new(0, 2.5, "يلا ادفعوا يسار، ادفعوا يسار"), new(2.5, 4, "باقي له طلقة وحدة"), new(4, 6, "حلو حلو حلو")]
                : [new(0, 2.5, "Okay push left, push left"), new(2.5, 4, "He is one shot"), new(4, 6, "Nice nice nice")];
            return Task.FromResult(lines);
        }
    }

    /// <summary>
    /// The editor's sample edit (made up, on the repository's test clip): a title, two captions, two lines of different
    /// colours, an arrow, a circle and a blur box; a part cut out and a faster, louder part; the playhead on the first part.
    /// </summary>
    static (Engine.Edit.TextItem Title, Engine.Edit.StrokeItem Line, Engine.Edit.ShapeItem Arrow) AddSampleItems(Edit.EditorViewModel vm)
    {
        var p = vm.Project;
        double end = Math.Max(0, p.Duration);
        var title = new Engine.Edit.TextItem(Guid.NewGuid(), 0, end, Text.IsArabic ? "أقوى لقطة" : "GG WP", new Rect(0.06, 0.08, 0.3, 0.1), "#FFFFFF", 0.06,
            Engine.Edit.TextEffect.Box);
        var group = Guid.NewGuid();
        double half = end / 2;
        var cap1 = new Engine.Edit.TextItem(Guid.NewGuid(), 0.2, Math.Max(0.4, half - 0.2), Text.IsArabic ? "يلا ادفعوا يسار" : "Okay push left",
            new Rect(0.3, 0.8, 0.4, 0.09), "#FFD54A", 0.06, Engine.Edit.TextEffect.Outline, "segoe", group);
        var cap2 = cap1 with { Id = Guid.NewGuid(), Start = half, End = end, Text = Text.IsArabic ? "باقي له طلقة وحدة" : "He is one shot" };
        var arrow = new Engine.Edit.ShapeItem(Guid.NewGuid(), 0, end, Engine.Edit.ShapeKind.Arrow, new Point(0.42, 0.62), new Point(0.6, 0.45), "#F48BA4", 0.008);
        var ring = new Engine.Edit.ShapeItem(Guid.NewGuid(), half, end, Engine.Edit.ShapeKind.Ellipse, new Point(0.62, 0.3), new Point(0.78, 0.55), "#FFD54A", 0.007);
        var blur = new Engine.Edit.BlurItem(Guid.NewGuid(), 0, half, new Rect(0.72, 0.72, 0.22, 0.16));
        var green = new Engine.Edit.StrokeItem(Guid.NewGuid(), 0, half,
            [[new Point(0.1, 0.8), new Point(0.15, 0.74), new Point(0.2, 0.79), new Point(0.25, 0.73), new Point(0.3, 0.78)]], "#3DDC84", 0.007);
        var blue = new Engine.Edit.StrokeItem(Guid.NewGuid(), half, end,
            [[new Point(0.12, 0.3), new Point(0.2, 0.26), new Point(0.28, 0.32)]], "#4FC3F7", 0.007);
        var sample = p.With(title).With(arrow).With(ring).With(blur).With(green).With(blue);
        vm.Apply(p.Duration > 0 ? sample.With(cap1).With(cap2) : sample); // a screenshot has no captions
        if (p.Duration > 3)
        {
            // A cut out part and a faster, louder one.
            var edited = vm.Project.SplitAt(2).SplitAt(3).DeleteSegmentAt(2.5);
            edited = edited.SetSpeed(0, 2, 2);
            edited = edited.WithParts([0], k => k with { Levels = k.Levels.With(0, 1.4) });
            vm.Apply(edited);
            vm.PlayheadOutput = 0.6;
        }
        return (title, green, arrow);
    }

    /// <summary>
    /// Kairos's mouse pointers side by side (as at 200%), on the app's dark background and on a bright picture, each named
    /// (for --render cursors).
    /// </summary>
    static int RenderCursors(System.Windows.Application app, string outPng)
    {
        var shapes = Enum.GetValues<ThemeCursors.Shape>();
        string[] names = ["Arrow", "Text", "Cross", "Move", "Stretch", "Up-down", "Corner", "Corner", "Pen", "Shapes", "Blur", "Crop"];
        var root = new System.Windows.Controls.StackPanel { Background = (Brush)app.Resources["Bg"] };
        foreach (bool bright in new[] { false, true })
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(24, 18, 24, 6) };
            row.Background = bright
                ? new LinearGradientBrush(Color.FromRgb(0xFF, 0xE8, 0xC8), Color.FromRgb(0x9F, 0xD8, 0xF7), 0)
                : (Brush)app.Resources["Surface"];
            for (int i = 0; i < shapes.Length; i++)
            {
                // At their real size, and twice as big beside it.
                var p = ThemeCursors.Picture(shapes[i]) ?? ThemeCursors.SystemPicture(shapes[i])!;
                var picture = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Bgra32, null, p.Bgra, p.Width * 4);
                var cell = new System.Windows.Controls.StackPanel { Margin = new Thickness(10, 12, 10, 10) };
                var both = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                both.Children.Add(new System.Windows.Controls.Image { Source = picture, Width = p.Width, Height = p.Height, VerticalAlignment = VerticalAlignment.Top });
                var big = new System.Windows.Controls.Image { Source = picture, Width = p.Width * 2, Height = p.Height * 2, Margin = new Thickness(6, 0, 0, 0) };
                RenderOptions.SetBitmapScalingMode(big, BitmapScalingMode.NearestNeighbor);
                both.Children.Add(big);
                cell.Children.Add(both);
                cell.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = names[i],
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = bright ? Brushes.Black : (Brush)app.Resources["TextSecondary"],
                    Margin = new Thickness(0, 6, 0, 0),
                });
                row.Children.Add(cell);
            }
            root.Children.Add(row);
        }
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight + 6, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        using var file = File.Create(outPng);
        encoder.Save(file);
        return 0;
    }

    /// <summary>FFmpeg for the editor's and the player's renders: Kairos's own, else (a development build has none) OBS's.</summary>
    static void LoadRenderFfmpeg()
    {
        foreach (string folder in new[] { Engine.Share.ClipTranscoder.DefaultFolder, @"C:\Program Files\obs-studio\bin\64bit" })
            if (File.Exists(Path.Combine(folder, "avcodec-62.dll")))
            {
                Engine.Share.ClipTranscoder.Load(folder);
                return;
            }
    }

    /// <summary>The repository's test clip (renders run from the build folder inside it), or KAIROS_RENDER_CLIP, or null.</summary>
    static string? SampleClip()
    {
        if (Environment.GetEnvironmentVariable("KAIROS_RENDER_CLIP") is { } given && File.Exists(given))
            return given;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "Kairos.Tests", "Fixtures", "Share", "clip-h264.mp4");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Draws one page to a PNG without creating a visible window (no focus change, nothing on screen).
    /// Usage: --render &lt;page&gt; &lt;out.png&gt; [ar|en] [theme color id] [enrolling|testing]
    /// The last argument shows a Smart Save state that otherwise needs the engine to be in that mode.
    /// </summary>
    public static int Render(string page, string outPng, string? language, string? voiceState = null)
    {
        Text.Language = language ?? SettingsStore.Load(SettingsStore.DefaultPath).General.Language;
        var app = CreateApplication();
        if (page == "cursors")
            return RenderCursors(app, outPng);
        var vm = new SettingsViewModel();

        StatusDto? status = null;
        CapsDto? caps = null;
        try
        {
            if (PipeClient.Send("status", 800) is { } s)
                status = JsonSerializer.Deserialize<StatusDto>(s, Ipc.Json);
            if (PipeClient.Send("caps", 3000) is { } c)
                caps = JsonSerializer.Deserialize<CapsDto>(c, Ipc.Json);
        }
        catch (JsonException) { }
        if (voiceState != null)
            status = (status ?? new StatusDto(null, [], null, null, false, false, "")) with
            {
                Voice = voiceState == "testing"
                    ? new Engine.Voice.VoiceStatus(true, true, "testing", 0, null, 0.031, true, 0.06)
                    : new Engine.Voice.VoiceStatus(true, false, "enrolling", 1, "voice.err.different", null, false, 0.06),
            };
        // status idle|recording|record-paused|ready: before a recording, during one, paused, or instant replay off;
        // notice-noapp|notice-gone|notice-guard|notice-nvidia: idle, saying why the full screen is recorded or the driver is old (made-up figures).
        string? notice = voiceState switch
        {
            "notice-noapp" => "status.captureNoApp",
            "notice-gone" => "status.captureGone",
            "notice-guard" => "status.antiCheat",
            _ => null,
        };
        if (voiceState is "recording" or "record-paused" or "ready" or "idle" or "notice-nvidia" || notice != null)
        {
            bool recording = voiceState is "recording" or "record-paused";
            var engine = new Engine.EngineStatus(voiceState != "ready", recording, 60, 0, 0, 1000, 300, 300,
                "obs_nvenc_av1_tex", 2560, 1440, 729, 600, @"D:\Clips", false, null,
                voiceState == "record-paused", 754, 1_300_000_000, notice, NvidiaNote: voiceState == "notice-nvidia" ? "status.nvidiaDriver" : null);
            status = new StatusDto(engine, [], @"D:\Clips\Desktop\Desktop 2026-10-14 20-37-26.mp4", null, false, false, AppInfo.Version);
            if (voiceState == "ready")
                vm.SeedReplay(false);
        }
        // clips-pictures | viewer: the Clips page over made-up screenshots in a temp folder, and one of them in the viewer.
        // status-latest: the Status page with the made-up clip and screenshot as the latest ones.
        string? samples = page is "clips-pictures" or "clips-chosen" or "clips-confirm" or "clips-preparing" or "viewer" or "status-latest" or "video-viewer"
            || page.StartsWith("editor", StringComparison.Ordinal)
            || page.StartsWith("share", StringComparison.Ordinal)
            ? SamplePictures() : null;
        if (samples != null)
        {
            string? Newest(string pattern) => Directory.GetFiles(samples, pattern, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            status = new StatusDto(new Engine.EngineStatus(true, false, 60, 0, 0, 1000, 300, 300, "obs_nvenc_av1_tex", 2560, 1440,
                700, 600, samples, false, null), [], Newest("*.mp4"), null, false, false, AppInfo.Version, LastShot: Newest("*.png"));
        }
        // <page> update-available|update-downloading|update-waiting|update-failed: the update bar over the page (made up);
        // update-important: an important one, the card on the Status page with made-up lines.
        if (voiceState?.StartsWith("update-", StringComparison.Ordinal) == true)
        {
            var state = voiceState switch
            {
                "update-downloading" => Engine.Updates.UpdateState.Downloading,
                "update-waiting" => Engine.Updates.UpdateState.WaitingForRecording,
                "update-window" => Engine.Updates.UpdateState.WaitingForWindow,
                "update-failed" => Engine.Updates.UpdateState.Failed,
                _ => Engine.Updates.UpdateState.Available,
            };
            status = (status ?? new StatusDto(null, [], null, null, false, false, AppInfo.Version)) with
            {
                Update = new Engine.Updates.UpdateStatus("1.0.1", state, 40, Engine.Updates.UpdateRules.NotesUrl("1.0.1", localSource: false),
                    Important: voiceState == "update-important",
                    Summary: Text.Language == "ar" ? ["إصلاح الشاشة السوداء في بعض الألعاب", "التسجيل أخف على الجهاز"] : ["Black screen fix in some games", "Lighter recording"]),
            };
        }
        // audio separate|separate-full|together: the Audio page over made-up settings, devices and apps (Discord not
        // running); separate-full adds two apps, so the last one shares the sixth track; separate-new adds Spotify unsaved;
        // together-sonar has SteelSeries Sonar on (Default is its Gaming device, one pair of headphones); together-goxlr and
        // separate-goxlr have a GoXLR, Default on its System device and the microphone on its Broadcast Stream Mix.
        bool audioState = page == "audio" && voiceState is "separate" or "separate-full" or "separate-new" or "together" or "together-sonar"
            or "together-goxlr" or "separate-goxlr";
        if (audioState)
        {
            bool goxlr = voiceState is "together-goxlr" or "separate-goxlr";
            var made = new AppSettings();
            made.Audio.Layout = voiceState is "together" or "together-sonar" or "together-goxlr" ? AudioLayout.Together : AudioLayout.Separate;
            made.Normalize();
            if (voiceState == "separate-full")
                made.Audio.Sources.AddRange([
                    new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", Label = "Spotify", Volume = 0.6 },
                    new AudioSource { Id = "app:brave", Kind = AudioSourceKind.App, Target = "brave", Label = "Brave", Muted = true },
                ]);
            if (goxlr)
                made.Audio.SelectMic("goxlr-mix");
            vm.SeedSettings(made);
            if (voiceState == "separate-new")
                vm.AddAudioAppCommand.Execute(new NameValue("Spotify", "Spotify"));
            caps = new CapsDto([], [], [new("Default", "default"), new("Microphone (Shure MV7)", "mv7")], [new("Default", "default")], [], "32.2.2",
                [new("Chrome", "chrome"), new("Spotify", "Spotify")]);
            if (voiceState == "together-sonar")
                caps = caps with
                {
                    Speakers = [new("Default", "default"), new("Headphones (Arctis Nova Pro Wireless)", "arctis"),
                        new("SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)", "sonar-game"),
                        new("SteelSeries Sonar - Chat (SteelSeries Sonar Virtual Audio Device)", "sonar-chat")],
                    DefaultSpeaker = "sonar-game",
                };
            if (goxlr)
                caps = caps with
                {
                    Mics = [new("Default", "default"), new("Broadcast Stream Mix (TC-Helicon GoXLR)", "goxlr-mix"), new("Chat Mic (TC-Helicon GoXLR)", "goxlr-voice")],
                    Speakers = [new("Default", "default"), new("System (TC-Helicon GoXLR)", "goxlr-system"), new("Game (TC-Helicon GoXLR)", "goxlr-game"),
                        new("Chat (TC-Helicon GoXLR)", "goxlr-chat"), new("Music (TC-Helicon GoXLR)", "goxlr-music")],
                    DefaultSpeaker = "goxlr-system",
                };
            status = new StatusDto(new Engine.EngineStatus(true, false, 60, 0, 0, 1000, 300, 300, "obs_nvenc_av1_tex", 2560, 1440,
                700, 600, @"D:\Clips", false, null, QuietAudioRows: ["discord", "app:brave"]), [], null, null, false, false, AppInfo.Version);
        }
        // capture camera: the Camera card on, a round camera in a corner, its own file kept (made-up camera list).
        if (page == "capture" && voiceState == "camera")
        {
            var made = new AppSettings();
            made.Camera.Enabled = true;
            made.Camera.Shape = CameraShape.Round;
            made.Camera.X = 0.72;
            made.Camera.Y = 0.55;
            made.Camera.SeparateFile = true;
            made.Normalize();
            vm.SeedSettings(made);
        }
        // capture apps: Apps mode with two chosen apps (one closed), more open to add, and Start fresh after each save on.
        // capture disk: the replay on the disk, its drive this PC's first hard disk when it has one (its line shows).
        if (page == "capture" && voiceState == "disk")
        {
            var made = new AppSettings();
            made.Replay.OnDisk = true;
            made.Replay.DiskDrive = App.DriveKinds.Fixed().FirstOrDefault(d => d.Hdd == true)?.Name;
            made.Normalize();
            vm.SeedSettings(made);
        }
        // capture apps[-win10]: Apps with two chosen apps; -win10: as on Windows 10 (its yellow-frame line shows).
        if (page == "capture" && voiceState is "apps" or "apps-win10")
        {
            Engine.WindowsCaptureFrame.Shown = voiceState == "apps-win10";
            var made = new AppSettings();
            made.Capture.Mode = CaptureMode.Window;
            made.Capture.Apps = ["Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe", "Minecraft* 1.21:GLFW30:javaw.exe"];
            made.Replay.FreshAfterSave = true;
            made.Normalize();
            vm.SeedSettings(made);
            caps = new CapsDto([], [new("Roblox", "Roblox:WINDOWSCLIENT:RobloxPlayerBeta.exe"), new("Discord", "Discord:Chrome_WidgetWin_1:Discord.exe"),
                new("Spotify Premium", "Spotify Premium:Chrome_WidgetWin_1:Spotify.exe")], [], [], [], "32.2.2");
        }
        // general cloud: the clips folder in the first cloud folder on this PC (its buttons and Free local copies show).
        // general no-cloud: a PC with no cloud app (the line saying how to get one).
        if (page == "general" && voiceState == "no-cloud")
            SettingsViewModel.Clouds = () => [];
        if (page == "general" && voiceState == "cloud" && App.CloudFolders.OnThisPc() is [var cloud, ..])
        {
            var made = new AppSettings();
            made.Output.Folder = Path.Combine(cloud.Path, AppInfo.Name);
            made.Output.FreeLocalCopies = true;
            made.Normalize();
            vm.SeedSettings(made);
        }
        // quality auto|manual: Automatic on (what it chose on a made-up RTX 3060), or off with 144 fps on a 60 Hz screen.
        if (page == "quality" && voiceState is "auto" or "manual")
        {
            var made = new AppSettings();
            made.Video.Auto = voiceState == "auto";
            made.Video.Fps = voiceState == "auto" ? 60 : 144;
            made.Normalize();
            vm.SeedSettings(made);
            caps = new CapsDto([], [], [], [], ["obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"], "32.2.2", RefreshHz: 60);
            var auto = new Engine.AutoPick("obs_nvenc_h264_tex", "NVIDIA GeForce RTX 3060", 1440, 60, QualityPreset.High);
            status = new StatusDto(new Engine.EngineStatus(true, false, 60, 0, 0, 1000, 300, 300, "obs_nvenc_h264_tex", 2560, 1440,
                700, 600, @"D:\Clips", false, null, Auto: auto), [], null, null, false, false, AppInfo.Version);
        }
        // quality av1-note: AV1 chosen (a made-up RTX 4070) on a Windows without the AV1 extension.
        if (page == "quality" && voiceState == "av1-note")
        {
            var made = new AppSettings();
            made.Video.Codec = VideoCodec.Av1;
            made.Normalize();
            vm.SeedSettings(made);
            SettingsViewModel.Plays = c => c != VideoCodec.Av1;
            caps = new CapsDto([], [], [], [], ["obs_nvenc_av1_tex", "obs_nvenc_hevc_tex", "obs_nvenc_h264_tex", "obs_x264"], "32.2.2", RefreshHz: 144);
            status = new StatusDto(new Engine.EngineStatus(true, false, 60, 0, 0, 1000, 300, 300, "obs_nvenc_av1_tex", 2560, 1440,
                700, 600, @"D:\Clips", false, null), [], null, null, false, false, AppInfo.Version);
        }
        // general check-idle|check-checking|check-uptodate|check-failed: the update row's Check now (never sent to an engine).
        if (page == "general" && voiceState is "check-idle" or "check-checking" or "check-uptodate" or "check-failed")
            status = new StatusDto(null, [], null, null, false, false, AppInfo.Version, Look: new Engine.Updates.UpdateLook(true, false, 1, Engine.Updates.LookResult.None));
        vm.Seed(status, caps);
        if (page == "general" && voiceState is "check-checking" or "check-uptodate" or "check-failed")
        {
            SettingsViewModel.PipeSend = _ => null;
            vm.CheckUpdatesNowCommand.Execute(null);
            if (voiceState != "check-checking")
                vm.Seed(status! with { Look = new Engine.Updates.UpdateLook(true, false, 2,
                    voiceState == "check-failed" ? Engine.Updates.LookResult.Failed : Engine.Updates.LookResult.UpToDate) }, caps);
        }
        ThemeAccent.Apply(app.Resources);

        // share[-x|-tiktok|-instagram|-copy|-all|-picture|-working|-done|-failed|-posting|-posted|-all-posting|-all-done]: the share
        // panel over the Clips page; -copysizes: Copy's sizes open under it, -copy-toolong: a cut too long for 10 MB, -copying:
        // converting to it.
        bool share = page.StartsWith("share", StringComparison.Ordinal);
        var shareHost = share ? new RenderShareHost(page) : null;
        var window = new MainWindow(vm, shareHost, new RenderAccountsHost());
        if (page == "probe")
            return Probe(window);
        if (page == "navtime")
            return NavTime(window, vm);
        if (page == "livecheck")
            return LiveCheck(window, vm, outPng);
        if (share)
            shareHost!.Show(window.ShowShare(page == "share-picture" ? RenderShareHost.Shot : RenderShareHost.Clip));
        else if (page == "status-latest")
            window.ShowPage("status");
        else if (page.StartsWith("editor", StringComparison.Ordinal))
        {
            // editor[-picture][-<state>][-share][-min] on the test clip or a sample screenshot (never the user's files):
            // states empty (nothing added), part, sound, text, captions[-ask|-listening], line, draw, shape, mixed, crop, zoom, zoomed,
            // save, leave.
            string? clip = SampleClip();
            LoadRenderFfmpeg();
            window.ShowPage("clips");
            bool picture = page.Contains("-picture", StringComparison.Ordinal);
            string state = page.Replace("editor", "").Replace("-picture", "").Replace("-share", "").Replace("-min", "").Trim('-');
            string? source = picture && samples != null
                ? Directory.GetFiles(samples, "*.png", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                : clip;
            // Auto captions with a stand-in (no model, no process, no download).
            var captions = new RenderCaptionMaker(present: state != "captions-ask", listening: state == "captions-listening");
            if (source != null && window.OpenEditor(source, picture, page.Contains("-share", StringComparison.Ordinal) ? Edit.EditorMode.Share : Edit.EditorMode.Library,
                    captions) is { } editor)
            {
                var m = editor.Model;
                var sample = state is "empty" or "captions-ask" or "captions-intro" or "captions-listening" or "captions" or "recaption" ? default : AddSampleItems(m);
                switch (state)
                {
                    case "part":
                        m.SelectPart(0);
                        break;
                    case "sound" when m.ClipTracks.Count > 1:
                        // A clip's own tracks (KAIROS_RENDER_CLIP: a clip with them): the second muted, the first quieter.
                        m.ClipTracks[1].On = false;
                        m.ClipTracks[0].Level = 0.6;
                        break;
                    case "text":
                        m.Select(sample.Title);
                        break;
                    case "captions" or "captions-ask" or "captions-intro" or "captions-listening" or "recaption":
                        m.CaptionsCommand.Execute(null);
                        if (state is "captions" or "captions-listening" or "recaption")
                            m.IntroStartCommand.Execute(null); // the explainer every press shows, answered
                        if (state is "captions" or "recaption")
                            m.DismissCaptionsNote();
                        if (state == "recaption")
                            m.CaptionsCommand.Execute(null); // pressed again: "make them again?"
                        break;
                    case "saving":
                        m.BeginSaving(replace: false, cancel: () => { });
                        m.ReportSaving(0.62);
                        break;
                    case "saved":
                        m.BeginSaving(replace: true, cancel: () => { });
                        m.FinishSaving();
                        break;
                    case "reset":
                        m.ResetCommand.Execute(null); // "reset the clip?" over the edits
                        break;
                    case "shortcuts":
                        m.ShortcutsCommand.Execute(null);
                        break;
                    case "partlook":
                        m.SelectPart(1);
                        m.PartLooks.Single(l => l.Look == Engine.Edit.ColourLook.Mono).IsSelected = true;
                        m.SelectPart(0);
                        m.SelectPartRange(1);
                        break;
                    case "many":
                        m.SetChoice(m.Project.Items.Where(i => i is Engine.Edit.TextItem));
                        break;
                    case "busy":
                        // Many things at once: lines of three colours and texts of two, drawn at the same moment.
                        double half = Math.Max(1, m.Project.Duration / 2);
                        var busy = m.Project;
                        foreach (var (hex, y) in new[] { ("#F48BA4", 0.2), ("#3DDC84", 0.3), ("#FFD54A", 0.4) })
                            busy = busy.With(new Engine.Edit.StrokeItem(Guid.NewGuid(), 0.5, half + 1,
                                [[new Point(0.5, y), new Point(0.6, y + 0.05), new Point(0.7, y)]], hex, 0.007));
                        busy = busy.With(sample.Title with { Id = Guid.NewGuid(), Text = Text.IsArabic ? "يا سلام" : "Clutch", ColorHex = "#3DDC84",
                            Start = 1, End = half + 2, Box = new Rect(0.55, 0.1, 0.3, 0.1) });
                        m.Apply(busy);
                        break;
                    case "line":
                        m.Select(sample.Line);
                        break;
                    case "draw":
                        m.EditMode = Edit.EditMode.Draw;
                        break;
                    case "shape":
                        m.Select(sample.Arrow);
                        break;
                    case "mixed":
                        m.Select(sample.Title);
                        m.Select(sample.Arrow, add: true);
                        break;
                    case "crop":
                        m.EditMode = Edit.EditMode.Crop;
                        break;
                    case "zoom" or "zoomed":
                        // The part playing zoomed into the right of the picture: the tool on (its box over the whole
                        // picture), or off (the preview zoomed, the part's Remove zoom).
                        m.PlayheadOutput = m.Project.OutputDuration / 2;
                        m.SelectPart(m.Project.PartAt(m.PlayheadOutput));
                        m.EditMode = Edit.EditMode.Zoom;
                        m.ZoomTo(new Rect(0.5, 0.25, 0.42, 0.42));
                        if (state == "zoomed")
                            m.EditMode = Edit.EditMode.None;
                        break;
                    case "save":
                        m.PrimaryCommand.Execute(null); // "how to save it?"
                        break;
                    case "leave":
                        m.CloseCommand.Execute(null); // "leave without saving?"
                        break;
                }
            }
        }
        else if (page == "video-viewer")
        {
            // A test clip from the repository (never the user's), in Kairos's own player with OBS's FFmpeg for its frames.
            if (SampleClip() is { } clip)
            {
                LoadRenderFfmpeg();
                window.ShowPage("clips");
                window.ShowVideo(new ClipItem
                {
                    Path = clip, Name = "Desktop 2026-10-16 18-20-05", App = "Desktop",
                    Time = new DateTime(2026, 10, 16, 18, 20, 5), Size = new FileInfo(clip).Length,
                });
            }
        }
        else if (samples != null)
        {
            window.ShowPage("clips");
            // clips-chosen | clips-confirm: two cards chosen, with the floating bar (asking to delete for good).
            if (page is "clips-chosen" or "clips-confirm")
            {
                foreach (var card in window.Library.Items.Take(2))
                    card.IsSelected = true;
                if (page == "clips-confirm")
                    window.Library.AskPermanentCommand.Execute(null);
            }
            // clips-preparing: the first card's H.264 copy being made for a drag (an HEVC clip), 37 % done.
            if (page == "clips-preparing" && window.Library.Items.FirstOrDefault() is { } first)
            {
                first.Preparing = true;
                first.PrepareProgress = 0.37;
            }
            if (page == "viewer")
            {
                // The newest picture, as the Library lists it (so its arrows know what is before and after it).
                window.ShowPicture(Directory.GetFiles(samples, "*.png", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTime).First());
            }
        }
        else
            window.ShowPage(page.EndsWith("-min", StringComparison.Ordinal) ? page[..^4] : page); // <page>-min: at the smallest size
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        // share-emoji: the caption's emoji picker on its own (it opens in a popup, outside the window's picture).
        if (page == "share-emoji")
        {
            var picker = window.TakeEmojiPicker();
            picker.DataContext = vm;
            root = new System.Windows.Controls.Border { Padding = new Thickness(24), Child = picker, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        }
        // editor-fontlist / editor-colour: the text tool's font list and colour picker on their own (they open in popups).
        if (page == "editor-fontlist")
            root = Standalone(FontList());
        else if (page == "editor-colour")
            root = Standalone(new Edit.ColourPicker { Colour = "#F48BA4" });
        // share-copysizes: Copy's sizes as they open under it (a popup: drawn over the panel here, once it is laid out).
        var sizesLayer = page == "share-copysizes" ? new System.Windows.Controls.Canvas() : null;
        if (sizesLayer != null)
            root = new System.Windows.Controls.Grid { Children = { root, sizesLayer } };
        root.DataContext = vm;
        root.FlowDirection = window.FlowDirection;
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
        var border = new System.Windows.Controls.Border
        {
            Background = (Brush)app.Resources["Bg"],
            Child = root,
            FlowDirection = window.FlowDirection,
        };
        border.SetResourceReference(System.Windows.Controls.Control.FontFamilyProperty, "UiFont");
        System.Windows.Documents.TextElement.SetFontFamily(border, (FontFamily)app.Resources["UiFont"]);
        System.Windows.Documents.TextElement.SetFontSize(border, 14);
        System.Windows.Documents.TextElement.SetForeground(border, (Brush)app.Resources["TextPrimary"]);

        // The window's own default size, or (…-min) the smallest it can be made.
        // The audio states are as tall as their whole page, so every row shows; <page> tall: any page's whole length.
        var size = page.EndsWith("-min", StringComparison.Ordinal) ? new Size(window.MinWidth, window.MinHeight)
            : new Size(window.Width, audioState ? 1240 : voiceState is "tall" or "cloud" or "camera" or "apps" or "apps-win10" or "disk" ? 1500 : window.Height);
        for (int i = 0; i < 12; i++)
        {
            border.Measure(size);
            border.Arrange(new Rect(size));
            border.UpdateLayout();
            Pump();
            Thread.Sleep(150);
        }

        const double scale = 1.5;
        if (sizesLayer != null)
        {
            // The menu drawn on its own (a ContextMenu takes no parent), then put where it opens: under its tile.
            var menu = window.CopySizes();
            var tile = (FrameworkElement)menu.PlacementTarget;
            menu.FlowDirection = window.FlowDirection;
            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            menu.Arrange(new Rect(menu.DesiredSize));
            menu.UpdateLayout();
            var drawn = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * scale), (int)Math.Ceiling(menu.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            drawn.Render(menu);
            var image = new System.Windows.Controls.Image { Source = drawn, Width = menu.ActualWidth, Height = menu.ActualHeight };
            var at = tile.TranslatePoint(new Point(0, tile.ActualHeight), sizesLayer);
            System.Windows.Controls.Canvas.SetLeft(image, at.X); // in a right-to-left layout both count from the right
            System.Windows.Controls.Canvas.SetTop(image, at.Y);
            sizesLayer.Children.Add(image);
            border.Measure(size);
            border.Arrange(new Rect(size));
            border.UpdateLayout();
        }
        var bitmap = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(border);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPng))!);
        using (var file = File.Create(outPng))
            encoder.Save(file);
        window.Close();
        // The made-up temp folder goes; a folder given in KAIROS_RENDER_SAMPLES is kept.
        if (samples != null && Environment.GetEnvironmentVariable("KAIROS_RENDER_SAMPLES") != samples)
            try
            {
                Directory.Delete(samples, recursive: true);
            }
            catch (IOException)
            {
            }
        return 0;
    }

    /// <summary>A temp clips folder with drawn screenshots (no game in them) and a video, for the Clips page renders.</summary>
    /// <summary>Made-up clips and screenshots in a temp folder, or the folder KAIROS_RENDER_SAMPLES names.</summary>
    static string SamplePictures()
    {
        if (Environment.GetEnvironmentVariable("KAIROS_RENDER_SAMPLES") is { } given && Directory.Exists(given))
            return given;
        string root = Path.Combine(Path.GetTempPath(), "kairos-render-pictures-" + Guid.NewGuid().ToString("N"));
        var start = DateTime.Now.AddHours(-1);
        void Picture(string folder, string name, System.Drawing.Color top, System.Drawing.Color bottom, int minutes)
        {
            string path = Path.Combine(root, folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var bmp = new System.Drawing.Bitmap(1280, 720))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                using var sky = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, 1280, 720), top, bottom, 90f);
                g.FillRectangle(sky, 0, 0, 1280, 720);
                using var sun = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(230, 255, 214, 160));
                g.FillEllipse(sun, 860, 140, 180, 180);
                using var hills = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 40, 30, 38));
                g.FillPolygon(hills, [new(0, 720), new(0, 520), new(260, 430), new(520, 540), new(820, 440), new(1280, 560), new(1280, 720)]);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            File.SetLastWriteTime(path, start.AddMinutes(minutes));
        }
        // Today, yesterday and earlier this week, so the Library shows its day headings.
        Picture("Elden Ring", "Elden Ring 2026-10-15 21-30-00.png", System.Drawing.Color.FromArgb(255, 244, 139, 164), System.Drawing.Color.FromArgb(255, 72, 52, 54), 30);
        Picture("Elden Ring", "Elden Ring 2026-10-14 21-12-00.png", System.Drawing.Color.FromArgb(255, 120, 150, 220), System.Drawing.Color.FromArgb(255, 46, 39, 38), -24 * 60);
        Picture("Desktop", "Desktop 2026-10-12 20-50-00.png", System.Drawing.Color.FromArgb(255, 255, 201, 60), System.Drawing.Color.FromArgb(255, 60, 40, 30), -3 * 24 * 60);
        Picture("Apex Legends", "Apex Legends 2026-10-14 18-05-00.png", System.Drawing.Color.FromArgb(255, 90, 200, 170), System.Drawing.Color.FromArgb(255, 40, 46, 44), -27 * 60);
        string video = Path.Combine(root, "Elden Ring", "Elden Ring 2026-10-15 21-20-00.mp4");
        File.WriteAllBytes(video, new byte[64 * 1024]);
        File.SetLastWriteTime(video, start.AddMinutes(20));
        return root;
    }

    /// <summary>
    /// Checks that an open settings window picks up an outside change to settings.json instead of keeping
    /// (and later saving) a stale copy. The file is restored afterwards.
    /// </summary>
    public static int SelfTestReload(TextWriter output)
    {
        string path = SettingsStore.DefaultPath;
        string backup = File.ReadAllText(path);
        try
        {
            CreateApplication();
            var vm = new SettingsViewModel();
            vm.Start();
            bool before = vm.Mic;
            var changed = SettingsStore.Load(path);
            changed.Audio.Mic = !before;
            SettingsStore.Save(path, changed);
            for (int i = 0; i < 30 && vm.Mic == before; i++)
            {
                Pump();
                Thread.Sleep(100);
            }
            bool reloaded = vm.Mic != before;
            vm.Stop();
            output.WriteLine($"reload: before={before} after={vm.Mic} reloaded={reloaded}");
            return reloaded ? 0 : 1;
        }
        finally
        {
            File.WriteAllText(path, backup);
        }
    }

    /// <summary>
    /// A clip saved while the Clips page is open (off screen, in a temp folder): it must show at once, first and marked
    /// new, and the folder list must keep showing its choice.
    /// </summary>
    static int LiveCheck(MainWindow window, SettingsViewModel vm, string outPng)
    {
        string root = Path.Combine(Path.GetTempPath(), "kairos-live-" + Guid.NewGuid().ToString("N"));
        string Clip(string name, int minutes)
        {
            string path = Path.Combine(root, "Desktop", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0, 1, 2]);
            File.SetLastWriteTime(path, DateTime.Now.AddMinutes(minutes - 60));
            return path;
        }
        StatusDto Status(string last, string? shot = null) => new(new Engine.EngineStatus(true, false, 60, 0, 0, 1000, 300, 300, "obs_nvenc_av1_tex", 2560, 1440,
            700, 600, root, false, null), [], last, null, false, false, AppInfo.Version, LastShot: shot);
        try
        {
            string old = "";
            for (int i = 0; i < 3; i++)
                old = Clip($"Desktop 2026-10-15 10-0{i}-00.mp4", i);
            vm.SeedStatus(Status(old));
            WindowPlacement.OffScreen(window).Show();
            static void Settle() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.ShowPage("clips");
            Settle();
            var page = (FrameworkElement)window.ClipsHost.Content;
            var clips = (ClipsViewModel)page.DataContext;
            // The folder list (the kind list beside it is another ComboBox).
            var box = FindChild<System.Windows.Controls.ComboBox>(page, b => ReferenceEquals(b.ItemsSource, clips.Apps));
            Console.WriteLine($"before: {clips.Items.Count} cards, list shows '{(box?.SelectedItem as FolderChoice)?.Label}'");

            string saved = Clip("Desktop 2026-10-15 11-00-00.mp4", 59);
            vm.SeedStatus(Status(saved)); // the engine reports a new last clip
            Settle();
            bool first = clips.Items.Count > 0 && clips.Items[0].Path == saved;
            bool marked = first && clips.Items[0].IsNew;
            string shown = (box?.SelectedItem as FolderChoice)?.Label ?? "";
            Console.WriteLine($"after a save: {clips.Items.Count} cards, first is the new clip {first}, marked new {marked}, list shows '{shown}'");

            // A screenshot taken while the page is open: first and marked new too.
            string picture = Path.Combine(root, "Desktop", "Desktop 2026-10-15 11-01-00.png");
            Engine.ScreenshotWriter.Write(picture, new Engine.ScreenshotFrame(new byte[64 * 36 * 4], 64, 36));
            File.SetLastWriteTime(picture, DateTime.Now);
            vm.SeedStatus(Status(saved, picture));
            Settle();
            bool shotFirst = clips.Items.Count > 0 && clips.Items[0].Path == picture && clips.Items[0].IsNew && clips.Items[0].IsPicture;
            Console.WriteLine($"after a screenshot: {clips.Items.Count} cards, first is the new screenshot {shotFirst}");
            var shot = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            shot.Render(page);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(shot));
            using (var file = File.Create(outPng))
                png.Save(file);
            window.Close();
            return first && marked && shown == $"{Text.Get("clips.all")} (4)" && shotFirst ? 0 : 1;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    static T? FindChild<T>(DependencyObject parent, Func<T, bool>? which = null) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found && (which == null || which(found)))
                return found;
            if (FindChild(child, which) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>
    /// Times going from Status to Clips (off screen: nothing shows, no focus): the folder scan, the page being built, the
    /// page on screen, and its thumbnails. Uses the real clips folder.
    /// </summary>
    static int NavTime(MainWindow window, SettingsViewModel vm)
    {
        WindowPlacement.OffScreen(window).Show();
        static void Settle() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ContextIdle);
        for (int i = 0; i < 3; i++)
        {
            var scan = System.Diagnostics.Stopwatch.StartNew();
            int count = Engine.ClipLibrary.Scan(vm.EffectiveFolder).Count;
            Console.WriteLine($"scan {i}: {count} videos in {scan.Elapsed.TotalMilliseconds:F0} ms");
        }
        window.ShowPage("status");
        Settle();
        for (int i = 0; i < 4; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            window.ShowPage("clips");
            double built = watch.Elapsed.TotalMilliseconds;
            Settle();
            double shown = watch.Elapsed.TotalMilliseconds;
            var clips = (ClipsViewModel)((FrameworkElement)window.ClipsHost.Content).DataContext;
            while (clips.Items.Any(c => c.Thumbnail == null) && watch.Elapsed.TotalSeconds < 10)
            {
                Pump();
                Thread.Sleep(5);
            }
            Console.WriteLine($"to clips {i}: page {built:F0} ms, on screen {shown:F0} ms, thumbnails ({clips.Items.Count}) {watch.Elapsed.TotalMilliseconds:F0} ms");
            window.ShowPage("status");
            Settle();
        }
        // Scrolling to the end of the cards adds the next ones (and only then).
        window.ShowPage("clips");
        Settle();
        var vm2 = (ClipsViewModel)((FrameworkElement)window.ClipsHost.Content).DataContext;
        int before = vm2.Items.Count;
        window.Scroller.ScrollToVerticalOffset(window.Scroller.ScrollableHeight); // as far as the wheel goes, like a user
        Settle();
        Console.WriteLine($"scrolled to the end: {before} -> {vm2.Items.Count} cards");
        window.Close();
        return 0;
    }

    /// <summary>Opens the real window off screen, visits every page, then closes it.</summary>
    static int Probe(MainWindow window)
    {
        WindowPlacement.OffScreen(window).Show();
        foreach (string page in new[] { "status", "clips", "capture", "quality", "audio", "hotkeys", "voice", "general" })
        {
            window.ShowPage(page);
            for (int i = 0; i < 3; i++)
                Pump();
        }
        bool ok = window.IsLoaded;
        window.Close();
        Console.WriteLine($"probe: loaded={ok}");
        return ok ? 0 : 1;
    }

    /// <summary>Made-up sign-ins for the Connections page (X and TikTok connected); nothing is read or changed.</summary>
    sealed class RenderAccountsHost : IAccountsHost
    {
        public bool Available => true;
        public Task<IReadOnlyDictionary<SharePlatform, bool>> SignedIn() => Task.FromResult<IReadOnlyDictionary<SharePlatform, bool>>(
            new Dictionary<SharePlatform, bool> { [SharePlatform.X] = true, [SharePlatform.TikTok] = true, [SharePlatform.Instagram] = false });
        public Task<bool> SignIn(SharePlatform platform) => Task.FromResult(false);
        public Task SignOut(SharePlatform platform) => Task.CompletedTask;
        public Task SignOutAll() => Task.CompletedTask;
        public IReadOnlyDictionary<SharePlatform, bool>? LastKnown() => null;
        public void Remember(IReadOnlyDictionary<SharePlatform, bool> state) { }
    }

    /// <summary>A share panel with a made-up 5:00 1440p60 clip; nothing is converted, copied or opened.</summary>
    sealed class RenderShareHost(string page) : IShareHost
    {
        public string SharedFolder => @"D:\Clips\Shared";
        public const string Clip = @"D:\Clips\ELDEN RING\ELDEN RING 2026-10-12 21-04-55.mp4";
        public const string Shot = @"D:\Clips\ELDEN RING\ELDEN RING 2026-10-15 21-30-00.png";
        readonly string _state = page.Contains('-') ? page[(page.IndexOf('-') + 1)..] : "";

        public MediaInfo Probe(string clip) => _state == "failed"
            ? throw new Engine.Share.ShareException("No H.264 encoder could be started.")
            : new MediaInfo(300, 2560, 1440, 60, "av1", "aac", 192_000, 3, Bytes: 333_000_000);

        public ShareResult Transcode(string source, string target, SharePlan plan, long maxBytes, IProgress<double> progress, CancellationToken ct)
        {
            if (_state == "copying")
            {
                // Caught part way: the ring shows 37 % (this thread never ends; the render exits).
                progress.Report(0.37);
                Thread.Sleep(Timeout.Infinite);
            }
            return new(target, 18_240_000, "h264_nvenc");
        }

        public ShareResult Cut(string source, string target, double start, double end, IProgress<double> progress, CancellationToken ct) =>
            new(target, 33_000_000, "copy");
        public void CopyToClipboard(string? file, string? text) { }
        // share-picture: a drawn picture (no game in it) in the share panel.
        public SharePicture? ReadPicture(string path)
        {
            if (_state != "picture")
                return null;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xF4, 0x8B, 0xA4), Color.FromRgb(0x48, 0x34, 0x36), 90), null, new Rect(0, 0, 1280, 720));
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0xA0)), null, new Point(950, 230), 90, 90);
                var hills = new StreamGeometry();
                using (var g = hills.Open())
                {
                    g.BeginFigure(new Point(0, 720), true, true);
                    g.PolyLineTo([new Point(0, 520), new Point(260, 430), new Point(520, 540), new Point(820, 440), new Point(1280, 560), new Point(1280, 720)], true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x28, 0x1E, 0x26)), null, hills);
            }
            var bitmap = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return new SharePicture(bitmap, 2560, 1440, 2_400_000);
        }
        public ShareResult MakeJpeg(string source, string target, long maxBytes) => new(target, maxBytes, "jpeg");
        public void CopyPicture(string path) { }
        public void Launch(string target) { }
        public Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct)
        {
            // all-posting: X done, TikTok uploading, Instagram posting; all-done: Instagram needs the user.
            bool pending = _state == "posting" || (_state == "all-posting" && platform != SharePlatform.X);
            status(_state == "posting" || (pending && platform == SharePlatform.TikTok) ? "share.pub.uploading" : "share.pub.posting");
            if (pending)
                return new TaskCompletionSource<PublishOutcome>().Task;
            return Task.FromResult(_state == "all-done" && platform == SharePlatform.Instagram
                ? new PublishOutcome(PublishResult.NeedsUser, Detail: "share")
                : new PublishOutcome(PublishResult.Posted, "https://x.com/kairos/status/1"));
        }
        public void RevealSite(SharePlatform platform) { }
        /// <summary>Every frame is the render thumbnail (KAIROS_RENDER_THUMB), so the trim bar has pictures.</summary>
        public void GrabFrames(string clip, double[] times, int width, bool exact, Action<int, BitmapSource?> done)
        {
            if (Environment.GetEnvironmentVariable("KAIROS_RENDER_THUMB") is not { } png || !File.Exists(png))
                return;
            var image = new BitmapImage(new Uri(Path.GetFullPath(png)));
            image.Freeze();
            for (int i = 0; i < times.Length; i++)
                done(i, image);
        }
        public void ReleaseFrames() { }
        public BitmapSource? Play(string clip, double from, double to, int width, int height, Action<double> position, Action<string?> ended) => null;
        public void StopPlaying() { }
        public void SetPlaybackVolume(double volume) { }
        public void After(TimeSpan delay, Action action) { }
        public void ShowInFolder(string path) { }
        public void Remember(Settings.ShareSettings prefs) { }
        readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
        public void Post(Action action)
        {
            if (_ui.CheckAccess())
                action();
            else
                _ui.BeginInvoke(action);
        }
        public void Run(Action work)
        {
            if (_state == "copying")
                new Thread(() => work()) { IsBackground = true }.Start();
            else if (_state != "working")
                work();
        }

        public string Folder => Path.GetTempPath();

        public void Show(ShareViewModel vm)
        {
            if (Environment.GetEnvironmentVariable("KAIROS_RENDER_THUMB") is { } png && File.Exists(png))
                vm.Thumbnail = new BitmapImage(new Uri(Path.GetFullPath(png)));
            // share: nothing picked yet, as the panel opens.
            vm.Platform = _state switch
            {
                "x" or "picture" => SharePlatform.X,
                "tiktok" or "working" => SharePlatform.TikTok,
                "posting" or "posted" => SharePlatform.X,
                "instagram" => SharePlatform.Instagram,
                "copy" or "done" => SharePlatform.Copy,
                "all" or "all-posting" or "all-done" => SharePlatform.All,
                _ => null,
            };
            if (_state.StartsWith("all", StringComparison.Ordinal))
                vm.Caption = "#clutch #gaming";
            if (_state is "tiktok" or "working")
                vm.Vertical = true;
            if (_state == "x")
                vm.TrimStart = 0;
            if (_state is "working" or "done" or "posting" or "posted" or "all-posting" or "all-done")
                vm.PrepareCommand.Execute(null);
            if (_state == "copying")
                vm.TrimStart = 270;
            if (_state is "copy-toolong" or "copying")
                vm.CopyAtCommand.Execute(10);
        }
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}
