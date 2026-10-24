using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>
/// Closing the settings window with unsaved changes asks first. They were dropped without a word, and a user's clips
/// folder "went back to the default" after Kairos started again (2026-10-24).
/// </summary>
public static class UnsavedCloseTests
{
    /// <summary>A window over default settings that saves to <paramref name="path"/> and tells no engine.</summary>
    static SettingsViewModel Seeded(string path, List<string>? sent = null)
    {
        var s = new AppSettings();
        s.Normalize();
        var vm = new SettingsViewModel
        {
            SettingsPath = path,
            Send = (command, _) =>
            {
                sent?.Add(command);
                return null;
            },
        };
        vm.SeedSettings(s);
        return vm;
    }

    static SettingsViewModel Changed(string path, List<string>? sent = null)
    {
        var vm = Seeded(path, sent);
        vm.Folder = @"D:\clip 2";
        return vm;
    }

    static string TempSettings() => Path.Combine(Path.GetTempPath(), "kairos-close-" + Guid.NewGuid().ToString("N") + ".json");

    [Test]
    public static void Closing_with_unsaved_changes_asks_and_save_keeps_them()
    {
        string path = TempSettings();
        var sent = new List<string>();
        try
        {
            var vm = Changed(path, sent);
            bool? answered = null;
            vm.CloseAnswered += save => answered = save;
            Assert.True(vm.AskBeforeClose(), "unsaved: the window stays open and asks");
            Assert.True(vm.AskingClose, "the question shows");
            vm.KeepOpenCommand.Execute(null);
            Assert.True(!vm.AskingClose && answered == null && vm.IsDirty, "Back: still open, the change still there");

            Assert.True(vm.AskBeforeClose(), "asked again at the next close");
            vm.SaveAndCloseCommand.Execute(null);
            Assert.True(answered == true && !vm.AskingClose, "Save: the window closes");
            vm.SaveBeforeClose();
            Assert.Equal(@"D:\clip 2", SettingsStore.Load(path).Output.Folder, "saved");
            Assert.True(!vm.IsDirty && sent.SequenceEqual(["apply"]), "and the engine told before the window's process ends");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public static void Close_without_saving_keeps_the_old_settings_and_no_change_closes_at_once()
    {
        string path = TempSettings();
        var vm = Changed(path);
        bool? answered = null;
        vm.CloseAnswered += save => answered = save;
        Assert.True(vm.AskBeforeClose(), "asked");
        vm.CloseWithoutSavingCommand.Execute(null);
        Assert.True(answered == false && !vm.AskingClose, "closes without saving");
        Assert.True(!File.Exists(path), "nothing written");

        var clean = Seeded(path);
        Assert.True(!clean.AskBeforeClose() && !clean.AskingClose, "nothing unsaved: closes at once, no question");
    }
}
