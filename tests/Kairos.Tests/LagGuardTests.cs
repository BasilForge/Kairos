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

    /// <summary>The encoder's counters only: <paramref name="bad"/> of <paramref name="all"/> frames skipped, none lagged.</summary>
    static FrameCount Skipped(ulong bad, ulong all) => new(0, 0, bad, all);

    /// <summary>Samples every 5 s at 60 fps with <paramref name="share"/> of them lagged or skipped; the last decision.</summary>
    static (bool Lighter, int At) Feed(LagGuard guard, double share, int seconds, ref ulong bad, ref ulong all, int from = 0)
    {
        bool lighter = false;
        int at = -1;
        for (int s = from; s <= from + seconds; s += 5)
        {
            if (guard.Add(T0.AddSeconds(s), Skipped(bad, all)) is not null && at < 0)
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
            guard.Add(T0.AddSeconds(s), Skipped(bad, all));
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
            guard.Add(T0.AddSeconds(s), Skipped(bad, all));
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
    public static void Late_pictures_and_the_repeats_skipped_for_them_count_once()
    {
        // A user's 1.1 log: "246 % of the frames lagged or skipped". A picture the graphics thread is late with reaches the
        // encoder as a repeat that it skips: the same frame in both counters, each out of its own total.
        var guard = new LagGuard();
        double? share = null;
        for (int s = 0; s <= 20; s += 5)
        {
            ulong n = (ulong)s * 60;
            share = guard.Add(T0.AddSeconds(s), new FrameCount(Lagged: n * 6 / 10, Rendered: n, Skipped: n * 6 / 10, Encoded: n));
        }
        Assert.True(share is { } x && Math.Abs(x - 0.6) < 0.001, $"60 % late and skipped: 60 %, not 120 % ({share:P1})");
        Assert.True(guard.Behind, "behind");
        var later = new LagGuard();
        for (int s = 0; s <= 20; s += 5)
        {
            ulong n = (ulong)s * 60;
            share = later.Add(T0.AddSeconds(s), new FrameCount(Lagged: n / 4, Rendered: n, Skipped: n / 20, Encoded: n));
        }
        Assert.True(share is { } y && Math.Abs(y - 0.25) < 0.001, $"the larger share: 25 % late, 5 % skipped ({share:P1})");
        var quiet = new LagGuard();
        for (int s = 0; s <= 20; s += 5)
            share = quiet.Add(T0.AddSeconds(s), new FrameCount(Lagged: 40_000, Rendered: 2_000_000 + (ulong)s * 60, Skipped: 0, Encoded: (ulong)s * 60));
        Assert.True(share == null && !quiet.Behind, "late pictures from before (the session-long counter) are not this window's");
    }

    [Test]
    public static void Lighter_means_the_light_encoder_then_60_fps_and_1080p_then_30_fps_then_the_cpu()
    {
        var mine = new VideoSettings { Fps = 120, Resolution = OutputResolution.Native, Codec = VideoCodec.Hevc };
        Assert.True(ReferenceEquals(mine, LagRelief.Video(mine, 0)), "none: as chosen");
        Assert.True(ReferenceEquals(mine, LagRelief.Video(mine, LagRelief.LightEncoder)), "the light encoder: the same size and rate");
        var light = LagRelief.Video(mine, LagRelief.Capped);
        Assert.True(light.Fps == 60 && light.Resolution == OutputResolution.P1080 && light.Codec == VideoCodec.Hevc, "60 fps, 1080p, the same encoder");
        Assert.True(LagRelief.Video(mine, LagRelief.HalfRate) is { Fps: 30, Resolution: OutputResolution.P1080 }, "then 30 fps");
        Assert.True(mine.Fps == 120 && mine.Resolution == OutputResolution.Native, "the user's own settings stay as they are");
        var small = new VideoSettings { Fps = 30, Resolution = OutputResolution.P720 };
        Assert.True(LagRelief.Video(small, LagRelief.HalfRate) is { Fps: 30, Resolution: OutputResolution.P720 }, "already lighter: kept");
        Assert.Equal(LagRelief.Cpu, LagRelief.Max(cpuThreads: 16, software: false), "a CPU with room: at last the CPU encodes");
        Assert.Equal(LagRelief.HalfRate, LagRelief.Max(cpuThreads: 8, software: false), "a user's 8 threads: down to 30 fps on the graphics card");
        Assert.Equal(LagRelief.HalfRate, LagRelief.Max(cpuThreads: 16, software: true), "already on the CPU: 30 fps there");
    }

    /// <summary>
    /// A user's RTX 3060 Ti in ARC Raiders: 43 % skipped with nothing lighter left ("already as light as it goes": 1080p60
    /// with 8 threads). Every step that changes something comes in turn: the light encoder (NVIDIA's only), then the
    /// picture. REALTIME is no step: the recording has it from the start.
    /// </summary>
    [Test]
    public static void A_step_that_would_change_nothing_is_skipped()
    {
        var p1080 = new VideoSettings { Fps = 60, Resolution = OutputResolution.P1080 };
        int Next(VideoSettings v, int relief, int max, bool nvidia = true) => LagRelief.Next(v, relief, max, nvidia);
        Assert.Equal(LagRelief.LightEncoder, Next(p1080, 0, LagRelief.HalfRate), "NVIDIA: the light encoder first");
        Assert.Equal(LagRelief.HalfRate, Next(p1080, LagRelief.LightEncoder, LagRelief.HalfRate), "already 1080p60: then 30 fps");
        Assert.Equal(LagRelief.HalfRate, Next(p1080, 0, LagRelief.HalfRate, nvidia: false), "AMD and Intel: no light encoder, 30 fps first");
        Assert.Equal(LagRelief.Cpu, Next(p1080, LagRelief.HalfRate, LagRelief.Cpu), "then the CPU, with room for it");
        Assert.Equal(LagRelief.HalfRate, Next(p1080, LagRelief.HalfRate, LagRelief.HalfRate), "nothing lighter left");
        var p1440 = new VideoSettings { Fps = 120, Resolution = OutputResolution.P1440 };
        Assert.Equal(LagRelief.Capped, Next(p1440, LagRelief.LightEncoder, LagRelief.Cpu), "1440p120: then 60 fps and 1080p");
        var p30 = new VideoSettings { Fps = 30, Resolution = OutputResolution.P720 };
        Assert.Equal(LagRelief.LightEncoder, Next(p30, LagRelief.LightEncoder, LagRelief.HalfRate), "720p30: nothing lighter after the encoder");
    }

    /// <summary>
    /// A recorder starts again when no packets come for a while: NVIDIA's encoder frozen at REALTIME
    /// (hardware scheduling with a full graphics card memory, Sunshine's notes) leaves the sound going on over one picture
    /// in every clip. Pictures that stop reaching the replay for 10 s are noticed.
    /// </summary>
    [Test]
    public static void Pictures_that_stop_reaching_the_replay_are_noticed_within_ten_seconds()
    {
        var watch = new PictureWatch();
        Assert.True(!watch.Stopped(T0, 0) && !watch.Stopped(T0.AddSeconds(1), 60) && !watch.Stopped(T0.AddSeconds(2), 120), "pictures come");
        Assert.True(!watch.Stopped(T0.AddSeconds(11), 120), "9 s without one: not yet");
        Assert.True(watch.Stopped(T0.AddSeconds(12), 120), "10 s without a picture: stopped");
        Assert.True(watch.Stopped(T0.AddSeconds(17), 120), "and still, a clip being written meanwhile: told again at the next look");
        watch.Reset();
        Assert.True(!watch.Stopped(T0.AddSeconds(18), 120), "built again: watched anew from its first look");
        Assert.True(!watch.Stopped(T0.AddSeconds(19), null), "no replay (paused, being built): nothing to watch");
        Assert.True(!watch.Stopped(T0.AddSeconds(20), 0) && !watch.Stopped(T0.AddSeconds(29), 0), "a new replay is timed from its first look");
        Assert.True(watch.Stopped(T0.AddSeconds(30), 0), "one that never gives a picture: stopped too");
        var fresh = new PictureWatch();
        Assert.True(!fresh.Stopped(T0, 900) && !fresh.Stopped(T0.AddSeconds(9), 900) && !fresh.Stopped(T0.AddSeconds(10), 3), "a count that starts again is pictures coming");
    }

    /// <summary>
    /// The PC asleep (or the engine's tick held up) between two looks is no stop: on waking, the tick came before the
    /// rebuild after sleep and read the same count hours later, which built the pipeline again for nothing and left
    /// REALTIME off for a week.
    /// </summary>
    [Test]
    public static void A_long_gap_between_looks_is_no_stop()
    {
        var watch = new PictureWatch();
        Assert.True(!watch.Stopped(T0, 500) && !watch.Stopped(T0.AddSeconds(5), 800), "pictures come");
        Assert.True(!watch.Stopped(T0.AddHours(3), 800), "woken after three hours: the same count is no stop");
        Assert.True(!watch.Stopped(T0.AddHours(3).AddSeconds(5), 800), "timed from the waking");
        Assert.True(watch.Stopped(T0.AddHours(3).AddSeconds(10), 800), "10 s after it with no picture: stopped");
    }

    /// <summary>The light encoder drops NVIDIA's CUDA passes (the same CQ measured as good or better); nothing else changes.</summary>
    [Test]
    public static void The_light_encoder_turns_off_nvidias_cuda_passes_only()
    {
        var plan = new EncoderPlan("obs_nvenc_h264_tex", "CQVBR", 24, 24000, 1, "p5", 2);
        var nvenc = new EncoderChoice("obs_nvenc_h264_tex", EncoderVendor.Nvidia, VideoCodec.H264);
        var full = EncoderCatalog.Settings(plan, nvenc);
        var light = EncoderCatalog.Settings(plan, nvenc, light: true);
        Assert.True(full["multipass"] is "qres" && full["adaptive_quantization"] is true && full["target_quality"] is 24L, "as chosen: full");
        Assert.True(light["multipass"] is "disabled" && light["adaptive_quantization"] is false, "light: no multipass, no AQ");
        Assert.True(light["target_quality"].Equals(full["target_quality"]) && light["preset"].Equals(full["preset"]) && light["bf"].Equals(full["bf"])
                    && light["max_bitrate"].Equals(full["max_bitrate"]), "the same CQ, preset, B-frames and cap");
        var amd = new EncoderChoice("h264_texture_amf", EncoderVendor.Amd, VideoCodec.H264);
        Assert.Equal(EncoderCatalog.Settings(plan, amd).Count, EncoderCatalog.Settings(plan, amd, light: true).Count, "other vendors: as they are");
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

    /// <summary>
    /// Kairos solves problems without telling what it does inside. A lighter step is taken silently, in
    /// every language: no Status note (it said "a game is using most of the graphics card" hours after the game was left)
    /// and no toast. The Status page speaks only when the user can do something.
    /// </summary>
    [Test]
    public static void Lighter_recording_is_never_shown_to_the_user()
    {
        Text.Language = "en";
        var engine = new EngineStatus(true, false, 60, 0, 0, 0, 300, 300, "x", 1920, 1080, 0, 0, @"D:\Clips", false, null);
        Assert.Equal("", Ui.StatusLogic.CaptureWarning(engine), "nothing to say");
        Assert.Equal(Text.Get("status.behind"), Ui.StatusLogic.CaptureWarning(engine with { Behind = true }), "still behind with nothing lighter left: what the user can do");
        Assert.True(!Text.AllKeys().Any(k => k.StartsWith("status.relief", StringComparison.Ordinal) || k == "toast.relief"), "no words for it in any language");
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

    /// <summary>
    /// A step that builds the pipeline again starts the replay empty: with a game in front it waits for the next saved
    /// clip, so the minutes the player may still save are never thrown away (a user saved 2 s after the step: a 2 s clip).
    /// </summary>
    [Test]
    public static void A_lighter_step_that_empties_the_replay_waits_for_a_save_while_a_game_is_in_front()
    {
        Assert.True(!LagRelief.RebuildNow(recording: false, saving: false, gameInReplay: true, savedSince: false), "playing, nothing saved: wait");
        Assert.True(LagRelief.RebuildNow(recording: false, saving: false, gameInReplay: true, savedSince: true), "a clip just saved: now");
        Assert.True(LagRelief.RebuildNow(recording: false, saving: false, gameInReplay: false, savedSince: false), "no game in front: now");
        Assert.True(!LagRelief.RebuildNow(recording: true, saving: false, gameInReplay: false, savedSince: true), "never during a recording");
        Assert.True(!LagRelief.RebuildNow(recording: false, saving: true, gameInReplay: false, savedSince: true), "nor while a clip is written");
    }
}
