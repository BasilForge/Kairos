using Kairos.App;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>The settings window after the UI review: the Library's day headings, the viewer's arrows, and more.</summary>
public static class UiRefreshTests
{
    static string TempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "kairos-ui-" + Guid.NewGuid().ToString("N"), "Videos");
        Directory.CreateDirectory(root);
        return root;
    }

    static string Put(string root, string relative, DateTime time, int kb = 1)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[kb * 1024]);
        File.SetLastWriteTime(path, time);
        return path;
    }

    static void Clean(string root) => Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);

    /// <summary>The usual tracks only: nothing about a voice chat to choose, and an older file that had one still loads.</summary>
    [Test]
    public static void An_older_voice_chat_choice_is_let_go() => OnSta(() =>
    {
        Assert.True(typeof(SettingsViewModel).GetProperty("VoiceChatApps") == null && typeof(SettingsViewModel).GetProperty("VoiceChatWindow") == null,
            "no voice chat choice");
        Assert.True(typeof(AudioSettings).GetProperty("VoiceChatWindow") == null, "not kept in the settings");
        var old = SettingsStore.Deserialize("""{"audio":{"mic":false,"voiceChatWindow":"Friends - Discord:Chrome_WidgetWin_1:Discord.exe"}}""");
        Assert.True(!old.Audio.Mic, "an older file still loads, the rest as it was");
    });

    [Test]
    public static void The_engines_answer_is_taken_on_the_windows_thread_even_before_its_loop_runs() => OnSta(() =>
    {
        // The window starts polling before its message loop runs, where there is no context to come back to: the answer
        // must still be handled on the window's thread, not on a pool thread beside it (that once refreshed the
        // Library in parallel with the window and crashed it).
        SynchronizationContext.SetSynchronizationContext(null);
        var vm = new SettingsViewModel();
        int handledOn = 0;
        vm.PropertyChanged += (_, _) => handledOn = Environment.CurrentManagedThreadId;
        vm.Start();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (handledOn == 0 && DateTime.UtcNow < deadline)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    frame.Continue = false;
                };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            Assert.True(handledOn != 0, "an answer (or none) was handled");
            Assert.True(handledOn == Environment.CurrentManagedThreadId, $"handled on thread {handledOn}, the window's is {Environment.CurrentManagedThreadId}");
        }
        finally
        {
            vm.Stop();
        }
    });

    static void OnSta(Action test)
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error == null, error ?? "");
    }

    [Test]
    public static void The_library_splits_videos_and_screenshots_when_both_are_shown()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            var now = DateTime.Now;
            Put(root, @"Game\Game today.mp4", now.AddMinutes(-5));
            Put(root, @"Game\Game yesterday.png", now.Date.AddDays(-1).AddHours(12));
            Put(root, @"Game\Game old.mp4", now.Date.AddDays(-60));
            var vm = new ClipsViewModel(() => root, () => "Alt+F10", () => "Alt+F12");
            vm.Refresh();
            Assert.Equal("Videos | Videos | Screenshots", string.Join(" | ", vm.Items.Select(c => c.GroupLabel)),
                "All: videos first, then screenshots, each with its own heading");
            vm.Kind = "video";
            Assert.Equal("Today | Older", string.Join(" | ", vm.Items.Select(c => c.GroupLabel)), "one kind: day headings again");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void The_viewer_steps_through_the_pictures_on_show()
    {
        Text.Language = "en";
        string root = TempRoot();
        try
        {
            var t = new DateTime(2026, 10, 1, 12, 0, 0);
            Put(root, @"Game\c.png", t.AddMinutes(3));
            Put(root, @"Game\video.mp4", t.AddMinutes(2));
            Put(root, @"Game\b.png", t.AddMinutes(1));
            Put(root, @"Other\a.png", t);
            var vm = new ClipsViewModel(() => root, () => "Alt+F10", () => "Alt+F12");
            vm.Refresh();
            Assert.Equal("video", vm.Items[0].Name, "All: the videos section is listed first");
            var first = vm.Items.First(c => c.IsPicture);
            Assert.Equal("c", first.Name, "newest screenshot");
            Assert.Equal("b", vm.NextPicture(first, +1)?.Name, "the next picture (videos sit in their own section)");
            Assert.Equal(null, vm.NextPicture(first, -1), "nothing before the first screenshot");
            vm.Filter = "Game";
            Assert.Equal(null, vm.NextPicture(vm.Items.Single(c => c.Name == "b"), +1), "only within what is shown (this folder)");
        }
        finally
        {
            Clean(root);
        }
    }

    [Test]
    public static void Restoring_the_default_keys_is_an_unsaved_change() => OnSta(() =>
    {
        Text.Language = "en";
        var vm = new SettingsViewModel();
        vm.HkScreenshot = "Ctrl+Shift+Alt+F1";
        vm.SeedReplay(true); // that key as the saved one (in memory), whatever this PC's settings file holds
        vm.ResetHotkeys();
        Assert.Equal(new HotkeySettings().Screenshot, vm.HkScreenshot, "Kairos's own key back");
        Assert.Equal(new HotkeySettings().Save, vm.HkSave, "all of them");
        Assert.True(vm.IsDirty, "Save applies it, Revert takes it back");
    });

    [Test]
    public static void Encoder_speed_reads_as_words() => OnSta(() =>
    {
        Text.Language = "en";
        var vm = new SettingsViewModel();
        vm.PresetLevel = 1;
        Assert.Equal("Faster", vm.PresetWord, "1");
        vm.PresetLevel = 4;
        Assert.Equal("Balanced", vm.PresetWord, "4");
        vm.PresetLevel = 5;
        Assert.Equal("Best quality", vm.PresetWord, "5: the slowest NVIDIA preset without look-ahead");
        vm.PresetLevel = 7;
        Assert.Equal(5, vm.PresetLevel, "p6 and p7 turn NVIDIA's look-ahead on, which a game holding the card starves: never");
    });

    /// <summary>
    /// NVIDIA's p6 and p7 presets turn look-ahead on (libobs keeps the preset's), which a game holding an RTX 30 card
    /// starved: 55-86 % of the frames skipped and frozen clips, gone at p5 (a user's report, 1.1.0). Settings from before
    /// say p5 at most, and Automatic records with its own p5 whatever a hidden manual setting says.
    /// </summary>
    [Test]
    public static void The_encoder_never_runs_nvidia_look_ahead()
    {
        var s = new AppSettings();
        s.Video.Preset = "p7";
        s.Normalize();
        Assert.Equal("p5", s.Video.Preset, "an old p7 becomes p5");
        s.Video.Preset = "p3";
        s.Normalize();
        Assert.Equal("p3", s.Video.Preset, "a lighter one stays");
        var smart = new Kairos.Engine.SmartChoice(Kairos.Engine.EncoderCatalog.ById("obs_nvenc_h264_tex"), OutputResolution.P1440, 60, QualityPreset.Ultra);
        var user = new VideoSettings { Auto = true, Preset = "p2", CustomCq = 30 };
        var auto = Kairos.Engine.CapturePipeline.AutoVideo(smart, user);
        Assert.True(auto is { Preset: "p5", CustomCq: 30, Resolution: OutputResolution.P1440, Fps: 60 }, "Automatic: its own preset, the user's custom numbers kept");
    }
}
