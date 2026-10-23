using Kairos.App;
using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Tests;

public static class ShareSettingsTests
{
    [Test]
    public static void Share_choices_have_sensible_defaults_and_are_clamped()
    {
        var s = new AppSettings();
        Assert.Equal((0.5, "#gaming", 1.0, false), (s.Share.CropPosition, s.Share.Tags, s.Share.PreviewVolume, s.Share.PreviewMuted), "defaults");
        Assert.Equal(0, s.Share.VerticalFor.Count, "9:16 is off for every platform at first");
        Assert.Equal(null, s.Share.XPremium, "X account type unknown until seen");

        s.Share.CropPosition = 4;
        s.Share.PreviewVolume = 7;
        s.Share.Tags = null!;
        s.Normalize();
        Assert.Equal((1.0, "", 1.0), (s.Share.CropPosition, s.Share.Tags, s.Share.PreviewVolume), "clamped");

        s.Share.VerticalFor = [SharePlatform.TikTok, SharePlatform.TikTok, (SharePlatform)42, SharePlatform.X];
        s.Normalize();
        Assert.Equal("TikTok X", string.Join(" ", s.Share.VerticalFor), "duplicates and unknown platforms dropped");
        s.Share.VerticalFor = null!;
        s.Normalize();
        Assert.Equal(0, s.Share.VerticalFor.Count, "missing list replaced");

        s.Share = null!;
        s.Normalize();
        Assert.True(s.Share != null, "missing section replaced");
    }

    [Test]
    public static void A_copy_of_the_share_choices_does_not_share_the_vertical_list()
    {
        var s = new ShareSettings { VerticalFor = [SharePlatform.TikTok], Tags = "#a" };
        var c = s.Copy();
        c.VerticalFor.Add(SharePlatform.X);
        c.Tags = "#b";
        Assert.Equal(("TikTok", "#a"), (string.Join(" ", s.VerticalFor), s.Tags), "original untouched");
    }

    [Test]
    public static void Remembering_share_choices_keeps_the_replay_buffer()
    {
        var before = new AppSettings();
        var after = SettingsStore.Deserialize(SettingsStore.Serialize(before));
        after.Share.Tags = "#fps";
        Assert.Equal(false, SettingsDiff.NeedsRebuild(before, after), "no pipeline rebuild");
    }

    [Test]
    public static void Share_choices_survive_a_save_and_load()
    {
        string path = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), "settings.json");
        var s = new AppSettings();
        s.Share.XPremium = true;
        s.Share.Tags = "#clips #ps5";
        s.Share.VerticalFor = [SharePlatform.Instagram, SharePlatform.Copy];
        SettingsStore.Save(path, s);
        var loaded = SettingsStore.Load(path);
        Assert.Equal(("#clips #ps5", (bool?)true), (loaded.Share.Tags, loaded.Share.XPremium), "round trip");
        Assert.Equal("Instagram Copy", string.Join(" ", loaded.Share.VerticalFor), "vertical per platform");
    }

    [Test]
    public static void The_tray_hands_a_clip_to_the_settings_window_once()
    {
        string file = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), "share-request.txt");
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        ShareRequest.Write(file, @"D:\Clips\لعبة\clip.mp4", now);
        Assert.Equal(@"D:\Clips\لعبة\clip.mp4", ShareRequest.Take(file, now.AddSeconds(2)), "taken");
        Assert.Equal(null, ShareRequest.Take(file, now.AddSeconds(3)), "only once");

        ShareRequest.Write(file, @"D:\Clips\old.mp4", now);
        Assert.Equal(null, ShareRequest.Take(file, now.AddMinutes(2)), "a stale request is ignored");
        Assert.True(!File.Exists(file), "stale request removed");
    }

    [Test]
    public static void Old_prepared_files_are_cleaned_but_recent_ones_stay()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), "Share");
        Directory.CreateDirectory(Path.Combine(dir, "Copy"));
        string old = Path.Combine(dir, "Copy", "old.mp4");
        string recent = Path.Combine(dir, "Copy", "recent.mp4");
        File.WriteAllText(old, "x");
        File.WriteAllText(recent, "x");
        var now = new DateTime(2026, 10, 13, 12, 0, 0);
        File.SetLastWriteTime(old, now.AddDays(-4));
        File.SetLastWriteTime(recent, now.AddHours(-5));
        ShareFiles.Clean(dir, now);
        Assert.Equal((false, true), (File.Exists(old), File.Exists(recent)), "old removed, recent kept");
        Assert.Equal(Path.Combine(dir, "TikTok", "ELDEN RING 2026-10-12 21-04-55.mp4"),
            ShareFiles.Target(dir, SharePlatform.TikTok, @"D:\Clips\ELDEN RING\ELDEN RING 2026-10-12 21-04-55.mp4"), "per-platform folder, same name");
    }

    [Test]
    public static void The_copy_size_is_saved()
    {
        var s = new AppSettings();
        Assert.Equal(0, s.Share.CopyMb, "Original until a size is picked");
        s.Share.CopyMb = 50;
        Assert.Equal(50, SettingsStore.Deserialize(SettingsStore.Serialize(s)).Share.CopyMb, "round trip");
        Assert.True(SettingsStore.Serialize(s).Contains("\"copyMb\": 50"), "named copyMb");
        Assert.Equal(0, SettingsStore.Deserialize("""{"share":{"tags":"#a"}}""").Share.CopyMb, "a 1.0 file: Original");
        s.Share.CopyMb = 33;
        s.Normalize();
        Assert.Equal(0, s.Share.CopyMb, "a size Copy does not offer: Original");
    }

    [Test]
    public static void A_copy_is_named_by_its_part_and_size()
    {
        const string clip = @"D:\Clips\ELDEN RING\ELDEN RING 2026-10-12 21-04-55.mp4";
        Assert.Equal(@"D:\Clips\Shared\ELDEN RING 2026-10-12 21-04-55 (0.05-0.35).mp4", ShareFiles.CopyTarget(@"D:\Clips\Shared", clip, 5, 35, 0), "a cut");
        Assert.Equal(@"D:\Clips\Shared\ELDEN RING 2026-10-12 21-04-55 (4.30-5.00, 10 MB).mp4", ShareFiles.CopyTarget(@"D:\Clips\Shared", clip, 270, 300, 10), "a cut at a size");
        Assert.Equal(@"D:\Clips\Shared\ELDEN RING 2026-10-12 21-04-55 (1.00.00-1.02.30).mp4", ShareFiles.CopyTarget(@"D:\Clips\Shared", clip, 3600, 3750, 0), "past an hour");
        Assert.Equal(@"D:\Clips\Shared", ShareFiles.SharedIn(@"D:\Clips"), "the clips folder's Shared");
    }
}
