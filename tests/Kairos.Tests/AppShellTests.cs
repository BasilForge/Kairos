using Kairos.App;

namespace Kairos.Tests;

public static class AppShellTests
{
    static readonly ScreenRect Monitor = new(0, 0, 2560, 1440);

    [Test]
    public static void Window_covering_monitor_is_fullscreen()
    {
        Assert.Equal(true, GameWatcher.IsFullscreen(new ScreenRect(0, 0, 2560, 1440), Monitor, "UnrealWindow"), "fullscreen");
        Assert.Equal(true, GameWatcher.IsFullscreen(new ScreenRect(-1, -1, 2561, 1441), Monitor, "SDL_app"), "oversized");
    }

    [Test]
    public static void Smaller_window_is_not_fullscreen()
    {
        Assert.Equal(false, GameWatcher.IsFullscreen(new ScreenRect(0, 0, 2560, 1400), Monitor, "Chrome_WidgetWin_1"), "maximized w/ taskbar");
    }

    [Test]
    public static void Desktop_and_shell_windows_are_never_games()
    {
        foreach (string cls in new[] { "Progman", "WorkerW", "Shell_TrayWnd" })
            Assert.Equal(false, GameWatcher.IsFullscreen(new ScreenRect(0, 0, 2560, 1440), Monitor, cls), cls);
    }

    /// <summary>
    /// "Only record while gaming" (Red's report): a maximized window with its title bar covers the screen when the taskbar
    /// hides itself, and a video in a browser or player fills it too; neither is a game. Kairos itself never is.
    /// </summary>
    [Test]
    public static void Only_a_real_full_screen_app_counts_as_a_game()
    {
        Assert.Equal(false, GameWatcher.IsFullscreen(new ScreenRect(-8, -8, 2568, 1448), Monitor, "CabinetWClass", framedMaximized: true),
            "a maximized window with a title bar (taskbar set to hide)");
        Assert.Equal(true, GameWatcher.IsFullscreen(new ScreenRect(0, 0, 2560, 1440), Monitor, "UnrealWindow", framedMaximized: false), "a borderless game");
        Assert.True(GameWatcher.IsGame("r5apex"), "a game");
        foreach (string notGame in new[] { "chrome", "msedge", "firefox", "brave", "vlc", "mpv", "wwahost", "msedgewebview2", "netflix", "Kairos", "explorer" })
            Assert.True(!GameWatcher.IsGame(notGame), notGame);
    }

    [Test]
    public static void Window_string_yields_decoded_exe_name()
    {
        Assert.Equal("Spotify", WindowTarget.ExeName("Spotify Premium:Chrome_WidgetWin_1:Spotify.exe"), "exe");
        Assert.Equal("obs64", WindowTarget.ExeName("OBS 32 - Profile#3A Untitled:Qt6111QWindowIcon:obs64.exe"), "encoded colon");
        Assert.Equal("Profile: Untitled", WindowTarget.Title("Profile#3A Untitled:Qt:x.exe"), "decoded title");
        Assert.Equal(null, WindowTarget.ExeName(""), "empty");
    }

    [Test]
    public static void Tray_menu_is_kept_while_its_inputs_do_not_change()
    {
        string a = TrayController.MenuKey(TrayState.Active, false, 30, paused: false, recording: false, "ar");
        string b = TrayController.MenuKey(TrayState.Active, false, 30, paused: false, recording: false, "ar");
        Assert.Equal(a, b, "same inputs");
        Assert.True(a != TrayController.MenuKey(TrayState.Paused, false, 30, paused: true, recording: false, "ar"), "pause changes menu");
        Assert.True(a != TrayController.MenuKey(TrayState.Active, true, 30, paused: false, recording: false, "ar"), "quick item changes menu");
        Assert.True(a != TrayController.MenuKey(TrayState.Active, false, 30, paused: false, recording: false, "en"), "language changes menu");
        Assert.True(a != TrayController.MenuKey(TrayState.Active, false, 30, paused: false, recording: false, "ar", canUndo: true), "a deletable last clip changes menu");
        Assert.True(a != TrayController.MenuKey(TrayState.Active, false, 30, paused: false, recording: false, "ar", canShare: true), "a shareable last clip changes menu");
        Assert.True(TrayController.MenuKey(TrayState.Active, false, 30, paused: false, recording: false, "ar", canShare: true, lastIsShot: true)
                    != TrayController.MenuKey(TrayState.Active, false, 30, paused: false, recording: false, "ar", canShare: true),
            "the last thing saved being a screenshot changes the menu (its share and delete items say so)");
    }

    [Test]
    public static void Autostart_command_quotes_path_and_runs_in_background()
    {
        Assert.Equal("\"D:\\Kairos\\bin\\64bit\\Kairos.exe\" --background", Autostart.CommandFor(@"D:\Kairos\bin\64bit\Kairos.exe"), "command");
    }
}
