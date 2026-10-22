namespace Kairos.Engine;

/// <summary>
/// Games mode never hooks a game whose anti-cheat forbids it: while one of <see cref="Games"/> runs, or while the Riot
/// Client starts one with Vanguard's client (vgc) up, it records the screen instead. The client and vgc are up before
/// the game's window shows, so the guard is on before the game capture could see the game. vgc alone does not count:
/// it can stay running after the Riot game closes, and other games are hooked as before.
/// </summary>
public static class AntiCheatGuard
{
    /// <summary>Process names (no ".exe"): the games Riot Vanguard protects.</summary>
    public static readonly string[] Games = ["VALORANT-Win64-Shipping", "League of Legends"];

    /// <summary>
    /// The <see cref="Games"/>' windows as libobs window strings ("title:class:exe"): "game sound only" records their own
    /// sound from these (the app's sound, no hook) while the guard keeps the game capture out.
    /// </summary>
    public static readonly string[] GameSoundWindows =
        ["VALORANT:UnrealWindow:VALORANT-Win64-Shipping.exe", "League of Legends (TM) Client:RiotWindowClass:League of Legends.exe"];

    /// <summary>The Riot Client's processes (no ".exe"), which start those games.</summary>
    public static readonly string[] Launchers = ["RiotClientServices", "Riot Client", "RiotClientUx"];

    const string Vanguard = "vgc";

    public static bool IsActive(IEnumerable<string> running)
    {
        var names = running.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Games.Any(names.Contains) || (names.Contains(Vanguard) && Launchers.Any(names.Contains));
    }

    /// <summary>Whether the guard is on now (Games mode asks every 2 s): the names that matter, from one process walk.</summary>
    public static bool AnyProtectedRunning()
    {
        var found = new List<string>(4);
        AnyRunning(name =>
        {
            if (IsWatched(name))
                found.Add(name);
            return false; // the whole list: vgc counts only with a launcher
        });
        return IsActive(found);
    }

    static bool IsWatched(string name) =>
        name.Equals(Vanguard, StringComparison.OrdinalIgnoreCase)
        || Games.Contains(name, StringComparer.OrdinalIgnoreCase) || Launchers.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a running process's name (no ".exe") passes <paramref name="match"/>: one snapshot, no process opened,
    /// and it stops at the first match.
    /// </summary>
    public static bool AnyRunning(Func<string, bool> match) => App.ProcessList.Any((_, name) => match(name));
}
