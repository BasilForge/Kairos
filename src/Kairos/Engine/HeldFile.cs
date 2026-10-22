namespace Kairos.Engine;

/// <summary>
/// A file just written is held a moment by another program (an antivirus reading it: ~30 ms on a test PC, longer
/// for a video), and Windows refuses to move, delete or open it meanwhile. These try again, a little later each time,
/// up to 3 s: tried at once, a copy ran through 48 other names in a few milliseconds ("(31).mp4", or "Couldn't prepare
/// the clip": a test PC, 2026-10-21).
/// </summary>
static class HeldFile
{
    /// <summary>Whether the file is held open by another program (a sharing or lock violation), most often only a moment.</summary>
    internal static bool IsHeld(IOException e) => (e.HResult & 0xFFFF) is 32 or 33;

    /// <summary>The waits between tries: 10, 20, 40 … ms, then 200 ms each, up to <paramref name="totalMs"/> in all.</summary>
    internal static IEnumerable<int> Waits(int totalMs = 3000)
    {
        for (int wait = 10, sum = 0; sum < totalMs; sum += wait, wait = Math.Min(wait * 2, 200))
            yield return wait;
    }

    /// <summary>
    /// Runs <paramref name="act"/>, again after each wait while the file is held, unless <paramref name="unless"/> says
    /// waiting cannot help; the last refusal is thrown.
    /// </summary>
    internal static T Retry<T>(Func<T> act, Func<bool>? unless = null, int totalMs = 3000)
    {
        using var waits = Waits(totalMs).GetEnumerator();
        while (true)
        {
            try
            {
                return act();
            }
            catch (IOException e) when (IsHeld(e) && unless?.Invoke() != true && waits.MoveNext())
            {
                Thread.Sleep(waits.Current);
            }
        }
    }

    internal static void Retry(Action act, Func<bool>? unless = null, int totalMs = 3000) => Retry(() =>
    {
        act();
        return 0;
    }, unless, totalMs);

    /// <summary>Whether <paramref name="path"/> is there and open in another program (it cannot be opened alone).</summary>
    internal static bool InUse(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
