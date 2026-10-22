using System.Runtime.InteropServices;
using System.Text;

namespace Kairos.App;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>An app in front that fills its screen: its window ("title:class:exe"), process, exe name and screen device.</summary>
public sealed record FullscreenApp(string Window, uint Pid, string Exe, string Monitor);

/// <summary>
/// Reports whether a game is in the foreground ("Only record while gaming"), with a grace period on exit: an app that
/// really fills its screen (no title bar), not a browser, a video player, Windows' own screens or Kairos.
/// </summary>
public sealed class GameWatcher : IDisposable
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd" };
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    DateTime _lastSeenUtc = DateTime.MinValue;

    public bool GameActive { get; private set; }
    public event Action<bool>? Changed;

    public GameWatcher()
    {
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    /// <param name="framedMaximized">A maximized window with its title bar: with the taskbar set to hide itself it covers
    /// the screen too, but it is a window (Explorer, a browser), not a full-screen app.</param>
    public static bool IsFullscreen(ScreenRect window, ScreenRect monitor, string className, bool framedMaximized = false) =>
        !framedMaximized && !ShellClasses.Contains(className) &&
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    static readonly string SelfName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Kairos");

    /// <summary>
    /// A full-screen app that counts as a game (<paramref name="exe"/> without ".exe"): not a browser, a player or Windows'
    /// own (<see cref="Engine.GameOverlay.MayHook"/>, the list Games mode uses), and not Kairos.
    /// </summary>
    public static bool IsGame(string exe) =>
        Engine.GameOverlay.MayHook(exe) && !exe.Equals(SelfName, StringComparison.OrdinalIgnoreCase)
        && !exe.Equals("Kairos", StringComparison.OrdinalIgnoreCase);

    string? _game;

    void Poll()
    {
        string? game = ForegroundGameExe();
        if (game != null)
        {
            _lastSeenUtc = DateTime.UtcNow;
            if (game != _game)
                Engine.ObsLog.Info($"game watcher: {game} in front, counted as a game");
            _game = game;
        }
        bool active = game != null || DateTime.UtcNow - _lastSeenUtc < Grace;
        if (active != GameActive)
        {
            GameActive = active;
            if (!active)
            {
                Engine.ObsLog.Info($"game watcher: {_game} left");
                _game = null;
            }
            Changed?.Invoke(active);
        }
    }

    /// <summary>The game in front now (its exe without ".exe"), or null: see <see cref="IsGame"/>.</summary>
    static string? ForegroundGameExe() => FrontApp().Fullscreen is { } app && IsGame(app.Exe) ? app.Exe : null;

    /// <summary>A game is in front now (no grace): for what must never happen over one, such as an update's restart.</summary>
    internal static bool ForegroundIsGame() => ForegroundGameExe() != null;

    /// <summary>The window in front: its process, whether it fills its screen, its class and its screen's device.</summary>
    static (IntPtr Hwnd, uint Pid, bool Fullscreen, string Class, string Device) Front()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r))
            return default;
        GetWindowThreadProcessId(hwnd, out uint pid);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfoEx(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info))
            return (hwnd, pid, false, "", "");
        var cls = new StringBuilder(256);
        GetClassName(hwnd, cls, cls.Capacity);
        string name = cls.ToString();
        bool framedMaximized = IsZoomed(hwnd) && (GetWindowLong(hwnd, -16 /* GWL_STYLE */) & 0x00C00000 /* WS_CAPTION */) == 0x00C00000;
        return (hwnd, pid, IsFullscreen(new ScreenRect(r.Left, r.Top, r.Right, r.Bottom),
            new ScreenRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom), name, framedMaximized), name, info.szDevice);
    }

    /// <summary>The last full-screen window's process path: the same game is not opened again every second.</summary>
    static (IntPtr Hwnd, uint Pid, string Path) _lastFront;

    /// <summary>The app in front when it fills its screen: its window as libobs names it, its process and exe name, and
    /// its screen's device ("\\.\DISPLAY1"); null otherwise. The engine's scene timer only.</summary>
    internal static FullscreenApp? FullscreenForeground() => FrontApp().Fullscreen;

    /// <summary>
    /// The app in front, read once (the scene timer's, once a second): its exe name without ".exe" (null for Kairos or
    /// when unknown) and, when it fills its screen, its <see cref="FullscreenApp"/>.
    /// </summary>
    internal static (FullscreenApp? Fullscreen, string? Exe) FrontApp()
    {
        var front = Front();
        if (front.Hwnd == IntPtr.Zero || front.Pid == Environment.ProcessId)
            return (null, null);
        string? path = _lastFront.Hwnd == front.Hwnd && _lastFront.Pid == front.Pid ? _lastFront.Path : ForegroundApp.ImagePath((int)front.Pid);
        if (path == null)
            return (null, null);
        _lastFront = (front.Hwnd, front.Pid, path);
        string exe = Path.GetFileName(path), name = Path.GetFileNameWithoutExtension(exe);
        if (!front.Fullscreen)
            return (null, name);
        var title = new StringBuilder(512);
        GetWindowText(front.Hwnd, title, title.Capacity);
        return (new FullscreenApp(WindowTarget.Encode(title.ToString(), front.Class, exe), front.Pid, name, front.Device), name);
    }

    /// <summary>The app in front: whether it fills its screen (a game, usually), and its process.</summary>
    internal static (bool Fullscreen, uint Pid) Foreground()
    {
        var front = Front();
        return (front.Fullscreen, front.Pid);
    }

    public void Dispose() => _timer.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoEx(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
}
