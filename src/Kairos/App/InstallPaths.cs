using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Kairos.Engine;

namespace Kairos.App;

/// <summary>
/// The installed Kairos: Velopack's folder (%LocalAppData%\Kairos), with the program in current\ beside Update.exe and
/// a small stub Kairos.exe at the top that always starts the current version. A development build is not installed.
/// </summary>
public static class InstallPaths
{
    /// <summary>The install folder when this Kairos runs from one (null for a development build).</summary>
    public static string? Root { get; } = Environment.ProcessPath is { } exe ? RootFor(exe) : null;

    /// <summary>What the Start menu, autostart and restarts launch: the stub when installed, else this exe.</summary>
    public static string LaunchExe => Root is { } root ? StubFor(root) : Environment.ProcessPath!;

    /// <summary>The install folder of <paramref name="exePath"/> (…\current\Kairos.exe beside Update.exe), or null.</summary>
    public static string? RootFor(string exePath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(exePath));
        string? root = folder == null ? null : Path.GetDirectoryName(folder);
        return root != null && string.Equals(Path.GetFileName(folder), "current", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(root, "Update.exe")) ? root : null;
    }

    public static string StubFor(string root) => Path.Combine(root, AppInfo.Name + ".exe");

    /// <summary><paramref name="path"/> is <paramref name="root"/> or a folder in it (any case, trailing slashes ignored).</summary>
    public static bool IsInside(string root, string path)
    {
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ids of Kairos processes running from <paramref name="root"/>, this one left out.</summary>
    public static IEnumerable<int> OwnProcesses(string root, IEnumerable<(int Id, string? Path)> processes, int self) =>
        processes.Where(p => p.Id != self && p.Path != null && IsInside(root, p.Path)).Select(p => p.Id);

    /// <summary>
    /// Velopack's start, first in Main: its install, update and uninstall steps run Kairos with arguments of their own.
    /// A downloaded update never goes in because some Kairos started (it would stop the engine mid-recording): the
    /// engine puts it in when nothing is under way.
    /// </summary>
    public static Velopack.VelopackApp VelopackHooks() =>
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).OnBeforeUninstallFastCallback(_ => UninstallHook());

    /// <summary>
    /// Velopack's uninstall hook (30 s): ends this install's engine and settings window (never another copy's: a test
    /// install shares the pipe name with the real one) and its autostart entry. Velopack then removes the folder;
    /// settings and clips stay.
    /// </summary>
    public static void UninstallHook()
    {
        if (Root is not { } root)
            return;
        if (EngineProcessId() is { } engine && Own(root).Contains(engine))
            PipeClient.Send("exit", 2000);
        var clock = Stopwatch.StartNew();
        while (Own(root).Count > 0 && clock.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(200);
        foreach (int id in Own(root))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill();
                process.WaitForExit(5000); // its files closed before they are removed below
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Gone meanwhile.
            }
        }
        Autostart.RemoveIfPointsInto(root);
        WizardLanguage.Forget();
        // The replay's ring files (gigabytes on whichever drive was chosen): Kairos's working files, not the user's.
        foreach (string folder in Engine.Replay.DiskReplayPlace.AllFolders(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            Engine.Replay.DiskReplayPlace.Remove(folder);
    }

    static List<int> Own(string root)
    {
        var all = Process.GetProcessesByName(AppInfo.Name);
        try
        {
            return OwnProcesses(root, all.Select(p => (p.Id, ForegroundApp.ImagePath(p.Id))), Environment.ProcessId).ToList();
        }
        finally
        {
            foreach (var p in all)
                p.Dispose();
        }
    }

    /// <summary>The process serving Kairos's pipe (the engine), or null when none answers.</summary>
    static int? EngineProcessId()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", AppInfo.PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect(500);
            return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id) ? (int)id : null;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
}
