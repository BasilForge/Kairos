using System.Runtime.InteropServices;

namespace Kairos.App;

/// <summary>Reads libobs window strings "title:class:exe" (":" is stored as "#3A", "#" as "#22").</summary>
public static class WindowTarget
{
    static string[] Parts(string? window) => string.IsNullOrEmpty(window) ? [] : window.Split(':');

    static string Decode(string s) => s.Replace("#3A", ":").Replace("#22", "#");

    static string Escape(string s) => s.Replace("#", "#22").Replace(":", "#3A");

    /// <summary>A window as libobs names it: its title, class and exe file, each escaped.</summary>
    public static string Encode(string title, string className, string exeFile) => $"{Escape(title)}:{Escape(className)}:{Escape(exeFile)}";

    public static string? Title(string? window) => Parts(window) is { Length: 3 } p ? Decode(p[0]) : null;

    public static string? ExeName(string? window) =>
        Parts(window) is { Length: 3 } p && p[2].Length > 0 ? Path.GetFileNameWithoutExtension(Decode(p[2])) : null;

    /// <summary>What makes two windows one app (its exe; the whole value when it names none).</summary>
    public static string AppKey(string window) => ExeName(window) ?? window;

    /// <summary>
    /// Whether the app has a window, found the way OBS's window capture finds it (by its exe): a visible top-level window
    /// that is not a tool window, minimized ones included (they keep their last picture). The app's processes come from
    /// one process snapshot, so no process is opened.
    /// </summary>
    public static bool IsOpen(string? window) => window != null && OpenApps([window]).Count > 0;

    /// <summary>
    /// The exe names (without ".exe") of <paramref name="windows"/>' apps that have such a window: one process snapshot
    /// and one pass over the windows, however many apps are asked about.
    /// </summary>
    public static HashSet<string> OpenApps(IEnumerable<string> windows)
    {
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = windows.Select(ExeName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return open;
        var apps = new Dictionary<uint, string>();
        ProcessList.Any((pid, name) =>
        {
            if (wanted.Contains(name))
                apps[pid] = name;
            return false;
        });
        int running = apps.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (running == 0)
            return open;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (apps.TryGetValue(pid, out string? name) &&
                Counts(IsWindowVisible(hwnd), (long)GetWindowLongPtr(hwnd, StyleIndex), (long)GetWindowLongPtr(hwnd, ExStyleIndex)))
                open.Add(name);
            return open.Count < running;
        }, IntPtr.Zero);
        return open;
    }

    /// <summary>
    /// The processes with a window someone can see now: one OBS's window capture would take (<see cref="Counts"/>), with a
    /// title, and not hidden by Windows (a suspended Store app's window is visible but cloaked).
    /// </summary>
    public static HashSet<uint> ProcessesWithWindows()
    {
        var pids = new HashSet<uint>();
        EnumWindows((hwnd, _) =>
        {
            if (Counts(IsWindowVisible(hwnd), (long)GetWindowLongPtr(hwnd, StyleIndex), (long)GetWindowLongPtr(hwnd, ExStyleIndex))
                && GetWindowTextLength(hwnd) > 0
                && (DwmGetWindowAttribute(hwnd, DwmCloaked, out int cloaked, sizeof(int)) != 0 || cloaked == 0))
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                pids.Add(pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    const int DwmCloaked = 14;
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>A window OBS's window capture would take: visible, not a child, not a tool window.</summary>
    public static bool Counts(bool visible, long style, long exStyle) =>
        visible && (style & WsChild) == 0 && (exStyle & WsExToolWindow) == 0;

    const int StyleIndex = -16, ExStyleIndex = -20;
    const long WsChild = 0x40000000, WsExToolWindow = 0x80;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
