using Kairos.App;
using Kairos.Engine.Updates;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>General's "Check now": the engine says how a look ended; the row shows it, on one line, after a press.</summary>
public static class UpdateCheckNowTests
{
    sealed class Updater(Func<UpdateFound?> check) : IUpdater
    {
        public Task<UpdateFound?> Check() => Task.FromResult(check());
        public Task Download(Action<int> progress, CancellationToken ct) => Task.CompletedTask;
        public void ApplyAndExit(string[] restartArgs) { }
    }

    static UpdateService Service(Func<UpdateFound?> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new UpdateService(new Updater(check), Path.Combine(dir, "update-seen.txt"), localSource: false);
    }

    [Test]
    public static void A_look_says_how_it_ended()
    {
        var none = Service(() => null);
        Assert.True(none.Look is { Works: true, Running: false, Done: 0, Last: LookResult.None }, "nothing looked yet");
        none.Check(DateTime.UtcNow).Wait();
        Assert.True(none.Look is { Done: 1, Last: LookResult.UpToDate }, "nothing newer: up to date");

        var found = Service(() => new UpdateFound("9.9.0", null));
        found.Check(DateTime.UtcNow).Wait();
        Assert.True(found.Look is { Done: 1, Last: LookResult.Found }, "a newer one: found (the bar shows it)");

        var offline = Service(() => throw new System.Net.Http.HttpRequestException("offline"));
        offline.Check(DateTime.UtcNow).Wait();
        Assert.True(offline.Look is { Done: 1, Last: LookResult.Failed }, "no connection: failed");

        Assert.True(!new UpdateService(null, Path.Combine(Path.GetTempPath(), "x.txt"), false).Look.Works, "not installed: nothing to look with");

        bool online = true;
        var later = Service(() => online ? new UpdateFound("9.9.0", null) : throw new System.Net.Http.HttpRequestException("offline"));
        later.Check(DateTime.UtcNow).Wait();
        online = false;
        later.Check(DateTime.UtcNow).Wait();
        Assert.True(later.Look is { Done: 2, Last: LookResult.Failed }, "found yesterday, offline now: this look failed (not 'found' again)");
    }

    [Test]
    public static void Check_now_says_checking_then_the_result_in_the_button_for_a_moment()
    {
        // No version on the row; the button itself says how the look went, then is itself again.
        Text.Language = "en";
        var s = new AppSettings();
        s.Normalize();
        var vm = new SettingsViewModel();
        vm.SeedSettings(s);
        var sent = new List<string>();
        SettingsViewModel.PipeSend = command =>
        {
            sent.Add(command);
            return null;
        };
        try
        {
            StatusDto Status(UpdateLook look) => new(null, [], null, null, false, false, AppInfo.Version, Look: look);
            vm.Seed(Status(new UpdateLook(true, false, 2, LookResult.Failed)), null);
            Assert.True(vm.CanCheckUpdates, "installed: the button shows");
            Assert.Equal(Text.Get("general.checkNow"), vm.UpdateLookText, "Check now (a daily look's failure is not shown)");

            vm.CheckUpdatesNowCommand.Execute(null);
            for (int i = 0; i < 50 && sent.Count == 0; i++)
                Thread.Sleep(20);
            Assert.Equal("update-check", sent.Single(), "the engine is asked");
            Assert.True(vm.IsCheckingUpdates && vm.UpdateLookText == Text.Get("general.checking"), "checking, the button waits");

            vm.Seed(Status(new UpdateLook(true, false, 3, LookResult.UpToDate)), null);
            Assert.True(!vm.IsCheckingUpdates && vm.UpdateLookOk && vm.UpdateLookText == Text.Get("general.upToDate"), "up to date, in the button");
            vm.EndLookShown();
            Assert.True(vm.UpdateLookText == Text.Get("general.checkNow") && !vm.UpdateLookOk, "a moment later: Check now again");

            vm.CheckUpdatesNowCommand.Execute(null);
            vm.Seed(Status(new UpdateLook(true, false, 4, LookResult.Failed)), null);
            Assert.True(vm.UpdateLookFailed && vm.UpdateLookText == Text.Get("general.checkFailed"), "could not check: said, in amber");
            vm.EndLookShown();
            Assert.True(!vm.UpdateLookFailed && vm.UpdateLookText == Text.Get("general.checkNow"), "then Check now again");

            vm.Seed(Status(new UpdateLook(false, false, 0, LookResult.None)), null);
            Assert.True(!vm.CanCheckUpdates, "not installed (a development run): no button");
        }
        finally
        {
            SettingsViewModel.PipeSend = command => PipeClient.Send(command);
        }
    }
}
