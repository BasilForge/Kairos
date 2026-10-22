using System.IO.Compression;
using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>What the engine learns about a PC's encoders, the NVIDIA driver note, the report, Automatic on the Quality page.</summary>
public static class HardwareStateTests
{
    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public static void Failed_encoders_are_kept_until_the_hardware_changes()
    {
        string path = Path.Combine(Folder(), "hardware.json");
        var state = HardwareState.Load(path, "gtx960|560.94");
        state.Failed = ["obs_nvenc_hevc_tex"];
        state.AutoStep = 1;
        state.Save(path);
        var again = HardwareState.Load(path, "gtx960|560.94");
        Assert.True(again.Failed.SequenceEqual(["obs_nvenc_hevc_tex"]) && again.AutoStep == 1, "the same PC: remembered");
        var updated = HardwareState.Load(path, "gtx960|581.57");
        Assert.True(updated.Failed.Count == 0 && updated.AutoStep == 0, "a new driver: tried again from the start");
    }

    [Test]
    public static void An_outdated_nvidia_driver_asks_for_an_update_then_a_restart()
    {
        var state = new HardwareState();
        Assert.Equal("status.nvidiaDriver", state.NvidiaNote("31.0.15.6094"), "first: update it");
        Assert.Equal("status.nvidiaDriver", state.NvidiaNote("31.0.15.6094"), "still the same driver: update it");
        Assert.Equal("status.nvidiaRestart", state.NvidiaNote("32.0.15.8157"), "a new driver NVENC still calls old: restart");
        Text.Language = "en";
        Assert.True(StatusLogic.CaptureWarning(new EngineStatus(true, false, 60, 0, 0, 0, 300, 300, "obs_x264", 1920, 1080, 0, 0, @"D:\Clips", false, null,
            NvidiaNote: "status.nvidiaRestart")).Contains("restart", StringComparison.OrdinalIgnoreCase),
            "the Status page says it");
    }

    [Test]
    public static void A_report_holds_the_logs_and_settings()
    {
        string data = Folder(), desktop = Folder();
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        File.WriteAllText(Path.Combine(data, "logs", "kairos-1.log"), "replay start failed");
        File.WriteAllText(Path.Combine(data, "settings.json"), "{}");
        string zip = Report.Save(data, desktop, new DateTime(2026, 10, 20, 1, 30, 0));
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal("logs/kairos-1.log,settings.json", string.Join(",", archive.Entries.Select(e => e.FullName).Order()), "the log and the settings");
        Assert.Equal("Kairos report 2026-10-20 01-30-00.zip", Path.GetFileName(zip), "named with the time");
    }

    [Test]
    public static void A_report_skips_empty_logs_and_holds_what_the_engine_said()
    {
        string data = Folder(), desktop = Folder();
        string logs = Path.Combine(data, "logs");
        Directory.CreateDirectory(logs);
        for (int i = 0; i < 8; i++)
        {
            string log = Path.Combine(logs, $"kairos-{i}.log");
            File.WriteAllText(log, "engine line");
            File.SetLastWriteTimeUtc(log, new DateTime(2026, 10, 20, 1, i, 0, DateTimeKind.Utc));
            string empty = Path.Combine(logs, $"settings-{i}.log");
            File.WriteAllText(empty, "");
            File.SetLastWriteTimeUtc(empty, new DateTime(2026, 10, 20, 2, i, 0, DateTimeKind.Utc)); // newer, but empty
        }
        string zip = Report.Save(data, desktop, new DateTime(2026, 10, 20, 1, 30, 0), "{\"engine\":{\"encoder\":\"obs_x264\"}}");
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(6, names.Count(n => n.StartsWith("logs/kairos-", StringComparison.Ordinal)), "the six newest logs that say something");
        Assert.True(!names.Any(n => n.StartsWith("logs/settings-", StringComparison.Ordinal)), "no empty log");
        using var status = new StreamReader(archive.GetEntry("status.json")!.Open());
        Assert.True(status.ReadToEnd().Contains("obs_x264"), "what the engine said");
    }

    [Test]
    public static void A_report_holds_the_log_the_running_engine_is_writing()
    {
        string data = Folder(), desktop = Folder();
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        // Held open as the engine holds its log: the folder may list it as empty until it is closed.
        using (var running = new FileStream(Path.Combine(data, "logs", "kairos-now.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            running.Write("capture started"u8);
            running.Flush();
            string zip = Report.Save(data, desktop, new DateTime(2026, 10, 20, 1, 45, 0));
            using var archive = ZipFile.OpenRead(zip);
            Assert.True(archive.GetEntry("logs/kairos-now.log") is { Length: > 0 }, "the running log, with what it says so far");
        }
    }

    [Test]
    public static void Automatic_hides_the_manual_choices_and_the_frame_rate_says_when_it_is_too_high()
    {
        Text.Language = "en";
        var s = new AppSettings();
        s.Video.Auto = true;
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        Assert.True(vm.AutoQuality && !vm.ManualQuality, "Automatic on: the choices are hidden");
        vm.AutoQuality = false;
        vm.Fps = 144;
        vm.Seed(null, new CapsDto([], [], [], [], [], "32.2.2", RefreshHz: 60));
        Assert.Equal("Your screen shows 60 frames a second: more are never seen", vm.FpsWarning, "above the screen's rate");
        vm.Fps = 60;
        Assert.Equal("", vm.FpsWarning, "at the screen's rate");
        Assert.True(SettingsStore.Load(Path.Combine(Folder(), "none.json")).Video.Auto, "a new install starts on Automatic");
    }

    [Test]
    public static void An_encoder_is_left_out_for_good_only_after_failing_in_two_runs()
    {
        string path = Path.Combine(Folder(), "hardware.json");
        var state = HardwareState.Load(path, "pc");
        var fallback = new EncoderFallback(state.Failed, state.NoBFrames);
        fallback.Failed("obs_qsv11_v2");
        Assert.Equal(0, state.Failed.Count, "a failure this run is not kept by itself");
        state.NoteFailure("obs_qsv11_v2");
        Assert.True(state.Failed.Count == 0 && state.Suspect.SequenceEqual(["obs_qsv11_v2"]), "once: only suspected");
        state.Save(path);
        var next = HardwareState.Load(path, "pc");
        Assert.True(new EncoderFallback(next.Failed, next.NoBFrames).Usable(["obs_qsv11_v2", "obs_x264"]).Contains("obs_qsv11_v2"), "the next run tries it again");
        next.NoteFailure("obs_qsv11_v2");
        Assert.True(next.Failed.SequenceEqual(["obs_qsv11_v2"]) && next.Suspect.Count == 0, "twice: left out from now on");
    }

    [Test]
    public static void An_encoder_that_works_again_is_no_longer_suspected()
    {
        var state = new HardwareState();
        state.NoteFailure("obs_nvenc_hevc_tex");
        Assert.True(state.NoteWorked("obs_nvenc_hevc_tex") && state.Suspect.Count == 0, "a passing failure (a driver reset) is forgotten");
        Assert.True(!state.NoteWorked("obs_nvenc_hevc_tex"), "nothing to forget");
    }

    [Test]
    public static void Automatic_steps_back_up_after_a_quiet_week()
    {
        var t0 = new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);
        var state = new HardwareState();
        state.StepDown(t0);
        state.StepDown(t0.AddMinutes(5));
        Assert.Equal(2, state.AutoStep, "two steps lighter");
        Assert.True(!state.StepUpIfQuiet(t0.AddDays(3)), "a few days: kept");
        Assert.True(state.StepUpIfQuiet(t0.AddDays(8)) && state.AutoStep == 1, "a week without skipping: one step back");
        Assert.True(!state.StepUpIfQuiet(t0.AddDays(9)), "the next one a week later");
        var old = new HardwareState { AutoStep = 1 };
        Assert.True(!old.StepUpIfQuiet(t0), "no date yet: the week starts now");
        Assert.True(old.StepUpIfQuiet(t0.AddDays(7)) && old.AutoStep == 0, "and ends a week later");
    }

    [Test]
    public static void A_59_hz_screen_counts_as_60()
    {
        Assert.Equal(60, Displays.NominalHz(59), "59.94 Hz, which Windows calls 59");
        Assert.Equal(120, Displays.NominalHz(119), "119.88");
        Assert.Equal(144, Displays.NominalHz(143), "143.86");
        Assert.Equal(30, Displays.NominalHz(29), "29.97");
        Assert.Equal(60, Displays.NominalHz(60), "60 as it is");
        Assert.Equal(165, Displays.NominalHz(165), "165 as it is");
        Assert.Equal(100, Displays.NominalHz(100), "100 as it is");
    }
}
