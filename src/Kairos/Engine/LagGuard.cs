using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>
/// Whether recording keeps up while it runs, from libobs's frame counters sampled every few seconds: over
/// <paramref name="share"/> of the frames lagged or skipped for <paramref name="windowSeconds"/>. With the defaults
/// (10 % for 20 s) the clip stutters and falls further behind real time each second (libobs encodes every repeat of a
/// late picture), so saves wait minutes: a game taking the graphics card (a user's PC, 2026-10-19: 22-95 % while
/// playing, none without the game). Automatic's own step down watches 2 % over a minute. Counters that went back (a new
/// pipeline) start it again.
/// </summary>
public sealed class LagGuard(double share = 0.10, int windowSeconds = 20)
{
    readonly TimeSpan _window = TimeSpan.FromSeconds(windowSeconds);
    readonly List<(DateTime At, ulong Bad, ulong All)> _samples = [];
    DateTime? _behindSince;

    /// <summary>The last full window was over the share: a save now waits for a late encoder.</summary>
    public bool Behind { get; private set; }

    /// <summary>
    /// Behind on every sample for <paramref name="span"/>: not a moment (a loading screen, shaders built as a game starts,
    /// which with a 20 s window stay behind some 10 s longer than they last).
    /// </summary>
    public bool BehindFor(DateTime now, TimeSpan span) => _behindSince is { } since && now - since >= span;

    /// <summary>Takes a sample; the share over the last full window when it is over the limit, else null.</summary>
    public double? Add(DateTime now, ulong bad, ulong all)
    {
        if (_samples.Count > 0 && (all < _samples[^1].All || bad < _samples[^1].Bad))
            Reset(); // a new pipeline: its own counters
        _samples.Add((now, bad, all));
        while (_samples.Count > 1 && now - _samples[1].At >= _window)
            _samples.RemoveAt(0);
        var first = _samples[0];
        if (now - first.At < _window || all <= first.All)
            return null;
        double over = (double)(bad - first.Bad) / (all - first.All);
        Behind = over > share;
        _behindSince = Behind ? _behindSince ?? now : null;
        return Behind ? over : null;
    }

    public void Reset()
    {
        _samples.Clear();
        Behind = false;
        _behindSince = null;
    }
}

/// <summary>
/// Recording lighter when it falls behind (<see cref="LagGuard"/>): first 60 fps and 1080p at most, then (a CPU with room
/// to spare) the CPU's encoder, which needs no time on the graphics card the game holds: Medal's own advice for a game
/// that takes all of it. The user's settings stay as they are.
/// </summary>
public static class LagRelief
{
    /// <summary>The step that records on the CPU.</summary>
    public const int Cpu = 2;

    public static bool OnCpu(int relief) => relief >= Cpu;

    /// <summary>How light it can go: the CPU step only with 12 threads or more, and not when the CPU already encodes.</summary>
    public static int Max(int cpuThreads, bool software) => cpuThreads >= 12 && !software ? Cpu : 1;

    /// <summary>
    /// The first step past <paramref name="relief"/>, up to <paramref name="max"/>, that records lighter than it (60 fps and
    /// 1080p change nothing for a 1080p60 choice); <paramref name="relief"/> itself when none does.
    /// </summary>
    public static int Next(VideoSettings chosen, int relief, int max)
    {
        for (int next = relief + 1; next <= max; next++)
            if (OnCpu(next) || Video(chosen, next) != Video(chosen, relief))
                return next;
        return relief;
    }

    /// <summary>What the Status page and the toast say about <paramref name="relief"/> (above 0).</summary>
    public static string TextKey(int relief) => OnCpu(relief) ? "status.reliefCpu" : "status.relief";

    /// <summary>The video settings recorded with at <paramref name="relief"/> (the same object at 0).</summary>
    public static VideoSettings Video(VideoSettings chosen, int relief) => relief <= 0 ? chosen : chosen with
    {
        Fps = Math.Min(chosen.Fps, 60),
        // 1080p at most: a smaller screen keeps its own size
        Resolution = ResolutionOptions.Height(chosen.Resolution, int.MaxValue) > 1080 ? OutputResolution.P1080 : chosen.Resolution,
    };
}
