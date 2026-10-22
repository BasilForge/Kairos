using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>The Capture page's apps: their names read left to right in a right-to-left window.</summary>
public static class CaptureAppsTests
{
    [Test]
    public static void An_apps_name_keeps_its_brackets_in_a_right_to_left_window()
    {
        const string open = "Spotify:Chrome_WidgetWin_1:Spotify.exe", closed = "Old game:UnityWndClass:old.exe";
        Text.Language = "ar";
        try
        {
            var s = new AppSettings();
            s.Capture.Mode = CaptureMode.Window;
            s.Capture.Apps.AddRange([open, closed]);
            s.Normalize();
            var vm = new SettingsViewModel();
            vm.SeedSettings(s);
            vm.Seed(null, new CapsDto([], [new("[Spotify.exe]: Spotify", open), new("[notepad.exe]: Notes", "Notes:Notepad:notepad.exe")], [], [], [], "32.2.2"));
            static bool Wrapped(string label) => label.StartsWith('‎') && label.EndsWith('‎');
            Assert.True(vm.Windows.All(o => Wrapped(o.Label)), "the open windows to add");
            Assert.True(vm.ChosenApps.All(o => Wrapped(o.Label)), "the chosen apps, open and closed");
            Assert.Equal(2, vm.ChosenApps.Count, "both chosen apps listed");
        }
        finally
        {
            Text.Language = "en";
        }
    }

    /// <summary>
    /// Windows 10 draws a yellow frame around a window recorded with Windows's own capture (Apps), and no app can turn
    /// it off there (Windows 11 lets Kairos turn it off): the Capture page says so, only on Windows 10 with Apps chosen.
    /// </summary>
    [Test]
    public static void Only_windows_10_with_apps_chosen_says_the_yellow_frame_is_not_in_the_clip()
    {
        bool was = WindowsCaptureFrame.Shown;
        try
        {
            var s = new AppSettings();
            s.Capture.Mode = CaptureMode.Window;
            var vm = new SettingsViewModel();
            vm.SeedSettings(s);
            WindowsCaptureFrame.Shown = true; // Windows 10
            Assert.True(vm.AppsYellowFrame, "Windows 10, Apps: said");
            vm.Mode = CaptureMode.Game;
            Assert.True(!vm.AppsYellowFrame, "Windows 10, Games: not said");
            vm.Mode = CaptureMode.Screen;
            Assert.True(!vm.AppsYellowFrame, "Windows 10, Full screen: not said");
            vm.Mode = CaptureMode.Window;
            WindowsCaptureFrame.Shown = false; // Windows 11: Kairos turns the frame off
            Assert.True(!vm.AppsYellowFrame, "Windows 11, Apps: not said");
        }
        finally
        {
            WindowsCaptureFrame.Shown = was;
        }
    }
}
