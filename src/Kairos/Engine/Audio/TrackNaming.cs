namespace Kairos.Engine.Audio;

/// <summary>
/// Names a saved file's sound tracks (<see cref="Mp4TrackNames"/>). A recording's muxer still holds its file a moment
/// after the recording stops, so a file another process holds is tried again a little later, for a few seconds.
/// </summary>
public static class TrackNaming
{
    static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);

    /// <param name="later">Runs the retry after the pause (the engine's UI thread, a timer).</param>
    /// <param name="tries">How many times in all (20: five seconds).</param>
    public static void Name(string path, IReadOnlyList<string> names, Action<TimeSpan, Action> later, int tries = 20)
    {
        if (names.Count == 0)
            return;
        try
        {
            Mp4TrackNames.Write(path, names);
        }
        catch (IOException e) when (tries > 1 && HeldFile.IsHeld(e))
        {
            later(Pause, () => Name(path, names, later, tries - 1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn($"track names: {e.Message}");
        }
    }
}
