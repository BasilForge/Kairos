using System.Net.Http;
using Kairos.Engine.Updates;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>When Kairos looks for an update, what it says, and when it may restart into one.</summary>
public static class UpdateRulesTests
{
    static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public static void The_first_check_waits_a_minute_after_the_buffer_then_daily()
    {
        Assert.True(!UpdateRules.ShouldCheck(false, T0, null, T0.AddHours(5)), "turned off: never");
        Assert.True(!UpdateRules.ShouldCheck(true, null, null, T0), "the replay buffer never ran: not yet");
        Assert.True(!UpdateRules.ShouldCheck(true, T0, null, T0.AddSeconds(59)), "59 s after the buffer: not yet");
        Assert.True(UpdateRules.ShouldCheck(true, T0, null, T0.AddSeconds(61)), "61 s after: yes");
        Assert.True(!UpdateRules.ShouldCheck(true, T0, T0.AddMinutes(1), T0.AddHours(23)), "checked 23 h ago: no");
        Assert.True(UpdateRules.ShouldCheck(true, T0, T0.AddMinutes(1), T0.AddMinutes(1).AddHours(24).AddSeconds(1)), "a day later: yes");
    }

    [Test]
    public static void Each_version_is_announced_once()
    {
        var seen = new HashSet<string> { "1.0.1" };
        Assert.True(!UpdateRules.ShouldNotify("1.0.1", seen), "said already");
        Assert.True(UpdateRules.ShouldNotify("1.0.2", seen), "a newer one");
    }

    /// <summary>
    /// A downloaded update goes in only when nothing is lost: it never ends a recording, and never closes the settings
    /// window while it is in use (unsaved changes, the editor, sharing).
    /// </summary>
    [Test]
    public static void A_downloaded_update_waits_for_a_recording_and_a_window_in_use()
    {
        Assert.Equal(UpdateState.WaitingForRecording, UpdateRules.WhenDownloaded(recording: true, windowBusy: false), "recording");
        Assert.Equal(UpdateState.WaitingForRecording, UpdateRules.WhenDownloaded(recording: true, windowBusy: true), "recording and the window in use");
        Assert.Equal(UpdateState.WaitingForWindow, UpdateRules.WhenDownloaded(recording: false, windowBusy: true), "the window in use");
        Assert.Equal(UpdateState.Ready, UpdateRules.WhenDownloaded(recording: false, windowBusy: false), "nothing under way");
    }

    /// <summary>The window says it is in use with each status question (twice a second); a closed window stops asking.</summary>
    [Test]
    public static void A_window_is_in_use_only_while_it_keeps_saying_so()
    {
        Assert.True(UpdateRules.WindowBusy(saidBusy: true, askedAt: T0, now: T0.AddSeconds(1)), "said so a second ago");
        Assert.True(!UpdateRules.WindowBusy(saidBusy: true, askedAt: T0, now: T0.AddSeconds(3)), "no question for 3 s: closed");
        Assert.True(!UpdateRules.WindowBusy(saidBusy: false, askedAt: T0, now: T0.AddSeconds(1)), "said it is free");
    }

    [Test]
    public static void Notes_link_only_for_github()
    {
        Assert.Equal(null, UpdateRules.NotesUrl("1.0.1", localSource: true), "a local folder has no page");
        Assert.Equal("https://github.com/BasilForge/Kairos/releases/tag/v1.0.1", UpdateRules.NotesUrl("1.0.1", localSource: false), "GitHub");
    }

    [Test]
    public static void A_local_feed_is_used_only_when_its_folder_exists()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", "feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "update-source.txt");
        try
        {
            Assert.Equal(null, UpdateRules.LocalSource(file), "no file: GitHub");
            File.WriteAllText(file, Path.Combine(dir, "missing") + Environment.NewLine);
            Assert.Equal(null, UpdateRules.LocalSource(file), "a folder that is not there: GitHub");
            File.WriteAllText(file, "  " + dir + "  " + Environment.NewLine);
            Assert.Equal(dir, UpdateRules.LocalSource(file), "an existing folder (spaces and line ends trimmed)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void An_old_settings_file_checks_for_updates()
    {
        Assert.True(SettingsStore.Deserialize("""{"general":{}}""").General.CheckUpdates, "missing: on");
        Assert.True(!SettingsStore.Deserialize("""{"general":{"checkUpdates":false}}""").General.CheckUpdates, "turned off stays off");
    }

    sealed class FakeUpdater(Func<string?> check, bool downloadFails = false) : IUpdater
    {
        public bool Failing { get; set; } = downloadFails;
        public bool ApplyFails { get; init; }
        public string[]? AppliedWith { get; private set; }
        public Task<UpdateFound?> Check() => Task.FromResult(check() is { } v ? new UpdateFound(v, null) : null);
        public Task Download(Action<int> progress, CancellationToken ct)
        {
            if (Failing)
                throw new HttpRequestException("offline");
            progress(50);
            progress(100);
            return Task.CompletedTask;
        }
        public void ApplyAndExit(string[] restartArgs)
        {
            if (ApplyFails)
                throw new InvalidOperationException("Cannot find Update.exe to apply updates");
            AppliedWith = restartArgs;
        }
    }

    static string SeenFile() => Path.Combine(Path.GetTempPath(), "kairos-tests", "seen-" + Guid.NewGuid().ToString("N") + ".txt");

    /// <summary>Offline, GitHub down or rate-limited: nothing is shown, and the next try is a day later.</summary>
    [Test]
    public static void A_failed_check_is_quiet_and_retried_tomorrow()
    {
        var service = new UpdateService(new FakeUpdater(() => throw new HttpRequestException("offline")), SeenFile(), localSource: false);
        service.Check(T0.AddMinutes(2)).GetAwaiter().GetResult();
        Assert.Equal(null, service.Status, "nothing to show");
        Assert.True(service.LastCheck == T0.AddMinutes(2), "the try counts");
        Assert.True(!UpdateRules.ShouldCheck(true, T0, service.LastCheck, T0.AddHours(3)), "not again for a day");
    }

    [Test]
    public static void A_new_version_is_told_once_and_can_be_downloaded_again_after_a_failure()
    {
        string seen = SeenFile();
        var updater = new FakeUpdater(() => "1.0.1", downloadFails: true);
        var service = new UpdateService(updater, seen, localSource: false);
        service.Check(T0).GetAwaiter().GetResult();
        Assert.Equal(UpdateState.Available, service.Status!.State, "available");
        Assert.Equal("https://github.com/BasilForge/Kairos/releases/tag/v1.0.1", service.Status.NotesUrl, "its notes");
        Assert.True(service.RemindDue(T0), "to tell (the engine waits for no game in front)");
        Assert.True(service.RemindDue(T0.AddMinutes(5)), "still, until it showed");
        service.Reminded(T0.AddMinutes(5));
        Assert.True(!service.RemindDue(T0.AddDays(2)), "told: never again");
        var restarted = new UpdateService(updater, seen, localSource: false);
        restarted.Check(T0.AddDays(1)).GetAwaiter().GetResult();
        Assert.True(!restarted.RemindDue(T0.AddDays(1)), "told once, even after a restart of Kairos");

        service.Download().GetAwaiter().GetResult();
        Assert.Equal(UpdateState.Failed, service.Status!.State, "a failed download says so");
        updater.Failing = false;
        service.Download().GetAwaiter().GetResult();
        Assert.Equal(100, service.Status!.Percent, "works when tried again: all of it");
        Assert.True(service.Settle(recording: false, windowBusy: false), "and can go in");
        Assert.True(service.Apply(openWindow: false), "Velopack took over");
        Assert.Equal("--background", string.Join(" ", updater.AppliedWith!), "after a recording: back in the tray, no window over a game");
        File.Delete(seen);
    }

    /// <summary>
    /// When a downloaded update goes in is decided by the engine on its own thread (where recordings start and stop),
    /// never on the thread the download finished on: a recording that starts meanwhile is never ended.
    /// </summary>
    [Test]
    public static void The_engine_decides_when_a_downloaded_update_goes_in()
    {
        string seen = SeenFile();
        var service = new UpdateService(new FakeUpdater(() => "1.0.1"), seen, localSource: false);
        service.Check(T0).GetAwaiter().GetResult();
        Assert.True(!service.Settle(recording: false, windowBusy: false), "not downloaded: nothing to put in");
        service.Download().GetAwaiter().GetResult();
        Assert.Equal(UpdateState.Downloading, service.Status!.State, "downloaded, nothing decided yet");
        Assert.True(!service.Settle(recording: true, windowBusy: false), "a recording: not now");
        Assert.Equal(UpdateState.WaitingForRecording, service.Status!.State, "waits for the recording");
        Assert.True(!service.Settle(recording: false, windowBusy: true), "the window in use: not now");
        Assert.Equal(UpdateState.WaitingForWindow, service.Status!.State, "waits for the window");
        Assert.True(service.Settle(recording: false, windowBusy: false), "both done: now");
        Assert.Equal(UpdateState.Ready, service.Status!.State, "ready");
        File.Delete(seen);
    }

    /// <summary>When Velopack's updater cannot start, Kairos still leaves properly (a half-closed engine would keep its lock).</summary>
    [Test]
    public static void An_updater_that_cannot_start_never_stops_Kairos_leaving()
    {
        string seen = SeenFile();
        var service = new UpdateService(new FakeUpdater(() => "1.0.1") { ApplyFails = true }, seen, localSource: false);
        service.Check(T0).GetAwaiter().GetResult();
        service.Download().GetAwaiter().GetResult();
        Exception? thrown = null;
        try
        {
            Assert.True(!service.Apply(openWindow: true), "said it could not (Kairos keeps running)");
            Assert.Equal(UpdateState.Failed, service.Status!.State, "and the bar says the update failed");
        }
        catch (Exception e)
        {
            thrown = e;
        }
        Assert.True(thrown == null, "Apply let this through: " + thrown?.Message);
        File.Delete(seen);
    }
}
