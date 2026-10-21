using System.Diagnostics;
using System.Text;
using Kairos.Engine;
using Kairos.Platform;

namespace Kairos.App;

/// <summary>Names the app a clip belongs to, from the X11 window in front (EWMH: _NET_ACTIVE_WINDOW, _NET_WM_PID).</summary>
public static class ForegroundApp
{
    /// <summary>Folder name for the foreground window's app.</summary>
    public static string Current()
    {
        using var x = XSession.Open();
        if (x == null || x.Active() is not { } window)
            return ClipOrganizer.AppFolderName(null, null, null);
        return FromProcessId(x.Pid(window), x.Title(window));
    }

    /// <summary>Whether the window in front is Kairos's own (its settings window runs the same program).</summary>
    public static bool IsKairos()
    {
        using var x = XSession.Open();
        if (x == null || x.Active() is not { } window || x.Pid(window) is not { } pid)
            return false;
        return pid == Environment.ProcessId || IsSameApp(ImagePath(pid), Environment.ProcessPath);
    }

    public static bool IsSameApp(string? path, string? ownPath) =>
        path != null && ownPath != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(ownPath), StringComparison.Ordinal);

    /// <summary>Folder name for a running program (Window mode targets): a process of that name, else the name itself.</summary>
    public static string ForExe(string exeName)
    {
        var proc = Process.GetProcessesByName(exeName).FirstOrDefault();
        return proc == null ? ClipOrganizer.AppFolderName(exeName, null, null) : FromProcessId(proc.Id, null);
    }

    static string FromProcessId(int? pid, string? title)
    {
        string? path = pid is { } p ? ImagePath(p) : null;
        return ClipOrganizer.AppFolderName(path != null ? Path.GetFileNameWithoutExtension(path) : null, null, title);
    }

    /// <summary>The command line of process <paramref name="pid"/> (arguments joined by spaces); empty when it is gone.</summary>
    internal static string CommandLine(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline", Encoding.UTF8).Replace('\0', ' ');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    /// The program file of process <paramref name="pid"/>, or null when it is gone. A Windows game under Wine or Proton is
    /// really "wine ... Game.exe": the .exe on its command line names it, not wine.
    /// </summary>
    internal static string? ImagePath(int pid)
    {
        try
        {
            string exe = new FileInfo($"/proc/{pid}/exe").LinkTarget ?? "";
            if (Path.GetFileName(exe) is "wine-preloader" or "wine64-preloader" or "wine" or "wine64" or "proton" or "reaper")
                foreach (string arg in File.ReadAllText($"/proc/{pid}/cmdline", Encoding.UTF8).Split('\0').Reverse())
                    if (arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return arg.Replace('\\', '/');
            return exe.Length > 0 ? exe : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
