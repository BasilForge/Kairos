namespace Kairos.App;

/// <summary>
/// What the settings window does outside itself: opening an app, a folder, a link or a folder picker, the clipboard, a
/// download, a report on the Desktop. The --perf walk check (every control pressed, off screen) writes them down instead
/// of doing them, so nothing shows and nothing of the user's changes.
/// </summary>
public static class SideEffects
{
    /// <summary>Set by the walk check: told what would have happened.</summary>
    public static Action<string>? Recorder { get; set; }

    /// <summary>True when <paramref name="what"/> was written down instead of done (the caller then does nothing).</summary>
    public static bool Held(string what)
    {
        if (Recorder is not { } record)
            return false;
        record(what);
        return true;
    }
}
