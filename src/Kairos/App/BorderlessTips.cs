namespace Kairos.App;

/// <summary>
/// The Borderless tip (<see cref="EngineApp.BorderlessToast"/>): a game Kairos cannot capture from inside (run as
/// administrator, or guarded by its anti-cheat) is recorded from the screen, which a game in exclusive full screen can leave
/// black. Told when its picture records black, once a game each run (a game whose picture records is never told). Windows
/// shows no card over such a game, so the tip waits until one can show (the game left).
/// </summary>
public sealed class BorderlessTips
{
    readonly HashSet<string> _told = new(StringComparer.OrdinalIgnoreCase);
    (string Exe, string Name)? _pending;

    /// <summary>A tip waits to be shown.</summary>
    public bool Pending => _pending != null;

    /// <summary>The game <paramref name="exe"/> was told this run: its picture needs no more looks.</summary>
    public bool Told(string exe) => _told.Contains(exe);

    /// <summary>The game <paramref name="exe"/> (its window <paramref name="title"/>) filled the screen, recorded black.</summary>
    public void SawBlack(string exe, string title)
    {
        if (!_told.Contains(exe))
            _pending = (exe, string.IsNullOrWhiteSpace(title) ? exe : title);
    }

    /// <summary>
    /// Shows the waiting tip with <paramref name="show"/> (the game's name; false when nothing showed). True when it
    /// showed: the game is told for this run.
    /// </summary>
    public bool TryShow(Func<string, bool> show)
    {
        if (_pending is not { } tip || !show(tip.Name))
            return false;
        _told.Add(tip.Exe);
        _pending = null;
        return true;
    }
}
