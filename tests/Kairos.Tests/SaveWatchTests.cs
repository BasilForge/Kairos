using Kairos.App;
using Kairos.Engine;

namespace Kairos.Tests;

/// <summary>A save that did not happen is said (it used to fail in silence), and a nearly full disk is said now and then.</summary>
public static class SaveWatchTests
{
    static readonly DateTime T0 = new(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);
    const long GB = 1L << 30, MB = 1L << 20;

    [Test]
    public static void A_press_that_cannot_save_says_why()
    {
        var watch = new SaveWatch();
        Assert.Equal("toast.notSaved.off", watch.Requested(SaveWatch.Request.Off, T0, 0, SaveWatch.OffReason(enabled: false, paused: false, waiting: false)), "instant replay off");
        Assert.Equal("toast.notSaved.paused", SaveWatch.OffReason(enabled: true, paused: true, waiting: false), "paused");
        Assert.Equal("toast.notSaved.waiting", SaveWatch.OffReason(enabled: true, paused: false, waiting: true), "waiting for a game");
        Assert.Equal("toast.notSaved.starting", watch.Requested(SaveWatch.Request.Starting, T0, 0, null), "just started");
        Assert.Equal(null, watch.Requested(SaveWatch.Request.Busy, T0, 0, null), "a second press while one is written: that one reports");
        Assert.Equal(null, watch.Requested(SaveWatch.Request.Started, T0, 0, null), "started: nothing yet");
    }

    [Test]
    public static void A_replay_that_could_not_start_says_so_not_off()
    {
        Assert.Equal("toast.notSaved.failed", SaveWatch.OffReason(enabled: true, paused: false, waiting: false, failed: true), "encoder busy");
        Assert.Equal("toast.notSaved.off", SaveWatch.OffReason(enabled: false, paused: false, waiting: false, failed: true), "turned off says off");
        Assert.Equal("toast.notSaved.paused", SaveWatch.OffReason(enabled: true, paused: true, waiting: false, failed: true), "paused says paused");
        Assert.Equal("toast.notSaved.waiting", SaveWatch.OffReason(enabled: true, paused: false, waiting: true, failed: true), "waiting says waiting");
    }

    [Test]
    public static void A_save_never_written_is_said_once_after_a_generous_wait()
    {
        var watch = new SaveWatch();
        long buffer = 2 * GB;
        var limit = SaveWatch.WriteLimit(buffer);
        Assert.True(limit >= TimeSpan.FromSeconds(60) && limit <= TimeSpan.FromSeconds(120), $"2 GB may take a while on a slow disk ({limit})");
        Assert.True(SaveWatch.WriteLimit(0) == TimeSpan.FromSeconds(30), "never under 30 s");
        watch.Requested(SaveWatch.Request.Started, T0, buffer, null);
        Assert.Equal(null, watch.Tick(T0 + limit - TimeSpan.FromSeconds(1)), "still writing");
        Assert.Equal("toast.notSaved.write", watch.Tick(T0 + limit + TimeSpan.FromSeconds(1)), "never finished");
        Assert.Equal(null, watch.Tick(T0 + limit + TimeSpan.FromSeconds(10)), "said once");

        watch.Requested(SaveWatch.Request.Started, T0, buffer, null);
        Assert.Equal(null, watch.Saved(500 * MB, 100 * GB), "written fine");
        Assert.Equal(null, watch.Tick(T0 + limit + TimeSpan.FromSeconds(1)), "a finished save is not reported later");
    }

    [Test]
    public static void An_empty_file_or_a_full_disk_is_not_a_saved_clip()
    {
        var watch = new SaveWatch();
        Assert.Equal("toast.notSaved.write", watch.Saved(0, 100 * GB), "empty or missing file");
        Assert.Equal("toast.notSaved.disk", watch.Saved(300 * MB, 10 * MB), "the disk ran out while writing");
        Assert.Equal(null, watch.Saved(300 * MB, -1), "free space unknown (network folder): trusted");
    }

    [Test]
    public static void A_nearly_full_disk_is_said_at_most_every_few_hours()
    {
        var watch = new SaveWatch();
        Assert.True(!watch.ShouldWarnLowDisk(50 * GB, T0), "plenty of room");
        Assert.True(watch.ShouldWarnLowDisk(8 * GB, T0), "under 10 GB");
        Assert.True(!watch.ShouldWarnLowDisk(7 * GB, T0.AddHours(1)), "not on every save");
        Assert.True(watch.ShouldWarnLowDisk(6 * GB, T0.AddHours(7)), "again hours later");
        Assert.True(!watch.ShouldWarnLowDisk(-1, T0.AddDays(1)), "unknown: nothing said");
    }

    [Test]
    public static void Every_message_reads_in_both_languages()
    {
        foreach (string key in new[] { "toast.notSaved", "toast.notSaved.off", "toast.notSaved.paused", "toast.notSaved.waiting",
                     "toast.notSaved.starting", "toast.notSaved.write", "toast.notSaved.disk", "toast.lowDisk", "toast.lowDiskBody" })
            foreach (string language in new[] { "ar", "en" })
            {
                Text.Language = language;
                Assert.True(Text.Get(key) != key && Text.Get(key).Length > 0, $"{key} ({language})");
            }
        Text.Language = "en";
        Assert.Equal("8.2 GB left on C:", Text.Format("toast.lowDiskBody", "8.2 GB", "C:"), "low disk body");
    }
}
