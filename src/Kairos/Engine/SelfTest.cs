using System.Diagnostics;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>Headless probes used from the command line during development and verification.</summary>
public static partial class SelfTest
{
    static string LogDir => Path.Combine(SettingsStore.AppDataDir, "logs");

    /// <summary>
    /// A run with a data folder of its own (KAIROS_DATA) leaves no ring file behind: each is gigabytes (a test campaign
    /// once filled the drive with them), and the next run makes its own. Never the real replay's (no KAIROS_DATA).
    /// </summary>
    public static int Tidy(int exit)
    {
        if (Environment.GetEnvironmentVariable("KAIROS_DATA") is { Length: > 0 } data)
            Replay.DiskReplayPlace.Remove(Replay.DiskReplayPlace.FolderFor(null, data));
        return exit;
    }

    public static int Host(TextWriter output)
    {
        ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, LogDir, new VideoConfig(1920, 1080, 1920, 1080, 30));
        ObsHost.LoadLater(); // the self-tests try every encoder
        try
        {
            output.WriteLine($"log: {ObsLog.CurrentPath}");
            output.WriteLine($"failed modules: {string.Join(", ", ObsHost.FailedModules)}");
            output.WriteLine("encoders: " + string.Join(", ", ObsHost.EncoderIds()));
            foreach (var m in ObsHost.Monitors())
                output.WriteLine($"monitor: {m.Width}x{m.Height} primary={m.Primary} {m.Name}");
            output.WriteLine($"mic devices: {ObsHost.ListProperty("wasapi_input_capture", "device_id").Count}");
            foreach (var (name, value) in ObsHost.Cameras())
            {
                var sizes = ObsHost.Camera(value).Sizes;
                output.WriteLine($"camera: {name}: {string.Join(" ", sizes)} -> {CameraMath.BestSize(sizes) ?? "its own default"}");
            }
            foreach (var (name, value) in ObsHost.ListProperty("window_capture", "window"))
                output.WriteLine($"window: {name} => {value}");
            return ObsHost.FailedModules.Count == 0 ? 0 : 1;
        }
        finally
        {
            ObsHost.Shutdown();
        }
    }

    /// <summary>
    /// The replay on the disk through quick rebuilds (settings saved again and again): each new pipeline opens the ring
    /// the last one is still closing, and must stay on the disk, its buffer running.
    /// </summary>
    public static int DiskRebuild(TextWriter output)
    {
        var s = new AppSettings();
        s.Output.Folder = Path.Combine(Path.GetTempPath(), "kairos-selftest-rebuild");
        s.Replay.OnDisk = true;
        s.Normalize();
        ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, LogDir, new VideoConfig(1920, 1080, 1920, 1080, 60));
        int onDisk = 0, rounds = 8;
        for (int i = 0; i < rounds; i++)
        {
            var pipeline = new CapturePipeline(s);
            try
            {
                pipeline.Build();
                pipeline.SetBufferActive(true);
                Thread.Sleep(i % 2 == 0 ? 1500 : 300); // sometimes at once, as a user clicking through settings
                bool ok = pipeline.ReplayOnDisk && pipeline.BufferActive;
                output.WriteLine($"rebuild {i + 1}: on the disk {pipeline.ReplayOnDisk}, running {pipeline.BufferActive}");
                if (ok)
                    onDisk++;
            }
            finally
            {
                pipeline.Dispose();
            }
        }
        ObsHost.Shutdown();
        output.WriteLine(onDisk == rounds ? "disk rebuild ok" : $"disk rebuild: FAIL {rounds - onDisk} of {rounds} not on the disk");
        return onDisk == rounds ? 0 : 1;
    }

    /// <summary>
    /// A screenshot of the canvas while the pipeline runs: the PNG must have the canvas's size and real content, and a
    /// second press while the first is under way must be ignored. Into the temp folder (deleted); nothing shows or plays.
    /// </summary>
    public static int Screenshot(TextWriter output)
    {
        var s = new AppSettings();
        s.Output.Folder = Path.Combine(Path.GetTempPath(), "kairos-selftest-shot");
        s.Normalize();
        ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, LogDir, new VideoConfig(1920, 1080, 1920, 1080, 30));
        ObsHost.LoadLater(); // the self-tests try every encoder
        var pipeline = new CapturePipeline(s);
        try
        {
            pipeline.Build();
            Thread.Sleep(1000);
            var (w, h) = pipeline.Base;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var first = ScreenshotGrabber.Grab(w, h, TimeSpan.FromSeconds(2));
            var second = ScreenshotGrabber.Grab(w, h, TimeSpan.FromSeconds(2));
            var frame = first.GetAwaiter().GetResult();
            long grabMs = clock.ElapsedMilliseconds;
            bool secondIgnored = second.IsCompleted && second.Result == null;
            if (frame == null)
            {
                output.WriteLine("screenshot: FAIL no frame");
                return 2;
            }
            clock.Restart();
            string path = ScreenshotWriter.Write(ClipOrganizer.TargetPath(s.Output.Folder, "Desktop", DateTime.Now, true, ".png"), frame);
            long writeMs = clock.ElapsedMilliseconds;
            int colours;
            (int W, int H) size;
            double same, flipped, swapped;
            using (var png = new System.Drawing.Bitmap(path))
            {
                size = (png.Width, png.Height);
                colours = SampledColours(png);
                // The same monitor copied by Windows: the picture must match it, not its upside-down or its red/blue swap.
                using var screen = new System.Drawing.Bitmap(png.Width, png.Height);
                var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                using (var g = System.Drawing.Graphics.FromImage(screen))
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, screen.Size);
                (same, flipped, swapped) = Compare(png, screen);
            }
            // The black check's small look, with Automatic and with Windows Graphics Capture (switched with no rebuild).
            bool? Black() => ScreenCheck.LookIsBlack(w, h).GetAwaiter().GetResult();
            bool? blackAuto = Black();
            pipeline.UseWgcForScreen(true);
            Thread.Sleep(2500);
            bool? blackWgc = Black();
            pipeline.UseWgcForScreen(false);
            Thread.Sleep(1500);
            bool? blackBack = Black();
            output.WriteLine($"screen check: black with Automatic {blackAuto}, with Windows Graphics Capture {blackWgc}, back to Automatic {blackBack}");
            bool checkOk = blackAuto == false && blackWgc == false && blackBack == false;
            bool again = ScreenshotGrabber.Grab(w, h, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult() != null;
            output.WriteLine($"screenshot: {size.W}x{size.H} (canvas {w}x{h}), {colours} colours sampled, grab {grabMs} ms, png {writeMs} ms, " +
                             $"{new FileInfo(path).Length / 1024} KB; second press ignored {secondIgnored}; another after it {again}");
            output.WriteLine($"screenshot vs the screen (mean difference of 32x18 blocks): as is {same:F1}, upside down {flipped:F1}, red/blue swapped {swapped:F1}");
            bool ok = size == (w, h) && colours > 1 && secondIgnored && again && same < flipped && same < swapped && same < 12 && checkOk;
            output.WriteLine(ok ? "screenshot ok" : "screenshot: FAIL");
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            pipeline.Dispose();
            ObsHost.Shutdown();
            try
            {
                Directory.Delete(s.Output.Folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>How many colours a coarse grid of the picture has: 1 for a black (or blank) canvas.</summary>
    static int SampledColours(System.Drawing.Bitmap png)
    {
        var seen = new HashSet<int>();
        for (int y = 0; y < png.Height; y += 37)
            for (int x = 0; x < png.Width; x += 41)
                seen.Add(png.GetPixel(x, y).ToArgb());
        return seen.Count;
    }

    /// <summary>
    /// What the canvas shows when the chosen capture has no picture (into the temp folder, deleted; nothing shows):
    /// window, One app whose app is not open; window-empty, One app with no app chosen; game-guard, Games mode with
    /// "fall back to the screen" off while the anti-cheat guard turns on and off during a recording. All must record the
    /// screen, and the guard must neither stop the recording nor empty the replay.
    /// </summary>
    public static int Scene(string[] args, TextWriter output)
    {
        string kind = args.Length > 1 ? args[1] : "window";
        var s = new AppSettings();
        s.Output.Folder = Path.Combine(Path.GetTempPath(), "kairos-selftest-scene");
        switch (kind)
        {
            case "window":
                s.Capture.Mode = CaptureMode.Window;
                s.Capture.Apps = ["Kairos selftest:none:kairos-missing.exe"];
                break;
            case "window-empty":
                s.Capture.Mode = CaptureMode.Window;
                s.Capture.Apps = [];
                break;
            case "screen-game":
                s.Capture.Mode = CaptureMode.Screen;
                break;
            case "apps":
                s.Capture.Mode = CaptureMode.Window;
                s.Capture.Apps = [.. (args.Length > 2 ? args[2] : "").Split('|', StringSplitOptions.RemoveEmptyEntries)];
                break;
            case "game-guard":
                s.Capture.Mode = CaptureMode.Game;
                s.Capture.GameFallbackToScreen = false;
                s.Capture.ScreenForRiotGames = true; // the guard is the user's choice now
                s.Audio.SourceAudioOnly = true; // "game sound only": the protected games' own sound joins when the guard turns on
                break;
            default:
                output.WriteLine($"scene: FAIL unknown scene {kind}");
                return 2;
        }
        s.Normalize();
        ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, LogDir, new VideoConfig(1920, 1080, 1920, 1080, 30));
        var pipeline = new CapturePipeline(s);
        try
        {
            pipeline.Build();
            if (kind == "game-guard")
                return GuardScene(pipeline, s.Output.Folder, output);
            if (kind == "screen-game")
                return ScreenGameScene(pipeline, s.Output.Folder, output, args.Length > 2 ? args[2] : null);
            if (kind == "apps")
                return AppsScene(pipeline, s.Capture.Apps, s.Output.Folder, output);
            string expected = kind == "window" ? "status.captureGone" : "status.captureNoApp";
            Thread.Sleep(1000);
            int first = CanvasColours(pipeline, s.Output.Folder); // before any check: never black at the start
            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(1000);
                pipeline.WatchWindow(); // the engine's once-a-second look
            }
            int colours = CanvasColours(pipeline, s.Output.Folder);
            string? notice = pipeline.CaptureNotice;
            output.WriteLine($"scene {kind}: {first} colours at the start, {colours} after the checks, notice {notice ?? "none"}");
            string? why = first <= 1 ? "black at the start" : colours <= 1 ? "the canvas is one colour (black)"
                : notice != expected ? $"notice {notice ?? "none"}, expected {expected}" : null;
            output.WriteLine(why == null ? "scene ok" : $"scene: FAIL {why}");
            return why == null ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            pipeline.Dispose();
            var leaked = ObsHost.AliveSources();
            output.WriteLine($"leaked sources after teardown: {(leaked.Count == 0 ? "none" : string.Join(", ", leaked))}");
            ObsHost.Shutdown();
            try
            {
                Directory.Delete(s.Output.Folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Screen with a game in front. With no <paramref name="window"/>, one that gives no picture (a window that does not
    /// exist): the screen keeps showing, the game layer stays hidden and is hooked again after 8 s; alt-tab hides it.
    /// With a real game window ("title:class:exe", e.g. ffplay parked off-screen by tools/test-game-layer.ps1): the layer
    /// hooks it and shows its picture.
    /// </summary>
    static int ScreenGameScene(CapturePipeline pipeline, string folder, TextWriter output, string? window)
    {
        var game = new App.FullscreenApp(window ?? "Kairos test game:KairosTestGame:kairos-test-game.exe", 1,
            App.WindowTarget.ExeName(window ?? "") ?? "kairos-test-game", pipeline.CaptureDevice ?? "");
        bool shown = false;
        for (int i = 0; i < (window == null ? 10 : 20) && !shown; i++)
        {
            pipeline.WatchScreenGame(game);
            Thread.Sleep(1000);
            shown = window != null && pipeline.GameLayerShown;
        }
        int colours = CanvasColours(pipeline, folder);
        output.WriteLine($"scene screen-game: {colours} colours, the game layer {(pipeline.GameLayerShown ? "shown" : "hidden")}");
        pipeline.WatchScreenGame(null);
        string? why = colours <= 1 ? "the canvas is black"
            : window == null && pipeline.GameLayerShown ? "a layer without a picture shows"
            : window != null && !shown ? "the game layer never hooked the game"
            : null;
        output.WriteLine(why == null ? "scene ok" : $"scene: FAIL {why}");
        return why == null ? 0 : 1;
    }

    /// <summary>
    /// Apps with two chosen windows ("a|b", from tools/test-apps.ps1: a test pattern and a plain red picture, parked
    /// off-screen): the one in front is recorded, and another app in front keeps the last one.
    /// </summary>
    static int AppsScene(CapturePipeline pipeline, IReadOnlyList<string> apps, string folder, TextWriter output)
    {
        if (apps.Count < 2)
        {
            output.WriteLine("scene: FAIL two apps needed");
            return 2;
        }
        string a = apps[0], b = apps[1];
        string Watch(string? front)
        {
            for (int i = 0; i < 3; i++)
            {
                pipeline.WatchWindow(null, front);
                Thread.Sleep(1000);
            }
            return pipeline.CapturedWindow ?? "";
        }
        string Exe(string w) => App.WindowTarget.AppKey(w);
        string onB = Watch(Exe(b));
        int coloursB = CanvasColours(pipeline, folder);
        string onA = Watch(Exe(a));
        int coloursA = CanvasColours(pipeline, folder);
        string stays = Watch("explorer");
        string? notice = pipeline.CaptureNotice;
        output.WriteLine($"scene apps: {Exe(b)} in front -> {Exe(onB)} ({coloursB} colours), {Exe(a)} in front -> {Exe(onA)} ({coloursA} colours), " +
                         $"another app in front -> {Exe(stays)}, notice {notice ?? "none"}");
        string? why = onB != b ? $"{Exe(b)} in front was not recorded"
            : onA != a ? $"{Exe(a)} in front was not recorded"
            : stays != a ? "another app in front changed what is recorded"
            : notice != null ? $"notice {notice}: the window gave no picture"
            : coloursA <= coloursB ? "the picture did not follow the app" : null;
        output.WriteLine(why == null ? "scene ok" : $"scene: FAIL {why}");
        return why == null ? 0 : 1;
    }

    static int GuardScene(CapturePipeline pipeline, string folder, TextWriter output)
    {
        string? recorded = null;
        using var stopped = new SemaphoreSlim(0);
        pipeline.RecordingStopped += (path, _, _) => { recorded = path; stopped.Release(); };
        bool started = pipeline.SetBufferActive(true) && pipeline.ToggleRecording(Path.Combine(folder, "guard.mp4"));
        Thread.Sleep(2000);
        pipeline.SetGuard(true); // Vanguard starts
        Thread.Sleep(1000);
        int colours = CanvasColours(pipeline, folder);
        string? noticeOn = pipeline.CaptureNotice;
        int soundSources = pipeline.SystemSoundSources; // the game's, plus each protected game's own
        pipeline.SetGuard(false); // and stops again
        Thread.Sleep(1000);
        string? noticeOff = pipeline.CaptureNotice;
        bool buffer = pipeline.BufferActive, recording = pipeline.Recording;
        pipeline.ToggleRecording();
        stopped.Wait(TimeSpan.FromSeconds(15));
        long bytes = recorded != null && File.Exists(recorded) ? new FileInfo(recorded).Length : 0;
        output.WriteLine($"scene game-guard: started {started}, {colours} colours sampled while guarded, notice {noticeOn ?? "none"} then " +
                         $"{noticeOff ?? "none"}, buffer {buffer}, recording {recording}, file {bytes / 1024} KB, system sound sources {soundSources}");
        string? why = !started ? $"could not start ({pipeline.LastError})"
            : colours <= 1 ? "the guarded canvas is one colour (black)"
            : noticeOn != "status.antiCheat" || noticeOff != null ? "the notice did not follow the guard"
            : !buffer || !recording ? "the guard stopped the replay or the recording"
            : bytes <= 0 ? "the recording is empty"
            : soundSources != 1 + AntiCheatGuard.GameSoundWindows.Length ? "the protected games' own sound did not join" : null;
        output.WriteLine(why == null ? "scene ok" : $"scene: FAIL {why}");
        return why == null ? 0 : 1;
    }

    /// <summary>A picture of the canvas now, as a screenshot takes it, and its <see cref="SampledColours"/>; 0 without one.</summary>
    static int CanvasColours(CapturePipeline pipeline, string folder)
    {
        var (w, h) = pipeline.Base;
        var frame = ScreenshotGrabber.Grab(w, h, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        if (frame == null)
            return 0;
        string path = ScreenshotWriter.Write(ClipOrganizer.TargetPath(folder, "Desktop", DateTime.Now, true, ".png"), frame);
        using var png = new System.Drawing.Bitmap(path);
        return SampledColours(png);
    }

    /// <summary>Mean colour difference of 32x18 block averages: as is, upside down, and with red and blue swapped.</summary>
    static (double Same, double Flipped, double Swapped) Compare(System.Drawing.Bitmap a, System.Drawing.Bitmap b)
    {
        const int Cols = 32, Rows = 18;
        static (double R, double G, double B)[,] Blocks(System.Drawing.Bitmap bmp)
        {
            var sums = new (double R, double G, double B)[Cols, Rows];
            for (int y = 0; y < bmp.Height; y += 4)
                for (int x = 0; x < bmp.Width; x += 4)
                {
                    var c = bmp.GetPixel(x, y);
                    int bx = x * Cols / bmp.Width, by = y * Rows / bmp.Height;
                    var s = sums[bx, by];
                    sums[bx, by] = (s.R + c.R, s.G + c.G, s.B + c.B);
                }
            double n = (bmp.Width / 4.0 / Cols) * (bmp.Height / 4.0 / Rows);
            for (int i = 0; i < Cols; i++)
                for (int j = 0; j < Rows; j++)
                    sums[i, j] = (sums[i, j].R / n, sums[i, j].G / n, sums[i, j].B / n);
            return sums;
        }
        var pa = Blocks(a);
        var pb = Blocks(b);
        double same = 0, flipped = 0, swapped = 0;
        for (int i = 0; i < Cols; i++)
            for (int j = 0; j < Rows; j++)
            {
                var x = pa[i, j];
                var y = pb[i, j];
                var f = pb[i, Rows - 1 - j];
                same += Math.Abs(x.R - y.R) + Math.Abs(x.G - y.G) + Math.Abs(x.B - y.B);
                flipped += Math.Abs(x.R - f.R) + Math.Abs(x.G - f.G) + Math.Abs(x.B - f.B);
                swapped += Math.Abs(x.R - y.B) + Math.Abs(x.G - y.G) + Math.Abs(x.B - y.R);
            }
        double count = Cols * Rows * 3;
        return (same / count, flipped / count, swapped / count);
    }

    /// <summary>
    /// A full recording paused in the middle while the replay buffer runs (they share the encoders): 3 s, a 1 s pause,
    /// 2 s. The file must be one 5 s video, the clock must leave the pause out, and the buffer must still save afterwards.
    /// Screen capture into the temp folder: nothing shows, nothing plays, and the files are deleted.
    /// </summary>
    public static int Recording(TextWriter output)
    {
        var s = new AppSettings();
        s.Output.Folder = Path.Combine(Path.GetTempPath(), "kairos-selftest-record");
        s.Normalize();
        ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, LogDir, new VideoConfig(1920, 1080, 1920, 1080, 30));
        ObsHost.LoadLater(); // the self-tests try every encoder
        var pipeline = new CapturePipeline(s);
        try
        {
            pipeline.Build();
            string? path = null;
            double clockSeconds = 0;
            using var stopped = new SemaphoreSlim(0);
            pipeline.RecordingStopped += (p, secs, _) => { path = p; clockSeconds = secs; stopped.Release(); };
            string? clip = null;
            using var saved = new SemaphoreSlim(0);
            pipeline.ReplaySaved += (p, _, _) => { clip = p; saved.Release(); };

            string target = ClipOrganizer.TargetPath(s.Output.Folder, "Desktop", DateTime.Now, perAppFolders: true);
            if (!pipeline.SetBufferActive(true) || !pipeline.ToggleRecording(target))
            {
                output.WriteLine($"FAIL start: {pipeline.LastError}");
                return 2;
            }
            Thread.Sleep(3000);
            bool paused = pipeline.TogglePauseRecording() && pipeline.RecordingPaused;
            double atPause = pipeline.GetStatus().RecordingSeconds;
            Thread.Sleep(1000);
            double afterPause = pipeline.GetStatus().RecordingSeconds;
            bool resumed = pipeline.TogglePauseRecording() && !pipeline.RecordingPaused;
            Thread.Sleep(2000);
            long bytes = pipeline.GetStatus().RecordingBytes;
            pipeline.ToggleRecording();
            stopped.Wait(TimeSpan.FromSeconds(15));

            double fileSeconds = path != null && File.Exists(path) ? Share.ClipTranscoder.Probe(path).Duration : 0;
            bool bufferSaves = pipeline.SaveReplay(false) && saved.Wait(TimeSpan.FromSeconds(30));
            output.WriteLine($"record: paused={paused} resumed={resumed} clock at pause {atPause:F2} s, a second later {afterPause:F2} s");
            output.WriteLine($"record: clock {clockSeconds:F2} s, file {fileSeconds:F2} s, {bytes / 1024} KB while recording, {path}");
            output.WriteLine($"buffer after the pause: saved={bufferSaves} {clip}");
            bool inAppFolder = path == target;
            output.WriteLine($"record: in the app's folder like a clip: {inAppFolder}");
            bool ok = inAppFolder && paused && resumed && Math.Abs(afterPause - atPause) < 0.05 && Math.Abs(clockSeconds - 5) < 0.4
                      && Math.Abs(fileSeconds - 5) < 0.7 && bytes > 0 && bufferSaves;
            output.WriteLine(ok ? "record: ok" : "record: FAIL");
            return ok ? 0 : 1;
        }
        catch (Exception e)
        {
            output.WriteLine($"FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            pipeline.Dispose();
            ObsHost.Shutdown();
            try
            {
                Directory.Delete(s.Output.Folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Builds the pipeline with default settings (plus overrides), buffers for <c>seconds</c>, saves once,
    /// and prints the file and resource figures. Options: --mode, --codec, --res, --fps, --quality, --mic,
    /// --window "title:class:exe", --quick, --record, --folder, --auto (Automatic quality), --voice (Smart Save listener in enroll mode, temp folder).
    /// </summary>
    public static int Pipeline(string[] args, TextWriter output)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 20;
        var s = new AppSettings();
        s.Output.Folder = Path.Combine(Path.GetTempPath(), "kairos-selftest");
        bool record = false, voiceTest = false, separate = false;
        int relief = 0;
        double? liveVolume = null;
        string? only = null, cameraClip = null, placeApp = null;
        var fallback = new EncoderFallback(); // --no-encoder: these could not start, the next one records
        for (int i = 2; i < args.Length; i++)
        {
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (args[i])
            {
                case "--mode": s.Capture.Mode = Enum.Parse<CaptureMode>(next, true); i++; break;
                case "--codec": s.Video.Codec = Enum.Parse<VideoCodec>(next, true); i++; break;
                case "--res": s.Video.Resolution = Enum.Parse<OutputResolution>(next, true); i++; break;
                case "--fps": s.Video.Fps = int.Parse(next); i++; break;
                case "--quality": s.Video.Quality = Enum.Parse<QualityPreset>(next, true); i++; break;
                case "--auto": s.Video.Auto = true; break;
                case "--relief": relief = int.Parse(next); i++; break;
                case "--window": s.Capture.Apps = [next]; i++; break;
                case "--folder": s.Output.Folder = next; i++; break;
                case "--mic": s.Audio.Mic = true; break;
                case "--mic-device": s.Audio.SelectMic(next); i++; break;
                case "--source-audio": s.Audio.SourceAudioOnly = true; break;
                case "--quick": s.Replay.QuickEnabled = true; s.Replay.QuickSeconds = 5; break;
                case "--record": record = true; break;
                case "--fresh": s.Replay.FreshAfterSave = true; break;
                case "--replay": s.Replay.OnDisk = next == "disk"; s.Replay.DiskDrive = null; i++; break;
                case "--disk-drive": s.Replay.DiskDrive = next; i++; break;
                case "--place": placeApp = next; i++; break;
                case "--buffer": s.Replay.Seconds = int.Parse(next); i++; break;
                case "--only": only = next; i++; break;
                case "--no-encoder": foreach (string id in next.Split(',')) fallback.Failed(id); i++; break;
                case "--fail-start": CapturePipeline.FailingEncoders = next.Split(',').ToHashSet(); i++; break;
                case "--voice": voiceTest = true; s.Voice.Enabled = true; break;
                case "--no-fallback": s.Capture.GameFallbackToScreen = false; break;
                case "--audio": separate = next == "separate"; s.Audio.Layout = separate ? AudioLayout.Separate : AudioLayout.Together; i++; break;
                case "--volume": liveVolume = double.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                // --camera corner|round|vertical: the camera (OBS's virtual camera here: no light, no real device) over the picture.
                case "--camera":
                    s.Camera.Enabled = true;
                    s.Camera.Layout = next == "vertical" ? CameraLayout.Vertical : CameraLayout.Corner;
                    s.Camera.Shape = next == "round" ? CameraShape.Round : CameraShape.Square;
                    i++;
                    break;
                case "--camera-file": s.Camera.SeparateFile = true; break;
                case "--camera-clip": cameraClip = next; i++; break;
            }
        }
        s.Normalize();

        ObsHost.Start(AppContext.BaseDirectory, SettingsStore.AppDataDir, LogDir, new VideoConfig(1920, 1080, 1920, 1080, 30));
        ObsHost.LoadLater(); // the self-tests try every encoder
        if (s.Camera.Enabled)
        {
            var cameras = ObsHost.Cameras();
            output.WriteLine($"cameras: {string.Join(", ", cameras.Select(c => c.Name))}");
            s.Camera.Device = cameras.FirstOrDefault(c => c.Name.Contains("OBS Virtual Camera", StringComparison.Ordinal)).Value ?? "";
        }
        string voiceDir = Path.Combine(Path.GetTempPath(), "kairos-selftest-voice");
        using var voice = voiceTest ? new Voice.VoiceTrigger(Voice.SpeechModels.DefaultDirectory, voiceDir) : null;
        Func<IReadOnlyList<string>, IReadOnlyList<string>>? filter = only == null ? null : ids => ids.Where(id => id.Contains(only) || id.Contains("aac")).ToList();
        // --fail-start: the chosen encoders fail as the buffer starts; each failure is handed to the fallback and the
        // pipeline built again, as the engine does (EngineApp.TryNextEncoder), until one starts.
        for (int attempt = 0; CapturePipeline.FailingEncoders.Count > 0 && attempt < 8; attempt++)
        {
            var trial = new CapturePipeline(s, filter, null, fallback);
            trial.Build();
            var choice = trial.Choice!;
            bool bframes = choice.Vendor == EncoderVendor.Nvidia && !choice.NoBFrames && trial.Plan?.BFrames > 0;
            bool started = trial.SetBufferActive(true);
            string error = trial.BufferError ?? "";
            trial.SetBufferActive(false);
            trial.Dispose();
            if (started)
            {
                output.WriteLine($"fallback: started with {choice.Id}{(choice.NoBFrames ? " without B-frames" : "")} after {attempt} failure(s)");
                break;
            }
            bool again = fallback.Failed(choice.Id, bframes);
            output.WriteLine($"fallback: {choice.Id}{(bframes ? " with B-frames" : "")} could not start ({error}): {(again ? "trying again" : "nothing left")}");
            if (!again)
                return 5;
        }
        var pipeline = new CapturePipeline(s, filter, voice, fallback)
        {
            CameraClip = cameraClip,
            Relief = relief,
        };
        try
        {
            pipeline.Build();
            var saved = new List<string>();
            using var savedSignal = new SemaphoreSlim(0);
            pipeline.ReplaySaved += (path, quick, _) => { lock (saved) saved.Add((quick ? "quick " : "main ") + path); savedSignal.Release(); };
            string? cameraFile = null;
            pipeline.CameraSaved += path => cameraFile = path;
            string? recordingPath = null;
            var recordSignal = new SemaphoreSlim(0); // not disposed: the recording's stop may come as the pipeline goes
            pipeline.RecordingStopped += (path, _, _) => { recordingPath = path; recordSignal.Release(); };

            if (!pipeline.SetBufferActive(true))
            {
                output.WriteLine($"FAIL start: {pipeline.LastError}");
                return 2;
            }
            output.WriteLine($"early save accepted: {pipeline.SaveReplay(false)} (expected False)");
            string? audioProblem = separate ? SeparateAudio(pipeline, s, output) : null;
            if (record)
                output.WriteLine($"recording: {pipeline.ToggleRecording()}");
            voice?.StartEnroll();

            var proc = Process.GetCurrentProcess();
            Thread.Sleep(3000);
            if (liveVolume is { } volume)
            {
                // A volume set on the running sources, as a saved settings change does (no rebuild).
                pipeline.ApplyLive(new LiveAudio(volume, s.Audio.MicVolume, []));
                output.WriteLine(FormattableString.Invariant($"volume: system={pipeline.SystemSourceVolume:0.##} active={pipeline.BufferActive}"));
            }
            proc.Refresh();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            Thread.Sleep(Math.Max(1, seconds - 3) * 1000);
            proc.Refresh();
            double cpuCore = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds * 100;

            for (int i = 0; i < 3; i++)
            {
                Thread.Sleep(700);
                output.WriteLine("levels: " + string.Join(" ", pipeline.TakeLevels().Select(l => FormattableString.Invariant($"{l.Id}={l.Db:F1} dB"))));
            }
            if (voice != null)
            {
                var vs = voice.Status();
                output.WriteLine($"voice: available={vs.Available} mode={vs.Mode} blocks={voice.BlocksReceived} analysed={voice.ChunksAnalysed} chunks " +
                                 $"({voice.ChunksAnalysed * 0.032:F1} s of audio)");
                voice.Cancel();
                try { Directory.Delete(voiceDir, true); } catch (IOException) { }
            }
            var st = pipeline.GetStatus();
            output.WriteLine($"encoder: {st.Encoder} ({pipeline.Choice?.Vendor}) {st.Width}x{st.Height} fps={st.Fps:F1} lagged={st.LaggedFrames} skipped={st.SkippedFrames}/{st.TotalFrames}");
            if (st.Auto is { } auto)
                output.WriteLine($"automatic: {auto}");
            if (st.EncoderFallback != null)
                output.WriteLine($"encoder fallback: chose {st.EncoderFallback}, recording with {st.Encoder}: {Ui.StatusLogic.CaptureWarning(st)}");
            output.WriteLine($"cpu: {cpuCore:F1}% of one core ({cpuCore / Environment.ProcessorCount:F2}% total), ram: {proc.WorkingSet64 / (1024 * 1024)} MB private: {proc.PrivateMemorySize64 / (1024 * 1024)} MB");
            output.WriteLine($"folder: {st.ClipsFolder} fellBack={st.FolderFellBack} estimate={st.EstimatedBufferMb} MB");
            output.WriteLine($"system sound sources: {pipeline.SystemSoundSources}");
            if (s.Replay.OnDisk)
                output.WriteLine($"replay on the disk: {pipeline.ReplayOnDisk} fellBack={pipeline.DiskFellBack} " +
                                 $"written {Replay.DiskReplayOutput.BytesWritten("Kairos replay") / 1024} KB");

            // --place <app>: saved as the engine saves it (into the app's folder and name); on the disk written straight there.
            bool first = placeApp == null ? pipeline.SaveReplay(false)
                : pipeline.RequestSave(false, ClipOrganizer.TargetPath(pipeline.ClipsFolder, placeApp, DateTime.Now, true)) == SaveWatch.Request.Started;
            bool second = pipeline.SaveReplay(false);
            output.WriteLine($"save accepted: {first}, immediate second accepted: {second} (expected True, False)");
            if (s.Replay.QuickEnabled)
            {
                savedSignal.Wait(TimeSpan.FromSeconds(30));
                output.WriteLine($"quick save accepted: {pipeline.SaveReplay(true)}");
            }
            int expectedSaves = s.Replay.QuickEnabled ? 2 : 1;
            for (int i = saved.Count; i < expectedSaves; i++)
                savedSignal.Wait(TimeSpan.FromSeconds(30));
            Thread.Sleep(1500);
            lock (saved)
                foreach (string line in saved)
                {
                    string path = line[(line.IndexOf(' ') + 1)..];
                    output.WriteLine($"saved {line} ({new FileInfo(path).Length / 1024} KB)");
                    Audio.Mp4TrackNames.Write(path, pipeline.TrackNames); // as the engine does after a save
                    output.WriteLine($"track names: {string.Join(", ", Audio.Mp4TrackNames.Read(path))}");
                }

            if (s.Replay.FreshAfterSave)
            {
                // "Start fresh after each save": the buffer started empty once the clip was written, so a save a few
                // seconds later holds those seconds, not the minutes before.
                Thread.Sleep(4000);
                int before;
                lock (saved)
                    before = saved.Count;
                bool again = pipeline.SaveReplay(false);
                savedSignal.Wait(TimeSpan.FromSeconds(30));
                string? last = null;
                lock (saved)
                    if (saved.Count > before)
                        last = saved[^1][(saved[^1].IndexOf(' ') + 1)..];
                double length = last != null ? Share.ClipTranscoder.Probe(last).Duration : 0;
                for (int i = 0; i < 40 && !pipeline.BufferActive; i++)
                    Thread.Sleep(50); // it starts empty again right after that save
                output.WriteLine(FormattableString.Invariant($"fresh: active={pipeline.BufferActive} saved again={again} length={length:F1} s (a few, the buffer held {seconds} s before)"));
                if (!pipeline.BufferActive || last == null || length > 10)
                    return 9;
                expectedSaves++;
            }
            if (record)
            {
                pipeline.ToggleRecording();
                recordSignal.Wait(TimeSpan.FromSeconds(15));
                output.WriteLine($"recording saved: {recordingPath} ({(recordingPath != null && File.Exists(recordingPath) ? new FileInfo(recordingPath).Length / 1024 : 0)} KB)");
                if (recordingPath != null)
                {
                    // Named as the engine names it once it stops (its muxer may still hold the file a moment).
                    bool named = false;
                    Audio.TrackNaming.Name(recordingPath, pipeline.TrackNames, (delay, retry) => { Thread.Sleep(delay); retry(); });
                    var read = Audio.Mp4TrackNames.Read(recordingPath);
                    named = read.SequenceEqual(pipeline.TrackNames);
                    output.WriteLine($"recording track names: {string.Join(", ", read)}{(named ? "" : " (FAIL: not named)")}");
                    if (!named)
                        return 10;
                }
            }
            if (s.Camera.Enabled)
            {
                output.WriteLine($"camera: picture={pipeline.CameraHasPicture} on={pipeline.CameraOn} canvas={pipeline.Base.W}x{pipeline.Base.H}");
                if (!pipeline.CameraHasPicture)
                    return 8;
                if (s.Camera.SeparateFile)
                {
                    for (int wait = 0; wait < 30 && cameraFile == null; wait++)
                        Thread.Sleep(500);
                    output.WriteLine($"camera file: {cameraFile} ({(cameraFile != null && File.Exists(cameraFile) ? new FileInfo(cameraFile).Length / 1024 : 0)} KB)");
                    if (cameraFile == null)
                        return 7;
                }
            }
            if (audioProblem != null)
            {
                output.WriteLine($"audio: FAIL {audioProblem}");
                return 6;
            }
            return saved.Count == expectedSaves ? 0 : 3;
        }
        catch (Exception e)
        {
            output.WriteLine($"FAIL {e.GetType().Name}: {e.Message}");
            return 4;
        }
        finally
        {
            pipeline.Dispose();
            var leaked = ObsHost.AliveSources();
            output.WriteLine($"leaked sources after teardown: {(leaked.Count == 0 ? "none" : string.Join(", ", leaked))}");
            ObsHost.Shutdown();
            if (leaked.Count > 0)
                Environment.Exit(5);
        }
    }
}
