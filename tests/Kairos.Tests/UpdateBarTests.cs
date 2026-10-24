using Kairos.App;
using Kairos.Engine.Updates;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>The settings window's update bar: what it says for each step, and when Update can be pressed.</summary>
public static class UpdateBarTests
{
    static SettingsViewModel With(UpdateStatus? update)
    {
        var vm = new SettingsViewModel();
        vm.SeedStatus(new StatusDto(null, null, null, false, false, "1.0.0-beta.1", Update: update));
        return vm;
    }

    [Test]
    public static void The_bar_says_what_the_update_is_doing() => LanguageTests.WithLanguage("en", () =>
    {
        const string notes = "https://github.com/BasilForge/Kairos/releases/tag/v1.0.1";
        var vm = With(new UpdateStatus("1.0.1", UpdateState.Available, 0, notes));
        Assert.True(vm.UpdateBarVisible, "shown");
        Assert.Equal("Kairos 1.0.1 is available", vm.UpdateText, "available");
        Assert.Equal("Update", vm.UpdateActionText, "its button");
        Assert.True(vm.UpdateActionVisible, "with a button");

        vm = With(new UpdateStatus("1.0.1", UpdateState.Downloading, 40, notes));
        Assert.Equal("Downloading the update… 40%", vm.UpdateText, "downloading");
        Assert.True(!vm.UpdateActionVisible, "no button while downloading");

        vm = With(new UpdateStatus("1.0.1", UpdateState.WaitingForRecording, 100, notes));
        Assert.Equal("Updates when the recording ends", vm.UpdateText, "waiting for the recording");

        vm = With(new UpdateStatus("1.0.1", UpdateState.WaitingForWindow, 100, notes));
        Assert.Equal("Updates when you're done here", vm.UpdateText, "waiting for the window");
        Assert.True(!vm.UpdateActionVisible, "no button while waiting");

        vm = With(new UpdateStatus("1.0.1", UpdateState.Failed, 30, notes));
        Assert.Equal("The update could not be downloaded", vm.UpdateText, "failed");
        Assert.Equal("Try again", vm.UpdateActionText, "retry");
        Assert.True(vm.UpdateActionVisible, "with a button");

        Assert.True(!With(null).UpdateBarVisible, "nothing: no bar");
        Assert.True(!With(new UpdateStatus("1.0.1", UpdateState.None, 0, null)).UpdateBarVisible, "none: no bar");
    });

    /// <summary>Update restarts Kairos, so unsaved settings changes would be lost: save or undo them first.</summary>
    [Test]
    public static void Update_waits_for_unsaved_changes() => LanguageTests.WithLanguage("en", () =>
    {
        var vm = With(new UpdateStatus("1.0.1", UpdateState.Available, 0, null));
        Assert.True(vm.UpdateCommand.CanExecute(null), "nothing unsaved: can update");
        vm.StartWithWindows = !vm.StartWithWindows;
        Assert.True(vm.IsDirty && !vm.UpdateCommand.CanExecute(null), "a change not saved: not yet");
        vm.RevertCommand.Execute(null);
        Assert.True(!vm.IsDirty && vm.UpdateCommand.CanExecute(null), "undone: can update");
    });

    /// <summary>
    /// The window tells the engine, with each status question, that it is in use (unsaved changes; the editor, sharing or
    /// a save still running): a downloaded update then waits instead of closing it.
    /// </summary>
    [Test]
    public static void The_window_says_when_it_is_in_use()
    {
        var vm = With(new UpdateStatus("1.0.1", UpdateState.Downloading, 50, null));
        Assert.Equal("status", vm.StatusQuestion, "nothing under way");
        vm.StartWithWindows = !vm.StartWithWindows;
        Assert.Equal("status busy", vm.StatusQuestion, "a change not saved");
        vm.RevertCommand.Execute(null);
        Assert.Equal("status", vm.StatusQuestion, "undone");
        vm.InUse = () => true;
        Assert.Equal("status busy", vm.StatusQuestion, "the editor or the share panel open (the window says)");
    }

    /// <summary>The bar's Update button can be pressed as soon as the bar shows (WPF re-asks a button only after input).</summary>
    [Test]
    public static void Update_can_be_pressed_as_soon_as_the_bar_shows()
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = With(null);
                static void Settle() =>
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Settle();
                int asked = 0;
                EventHandler look = (_, _) => asked++;
                vm.UpdateCommand.CanExecuteChanged += look;
                // The next status answer: everything as before, only the update is new.
                vm.SeedStatus(new StatusDto(null, null, null, false, false, "1.0.0-beta.1",
                    Update: new UpdateStatus("1.0.1", UpdateState.Available, 0, null)));
                Settle();
                Assert.True(asked > 0, "the button was told to look again");
                GC.KeepAlive(look);
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
    public static void What_s_new_shows_only_with_a_link()
    {
        Assert.True(With(new UpdateStatus("1.0.1", UpdateState.Available, 0, "https://github.com/BasilForge/Kairos/releases/tag/v1.0.1")).UpdateNotesVisible, "GitHub: a link");
        Assert.True(!With(new UpdateStatus("1.0.1", UpdateState.Available, 0, null)).UpdateNotesVisible, "a local folder: none");
    }
}
