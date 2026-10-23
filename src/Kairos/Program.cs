using Kairos.App;
using Kairos.Engine;

namespace Kairos;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        // Velopack's install, update and uninstall steps run Kairos with arguments of their own; the helpers Kairos starts
        // itself (the settings window, auto captions, --send) skip its start-up work.
        if (mode is not ("--settings" or "--captions" or "--send"))
            InstallPaths.VelopackHooks().Run();
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        Resilience.InstallHandlers();

        switch (mode)
        {
            case "--selftest-host":
                return SelfTest.Host(Console.Out);
            case "--selftest":
                return SelfTest.Tidy(SelfTest.Pipeline(args, Console.Out));
            case "--selftest-record":
                return SelfTest.Tidy(SelfTest.Recording(Console.Out));
            case "--selftest-screenshot":
                return SelfTest.Tidy(SelfTest.Screenshot(Console.Out));
            case "--selftest-disk-rebuild":
                return SelfTest.Tidy(SelfTest.DiskRebuild(Console.Out));
            case "--selftest-scene":
                return SelfTest.Tidy(SelfTest.Scene(args, Console.Out));
            case "--selftest-process-audio":
                return SelfTest.Tidy(SelfTest.ProcessAudio(Console.Out));
            case "--tone":
                return SelfTest.Tone(args); // the hidden helper of --selftest-process-audio
            case "--settings":
                return Ui.SettingsApp.Run();
            case "--selftest-reload":
                return Ui.SettingsApp.SelfTestReload(Console.Out);
            case "--render-toast":
                Text.Language = args.Length > 2 ? args[2] : "ar";
                // --render-toast <png> [ar|en] [keys-taken|key-taken|not-saved|disk-full|starting|low-disk|record-failed|replay-failed|update|update-important|updated]
                string state = args.Length > 3 ? args[3] : "";
                string next = AppInfo.Display("1.2.0");
                var taken = state is "keys-taken" or "key-taken"
                    ? EngineApp.TakenToast(state == "keys-taken" ? ["Alt+F10", "Alt+Shift+F10", "Alt+F9"] : ["Alt+F10"])
                    : state == "not-saved" ? EngineApp.NotSavedToast("toast.notSaved.paused")
                    : state == "disk-full" ? EngineApp.NotSavedToast("toast.notSaved.disk")
                    : state == "starting" ? EngineApp.NotSavedToast("toast.notSaved.starting")
                    : state == "record-failed" ? EngineApp.RecordFailedToast()
                    : state == "replay-failed" ? EngineApp.NotSavedToast("toast.notSaved.failed")
                    : state == "low-disk" ? (Title: Text.Get("toast.lowDisk"), Detail: Text.Format("toast.lowDiskBody", Text.Ltr("8.2 GB"), Text.Ltr("C:")))
                    : state == "update" ? (Title: Text.Format("toast.update", next), Detail: Text.Get("toast.updateDetail"))
                    : state == "update-important" ? (Title: Text.Format("toast.updateImportant", next), Detail: Text.Get("toast.updateImportantDetail"))
                    : state == "updated" ? (Title: Text.Format("toast.updated", next), Detail: Text.Get("toast.updatedDetail"))
                    : default;
                var icon = state is "update" or "update-important" ? ToastIcon.Update : state == "updated" ? ToastIcon.Saved : ToastIcon.Warning;
                using (var card = taken.Title != null
                           ? SaveToast.Paint(taken.Title, taken.Detail, Text.IsRightToLeft, 1.5f, icon)
                           : SaveToast.Paint(Text.Get("toast.saved"), "ELDEN RING™ • 5:00", Text.IsRightToLeft, 1.5f))
                    card.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                return 0;
            case "--selftest-toast":
                using (var toast = new SaveToast { TestPosition = new System.Drawing.Point(-32000, -32000) })
                {
                    toast.Show("test", "test", false);
                    var until = DateTime.UtcNow.AddSeconds(SaveToast.Total + 0.3);
                    while (DateTime.UtcNow < until)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(5);
                    }
                    Console.WriteLine($"toast frames={toast.Frames} lastOk={toast.LastFrameOk} excludedFromCapture={toast.ExcludedFromCapture}");
                    return toast.Frames > 20 && toast.LastFrameOk && toast.ExcludedFromCapture ? 0 : 1;
                }
            case "--render":
                return Ui.SettingsApp.Render(args[1], args[2], args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null);
            case "--write-icon":
                // Kairos.exe --write-icon <app.ico>: app.ico from the same drawing the window uses.
                File.WriteAllBytes(args[1], AppIconArt.Ico());
                Console.WriteLine($"wrote {args[1]}");
                return 0;
            case "--perf":
                return Ui.SettingsApp.Perf(args);
            case "--voice-info":
                return VoiceInfo();
            case "--selftest-picture-look":
                return SelfTestPictureLook(args);
            case "--captions":
                // A hidden helper started by the editor: auto captions in a process of their own.
                return Engine.Captions.CaptionProcess.Helper(args);
            case "--selftest-share":
                return SelfTestShare(args);
            case "--selftest-share-site":
                return SelfTestShareSite(args.Length > 1 ? args[1] : null);
            case "--selftest-accounts":
                return SelfTestAccounts();
            case "--selftest-preview":
                return SelfTestPreview(args.Length > 1 ? args[1] : null,
                    args.Length > 2 && double.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out double secs) ? secs : 4,
                    args.Length > 3 && double.TryParse(args[3], System.Globalization.CultureInfo.InvariantCulture, out double start) && start >= 0 ? start : null,
                    args.Length > 4 && int.TryParse(args[4], out int track) ? track : 0);
            case "--send":
                Console.WriteLine(PipeClient.Send(args.Length > 1 ? args[1] : "status") ?? "(engine not running)");
                return 0;
        }

        using var mutex = new Mutex(true, AppInfo.EngineLock(Environment.GetEnvironmentVariable("KAIROS_PIPE")), out bool first);
        if (!first)
        {
            // Already running: a plain launch means "show me the settings".
            if (mode != "--background")
                PipeClient.Send("open-settings");
            return 0;
        }

        // An important update downloaded earlier goes in now, before anything records; Kairos then starts again into it.
        if (Engine.Updates.UpdateService.ApplyPendingImportant(Settings.SettingsStore.AppDataDir, mode == "--background" ? ["--background"] : []))
            return 0;

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        System.Windows.Forms.Application.Run(new EngineApp(openSettings: mode != "--background", background: mode == "--background"));
        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>
    /// Plays the end of a clip in the share panel's player, off screen and silent (it plays silence, the volume is left
    /// alone), and checks it kept time with the pictures shown (exit 0).
    /// </summary>
    /// <param name="start">Where the cut starts (seconds); none = near the end of the clip.</param>
    static int SelfTestPreview(string? clip, double seconds, double? start = null, int track = 0)
    {
        if (clip == null || !File.Exists(clip))
        {
            Console.WriteLine("usage: --selftest-preview <clip.mp4> [seconds]");
            return 2;
        }
        var info = Engine.Share.ClipTranscoder.Probe(clip);
        seconds = Math.Min(seconds, info.Duration);
        double from = start is { } s0 ? Math.Clamp(s0, 0, Math.Max(0, info.Duration - seconds)) : Math.Max(0, info.Duration - seconds - 0.5), to = from + seconds;
        string result = "running";
        double last = from;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var player = new Ui.PreviewPlayer(clip, from, to, Ui.ShareViewModel.PreviewPixels,
            (int)Math.Round(Ui.ShareViewModel.PreviewPixels * info.Height / (double)info.Width) & ~1, mute: true,
            t => last = t, failure => result = failure ?? "ended", mix: track > 0 ? new Engine.Share.TrackMix([track], [1.0]) : null);
        // Ticks stand in for screen refreshes: a precise timer, or Windows stretches each 4 ms sleep to 15.6 ms.
        timeBeginPeriod(1);
        while (result == "running" && watch.Elapsed.TotalSeconds < seconds + 5)
        {
            player.Tick();
            Thread.Sleep(4);
        }
        timeEndPeriod(1);
        double took = watch.Elapsed.TotalSeconds;
        double expected = seconds * info.Fps;
        Console.WriteLine($"preview: {result} after {took:F2}s for {seconds:F1}s of {info.VideoCodec} {info.Width}x{info.Height}, " +
            $"shown {player.Shown}/{expected:F0}, skipped {player.Skipped} ({player.SkipReasons}), reached {last - from:F2}s");
        Console.WriteLine($"preview: from {from:F1}s, sound track {player.HasAudio}, sound {player.SoundSeconds:F2}s peak {player.SoundPeak}, " +
            $"longest wait between pictures {player.LongestGapMs:F0} ms, waits over 50 ms {player.LongGaps}");
        return result == "ended" && Math.Abs(took - seconds) < 0.6 && player.Shown >= expected * 0.9 ? 0 : 1;
    }

    /// <summary>
    /// --selftest-share &lt;clip&gt; [short side, 2160 = 4K] [--mb N]: Copy's conversion of the clip's last 30 s (10 s at a
    /// given size), or with --mb the whole clip made to fit N MB (Discord's sizes), with the FFmpeg next to Kairos into
    /// the temp folder; prints the result. Nothing is copied, opened or shown. Checks that a packaged build can share.
    /// </summary>
    static int SelfTestShare(string[] args)
    {
        string? clip = args.Length > 1 ? args[1] : null;
        int? shortSide = args.Length > 2 && int.TryParse(args[2], out int s) ? s : null;
        int mbAt = Array.IndexOf(args, "--mb");
        int mb = mbAt > 0 && mbAt + 1 < args.Length && int.TryParse(args[mbAt + 1], out int m) ? m : 0;
        if (clip == null || !File.Exists(clip))
        {
            Console.WriteLine("usage: --selftest-share <clip.mp4> [short side, e.g. 2160] [--mb 10]");
            return 2;
        }
        var info = Engine.Share.ClipTranscoder.Probe(clip);
        var spec = Engine.Share.SharePlanner.CopySpec(mb);
        double end = info.Duration, start = mb > 0 ? 0 : Math.Max(0, end - (shortSide == null ? 30 : 10));
        int side = shortSide ?? Engine.Share.SharePlanner.DefaultSize(spec, end - start, info.Width, info.Height, info.Fps, info.AudioBitrate, false).ShortSide;
        var plan = Engine.Share.SharePlanner.Plan(spec, start, end - start, info.Width, info.Height, info.Fps, info.AudioBitrate,
            vertical: false, cropPosition: 0.5, shortSide: side);
        string target = Path.Combine(Path.GetTempPath(), "kairos-selftest-share.mp4");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Engine.Share.ClipTranscoder.Transcode(clip, target, plan, spec.MaxBytes, null, CancellationToken.None);
        var output = Engine.Share.ClipTranscoder.Probe(target);
        Console.WriteLine($"share: {info.VideoCodec} {info.Width}x{info.Height} -> {output.VideoCodec}/{output.AudioCodec} " +
            $"{output.Width}x{output.Height} {output.Duration:F1}s {result.Bytes / 1e6:F1} MB via {result.Encoder} in {watch.Elapsed.TotalSeconds:F1}s");
        File.Delete(target);
        if (mb > 0)
            Console.WriteLine($"share: {result.Bytes / 1e6:F1} MB <= {mb:F1} MB: {result.Bytes <= spec.MaxBytes}, sound {output.AudioBitrate / 1000} kb/s");
        bool ok = output.VideoCodec == "h264" && output.AudioCodec is "aac" or null && result.Bytes <= spec.MaxBytes;
        return ok ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint milliseconds);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint milliseconds);

    /// <summary>
    /// Reads which sites are signed in, in Kairos's real site profile, with an invisible browser. Read only: it prints
    /// true/false per site, never a cookie, and changes nothing.
    /// </summary>
    static int SelfTestAccounts()
    {
        if (!Ui.ShareSites.Available)
        {
            Console.WriteLine("accounts: WebView2 not installed");
            return 1;
        }
        string result = "timed out";
        var thread = new Thread(() =>
        {
            var window = Ui.WindowPlacement.OffScreen(new System.Windows.Window { Width = 300, Height = 200, WindowStyle = System.Windows.WindowStyle.None });
            window.Loaded += async (_, _) =>
            {
                try
                {
                    var host = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    var state = await Ui.ShareSites.SignedIn(host);
                    result = string.Join(" ", state.Select(p => $"{p.Key}:{(p.Value ? "signed in" : "signed out")}"));
                    // Kept as the Connections page keeps it, and read back as the page shows it before checking.
                    Ui.ShareSites.Remember(state);
                    var known = Ui.ShareSites.LastKnown();
                    bool same = known != null && state.All(p => known.TryGetValue(p.Key, out bool on) && on == p.Value);
                    result += same ? ", remembered" : ", NOT remembered";
                }
                catch (Exception e)
                {
                    result = e.GetType().Name + ": " + e.Message;
                }
                window.Close();
            };
            window.Closed += (_, _) => System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
            window.Show();
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        Console.WriteLine("accounts: " + result);
        return result.EndsWith(", remembered") ? 0 : 1;
    }

    /// <summary>
    /// Publishes a clip on hidden local pages shaped like X, TikTok and Instagram (no network, nothing on screen,
    /// throwaway browser profiles). Checks that a packaged build has a working WebView2 and publishing steps.
    /// </summary>
    static int SelfTestShareSite(string? clip)
    {
        if (clip == null || !File.Exists(clip))
        {
            Console.WriteLine("usage: --selftest-share-site <clip.mp4>");
            return 2;
        }
        int failures = 0;
        foreach (string mode in new[] { "x", "tiktok", "instagram", "login", "stuck" })
        {
            string data = Path.Combine(Path.GetTempPath(), "kairos-selftest-webview-" + mode);
            string? problem = Ui.PublishRunner.SelfTest(mode, Path.GetFullPath(clip), "Kairos #gaming", data);
            try { Directory.Delete(data, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Console.WriteLine($"publish {mode}: {problem ?? "ok"}");
            if (problem != null)
                failures++;
        }
        return failures;
    }

    /// <summary>
    /// Kairos.exe --selftest-picture-look &lt;picture&gt; &lt;out.png&gt;: saves the picture with the Vivid look as the editor
    /// does, nothing shown. Checks that FFmpeg loads by itself when a picture is the first thing edited.
    /// </summary>
    static int SelfTestPictureLook(string[] args)
    {
        if (args.Length < 3 || !File.Exists(args[1]))
        {
            Console.WriteLine("usage: --selftest-picture-look <picture> <out.png>");
            return 2;
        }
        try
        {
            var p = Engine.Edit.EditProject.For(args[1], 0) with { Look = Engine.Edit.ColourLook.Vivid, LookStrength = 1 };
            Ui.Edit.PictureComposer.Save(args[1], p, args[2]);
            Console.WriteLine("picture look: saved");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("picture look: " + e.Message);
            return 1;
        }
    }

    /// <summary>Prints how the recorded Smart Save phrase varies and the thresholds that follow (no audio is played).</summary>
    static int VoiceInfo()
    {
        string dir = Path.Combine(Settings.SettingsStore.AppDataDir, "voice");
        var samples = Engine.Voice.VoicePhrase.LoadSamples(dir);
        if (samples.Count != 3)
        {
            Console.WriteLine("no phrase recorded");
            return 1;
        }
        using var models = new Engine.Voice.SpeechModels(Engine.Voice.SpeechModels.DefaultDirectory);
        var phrase = Engine.Voice.VoicePhrase.Build(models, samples);
        Console.WriteLine($"samples: {string.Join(", ", samples.Select(x => $"{x.Length / 16000.0:F2} s"))}");
        Console.WriteLine($"spread: {phrase.Spread:F3}");
        foreach (var s in Enum.GetValues<Settings.VoiceSensitivity>())
            Console.WriteLine($"{s}: {Settings.VoiceSettings.Threshold(s, phrase.Spread):F3}");
        return 0;
    }
}
