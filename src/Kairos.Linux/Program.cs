using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Kairos.App;
using Kairos.Native;
using Kairos.Platform;

namespace Kairos;

public static class Program
{
    public static int Main(string[] args)
    {
        string prefix = Engine.CaptureBackend.Prefix(AppContext.BaseDirectory);
        NativeLibrary.SetDllImportResolver(typeof(Obs).Assembly, (name, _, _) =>
            name == "obs" ? NativeLibrary.Load(Path.Combine(prefix, "lib", "libobs.so.30")) : IntPtr.Zero);

        string mode = args.Length > 0 ? args[0] : "";
        switch (mode)
        {
            case "--selftest-record": return SelfTests.Record();
            case "--selftest-hotkeys": return SelfTests.Hotkeys();
            case "--selftest-foreground": return SelfTests.Foreground();
            case "--selftest-share-site": return SiteSelfTest.Run(args);
            case "--captions": return Kairos.Engine.Captions.CaptionProcess.Helper(args);
            case "--settings":
                if (Environment.GetEnvironmentVariable("KAIROS_UILOG") == "1")
                    System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener(useErrorStream: true)); // Avalonia's warnings (failed bindings)
                KairosApp.ThemeEnabled = true;
                return Ui.SettingsApp.Run();
            case "--write-icon":
                // kairos --write-icon <app.png>: the app icon, from the same drawing the window uses.
                using (var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://kairos/Assets/icons/kairos-256.png")))
                using (var file = File.Create(args[1]))
                    icon.CopyTo(file);
                return 0;
            case "--send":
                Console.WriteLine(PipeClient.Send(args.Length > 1 ? args[1] : "status") ?? "(engine not running)");
                return 0;
            case "" or "--background":
                return RunEngine(mode == "--background");
            default:
                Console.Error.WriteLine("usage: kairos [--background] | --send <command> | --selftest-record | --selftest-hotkeys | --selftest-foreground");
                return 2;
        }
    }

    static int RunEngine(bool background)
    {
        using var instance = InstanceLock.TryAcquire("engine");
        if (instance == null)
        {
            // Already running: a plain launch means "show me the settings".
            if (!background)
                PipeClient.Send("open-settings");
            return 0;
        }

        Resilience.InstallHandlers();
        PowerEvents.Start();
        EngineApp? engine = null;
        KairosApp.Opened = _ => engine = new EngineApp(openSettings: !background, background);
        int code = KairosApp.Build(software: true).StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        GC.KeepAlive(engine);
        GC.KeepAlive(instance);
        return code;
    }
}
