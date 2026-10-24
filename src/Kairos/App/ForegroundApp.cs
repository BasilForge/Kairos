using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Kairos.Engine;

namespace Kairos.App;

/// <summary>Names the app a clip belongs to.</summary>
public static class ForegroundApp
{
    const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Folder name for the foreground window's app.</summary>
    public static string Current()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return ClipOrganizer.AppFolderName(null, null, null);
        GetWindowThreadProcessId(hwnd, out uint pid);
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        return FromProcessId((int)pid, title.ToString());
    }

    /// <summary>Whether the window in front is Kairos's own (its settings window runs the same exe).</summary>
    public static bool IsKairos()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId || IsSameApp(ImagePath((int)pid), Environment.ProcessPath);
    }

    public static bool IsSameApp(string? path, string? ownPath) =>
        path != null && ownPath != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(ownPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folder name for a running exe (Window mode targets).</summary>
    public static string ForExe(string exeName)
    {
        var proc = Process.GetProcessesByName(exeName).FirstOrDefault();
        return proc == null ? ClipOrganizer.AppFolderName(exeName, null, null) : FromProcessId(proc.Id, null);
    }

    /// <summary>
    /// Folder name for a process seen earlier (a snapshot, the last game in front): its exe name when it is gone, or when
    /// its pid is another process's by now.
    /// </summary>
    public static string ForProcess(uint pid, string exeName) =>
        ImagePath((int)pid) is { } path && Path.GetFileNameWithoutExtension(path).Equals(exeName, StringComparison.OrdinalIgnoreCase)
            ? FromProcessId((int)pid, null)
            : ClipOrganizer.AppFolderName(exeName, null, null);

    static string FromProcessId(int pid, string? title)
    {
        string? path = ImagePath(pid);
        if (path == null)
            return Unopened(ProcessList.NameOf((uint)pid), title);
        string? processName = path != null ? Path.GetFileNameWithoutExtension(path) : null;
        string? product = null;
        if (path != null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                product = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
            }
            catch (FileNotFoundException) { }
        }
        return ClipOrganizer.AppFolderName(processName, product, title);
    }

    /// <summary>
    /// A process Kairos cannot open (a game whose anti-cheat guards it): its name from a snapshot, and its window's title
    /// in place of the name its file would give (a user's Genshin Impact clips all went to "Desktop").
    /// </summary>
    internal static string Unopened(string? snapshotName, string? title) =>
        ClipOrganizer.AppFolderName(snapshotName, string.IsNullOrWhiteSpace(title) ? null : title, title);

    /// <summary>The program file of process <paramref name="pid"/>, or null when it is gone.</summary>
    internal static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
