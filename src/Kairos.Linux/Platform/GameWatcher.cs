using Kairos.Platform;

namespace Kairos.App;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>Reports whether a fullscreen app (usually a game) is in the foreground, with a grace period on exit.</summary>
public sealed class GameWatcher : IDisposable
{
    static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Cinnamon", "Gnome-shell", "plasmashell", "Xfdesktop", "nemo-desktop", "Desktop" };
    static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);

    readonly Timer _timer;
    DateTime _lastSeenUtc = DateTime.MinValue;

    public bool GameActive { get; private set; }
    public event Action<bool>? Changed;

    public GameWatcher()
    {
        Poll();
        _timer = new Timer(_ => Poll(), null, 2000, 2000);
    }

    public static bool IsFullscreen(ScreenRect window, ScreenRect monitor, string className) =>
        !ShellClasses.Contains(className) &&
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    void Poll()
    {
        bool now = ForegroundIsFullscreen();
        if (now)
            _lastSeenUtc = DateTime.UtcNow;
        bool active = now || DateTime.UtcNow - _lastSeenUtc < Grace;
        if (active == GameActive)
            return;
        GameActive = active;
        UiThread.Post(() => Changed?.Invoke(active));
    }

    static bool ForegroundIsFullscreen()
    {
        using var x = XSession.Open();
        if (x == null || x.Active() is not { } window || x.Rect(window) is not { } r)
            return false;
        string cls = x.Class(window) ?? "";
        if (x.HasState(window, "_NET_WM_STATE_FULLSCREEN") && !ShellClasses.Contains(cls))
            return true;
        // Games that go fullscreen without telling the window manager still cover their whole monitor.
        return x.MonitorAt(r.Left, r.Top) is { } m &&
            IsFullscreen(new ScreenRect(r.Left, r.Top, r.Right, r.Bottom), new ScreenRect(m.Left, m.Top, m.Right, m.Bottom), cls);
    }

    public void Dispose() => _timer.Dispose();
}
