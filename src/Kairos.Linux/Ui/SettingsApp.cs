using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Kairos.App;
using Kairos.Platform;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>The settings process (kairos --settings). Runs only while the window is open.</summary>
public static class SettingsApp
{
    /// <summary>A second launch asks the open window to come forward over this pipe (Windows uses a named event).</summary>
    static readonly string ShowPipe = "Kairos.Settings" + Ipc.PipeName[AppInfo.PipeName.Length..];

    public static int Run()
    {
        using var instance = InstanceLock.TryAcquire("settings");
        if (instance == null)
        {
            PipeClient.Send("show", 1000, ShowPipe);
            return 0;
        }

        Text.Language = SettingsStore.Load(SettingsStore.DefaultPath).General.Language;
        Engine.ObsLog.Open(Path.Combine(SettingsStore.AppDataDir, "logs"), "settings");
        Resilience.InstallHandlers();
        PowerEvents.Start();
        MainWindow? window = null;
        // A logout, or the engine leaving, ends this window as its own close button would (the signal is otherwise swallowed).
        PowerEvents.SessionEnding += () => UiThread.Post(() => window?.Close());

        // Self-tests on a PC someone is using play silence.
        Edit.PlayerSession.Muted = Environment.GetEnvironmentVariable("KAIROS_MUTE") == "1";
        SettingsViewModel? vm = null;
        bool swapping = false;
        IClassicDesktopStyleApplicationLifetime? lifetime = null;

        void Open()
        {
            var old = window;
            window = new MainWindow(vm!);
            window.Closed += (_, _) =>
            {
                if (swapping)
                    return;
                vm!.Stop();
                lifetime?.Shutdown();
            };
            if (old != null)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = old.Position;
                window.Width = old.Width;
                window.Height = old.Height;
            }
            window.Show();
            if (old != null)
            {
                swapping = true;
                old.Close();
                swapping = false;
            }
        }

        using var show = new PipeServer(command =>
        {
            UiThread.Post(() =>
            {
                if (window == null)
                    return;
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
            });
            return "ok";
        }, () => (0, 0), ShowPipe);

        KairosApp.Opened = desktop =>
        {
            lifetime = desktop;
            vm = new SettingsViewModel(); // it reads the application's resources, so only once the application is up
            vm.LanguageChanged += Open;
            Open();
            vm.Start();
        };
        int code = KairosApp.Build(software: false).StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        GC.KeepAlive(instance);
        return code;
    }
}
