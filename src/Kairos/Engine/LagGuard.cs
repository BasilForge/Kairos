using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>
/// libobs's frame counters: pictures the graphics thread was late with out of all it rendered (for the whole session: a
/// rebuild does not reset them), and pictures the encoder skipped out of all it was given (each pipeline's own). A late
/// picture comes to the encoder as a repeat it skips too, so the two shares are never added (a log said "246 %").
/// </summary>
public readonly record struct FrameCount(ulong Lagged, ulong Rendered, ulong Skipped, ulong Encoded)
{
    /// <summary>A counter went back: a new pipeline (or libobs) counts from zero.</summary>
    public bool WentBack(FrameCount before) =>
        Lagged < before.Lagged || Rendered < before.Rendered || Skipped < before.Skipped || Encoded < before.Encoded;

    /// <summary>The larger share since <paramref name="before"/>: late pictures, or skipped ones; null with none encoded since.</summary>
    public double? ShareSince(FrameCount before) =>
        Encoded <= before.Encoded ? null : Math.Max(SkippedSince(before), LaggedSince(before));

    /// <summary>Pictures the encoder skipped out of those it was given since <paramref name="before"/> (0 with none).</summary>
    public double SkippedSince(FrameCount before) => Share(Skipped, Encoded, before.Skipped, before.Encoded);

    /// <summary>Pictures rendered late out of all rendered since <paramref name="before"/> (0 with none).</summary>
    public double LaggedSince(FrameCount before) => Share(Lagged, Rendered, before.Lagged, before.Rendered);

    /// <summary>A part out of its whole since the last look, from zero when the whole went back (counted anew).</summary>
    static double Share(ulong part, ulong whole, ulong partWas, ulong wholeWas)
    {
        bool anew = whole < wholeWas;
        ulong frames = whole - (anew ? 0 : wholeWas);
        return frames == 0 ? 0 : (double)(part - (anew ? 0 : Math.Min(partWas, part))) / frames;
    }
}

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
    readonly List<(DateTime At, FrameCount Frames)> _samples = [];
    DateTime? _behindSince;

    /// <summary>The last full window was over the share: a save now waits for a late encoder.</summary>
    public bool Behind { get; private set; }

    /// <summary>
    /// Behind on every sample for <paramref name="span"/>: not a moment (a loading screen, shaders built as a game starts,
    /// which with a 20 s window stay behind some 10 s longer than they last).
    /// </summary>
    public bool BehindFor(DateTime now, TimeSpan span) => _behindSince is { } since && now - since >= span;

    /// <summary>Takes a sample; the share over the last full window when it is over the limit, else null.</summary>
    public double? Add(DateTime now, FrameCount frames)
    {
        if (_samples.Count > 0 && frames.WentBack(_samples[^1].Frames))
            Reset(); // a new pipeline: its own counters
        _samples.Add((now, frames));
        while (_samples.Count > 1 && now - _samples[1].At >= _window)
            _samples.RemoveAt(0);
        var first = _samples[0];
        if (now - first.At < _window || frames.ShareSince(first.Frames) is not { } over)
            return null;
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
/// Recording lighter when it falls behind (<see cref="LagGuard"/>), a step after each minute behind, kept for this PC
/// and eased after a quiet week: only a PC whose game takes the graphics card records lighter, every other one as chosen.
/// <list type="number">
/// <item>NVIDIA's encoder without its multipass and adaptive quantization, which NVIDIA runs on the shader cores the game
/// fills (the same kind as the p6 look-ahead that skipped 55-86 % of a user's frames): at the same CQ its gameplay VMAF
/// was as high or higher, the clips about 5 % larger.</item>
/// <item>The REALTIME GPU scheduling class for the whole process (Sunshine's default: at HIGH, its docs say, capture
/// suffers when a game loads the card). NVIDIA's driver can freeze an encoder at REALTIME with the card's memory nearly
/// full, so it is this step only: behind still after it, the next steps record at HIGH again.</item>
/// <item>60 fps and 1080p at most.</item>
/// <item>30 fps: half the work on the graphics card, where the CPU has no room for the last step (a user's 8 threads).</item>
/// <item>The CPU's encoder (12 threads or more), which needs no time on the graphics card: Medal's own advice.</item>
/// </list>
/// The user's settings stay as they are.
/// </summary>
public static class LagRelief
{
    /// <summary>NVIDIA's encoder without the passes it runs on the shader cores (kept by every later step).</summary>
    public const int LightEncoder = 1;

    /// <summary>The REALTIME GPU scheduling class (this step only).</summary>
    public const int Realtime = 2;

    /// <summary>60 fps and 1080p at most.</summary>
    public const int Capped = 3;

    /// <summary>30 fps.</summary>
    public const int HalfRate = 4;

    /// <summary>The step that records on the CPU.</summary>
    public const int Cpu = 5;

    /// <summary>
    /// These step numbers, kept with a step in hardware.json: 1.1.0 and the first 1.1.1 tests numbered them otherwise
    /// (0 there), so a step they kept means another one here. One more whenever the steps change.
    /// </summary>
    public const int Numbering = 1;

    public static bool OnCpu(int relief) => relief >= Cpu;

    /// <summary>How light it can go: the CPU step only with 12 threads or more, and not when the CPU already encodes.</summary>
    public static int Max(int cpuThreads, bool software) => cpuThreads >= 12 && !software ? Cpu : HalfRate;

    /// <summary>
    /// The first step past <paramref name="relief"/>, up to <paramref name="max"/>, that records lighter than it (the light
    /// encoder is NVIDIA's only, REALTIME left out on Intel graphics as OBS leaves them; 60 fps and 1080p change nothing
    /// for a 1080p60 choice); <paramref name="relief"/> when none does.
    /// </summary>
    public static int Next(VideoSettings chosen, int relief, int max, bool nvidia, bool realtime)
    {
        for (int next = relief + 1; next <= max; next++)
            if (OnCpu(next) || (next == LightEncoder && nvidia) || (next == Realtime && realtime) || Video(chosen, next) != Video(chosen, relief))
                return next;
        return relief;
    }

    /// <summary>
    /// Whether going from <paramref name="from"/> to <paramref name="to"/> changes only the GPU class: set as the recording
    /// runs, the replay kept (the light encoder is NVIDIA's step only, so another card goes from 0 straight to it).
    /// </summary>
    public static bool Live(int from, int to, bool nvidia) => to == Realtime && (from == LightEncoder || from == 0 && !nvidia);

    /// <summary>
    /// Whether a step that builds the pipeline again, which starts the replay empty, is taken now: never during a
    /// recording or while a clip is written; while the replay may hold a game played only once a clip was saved since, so
    /// the minutes the player may still save are never thrown away (a user saved 2 s after the step and got a 2 s clip).
    /// </summary>
    public static bool RebuildNow(bool recording, bool saving, bool gameInReplay, bool savedSince) =>
        !recording && !saving && (!gameInReplay || savedSince);

    /// <summary>What the Status page and the toast say about <paramref name="relief"/> (above 0).</summary>
    public static string TextKey(int relief) => relief switch
    {
        >= Cpu => "status.reliefCpu",
        >= HalfRate => "status.reliefHalf",
        >= Capped => "status.relief",
        Realtime => "status.reliefPriority",
        _ => "status.reliefEncoder",
    };

    /// <summary>The video settings recorded with at <paramref name="relief"/> (the same object until <see cref="Capped"/>).</summary>
    public static VideoSettings Video(VideoSettings chosen, int relief) => relief < Capped ? chosen : chosen with
    {
        Fps = Math.Min(chosen.Fps, relief >= HalfRate ? 30 : 60),
        // 1080p at most: a smaller screen keeps its own size
        Resolution = ResolutionOptions.Height(chosen.Resolution, int.MaxValue) > 1080 ? OutputResolution.P1080 : chosen.Resolution,
    };
}
