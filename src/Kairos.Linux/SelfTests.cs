using Kairos.App;
using Kairos.Engine;
using Kairos.Native;
using Kairos.Settings;

namespace Kairos;

/// <summary>Console checks of the Linux pieces against the real system: kairos --selftest-record, --selftest-hotkeys.</summary>
static class SelfTests
{
    /// <summary>Records a few seconds into the replay buffer, saves a clip, and exits 0 when the file exists.</summary>
    public static int Record()
    {
        string app = AppContext.BaseDirectory;
        string work = Path.Combine(Path.GetTempPath(), "kairos-spike");
        string clips = Path.Combine(work, "clips");
        Directory.CreateDirectory(work);
        var settings = new AppSettings();
        settings.Output.Folder = clips;
        settings.Replay.Seconds = 15;
        settings.Video.Fps = 30;
        settings.Video.Resolution = OutputResolution.P1080;
        ObsHost.Start(app, work, work, new VideoConfig(1920, 1080, 1920, 1080, 30), settings.Video.Codec);
        Console.WriteLine("failed modules: " + string.Join(",", ObsHost.FailedModules));
        var pipeline = new CapturePipeline(settings);
        pipeline.Build();
        Console.WriteLine($"built: {pipeline.Choice!.Id} {pipeline.Output.W}x{pipeline.Output.H}@{pipeline.Fps}");
        var saved = new ManualResetEventSlim();
        string? path = null;
        pipeline.ReplaySaved += (p, _) => { path = p; saved.Set(); };
        Console.WriteLine("buffer started: " + pipeline.SetBufferActive(true) + " " + pipeline.LastError);
        Thread.Sleep(8000);
        Console.WriteLine("save requested: " + pipeline.RequestSave(false));
        Console.WriteLine("saved signal: " + saved.Wait(15000) + " -> " + path);
        pipeline.SetBufferActive(false);
        pipeline.Dispose();
        ObsHost.Shutdown();
        return path != null && File.Exists(path) ? 0 : 1;
    }

    /// <summary>Grabs Alt+F10, waits for it (send it with xdotool), and exits 0 when it came.</summary>
    public static int Hotkeys()
    {
        HotkeyParser.TryParse("Alt+F10", out var key);
        Console.WriteLine("free before: " + HotkeyManager.IsFree(key));
        using var keys = new HotkeyManager();
        var pressed = new ManualResetEventSlim();
        keys.Pressed += id => { Console.WriteLine("pressed " + id); pressed.Set(); };
        Console.WriteLine("registered: " + keys.Register("save", key));
        Console.WriteLine("free while held: " + HotkeyManager.IsFree(key));
        Console.WriteLine("ready");
        bool got = pressed.Wait(8000);
        Console.WriteLine("got: " + got);
        keys.UnregisterAll();
        Console.WriteLine("free after: " + HotkeyManager.IsFree(key));
        return got ? 0 : 1;
    }

    /// <summary>Prints what the desktop says is in front, after a pause to switch windows.</summary>
    public static int Foreground()
    {
        Thread.Sleep(3000);
        Console.WriteLine("app: " + ForegroundApp.Current());
        Console.WriteLine("is kairos: " + ForegroundApp.IsKairos());
        using var watcher = new GameWatcher();
        Console.WriteLine("game active: " + watcher.GameActive);
        return 0;
    }
}
