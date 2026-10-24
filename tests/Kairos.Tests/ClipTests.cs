using Kairos.App;
using Kairos.Engine;

namespace Kairos.Tests;

public static class ClipTests
{
    static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>A user's Genshin Impact clips all went to "Desktop": its anti-cheat lets Kairos read neither its file nor its product.</summary>
    [Test]
    public static void A_game_kairos_cannot_open_is_named_after_its_window()
    {
        Assert.Equal("Genshin Impact", ForegroundApp.Unopened("GenshinImpact", "Genshin Impact"), "its window's title");
        Assert.Equal("Honkai Star Rail", ForegroundApp.Unopened("StarRail", "Honkai: Star Rail"), "':' is no file name's");
        Assert.Equal("Client-Win64-Shipping", ForegroundApp.Unopened("Client-Win64-Shipping", " "), "no title: its exe");
        Assert.Equal("Desktop", ForegroundApp.Unopened(null, null), "gone: as before");
        Assert.Equal("Desktop", ForegroundApp.Unopened("explorer", "Program Manager"), "the shell stays the desktop");
    }

    /// <summary>
    /// A user saved from a macro (Kairos.exe --send save) with the desktop in front: his ARC Raiders clips all went to
    /// "Desktop". A save from the desktop or Kairos's own window goes to the game in front within the clip's length.
    /// </summary>
    [Test]
    public static void A_save_from_the_desktop_or_kairos_goes_to_the_game_played_just_before()
    {
        static string Arc() => "ARC Raiders";
        Assert.Equal("ARC Raiders", ClipOrganizer.SaveFolderName("Desktop", () => false, Arc, TimeSpan.FromSeconds(4), 180), "a macro's save: the game a moment ago");
        Assert.Equal("ARC Raiders", ClipOrganizer.SaveFolderName("Kairos", () => true, Arc, TimeSpan.FromSeconds(30), 180), "Kairos's own Save button");
        Assert.Equal("Desktop", ClipOrganizer.SaveFolderName("Desktop", () => false, Arc, TimeSpan.FromMinutes(10), 180), "the game left before the clip's minutes");
        Assert.Equal("Brave Browser", ClipOrganizer.SaveFolderName("Brave Browser", () => false, Arc, TimeSpan.FromSeconds(4), 180), "another app in front: its own");
        Assert.Equal("Desktop", ClipOrganizer.SaveFolderName("Desktop", () => false, null, TimeSpan.Zero, 180), "no game seen");
    }

    /// <summary>A game's pid kept after it closed can be another process's by then: the game's own name, not that one's.</summary>
    [Test]
    public static void A_game_seen_earlier_is_named_after_its_exe_when_its_pid_is_another_process_now()
    {
        uint self = (uint)Environment.ProcessId;
        Assert.Equal("ARC Raiders", ForegroundApp.ForProcess(self, "ARC Raiders"), "the pid is this test's now: the game's name");
        Assert.True(ForegroundApp.ForProcess(self, Path.GetFileNameWithoutExtension(Environment.ProcessPath!)) != "ARC Raiders", "its own exe: read from it");
    }

    [Test]
    public static void Product_name_is_preferred_over_process_name()
    {
        Assert.Equal("ELDEN RING™", ClipOrganizer.AppFolderName("eldenring", "ELDEN RING™", "ELDEN RING™"), "name");
    }

    [Test]
    public static void Generic_windows_product_falls_back_to_process_name()
    {
        Assert.Equal("notepad", ClipOrganizer.AppFolderName("notepad", "Microsoft® Windows® Operating System", "Untitled - Notepad"), "name");
    }

    [Test]
    public static void Shell_and_empty_map_to_desktop()
    {
        Assert.Equal("Desktop", ClipOrganizer.AppFolderName("explorer", "Microsoft® Windows® Operating System", "Program Manager"), "explorer");
        Assert.Equal("Desktop", ClipOrganizer.AppFolderName(null, null, null), "nothing");
        Assert.Equal("Desktop", ClipOrganizer.AppFolderName("", "", ""), "empty");
    }

    [Test]
    public static void Illegal_characters_are_replaced_and_trimmed()
    {
        Assert.Equal("My Game 2 Ultimate", ClipOrganizer.AppFolderName("game", "My:Game/2 *Ultimate*", null), "name");
        Assert.Equal("x", ClipOrganizer.AppFolderName("x", "\\/:*?\"<>|", null), "product with only illegal chars falls back");
    }

    [Test]
    public static void Long_names_are_cut_to_60_characters()
    {
        string name = ClipOrganizer.AppFolderName("game", new string('A', 100), null);
        Assert.Equal(60, name.Length, "length");
    }

    [Test]
    public static void Reserved_device_names_are_escaped()
    {
        Assert.Equal("con_", ClipOrganizer.AppFolderName("con", null, null), "name");
        Assert.Equal("NUL_", ClipOrganizer.AppFolderName("x", "NUL", null), "product name");
    }

    [Test]
    public static void Target_path_uses_app_folder_and_timestamp()
    {
        var t = new DateTime(2026, 10, 12, 18, 30, 5);
        Assert.Equal(@"D:\Clips\ELDEN RING\ELDEN RING 2026-10-12 18-30-05.mp4",
            ClipOrganizer.TargetPath(@"D:\Clips", "ELDEN RING", t, perAppFolders: true), "per app");
        Assert.Equal(@"D:\Clips\ELDEN RING 2026-10-12 18-30-05.mp4",
            ClipOrganizer.TargetPath(@"D:\Clips", "ELDEN RING", t, perAppFolders: false), "flat");
    }

    [Test]
    public static void A_recording_never_takes_the_name_of_a_file_already_there()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-freepath-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string target = Path.Combine(dir, "Desktop 2026-10-14 23-17-00.mp4");
            Assert.Equal(target, ClipOrganizer.FreePath(target), "free: as is");
            File.WriteAllBytes(target, [1]);
            Assert.Equal(Path.Combine(dir, "Desktop 2026-10-14 23-17-00 (2).mp4"), ClipOrganizer.FreePath(target), "taken by a clip saved that second");
            Assert.Equal(1, File.ReadAllBytes(target).Length, "the clip is untouched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void Move_adds_suffix_when_target_exists()
    {
        string dir = TempDir();
        string target = Path.Combine(dir, "App", "App 2026-10-12 18-30-05.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "old");
        string src = Path.Combine(dir, "raw.mp4");
        File.WriteAllText(src, "new");

        string final = ClipOrganizer.Move(src, target);
        Assert.Equal(Path.Combine(dir, "App", "App 2026-10-12 18-30-05 (2).mp4"), final, "final path");
        Assert.Equal("new", File.ReadAllText(final), "moved content");
        Assert.Equal("old", File.ReadAllText(target), "existing kept");
        Assert.True(!File.Exists(src), "source removed");
    }

    [Test]
    public static void Usable_preferred_folder_is_kept()
    {
        string folder = ClipOrganizer.ResolveFolder(@"E:\Clips", _ => true, out bool fellBack);
        Assert.Equal(@"E:\Clips", folder, "folder");
        Assert.Equal(false, fellBack, "fell back");
    }

    [Test]
    public static void Unusable_folder_falls_back_to_videos()
    {
        string videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kairos");
        string folder = ClipOrganizer.ResolveFolder(@"Q:\Gone", p => p == videos, out bool fellBack);
        Assert.Equal(videos, folder, "videos folder");
        Assert.Equal(true, fellBack, "fell back");

        string none = ClipOrganizer.ResolveFolder(@"Q:\Gone", _ => false, out bool fellBack2);
        Assert.Equal(videos, none, "nothing usable still names videos");
        Assert.Equal(true, fellBack2, "fell back");
    }

    [Test]
    public static void Janitor_deletes_oldest_until_under_limit()
    {
        const long GB = 1L << 30;
        var files = new[]
        {
            ("a.mp4", GB, new DateTime(2026, 1, 1)),
            ("c.mp4", GB, new DateTime(2026, 1, 3)),
            ("b.mp4", GB, new DateTime(2026, 1, 2)),
        };
        var victims = StorageJanitor.SelectForDeletion(files, 2 * GB);
        Assert.Equal(1, victims.Count, "count");
        Assert.Equal("a.mp4", victims[0], "oldest");
    }

    [Test]
    public static void Janitor_with_zero_limit_deletes_nothing()
    {
        var files = new[] { ("a.mp4", 10L << 30, new DateTime(2026, 1, 1)) };
        Assert.Equal(0, StorageJanitor.SelectForDeletion(files, 0).Count, "count");
    }

    [Test]
    public static void Janitor_never_deletes_the_newest_clip()
    {
        const long GB = 1L << 30;
        var files = new[] { ("old.mp4", GB, new DateTime(2026, 1, 1)), ("new.mp4", 5 * GB, new DateTime(2026, 1, 2)) };
        var victims = StorageJanitor.SelectForDeletion(files, GB);
        Assert.Equal(1, victims.Count, "count");
        Assert.Equal("old.mp4", victims[0], "only the old one");
    }
}
