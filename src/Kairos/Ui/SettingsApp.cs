using System.Windows;
using Kairos.App;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>The settings process (Kairos.exe --settings). Runs only while the window is open.</summary>
public static partial class SettingsApp
{
    const string InstanceMutex = AppInfo.SettingsMutex;
    const string ShowEventName = AppInfo.SettingsShowEvent;

    public static int Run()
    {
        using var mutex = new Mutex(true, InstanceMutex, out bool first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                existing.Set();
            return 0;
        }

        Text.Language = SettingsStore.Load(SettingsStore.DefaultPath).General.Language;
        // Its own log beside the engine's: a window error is written down, not only shrugged off.
        Engine.ObsLog.Open(Path.Combine(SettingsStore.AppDataDir, "logs"), "settings");
        var app = CreateApplication();
        var vm = new SettingsViewModel();
        MainWindow? window = null;
        bool swapping = false;

        void Open()
        {
            var old = window;
            window = new MainWindow(vm);
            window.Closed += (_, _) =>
            {
                if (swapping)
                    return;
                vm.Stop();
                app.Shutdown();
            };
            if (old != null)
                WindowPlacement.Carry(old, window);
            window.Show();
            if (old != null)
            {
                swapping = true;
                old.Close();
                swapping = false;
            }
        }

        // The tray's "Share last clip" leaves the clip in a request file and then opens (or shows) this window.
        void TakeShareRequest()
        {
            if (window != null && ShareRequest.Take(ShareRequest.DefaultPath, DateTime.UtcNow) is { } clip && File.Exists(clip))
                window.ShowShare(clip);
        }

        vm.LanguageChanged += Open;
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
                app.Dispatcher.BeginInvoke(() =>
                {
                    if (window == null)
                        return;
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;
                    window.Activate();
                    TakeShareRequest();
                });
        }) { IsBackground = true };
        listener.Start();

        Open();
        vm.Start();
        TakeShareRequest();
        // What a save or a model download left when Kairos was ended in the middle of one (once, in the background).
        string clips = vm.EffectiveFolder;
        Task.Run(() =>
        {
            try
            {
                Edit.EditJobs.SweepLeftovers(clips);
                Engine.Captions.CaptionModel.Default.RemoveParts();
                Engine.Captions.CaptionRuntime.Default.RemoveParts();
                // Kept in the roaming data folder before 1.0: moved to the local one (the same drive: a rename).
                Engine.Captions.CaptionModel.Default.MoveFrom(Engine.Captions.CaptionModel.OldFolder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        });
        app.Run();
        // The window closed: a save still running stops and removes its part file before the process ends.
        window?.FinishEdits();
        GC.KeepAlive(mutex);
        return 0;
    }

    /// <summary>Where the window's start-up time goes (set by the measuring harness only).</summary>
    internal static Action<string>? StartupTrace;

    static System.Windows.Application CreateApplication()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        StartupTrace?.Invoke("application");
        app.DispatcherUnhandledException += (_, e) =>
        {
            Engine.ObsLog.Error("settings ui: " + e.Exception);
            e.Handled = true;
        };
#pragma warning disable WPF0001
        app.ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Kairos;component/Ui/Theme.xaml"),
        });
        StartupTrace?.Invoke("theme");
        ThemeCursors.Install(); // Kairos's own mouse pointers on every window
        StartupTrace?.Invoke("cursors");
        return app;
    }
}
