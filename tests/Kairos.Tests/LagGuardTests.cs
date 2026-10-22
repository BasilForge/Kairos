using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>
/// A game that takes the graphics card made a user's clips stutter (4-15 new pictures a second) and come minutes late, so
/// saves failed (1.0.1, 2026-10-19/20: 22-95 % of the frames skipped while playing, none without the game).
/// Kairos now notices within 20 s, records lighter once that lasts a minute (a loading screen's stall does not), and says so.
/// </summary>
public static class LagGuardTests
{
    static readonly DateTime T0 = new(2026, 10, 22, 20, 0, 0, DateTimeKind.Utc);

    /// <summary>Samples every 5 s at 60 fps with <paramref name="share"/> of them lagged or skipped; the last decision.</summary>
    static (bool Lighter, int At) Feed(LagGuard guard, double share, int seconds, ref ulong bad, ref ulong all, int from = 0)
    {
        bool lighter = false;
        int at = -1;
        for (int s = from; s <= from + seconds; s += 5)
        {
            if (guard.Add(T0.AddSeconds(s), bad, all) is not null && at < 0)
            {
                lighter = true;
                at = s - from;
            }
            all += 300;
            bad += (ulong)(300 * share);
        }
        return (lighter, at);
    }

    [Test]
    public static void A_few_skipped_frames_never_make_it_lighter()
    {
        var guard = new LagGuard();
        ulong bad = 0, all = 0;
        Assert.True(!Feed(guard, 0.03, 120, ref bad, ref all).Lighter, "3 % for two minutes: as it is");
        Assert.True(!guard.Behind, "and not behind");
    }

    [Test]
    public static void Recording_that_falls_behind_is_made_lighter_after_20_s()
    {
        var guard = new LagGuard();
        ulong bad = 0, all = 0;
        var (lighter, at) = Feed(guard, 0.3, 60, ref bad, ref all);
        Assert.True(lighter && at == 20, $"30 % skipped: lighter once 20 s show it (at {at} s)");
        Assert.True(guard.Behind, "behind (a save now says why it is late)");
    }

    [Test]
    public static void A_stall_of_a_loading_screen_is_not_behind_for_a_minute()
    {
        var guard = new LagGuard();
        ulong bad = 0, all = 0;
        bool sustained = false;
        for (int s = 0; s <= 180; s += 5)
        {
            guard.Add(T0.AddSeconds(s), bad, all);
            sustained |= guard.BehindFor(T0.AddSeconds(s), TimeSpan.FromMinutes(1));
            all += 300;
            bad += s is >= 40 and < 80 ? 300UL : 0; // 40 s with every frame late (shaders built as a game starts)
        }
        Assert.True(!sustained, "a 40 s stall: a moment, the recording stays as chosen");
    }

    [Test]
    public static void Recording_behind_for_a_minute_is_made_lighter()
    {
        var guard = new LagGuard();
        ulong bad = 0, all = 0;
        int at = -1;
        for (int s = 0; s <= 120 && at < 0; s += 5)
        {
            guard.Add(T0.AddSeconds(s), bad, all);
            if (guard.BehindFor(T0.AddSeconds(s), TimeSpan.FromMinutes(1)))
                at = s;
            all += 300;
            bad += 90; // 30 %
        }
        Assert.Equal(80, at, "behind from 20 s on: lighter a minute later");
        guard.Reset();
        Assert.True(!guard.BehindFor(T0.AddSeconds(200), TimeSpan.Zero), "reset: not behind");
    }

    [Test]
    public static void A_new_pipeline_is_judged_on_its_own_frames()
    {
        var guard = new LagGuard();
        ulong bad = 0, all = 0;
        Feed(guard, 0.3, 15, ref bad, ref all);
        ulong freshBad = 0, freshAll = 0; // built again: the counters start from zero
        var (lighter, at) = Feed(guard, 0.3, 30, ref freshBad, ref freshAll, from: 20);
        Assert.True(lighter && at == 20, $"a full 20 s of the new one first (at {at} s)");
        guard.Reset();
        Assert.True(!guard.Behind, "reset: not behind");
    }

    [Test]
    public static void Lighter_means_60_fps_and_1080p_at_most_then_the_cpu()
    {
        var mine = new VideoSettings { Fps = 120, Resolution = OutputResolution.Native, Codec = VideoCodec.Hevc };
        var light = LagRelief.Video(mine, 1);
        Assert.True(light.Fps == 60 && light.Resolution == OutputResolution.P1080 && light.Codec == VideoCodec.Hevc, "60 fps, 1080p, the same encoder");
        Assert.True(mine.Fps == 120 && mine.Resolution == OutputResolution.Native, "the user's own settings stay as they are");
        var small = new VideoSettings { Fps = 30, Resolution = OutputResolution.P720 };
        Assert.True(LagRelief.Video(small, 1) is { Fps: 30, Resolution: OutputResolution.P720 }, "already lighter: kept");
        Assert.True(ReferenceEquals(mine, LagRelief.Video(mine, 0)), "none: as chosen");
        Assert.Equal(2, LagRelief.Max(cpuThreads: 16, software: false), "a CPU with room: then the CPU encodes");
        Assert.Equal(1, LagRelief.Max(cpuThreads: 8, software: false), "a smaller CPU: lighter only");
        Assert.Equal(1, LagRelief.Max(cpuThreads: 16, software: true), "already on the CPU");
    }

    [Test]
    public static void A_step_that_would_change_nothing_is_skipped()
    {
        var p1080 = new VideoSettings { Fps = 60, Resolution = OutputResolution.P1080 };
        Assert.Equal(2, LagRelief.Next(p1080, 0, max: 2), "already 1080p60: straight to the CPU");
        Assert.Equal(0, LagRelief.Next(p1080, 0, max: 1), "and no room on the CPU: nothing lighter left");
        var p1440 = new VideoSettings { Fps = 120, Resolution = OutputResolution.P1440 };
        Assert.Equal(1, LagRelief.Next(p1440, 0, max: 2), "1440p120: 60 fps and 1080p first");
        Assert.Equal(2, LagRelief.Next(p1440, 1, max: 2), "then the CPU");
        Assert.Equal(2, LagRelief.Next(p1440, 2, max: 2), "the CPU: the lightest");
    }

    [Test]
    public static void Lighter_recording_is_kept_for_the_pc_and_eased_after_a_quiet_week()
    {
        var state = new HardwareState();
        state.Relieve(T0, 1);
        Assert.Equal(1, state.Relief, "one step lighter, kept in hardware.json");
        Assert.True(!state.EaseIfQuiet(T0.AddDays(3)), "a few days: kept");
        Assert.True(state.EaseIfQuiet(T0.AddDays(8)) && state.Relief == 0, "a week without falling behind: as chosen again");
        state.Relieve(T0, 2);
        state.ChoseVideo();
        Assert.Equal(0, state.Relief, "new video settings from the user: theirs, as chosen");
    }

    [Test]
    public static void The_status_page_says_why_it_records_lighter()
    {
        Text.Language = "en";
        var engine = new EngineStatus(true, false, 60, 0, 0, 0, 300, 300, "x", 1920, 1080, 0, 0, @"D:\Clips", false, null);
        Assert.Equal("", Ui.StatusLogic.CaptureWarning(engine), "nothing to say");
        Assert.Equal(Text.Get("status.relief"), Ui.StatusLogic.CaptureWarning(engine with { Relief = 1 }), "lighter");
        Assert.Equal(Text.Get("status.reliefCpu"), Ui.StatusLogic.CaptureWarning(engine with { Relief = 2 }), "on the CPU");
        Assert.Equal(Text.Get("status.behind"), Ui.StatusLogic.CaptureWarning(engine with { Relief = 2, Behind = true }), "still behind: what the user can do");
    }

    [Test]
    public static void A_save_that_waits_for_a_lagging_encoder_says_so()
    {
        var watch = new SaveWatch();
        watch.Requested(SaveWatch.Request.Started, T0, 0, null);
        Assert.True(watch.Pending, "waiting for the clip");
        Assert.Equal("toast.notSaved.behind", watch.Tick(T0.AddMinutes(5), behind: true), "the recording is behind, not the disk");
        watch.Requested(SaveWatch.Request.Started, T0, 0, null);
        Assert.Equal("toast.notSaved.write", watch.Tick(T0.AddMinutes(5), behind: false), "keeping up: a write that never finished");
        watch.Requested(SaveWatch.Request.Started, T0, 0, null);
        watch.Cancel();
        Assert.True(!watch.Pending && watch.Tick(T0.AddMinutes(5), behind: false) == null, "built again: the old save is not reported later");
    }
}
