using Kairos.Engine;
using Kairos.Native;
using Kairos.Platform;

namespace Kairos.App;

/// <summary>
/// Games mode: points the pipeline's window source at the window in front, once a second, so a game (or whatever is in front)
/// is recorded without copying the whole screen. Kairos's own windows and the desktop itself are not followed: the source
/// keeps the last window until another app takes focus.
/// </summary>
public sealed class GameFollower : IDisposable
{
    static readonly HashSet<string> NotFollowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cinnamon", "Gnome-shell", "plasmashell", "Xfdesktop", "nemo-desktop", "Desktop", "kairos", "Kairos", "Xfce4-panel", "Tint2",
    };

    static string? _last;

    readonly CapturePipeline _pipeline;
    readonly UiTimer _timer = new(1000);

    public GameFollower(CapturePipeline pipeline)
    {
        _pipeline = pipeline;
        _timer.Tick += Follow;
        _timer.Start();
    }

    /// <summary>libobs's name for the window in front ("id\r\ntitle\r\nclass"), or the last one followed when this is not one to follow.</summary>
    public static string? Target()
    {
        using var x = XSession.Open();
        if (x == null || x.Active() is not { } window)
            return _last;
        string cls = x.Class(window) ?? "";
        if (NotFollowed.Contains(cls) || x.Pid(window) == Environment.ProcessId)
            return _last;
        return _last = $"{(ulong)window}\r\n{x.Title(window) ?? ""}\r\n{cls}";
    }

    void Follow()
    {
        string? before = _last;
        if (Target() is { } target && target != before)
        {
            using var settings = new ObsData().Set("capture_window", target);
            _pipeline.UpdateGameSource(settings);
            ObsLog.Info($"games mode now records: {target.Replace("\r\n", " | ")}");
        }
    }

    public void Dispose() => _timer.Dispose();
}
