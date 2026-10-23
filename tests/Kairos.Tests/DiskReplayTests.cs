using Kairos.Engine.Replay;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>
/// The replay on the disk (a 1.0.1 user: 2.1 GB of RAM at 1440p60 H.264 Ultra, 5 min): where its ring file goes, how big
/// it is, and that switching it rebuilds the pipeline (the buffer starts empty in its new place, no restart).
/// </summary>
public static class DiskReplayTests
{
    const string Local = @"C:\Users\x\AppData\Local";

    [Test]
    public static void The_ring_never_sits_in_the_clips_folder()
    {
        Assert.Equal(@"D:\.kairos-replay", DiskReplayPlace.Folder(null, @"D:\OneDrive\Kairos", "C:", Local), "the clips folder's drive, at its root");
        Assert.Equal(@"C:\Users\x\AppData\Local\Kairos\Replay", DiskReplayPlace.Folder(null, @"C:\Users\x\Videos\Kairos", "C:", Local),
            "the Windows drive: under the user's own folder");
        Assert.Equal(@"E:\.kairos-replay", DiskReplayPlace.Folder("E:", @"C:\Users\x\Videos\Kairos", "C:", Local), "a chosen drive");
        Assert.Equal(@"C:\Users\x\AppData\Local\Kairos\Replay", DiskReplayPlace.Folder("c:", @"D:\Clips", "C:", Local), "a chosen Windows drive");
    }

    [Test]
    public static void Ring_size_follows_the_bitrate_and_length()
    {
        long mib = 1024L * 1024;
        Assert.Equal((3219L * 5 / 4 + 64) * mib, DiskReplayPlace.RingBytes(90000, 300), "5 min at 90 Mb/s, with room");
        Assert.Equal(DiskReplayPlace.RingBytes(90000, 300) + 1024 * mib, DiskReplayPlace.NeededBytes(DiskReplayPlace.RingBytes(90000, 300)),
            "free space: the ring and 1 GB more");
        Assert.True(DiskReplayPlace.RingBytes(15000, 30) < DiskReplayPlace.RingBytes(15000, 300), "shorter replay, smaller file");
    }

    [Test]
    public static void Turning_the_disk_replay_on_rebuilds_and_is_kept()
    {
        var before = new AppSettings();
        before.Replay.OnDisk = false;
        var on = SettingsStore.Deserialize(SettingsStore.Serialize(before));
        on.Replay.OnDisk = true;
        Assert.True(SettingsDiff.NeedsRebuild(before, on), "on: the buffer starts again on the disk");
        var drive = SettingsStore.Deserialize(SettingsStore.Serialize(on));
        drive.Replay.DiskDrive = "E:";
        Assert.True(SettingsDiff.NeedsRebuild(on, drive), "another drive: a new ring");
        var back = SettingsStore.Deserialize(SettingsStore.Serialize(drive));
        Assert.True(back.Replay.OnDisk && back.Replay.DiskDrive == "E:", "saved and read back");
        Assert.True(new AppSettings().Replay.OnDisk, "on by default (chosen 2026-10-21)");
        // 1.0.1's settings have no such key: an update puts the replay on the disk too.
        var old = SettingsStore.Deserialize("{\"replay\":{\"enabled\":true,\"seconds\":300}}");
        Assert.True(old.Replay.OnDisk && old.Replay.Seconds == 300, "a 1.0.1 file: on the disk, its length kept");
        var chosen = SettingsStore.Deserialize("{\"replay\":{\"onDisk\":false}}");
        Assert.True(!chosen.Replay.OnDisk, "someone who turned it off keeps it off");
    }

    [Test]
    public static void Status_says_when_the_replay_could_not_use_the_disk()
    {
        Kairos.App.Text.Language = "en";
        var engine = new Kairos.Engine.EngineStatus(true, false, 60, 0, 0, 0, 300, 300, "x", 1920, 1080, 0, 0, @"D:\Clips", false, null, DiskFellBack: true);
        Assert.Equal(Kairos.App.Text.Get("status.diskReplayFailed"), Kairos.Ui.StatusLogic.CaptureWarning(engine), "the note");
        Assert.Equal("", Kairos.Ui.StatusLogic.CaptureWarning(engine with { DiskFellBack = false }), "none when the disk is used");
    }
}

public static class DiskReplayPageTests
{
    [Test]
    public static void The_switch_shows_its_drive_and_the_quality_line_counts_the_disk()
    {
        Kairos.App.Text.Language = "en";
        var s = new AppSettings();
        s.Normalize();
        s.Replay.OnDisk = false;
        var vm = new Kairos.Ui.SettingsViewModel();
        vm.SeedSettings(s);
        string ram = vm.EstimateText;
        Assert.True(!vm.ReplayOnDisk && !vm.DiskDriveIsHdd && vm.DiskDrives.Count == 0, "off: no drive list, no line");
        vm.ReplayOnDisk = true;
        Assert.True(vm.EstimateText.StartsWith("On the disk", StringComparison.Ordinal) && vm.EstimateText != ram, vm.EstimateText);
        Assert.True(vm.DiskDrives.Count > 0 && vm.DiskDrives.All(o => o.Label.Contains("free", StringComparison.Ordinal)), "each drive with its free space");
        Assert.True(vm.DiskDrives.Any(o => (string)o.Value == vm.DiskDrive), $"the clips folder's drive is listed ({vm.DiskDrive})");
        vm.DiskDrive = (string)vm.DiskDrives[^1].Value;
        Assert.True(vm.IsDirty, "a change to save");
    }

    [Test]
    public static void The_place_bar_names_the_drive_kind_and_its_two_sides_set_the_switch()
    {
        Kairos.App.Text.Language = "en";
        var s = new AppSettings();
        s.Normalize();
        s.Replay.OnDisk = false;
        Kairos.Ui.SettingsViewModel.FixedDrives = () => [new("C:", 50L << 30, Hdd: false), new("D:", 900L << 30, Hdd: true)];
        try
        {
            var vm = new Kairos.Ui.SettingsViewModel();
            vm.SeedSettings(s);
            Assert.Equal("SSD/HDD", vm.DiskKind, "RAM chosen: no drive asked, both kinds named");
            vm.ReplayOnDisk = true;
            vm.DiskDrive = "C:";
            Assert.Equal("SSD", vm.DiskKind, "an SSD");
            vm.DiskDrive = "D:";
            Assert.True(vm.DiskKind == "HDD" && vm.DiskDriveIsHdd, "a hard disk: named, and its line shows");
        }
        finally
        {
            Kairos.Ui.SettingsViewModel.FixedDrives = Kairos.App.DriveKinds.Fixed;
        }

        var eq = new Kairos.Ui.EqualsConverter();
        Assert.Equal(true, eq.ConvertBack(true, typeof(bool), "True", null!), "the disk side ticked: on the disk");
        Assert.Equal(false, eq.ConvertBack(true, typeof(bool), "False", null!), "the RAM side ticked: in RAM");
        Assert.Equal(true, eq.Convert(false, typeof(bool?), "False", null!), "in RAM: the RAM side shows ticked");
    }

    [Test]
    public static void This_pcs_drives_say_ssd_or_hdd()
    {
        var drives = Kairos.App.DriveKinds.Fixed();
        Assert.True(drives.Any(d => d.Name.Equals("C:", StringComparison.OrdinalIgnoreCase) && d.Hdd != null), "Windows's drive answers");
    }
}

public static class DiskReplayCleanupTests
{
    [Test]
    public static void Turning_it_off_gives_the_space_back_and_keeps_anything_else()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), ".kairos-replay");
        Directory.CreateDirectory(folder);
        foreach (string f in DiskReplayPlace.Files)
            File.WriteAllBytes(Path.Combine(folder, f), new byte[1024]);
        DiskReplayPlace.Remove(folder);
        Assert.True(!Directory.Exists(folder), "only rings in it: the folder goes too");

        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, DiskReplayPlace.FileName), new byte[1024]);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "the user's");
        DiskReplayPlace.Remove(folder);
        Assert.True(!File.Exists(Path.Combine(folder, DiskReplayPlace.FileName)) && File.Exists(Path.Combine(folder, "notes.txt")),
            "the ring goes, anything else stays");
        Assert.True(DiskReplayPlace.AllFolders(@"C:\Users\x\AppData\Local").First().EndsWith(@"Kairos\Replay", StringComparison.Ordinal), "the uninstall looks there");
    }
}

public static class ClipMoveTests
{
    [Test]
    public static void A_clip_held_open_for_a_moment_still_goes_into_its_app_folder()
    {
        // An Apex clip stayed in the clips folder as "Kairos ...mp4": something (an antivirus, a
        // thumbnail) had the new 1 GB file open as Kairos moved it, and one try was all it had.
        string root = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string clip = Path.Combine(root, "Kairos 2026-10-21 00-41-44.mp4");
        File.WriteAllBytes(clip, new byte[4096]);
        var held = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); // no delete share: a move fails
        var release = Task.Run(async () =>
        {
            await Task.Delay(500);
            held.Dispose();
        });
        string moved = Kairos.Engine.ClipOrganizer.Move(clip, Path.Combine(root, "Apex Legends", "Apex Legends 2026-10-21 00-41-44.mp4"));
        release.Wait();
        Assert.True(File.Exists(moved) && moved.Contains("Apex Legends", StringComparison.Ordinal) && !File.Exists(clip), moved);
    }
}

public static class ClipPlaceTests
{
    [Test]
    public static void A_clip_written_where_it_belongs_is_not_moved_again()
    {
        string target = @"D:\Clips\Apex Legends\Apex Legends 2026-10-21 00-41-44.mp4";
        Assert.True(Kairos.Engine.ClipOrganizer.IsAt(target, target), "its own place");
        Assert.True(Kairos.Engine.ClipOrganizer.IsAt(@"D:\Clips\Apex Legends\Apex Legends 2026-10-21 00-41-44 (2).mp4", target), "that name taken: (2)");
        Assert.True(!Kairos.Engine.ClipOrganizer.IsAt(@"D:\Clips\Kairos 2026-10-21 00-41-44.mp4", target), "a RAM clip in the clips folder: moved");
        Assert.True(!Kairos.Engine.ClipOrganizer.IsAt(@"D:\Clips\Kairos 2026-10-21 00-41-44.mp4", @"D:\Clips\Apex Legends 2026-10-21 00-41-44.mp4"),
            "one folder for all: the name still says the app");
        Assert.True(!Kairos.Engine.ClipOrganizer.IsAt(target, null), "no place asked");
    }
}
