using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Kairos.Platform;

namespace Kairos.App;

/// <summary>
/// The Avalonia application behind both of Kairos's processes. The engine (the tray, hotkeys, pipeline and IPC) has no
/// window of its own; the settings process (--settings) opens its window from <see cref="Opened"/>.
/// </summary>
public sealed class KairosApp : Application
{
    /// <summary>What to do once Avalonia is up; set by Program before the app starts.</summary>
    public static Action<IClassicDesktopStyleApplicationLifetime>? Opened { get; set; }

    public override void Initialize()
    {
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        // The look of the settings window; the engine, which has no window, never needs it.
        if (ThemeEnabled)
            Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://kairos/")) { Source = new Uri("avares://kairos/Ui/Theme.axaml") });
    }

    /// <summary>Whether to load the settings window's theme (set by Program before the app starts).</summary>
    public static bool ThemeEnabled { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        UiThread.Poster = action => Dispatcher.UIThread.Post(action);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Opened?.Invoke(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Ends the Avalonia loop (and with it the process).</summary>
    public static void Quit() =>
        Dispatcher.UIThread.Post(() => (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());

    /// <param name="software">
    /// Draw with the CPU. The engine does: libobs owns the GPU's EGL in that process, and a second GL user there ended it
    /// the first time the toast appeared; a tray icon and a small card need no GPU anyway.
    /// </param>
    public static AppBuilder Build(bool software) => AppBuilder.Configure<KairosApp>().UsePlatformDetect().LogToTrace(Avalonia.Logging.LogEventLevel.Warning)
        .With(new X11PlatformOptions { RenderingMode = software ? [X11RenderingMode.Software] : [X11RenderingMode.Glx, X11RenderingMode.Egl, X11RenderingMode.Software] });
}
