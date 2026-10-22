using Kairos.App;
using Kairos.Engine.Updates;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>
/// An update marked important in its release notes: told every day until it is in (never over a game), downloaded in the
/// background and put in at the next start, and shown as a card on the Status page.
/// </summary>
public static class UpdateImportantTests
{
    static readonly DateTime T0 = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    const string Notes = """
        <!-- important -->
        <!-- summary.ar: إصلاح الشاشة السوداء في بعض الألعاب | التسجيل أخف على الجهاز -->
        Kairos 1.2: fixes from your reports.

        - Black screen fix: some games recorded black in Games mode.
        - Lighter recording: less GPU work while the replay runs.
        - Smoother scrubbing.
        - Fourth line.
        """;

    [Test]
    public static void The_notes_say_whether_an_update_is_important_and_what_it_brings()
    {
        Assert.True(UpdateNotes.IsImportant(Notes), "marked");
        Assert.True(!UpdateNotes.IsImportant("- Smoother scrubbing."), "not marked");
        Assert.True(!UpdateNotes.IsImportant(null), "no notes");
        Assert.Equal("إصلاح الشاشة السوداء في بعض الألعاب|التسجيل أخف على الجهاز", string.Join("|", UpdateNotes.Summary(Notes, "ar")),
            "the release's own lines in the user's language");
        Assert.Equal("Black screen fix|Lighter recording|Smoother scrubbing", string.Join("|", UpdateNotes.Summary(Notes, "de")),
            "no lines in that language: the first three points, up to their colon");
        Assert.Equal(0, UpdateNotes.Summary(null, "en").Count, "no notes, no lines");
    }

    [Test]
    public static void An_important_update_is_told_every_day_until_it_is_in()
    {
        Assert.True(UpdateRules.RemindDue(lastReminded: null, now: T0), "never told: now");
        Assert.True(!UpdateRules.RemindDue(lastReminded: T0, now: T0.AddHours(23)), "told today: not again");
        Assert.True(UpdateRules.RemindDue(lastReminded: T0, now: T0.AddHours(24)), "a day later: again");
    }

    [Test]
    public static void Kairos_says_it_was_updated_once_after_an_update()
    {
        Assert.True(UpdateRules.JustUpdated(lastRun: "1.1.0", current: "1.2.0"), "a newer version runs");
        Assert.True(!UpdateRules.JustUpdated(lastRun: "1.2.0", current: "1.2.0"), "the same one");
        Assert.True(!UpdateRules.JustUpdated(lastRun: null, current: "1.2.0"), "a first install is not an update");
        Assert.True(UpdateRules.JustUpdated(lastRun: null, current: "1.1.0", hadSettings: true), "1.0.x kept no version: its settings say it ran");
    }

    [Test]
    public static void An_update_never_goes_in_over_a_game_or_a_clip_being_written()
    {
        Assert.Equal(UpdateState.WaitingForGame, UpdateRules.WhenDownloaded(recording: false, windowBusy: false, gaming: true), "a game in front");
        Assert.Equal(UpdateState.WaitingForRecording, UpdateRules.WhenDownloaded(recording: true, windowBusy: false, gaming: true), "a recording first");
        Assert.Equal(UpdateState.Ready, UpdateRules.WhenDownloaded(recording: false, windowBusy: false, gaming: false), "nothing under way");
    }

    [Test]
    public static void An_important_update_that_failed_at_a_start_is_not_put_in_at_every_start()
    {
        string dir = Folder();
        File.WriteAllText(Path.Combine(dir, "update-important.txt.tried"), "1.2.0");
        var service = new UpdateService(new FakeUpdater("1.2.0", Notes), Path.Combine(dir, "update-seen.txt"), localSource: false);
        service.Check(T0, "en").GetAwaiter().GetResult();
        Assert.Equal(null, UpdateService.PendingImportant(dir, pendingVersion: "1.2.0"), "tried once at a start: it waits for Update now");
        Assert.True(service.Status is { Important: true, State: UpdateState.Available }, "still offered");
        UpdateService.ForgetImportant(dir);
        Directory.Delete(dir, recursive: true);
    }

    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", "important-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    sealed class FakeUpdater(string version, string? notes) : IUpdater
    {
        public int Downloads { get; private set; }
        public bool Failing { get; set; }
        public Task<UpdateFound?> Check() => Task.FromResult<UpdateFound?>(new UpdateFound(version, notes));
        public Task Download(Action<int> progress, CancellationToken ct)
        {
            Downloads++;
            if (Failing)
                throw new System.Net.Http.HttpRequestException("offline");
            progress(100);
            return Task.CompletedTask;
        }
        public void ApplyAndExit(string[] restartArgs) { }
    }

    [Test]
    public static void An_important_update_downloads_quietly_and_waits_for_the_next_start()
    {
        string dir = Folder();
        var updater = new FakeUpdater("1.2.0", Notes);
        var service = new UpdateService(updater, Path.Combine(dir, "update-seen.txt"), localSource: false);
        service.Check(T0, "ar").GetAwaiter().GetResult();
        Assert.True(service.Status is { Important: true, State: UpdateState.Available }, "found, important");
        Assert.Equal(2, service.Status!.Summary!.Count, "its lines for the card");
        Assert.True(service.RemindDue(T0), "told today");
        service.Reminded(T0);
        Assert.True(!service.RemindDue(T0.AddHours(2)), "once a day");
        Assert.Equal(1, updater.Downloads, "downloaded by itself with the check");
        service.Check(T0.AddHours(1), "ar").GetAwaiter().GetResult();
        Assert.Equal(1, updater.Downloads, "once");
        Assert.Equal(UpdateState.Available, service.Status!.State, "still offered (Update now restarts into it)");
        Assert.True(!service.Settle(recording: false, windowBusy: false), "never put in mid-session by itself");
        Assert.Equal("1.2.0", UpdateService.PendingImportant(dir, pendingVersion: "1.2.0"), "put in at the next start");
        Assert.Equal(null, UpdateService.PendingImportant(dir, pendingVersion: null), "nothing waiting: nothing to put in");

        service.Download().GetAwaiter().GetResult();
        Assert.Equal(1, updater.Downloads, "Update now uses what was downloaded");
        Assert.True(service.Settle(recording: false, windowBusy: false), "and goes in when nothing is under way");
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public static void A_failed_background_download_waits_for_the_next_check()
    {
        string dir = Folder();
        var updater = new FakeUpdater("1.2.0", Notes) { Failing = true };
        var service = new UpdateService(updater, Path.Combine(dir, "update-seen.txt"), localSource: false);
        service.Check(T0, "en").GetAwaiter().GetResult();
        Assert.Equal(1, updater.Downloads, "tried with the check, not on every tick");
        Assert.Equal(null, UpdateService.PendingImportant(dir, pendingVersion: "1.2.0"), "nothing noted for the next start");
        updater.Failing = false;
        service.Check(T0.AddDays(1), "en").GetAwaiter().GetResult();
        Assert.Equal(2, updater.Downloads, "the next day's check tries again");
        Assert.Equal("1.2.0", UpdateService.PendingImportant(dir, pendingVersion: "1.2.0"), "and notes it");
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public static void A_usual_update_waits_for_the_user_as_before()
    {
        string dir = Folder();
        var service = new UpdateService(new FakeUpdater("1.1.1", "- Smoother scrubbing."), Path.Combine(dir, "update-seen.txt"), localSource: false);
        service.Check(T0, "en").GetAwaiter().GetResult();
        Assert.True(service.Status is { Important: false }, "not important");
        Assert.True(service.RemindDue(T0), "told once");
        service.Reminded(T0);
        Assert.True(!service.RemindDue(T0.AddDays(1)), "no daily notice");
        Assert.Equal(null, UpdateService.PendingImportant(dir, pendingVersion: "1.1.1"), "never put in by itself");
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public static void The_status_page_shows_an_important_update_as_a_card()
    {
        Text.Language = "en";
        var vm = new SettingsViewModel();
        vm.SeedSettings(new AppSettings());
        vm.OnStatusPage = true;
        vm.SeedStatus(new StatusDto(null, [], null, null, false, false, "1.1.0",
            Update: new UpdateStatus("1.2.0", UpdateState.Available, 0, "https://example.org", Important: true, Summary: ["Black screen fix", "Lighter recording"])));
        Assert.True(vm.UpdateCardVisible, "the card on the Status page");
        Assert.True(!vm.UpdateBarVisible, "not the slim bar too");
        Assert.Equal("Black screen fix|Lighter recording", string.Join("|", vm.UpdateSummary), "its lines");
        vm.OnStatusPage = false;
        Assert.True(vm.UpdateBarVisible && !vm.UpdateCardVisible, "on other pages: the slim bar");
    }

    [Test]
    public static void The_tray_offers_the_update_first_and_its_icon_says_so()
    {
        Assert.True(TrayController.MenuKey(TrayState.Active, false, 30, false, false, "en", update: "1.2.0")
                    != TrayController.MenuKey(TrayState.Active, false, 30, false, false, "en"), "the menu is made again when an update comes");
    }
}
