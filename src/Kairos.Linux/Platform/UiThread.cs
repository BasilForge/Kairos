namespace Kairos.Platform;

/// <summary>
/// The engine's "UI thread": where hotkeys, the pipeline's signals and the timers meet, as WinForms' message loop is on
/// Windows. The Avalonia host sets it to its dispatcher; without one (the console self-tests) actions run on the caller.
/// </summary>
public static class UiThread
{
    public static Action<Action> Poster { get; set; } = action => action();

    public static void Post(Action action) => Poster(action);
}
