using Kairos.Engine.Share;

namespace Kairos.Tests;

public static class SharePlannerTests
{
    /// <summary>A size limit like the one X has, on a copy (sizes and frame rate under a limit).</summary>
    static readonly ShareSpec Limited20Mb = new(SharePlatform.Copy, 20_000_000, double.PositiveInfinity, 2160, 60);

    [Test]
    public static void The_cut_opens_as_long_as_the_platform_takes_ending_at_the_end_of_the_clip()
    {
        Assert.Equal((0.0, 300.0), SharePlanner.DefaultTrim(300, double.PositiveInfinity), "the whole clip");
        Assert.Equal((160.0, 300.0), SharePlanner.DefaultTrim(300, 140), "X: the last 2:20");
        Assert.Equal((0.0, 12.0), SharePlanner.DefaultTrim(12, 140), "a short clip stays whole");
    }

    [Test]
    public static void A_trim_stays_inside_the_clip_and_the_platform_limit()
    {
        Assert.Equal((0.0, 300.0), SharePlanner.ClampTrim(-5, 400, 300, double.PositiveInfinity), "inside the clip");
        Assert.Equal((160.0, 300.0), SharePlanner.ClampTrim(0, 300, 300, 140), "X: the end is kept, the start follows");
        Assert.Equal((100.0, 101.0), SharePlanner.ClampTrim(100, 100.2, 300, double.PositiveInfinity), "at least one second");
        Assert.Equal((299.0, 300.0), SharePlanner.ClampTrim(300, 300, 300, double.PositiveInfinity), "one second even at the very end");
    }

    [Test]
    public static void X_premium_takes_long_clips_and_free_accounts_two_minutes_twenty()
    {
        Assert.Equal(140.0, SharePlanner.Spec(SharePlatform.X).MaxSeconds, "free");
        Assert.True(SharePlanner.Spec(SharePlatform.X, xPremium: true).MaxSeconds >= 3600, "Premium");
        Assert.True(SharePlanner.Spec(SharePlatform.X, xPremium: true).MaxBytes > SharePlanner.Spec(SharePlatform.X).MaxBytes, "bigger files");
    }

    static string Steps(IEnumerable<SizeStep> steps) =>
        string.Join(" ", steps.Select(s => s.Original ? $"[{s.ShortSide}]" : s.ShortSide.ToString()));

    [Test]
    public static void The_sizes_tried_are_smaller_ones_the_original_and_up_to_the_platforms_top()
    {
        Assert.Equal("480 720 1080 [1440] 2160", Steps(SharePlanner.SizeSteps(1440, 2160)), "copy of a 1440p clip: down to 480, up to 4K");
        Assert.Equal("480 720 [1080] 1440 2160", Steps(SharePlanner.SizeSteps(1080, 2160)), "copy of a 1080p clip");
        Assert.Equal("480 720 1080 1440 [2160]", Steps(SharePlanner.SizeSteps(2160, 2160)), "a 4K clip is already the top");
        Assert.Equal("480 720 1080", Steps(SharePlanner.SizeSteps(1440, 1080)), "X shows 1080p at most, so 1440p is not offered");
        Assert.Equal("480 720 [810] 1080", Steps(SharePlanner.SizeSteps(810, 1080)), "a 9:16 cut of 1440p can go up to 1080x1920");
        Assert.Equal("[360] 480 720 1080", Steps(SharePlanner.SizeSteps(360, 1080)), "a tiny clip");
    }

    [Test]
    public static void The_clips_own_size_goes_everywhere_but_X_which_takes_1920_by_1200_at_most()
    {
        foreach (var p in new[] { SharePlatform.Copy, SharePlatform.TikTok, SharePlatform.Instagram })
            Assert.Equal(2160, SharePlanner.Spec(p).MaxShortSide, p.ToString());
        Assert.Equal(1080, SharePlanner.Spec(SharePlatform.X).MaxShortSide, "X");
    }

    [Test]
    public static void Landscape_scales_to_the_chosen_size_up_or_down_and_keeps_even_sizes()
    {
        var f = SharePlanner.Frame(2560, 1440, vertical: false, cropPosition: 0.5, shortSide: 1080);
        Assert.Equal((1920, 1080), (f.Width, f.Height), "1440p scaled to 1080p");
        Assert.True(f.Crop == null, "no crop");
        var up = SharePlanner.Frame(2560, 1440, vertical: false, cropPosition: 0.5, shortSide: 2160);
        Assert.Equal((3840, 2160), (up.Width, up.Height), "1440p upscaled to 4K");
        var original = SharePlanner.Frame(2560, 1440, vertical: false, cropPosition: 0.5, shortSide: 0);
        Assert.Equal((2560, 1440), (original.Width, original.Height), "0 keeps the clip's own size");
        var odd = SharePlanner.Frame(1366, 768, vertical: false, cropPosition: 0.5, shortSide: 720);
        Assert.True(odd.Width % 2 == 0 && odd.Height % 2 == 0, $"even {odd.Width}x{odd.Height}");
    }

    [Test]
    public static void Vertical_cuts_a_nine_by_sixteen_window_where_the_user_slides_it()
    {
        var center = SharePlanner.Frame(2560, 1440, vertical: true, cropPosition: 0.5, shortSide: 0);
        Assert.Equal(new CropRect(874, 0, 810, 1440), center.Crop, "centred 9:16 window");
        Assert.Equal((810, 1440), (center.Width, center.Height), "the cut's own size");
        var full = SharePlanner.Frame(2560, 1440, vertical: true, cropPosition: 0.5, shortSide: 1080);
        Assert.Equal((1080, 1920), (full.Width, full.Height), "up to the sites' 1080x1920");
        var left = SharePlanner.Frame(2560, 1440, vertical: true, cropPosition: 0, shortSide: 0);
        Assert.Equal(0, left.Crop!.X, "left edge");
        var right = SharePlanner.Frame(1920, 1080, vertical: true, cropPosition: 1, shortSide: 0);
        Assert.Equal(1920 - 606, right.Crop!.X, "right edge");
        Assert.Equal((606, 1080), (right.Width, right.Height), "1080p source");
    }

    [Test]
    public static void A_twenty_megabyte_copy_fits_its_limit()
    {
        var spec = Limited20Mb;
        var size = SharePlanner.DefaultSize(spec, 30, 2560, 1440, 60, 192_000, vertical: false);
        Assert.Equal(new SizeStep(1080, false), size, "1440p would turn to blocks in 20 MB, 1080p does not");
        var plan = SharePlanner.Plan(spec, start: 270, duration: 30, srcWidth: 2560, srcHeight: 1440, srcFps: 60, audioBitrate: 192_000,
            vertical: false, cropPosition: 0.5, shortSide: size.ShortSide);
        double bytes = (plan.VideoBitrate + 192_000) * plan.Duration / 8;
        Assert.True(bytes <= 20_000_000 * 0.95, $"estimated {bytes / 1e6:F1} MB");
        Assert.True(plan.VideoBitrate > 3_000_000, $"still good quality at {plan.VideoBitrate / 1e6:F1} Mb/s");
        Assert.Equal((1920, 1080), (plan.Width, plan.Height), "1080p");
    }

    [Test]
    public static void A_long_clip_for_a_small_limit_drops_resolution_instead_of_turning_to_mush()
    {
        var spec = Limited20Mb;
        var size = SharePlanner.DefaultSize(spec, 300, 2560, 1440, 60, 192_000, false);
        var plan = SharePlanner.Plan(spec, 0, 300, 2560, 1440, 60, 192_000, false, 0.5, size.ShortSide);
        Assert.True(plan.Height <= 480, $"{plan.Height}p for 5 minutes in 20 MB");
        Assert.Equal(30.0, plan.Fps, "and 30 fps");
        Assert.Equal(64_000L, plan.AudioBitrate, "the sound made smaller too");
        Assert.True((plan.VideoBitrate + plan.AudioBitrate) * plan.Duration / 8 <= 20_000_000 * 0.95, "fits");
        var x = SharePlanner.Plan(SharePlanner.Spec(SharePlatform.X), 160, 140, 2560, 1440, 60, 192_000, false, 0.5, 1080);
        Assert.Equal((160.0, 140.0), (x.Start, x.Duration), "the trimmed part");
        Assert.True(x.VideoBitrate <= 12_000_000, "quality cap");
    }

    [Test]
    public static void Instagram_gets_the_full_rate_like_the_others_because_less_showed_after_its_recompression()
    {
        // Measured 2026-10-13 (VMAF after a simulated site re-encode): 6 Mb/s scored 1-2 points under 12 Mb/s.
        var ig = SharePlanner.Plan(SharePlanner.Spec(SharePlatform.Instagram), 270, 30, 2560, 1440, 60, 192_000, true, 0.5, 1080);
        var tiktok = SharePlanner.Plan(SharePlanner.Spec(SharePlatform.TikTok), 270, 30, 2560, 1440, 60, 192_000, true, 0.5, 1080);
        Assert.Equal((12_000_000L, 12_000_000L), (ig.VideoBitrate, tiktok.VideoBitrate), "full rate at 1080x1920");
        Assert.Equal(60.0, ig.Fps, "fps");
    }

    [Test]
    public static void The_bitrate_follows_the_picture_size_so_smaller_is_lighter_and_bigger_stays_clean()
    {
        var copy = SharePlanner.Spec(SharePlatform.Copy);
        var k4 = SharePlanner.Plan(copy, 0, 30, 2560, 1440, 60, 192_000, false, 0.5, 2160);
        Assert.Equal((3840, 2160, 48_000_000L), (k4.Width, k4.Height, k4.VideoBitrate), "4K: four times the pixels of 1080p");
        var p720 = SharePlanner.Plan(copy, 0, 30, 2560, 1440, 60, 192_000, false, 0.5, 720);
        Assert.Equal((1280, 720, 5_333_333L), (p720.Width, p720.Height, p720.VideoBitrate), "720p");
        var x = SharePlanner.Spec(SharePlatform.X);
        var small = SharePlanner.Plan(x, 0, 140, 2560, 1440, 60, 192_000, false, 0.5, 480);
        Assert.True(small.VideoBitrate < 3_000_000, $"480p on X is a small upload ({small.VideoBitrate / 1e6:F1} Mb/s)");
        var vertical = SharePlanner.Plan(SharePlanner.Spec(SharePlatform.TikTok), 0, 30, 2560, 1440, 60, 192_000, true, 0.5, 0);
        Assert.Equal((810, 1440, 6_750_000L), (vertical.Width, vertical.Height, vertical.VideoBitrate), "the 9:16 cut's own size");
    }

    [Test]
    public static void The_size_is_the_clips_own_unless_the_site_or_the_size_limit_needs_less()
    {
        Assert.Equal(new SizeStep(1440, true), SharePlanner.DefaultSize(SharePlanner.Spec(SharePlatform.Copy), 30, 2560, 1440, 60, 192_000, false), "copy without a limit");
        Assert.Equal(new SizeStep(1440, true), SharePlanner.DefaultSize(SharePlanner.Spec(SharePlatform.TikTok), 30, 2560, 1440, 60, 192_000, false), "TikTok");
        Assert.Equal(new SizeStep(1440, true), SharePlanner.DefaultSize(SharePlanner.Spec(SharePlatform.Instagram), 30, 2560, 1440, 60, 192_000, false), "Instagram");
        Assert.Equal(new SizeStep(810, true), SharePlanner.DefaultSize(SharePlanner.Spec(SharePlatform.TikTok), 30, 2560, 1440, 60, 192_000, true), "TikTok 9:16 cut");
        Assert.Equal(new SizeStep(1080, false), SharePlanner.DefaultSize(SharePlanner.Spec(SharePlatform.X), 30, 2560, 1440, 60, 192_000, false), "X: 1080p");
        Assert.Equal(new SizeStep(1080, true), SharePlanner.DefaultSize(SharePlanner.Spec(SharePlatform.X), 30, 1920, 1080, 60, 192_000, false), "a 1080p clip on X");
    }

    [Test]
    public static void Caption_tags_the_game_and_keeps_the_users_hashtags()
    {
        Assert.Equal("#EldenRing #gaming", SharePlanner.Caption("ELDEN RING™", "#gaming"), "game tag");
        Assert.Equal("#لعبة_تجريبية #clips", SharePlanner.Caption("لعبة تجريبية", "#clips"), "Arabic game");
        Assert.Equal("#gaming", SharePlanner.Caption("Desktop", "#gaming"), "no tag for the desktop");
        Assert.Equal("#Valorant", SharePlanner.Caption("Valorant", ""), "no saved tags");
    }

    [Test]
    public static void The_game_name_comes_from_the_clip_file_name()
    {
        Assert.Equal("ELDEN RING", SharePlanner.GameFromFile(@"D:\Clips\ELDEN RING\ELDEN RING 2026-10-12 21-04-55.mp4"), "per-app folder");
        Assert.Equal("Valorant", SharePlanner.GameFromFile(@"D:\Clips\Valorant 2026-10-12 21-04-55 (2).mp4"), "numbered copy");
        Assert.Equal("my clip", SharePlanner.GameFromFile(@"D:\Clips\my clip.mp4"), "renamed by the user");
    }

    [Test]
    public static void Only_the_users_own_hashtags_are_remembered_for_next_time()
    {
        Assert.Equal("#gaming #fps", SharePlanner.RememberTags("What a clutch #Valorant #gaming #fps", "Valorant"), "game tag and words dropped");
        Assert.Equal("", SharePlanner.RememberTags("no tags here", "Valorant"), "nothing to keep");
        Assert.Equal("#gaming", SharePlanner.RememberTags("#gaming #gaming", "Desktop"), "no duplicates");
    }

    [Test]
    public static void X_opens_the_composer_with_the_caption_and_others_open_their_upload_page()
    {
        Assert.Equal("https://x.com/intent/post?text=%23Game%20%23gaming", SharePlanner.OpenTarget(SharePlatform.X, "#Game #gaming"), "x");
        Assert.True(SharePlanner.OpenTarget(SharePlatform.TikTok, "x").StartsWith("https://www.tiktok.com/"), "tiktok");
        Assert.True(SharePlanner.OpenTarget(SharePlatform.Instagram, "x").StartsWith("https://www.instagram.com/"), "instagram");
        Assert.Equal(long.MaxValue, SharePlanner.Spec(SharePlatform.Copy).MaxBytes, "a copy has no size limit");
    }

    [Test]
    public static void Copy_sizes_are_original_and_discords_limits()
    {
        Assert.Equal("0,10,50,500", string.Join(",", SharePlanner.CopySizes), "Original, free, Nitro Basic, Nitro");
        Assert.Equal(long.MaxValue, SharePlanner.CopySpec(0).MaxBytes, "Original: no limit");
        Assert.Equal(10_000_000L, SharePlanner.CopySpec(10).MaxBytes, "10 MB");
    }

    [Test]
    public static void The_audio_bitrate_follows_the_size()
    {
        Assert.Equal(96_000L, SharePlanner.AudioFor(SharePlanner.CopySpec(10), 60, 192_000), "10 MB, a minute");
        Assert.Equal(64_000L, SharePlanner.AudioFor(SharePlanner.CopySpec(10), 120, 192_000), "10 MB, two minutes: never below 64");
        Assert.Equal(0L, SharePlanner.AudioFor(SharePlanner.CopySpec(50), 60, 192_000), "50 MB: the sound is copied");
        Assert.Equal(0L, SharePlanner.AudioFor(SharePlanner.CopySpec(0), 60, 192_000), "Original: copied");
        Assert.Equal(0L, SharePlanner.AudioFor(SharePlanner.Spec(SharePlatform.X), 140, 192_000), "X: copied as before");
    }

    [Test]
    public static void A_sized_copy_plans_under_its_limit()
    {
        var spec = SharePlanner.CopySpec(10);
        var size = SharePlanner.DefaultSize(spec, 60, 2560, 1440, 60, 192_000, false);
        var plan = SharePlanner.Plan(spec, 0, 60, 2560, 1440, 60, 192_000, false, 0.5, size.ShortSide);
        Assert.True((plan.VideoBitrate + plan.AudioBitrate) * 60 / 8 <= 10_000_000 * 0.92, $"fits: {plan.VideoBitrate} + {plan.AudioBitrate}");
        Assert.Equal(96_000L, plan.AudioBitrate, "the sound is made smaller too");
        var big = SharePlanner.Plan(SharePlanner.CopySpec(500), 0, 60, 2560, 1440, 60, 192_000, false, 0.5, 0);
        Assert.Equal((long)(SharePlanner.QualityCapBitrate * (2560.0 * 1440 / (1920 * 1080))), big.VideoBitrate, "500 MB: the quality cap");
    }

    [Test]
    public static void Seconds_that_fit_say_when_trim_is_needed()
    {
        double fit = SharePlanner.SecondsThatFit(SharePlanner.CopySpec(10), 2560, 1440, false);
        Assert.True(fit > 100 && fit < 200, $"10 MB at 1440p: {fit:0} s");
        Assert.Equal(double.PositiveInfinity, SharePlanner.SecondsThatFit(SharePlanner.CopySpec(0), 2560, 1440, false), "Original");
    }
}
