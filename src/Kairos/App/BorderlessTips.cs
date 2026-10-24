namespace Kairos.App;

/// <summary>
/// The Borderless tip (<see cref="EngineApp.BorderlessToast"/>), once per game ever: a game Kairos cannot capture from
/// inside (run as administrator, or guarded by its anti-cheat) is recorded from the screen, which a game in exclusive full
/// screen leaves black. Windows shows no card over such a game, so the tip waits until one can show (the game left).
/// </summary>
/// <param name="tipped">
/// The games told already, by exe: the settings' own list (kept across starts), asked each time (the settings are read
/// again when they change).
/// </param>
public sealed class BorderlessTips(Func<List<string>> tipped)
{
    (string Exe, string Name)? _pending;

    /// <summary>A tip waits to be shown.</summary>
    public bool Pending => _pending != null;

    /// <summary>Kairos could not open the game <paramref name="exe"/> (its window <paramref name="title"/>).</summary>
    public void Seen(string exe, string title)
    {
        if (!tipped().Contains(exe, StringComparer.OrdinalIgnoreCase))
            _pending = (exe, string.IsNullOrWhiteSpace(title) ? exe : title);
    }

    /// <summary>
    /// Shows the waiting tip with <paramref name="show"/> (the game's name; false when nothing showed). True when it
    /// showed: the game is told for good.
    /// </summary>
    public bool TryShow(Func<string, bool> show)
    {
        if (_pending is not { } tip || !show(tip.Name))
            return false;
        tipped().Add(tip.Exe);
        _pending = null;
        return true;
    }
}
