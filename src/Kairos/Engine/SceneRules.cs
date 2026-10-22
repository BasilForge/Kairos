using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>What the canvas is built from: a capture with nothing to show records the screen instead of black.</summary>
public static class SceneRules
{
    /// <summary>The mode the scene is built in: One app with no app chosen records the full screen.</summary>
    public static CaptureMode BuildMode(CaptureSettings c) =>
        c.Mode == CaptureMode.Window && !c.Apps.Any(a => !string.IsNullOrWhiteSpace(a)) ? CaptureMode.Screen : c.Mode;

    /// <summary>
    /// The replay waits for a game (what counts: <see cref="App.GameWatcher"/>, never a browser or a video player): "Only
    /// record while gaming", a Full screen choice (the switch shows there only), and Games with "Show the screen when no
    /// game runs" off, which records games only, so with no game nothing is kept, picture or sound (it kept a black
    /// picture with the mic and a browser's sound, and saved it). Apps records its apps.
    /// </summary>
    public static bool PausesWithoutGame(AppSettings s) =>
        s.Capture.Mode == CaptureMode.Screen ? s.General.OnlyWhenGaming
        : s.Capture.Mode == CaptureMode.Game && !s.Capture.GameFallbackToScreen;

    /// <summary>
    /// The chosen app's window is what is recorded (One app, and nothing says otherwise): clips go under that app.
    /// Otherwise they go under what is in front, like a full-screen clip.
    /// </summary>
    public static bool RecordsChosenWindow(CaptureSettings c, string? captureNotice) =>
        BuildMode(c) == CaptureMode.Window && captureNotice == null;
}

/// <summary>Window mode with several apps: which one is recorded now.</summary>
public static class AppTargets
{
    /// <summary>
    /// The chosen app in front (<paramref name="frontExe"/>, without ".exe"); else the one recorded now while it is open;
    /// else the first open one; with none open, the one recorded now is kept (the screen shows), the first at the start.
    /// </summary>
    public static string? Pick(IReadOnlyList<string> apps, string? frontExe, Func<string, bool> isOpen, string? current)
    {
        if (apps.Count == 0)
            return null;
        if (frontExe != null && apps.FirstOrDefault(a => string.Equals(App.WindowTarget.ExeName(a), frontExe, StringComparison.OrdinalIgnoreCase)) is { } front)
            return front;
        bool known = current != null && apps.Contains(current);
        if (known && isOpen(current!))
            return current;
        return apps.FirstOrDefault(a => a != current && isOpen(a)) ?? (known ? current : apps[0]);
    }
}

/// <summary>
/// Whether the chosen app's window gives a picture, checked once a second. The screen shows after two misses in a row
/// (a window that is just starting gets one), and hides on the first check that sees the window again.
/// </summary>
public sealed class WindowPresence
{
    int _misses;

    /// <param name="showScreen">The window is not on screen when recording starts: the screen from the first frame.</param>
    public WindowPresence(bool showScreen = false)
    {
        ShowScreen = showScreen;
        _misses = showScreen ? 2 : 0;
    }

    public bool ShowScreen { get; private set; }

    /// <summary>One check; true when <see cref="ShowScreen"/> changed.</summary>
    public bool Update(bool hasPicture)
    {
        _misses = hasPicture ? 0 : _misses + 1;
        bool show = _misses >= 2;
        if (show == ShowScreen)
            return false;
        ShowScreen = show;
        return true;
    }
}

/// <summary>
/// A game capture laid over another capture (the screen, or the chosen app's window) for a game that fills the screen,
/// where the other capture can be black (a stretched or exclusive fullscreen game). Checked once a second: a game in
/// front is targeted (hooked) once, its picture shows while it is in front, and with no picture for 8 s it is hooked
/// again (a hook that took a GL device first never gives a D3D picture).
/// </summary>
public sealed class GameOverlay
{
    static readonly TimeSpan Rehook = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Apps that fill the screen without being games (OBS's own list for "any fullscreen", with today's browsers and
    /// players): in Screen mode the screen shows them; hooking them gives no picture and only retries.
    /// </summary>
    static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "steam", "steamwebhelper", "battle.net", "galaxyclient", "skype", "uplay", "origin", "devenv", "taskmgr",
        "systemsettings", "applicationframehost", "shellexperiencehost", "searchui", "searchhost", "lockapp", "textinputhost",
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "discord", "obs64",
        "vlc", "mpc-hc64", "mpc-be64", "potplayermini64", "wmplayer", "microsoft.media.player", "mpv", "powerpnt",
        // Video apps (Netflix's app, Films & TV, web apps in a WebView), Windows' own screens and the screen-snipping tool.
        "wwahost", "msedgewebview2", "netflix", "video.ui", "plex", "kodi", "startmenuexperiencehost", "screenclippinghost",
    };

    /// <summary>A fullscreen app the Screen mode's game layer may hook (<paramref name="exe"/> without ".exe").</summary>
    public static bool MayHook(string exe) => !NotGames.Contains(exe);
    DateTime _since;

    /// <summary>The window the game capture takes ("title:class:exe"), "" before any game.</summary>
    string _target = "";

    /// <param name="front">The game window that fills the captured screen now, or null (none, or alt-tabbed out).</param>
    /// <param name="hasPicture">The game capture has a picture.</param>
    /// <returns>The window to (re)target now, or null to leave it; and whether the layer shows.</returns>
    /// <summary>
    /// The same window whatever its title says ("title:class:exe"): a game that shows its frame rate in its title would
    /// otherwise be hooked again every second, and game_capture keeps its target by class and exe.
    /// </summary>
    static bool SameWindow(string a, string b) =>
        a.IndexOf(':') is var i and >= 0 && b.IndexOf(':') is var j and >= 0 && a.AsSpan(i).SequenceEqual(b.AsSpan(j));

    public (string? Retarget, bool Show) Update(string? front, bool hasPicture, DateTime now)
    {
        if (front == null)
            return (null, false);
        if (!SameWindow(front, _target))
        {
            _target = front;
            _since = now;
            return (front, false);
        }
        if (hasPicture)
        {
            _since = now;
            return (null, true);
        }
        if (now - _since < Rehook)
            return (null, false);
        _since = now;
        return (front, false);
    }
}
