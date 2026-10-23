using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>
/// Windows opened off screen (renders, checks, measurements, hidden browsers) never take the foreground, even when a
/// control inside them takes the keyboard focus: they once pulled a game being played out of focus.
/// </summary>
public static class OffScreenWindowTests
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [Test]
    public static void An_off_screen_window_never_takes_the_foreground()
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var button = new Button { Content = "Close" };
                var window = WindowPlacement.OffScreen(new Window { Width = 300, Height = 200, Content = button });
                window.Show();
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                // What the player and the editor do when they open: put the keyboard focus on a button.
                button.Focus();
                var until = DateTime.UtcNow.AddMilliseconds(500);
                while (DateTime.UtcNow < until)
                {
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(20);
                }
                bool stole = GetForegroundWindow() == hwnd;
                // Whether Windows would let this test process take the foreground at all varies; the window's style is
                // what keeps any process's off-screen window from it (WS_EX_NOACTIVATE), and off the Alt+Tab list.
                nint style = GetWindowLongPtr(hwnd, -20);
                window.Close();
                Assert.True(!stole, "the off-screen window became the foreground window");
                Assert.True((style & 0x08000000) != 0, "it can never be activated");
                Assert.True((style & 0x00000080) != 0, "and stays off Alt+Tab");
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

    /// <summary>
    /// The window built again in another language takes the old one's place: a maximized window's Left and Top are its
    /// normal place but its Width and Height the screen's, so it came back unmaximized and past the screen (a test
    /// PC, 2026-10-21: 2576×1408 at 730,346 on a 2560×1440 screen, the Arabic sidebar off it).
    /// </summary>
    [Test]
    public static void A_maximized_window_built_again_comes_back_maximized_at_its_normal_size()
    {
        var current = new Rect(730, 346, 2576, 1408);
        var normal = new Rect(730, 346, 1100, 700);
        Assert.Equal((normal, true), WindowPlacement.Carried(WindowState.Maximized, current, normal), "maximized");
        Assert.Equal((new Rect(100, 80, 1100, 700), false), WindowPlacement.Carried(WindowState.Normal, new Rect(100, 80, 1100, 700), Rect.Empty), "normal: where it is");
        Assert.Equal((normal, false), WindowPlacement.Carried(WindowState.Minimized, current, normal), "minimized: shown again at its normal place");
    }
}
