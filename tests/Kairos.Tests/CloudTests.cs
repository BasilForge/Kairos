using System.IO;
using Kairos.App;

namespace Kairos.Tests;

/// <summary>Saving clips into OneDrive, Dropbox or Google Drive, and never downloading them just to look at them.</summary>
public static class CloudTests
{
    sealed class FakeSources : ICloudSources
    {
        public Dictionary<string, string> Environment = [];
        public List<string> OneDriveFolders = [];
        public Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Root, string Label)> DriveList = [];
        public HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase);
        public string UserProfile => @"C:\Users\Alex";
        public string LocalAppData => @"C:\Users\Alex\AppData\Local";
        public string AppData => @"C:\Users\Alex\AppData\Roaming";
        public string? Env(string name) => Environment.GetValueOrDefault(name);
        public IEnumerable<string> OneDriveUserFolders() => OneDriveFolders;
        public string? ReadFile(string path) => Files.GetValueOrDefault(path);
        public IEnumerable<(string Root, string Label)> Drives() => DriveList;
        public bool DirectoryExists(string path) => Folders.Contains(path.TrimEnd('\\'));
    }

    [Test]
    public static void Providers_are_found_from_what_they_leave_on_the_PC()
    {
        var s = new FakeSources();
        s.Environment["OneDrive"] = @"C:\Users\Alex\OneDrive";
        s.Environment["OneDriveConsumer"] = @"C:\Users\Alex\OneDrive";
        s.OneDriveFolders.Add(@"C:\Users\Alex\OneDrive\");
        s.Files[@"C:\Users\Alex\AppData\Local\Dropbox\info.json"] = """{"personal": {"path": "D:\\Dropbox", "host": 1}}""";
        s.DriveList.Add((@"G:\", "Google Drive"));
        s.Folders.UnionWith([@"C:\Users\Alex\OneDrive", @"D:\Dropbox", @"G:\My Drive"]);
        var found = CloudFolders.Find(s);
        Assert.Equal(@"OneDrive C:\Users\Alex\OneDrive|Dropbox D:\Dropbox|GoogleDrive G:\My Drive",
            string.Join("|", found.Select(f => $"{f.Kind} {f.Path}")), "one OneDrive, Dropbox, Google Drive");
        Assert.True(found.Single(f => f.Kind == CloudKind.GoogleDrive).Streams && !found[0].Streams, "Google Drive streams by itself");
    }

    [Test]
    public static void Nothing_is_offered_that_is_not_there()
    {
        var s = new FakeSources();
        s.Environment["OneDrive"] = @"C:\Users\Alex\OneDrive"; // left behind, the folder is gone
        s.Files[@"C:\Users\Alex\AppData\Roaming\Dropbox\info.json"] = "not json";
        s.DriveList.Add((@"E:\", "Backup"));
        Assert.Equal(0, CloudFolders.Find(s).Count, "nothing");
        s.Folders.Add(@"C:\Users\Alex\My Drive");
        Assert.Equal(@"C:\Users\Alex\My Drive", CloudFolders.Find(s).Single().Path, "Google Drive in mirror mode");
    }

    [Test]
    public static void A_folder_is_in_the_cloud_only_inside_a_providers_folder()
    {
        var found = new List<CloudFolder> { new(CloudKind.OneDrive, @"C:\Users\Alex\OneDrive") };
        Assert.Equal(CloudKind.OneDrive, CloudFolders.Under(@"C:\Users\Alex\OneDrive\Kairos", found)?.Kind, "inside");
        Assert.Equal(CloudKind.OneDrive, CloudFolders.Under(@"c:\users\alex\onedrive", found)?.Kind, "the folder itself, any case");
        Assert.Equal(null, CloudFolders.Under(@"C:\Users\Alex\OneDriveBackup\Kairos", found), "a name that only starts the same");
        Assert.Equal(null, CloudFolders.Under(@"D:\Clips", found), "elsewhere");
    }

    [Test]
    public static void Online_only_files_are_not_local_and_freeing_one_unpins_it()
    {
        Assert.True(CloudFiles.IsLocal(FileAttributes.Archive), "a normal file");
        Assert.True(!CloudFiles.IsLocal(FileAttributes.Archive | (FileAttributes)0x400000), "recall on data access: online only");
        Assert.True(!CloudFiles.IsLocal(FileAttributes.Offline), "offline");
        var freed = CloudFiles.FreedLocal(FileAttributes.Archive | (FileAttributes)0x80000);
        Assert.True((freed & (FileAttributes)0x100000) != 0 && (freed & (FileAttributes)0x80000) == 0, "unpinned, not pinned");
        CloudFiles.FreeLocalCopy(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4")); // gone: nothing thrown
    }

    [Test]
    public static void In_a_cloud_folder_the_limit_counts_and_frees_only_local_copies()
    {
        var t0 = new DateTime(2026, 10, 1);
        const long Gb = 1L << 30;
        const FileAttributes Online = FileAttributes.Archive | (FileAttributes)0x400000;
        var files = new List<(string, long, DateTime, FileAttributes)>
        {
            ("a.mp4", 4 * Gb, t0, FileAttributes.Archive),
            ("b.mp4", 4 * Gb, t0.AddDays(1), Online),
            ("c.mp4", 2 * Gb, t0.AddDays(2), FileAttributes.Archive),
        };
        Assert.Equal("a.mp4", string.Join(",", StorageJanitor.Choose(files, 3 * Gb, cloud: true)), "the oldest local copy; the online one and the newest stay");
        Assert.Equal("a.mp4,b.mp4", string.Join(",", StorageJanitor.Choose(files, 3 * Gb, cloud: false)), "elsewhere: as before, every file counts");
    }

    [Test]
    public static void An_online_only_file_shows_only_a_cached_thumbnail()
    {
        Assert.Equal(0x1, ShellImage.Flags(FileAttributes.Archive), "a local file: made if needed");
        Assert.Equal(0x1 | 0x10, ShellImage.Flags(FileAttributes.Archive | (FileAttributes)0x400000), "online only: the cache, never a download");
    }

    [Test]
    public static void Freeing_local_copies_is_on_for_new_installs_only()
    {
        Assert.True(!new Kairos.Settings.AppSettings().Output.FreeLocalCopies, "an existing install's settings: off as before");
        string missing = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), "settings.json");
        Assert.True(Kairos.Settings.SettingsStore.Load(missing).Output.FreeLocalCopies, "a new install: on");
    }

    [Test]
    public static void A_folder_limit_never_removes_files_from_google_drive()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var index = new ClipIndex(Path.Combine(dir, "index.txt"));
        for (int i = 0; i < 3; i++)
        {
            string clip = Path.Combine(dir, $"clip {i}.mp4");
            File.WriteAllBytes(clip, new byte[300 * 1024]);
            File.SetLastWriteTime(clip, new DateTime(2026, 10, 1).AddDays(i));
            index.Add(clip);
        }
        var recycled = new List<string>();
        Assert.Equal(0, StorageJanitor.Enforce(dir, 400 * 1024, index, recycled.Add, new CloudFolder(CloudKind.GoogleDrive, dir)),
            "Google Drive: removing a file removes it from the cloud too, so nothing goes");
        Assert.Equal(0, recycled.Count, "nothing recycled");
        Assert.True(StorageJanitor.Enforce(dir, 400 * 1024, index, recycled.Add) > 0 && recycled.Count > 0, "an ordinary folder: the oldest go");
    }

    [Test]
    public static void A_pc_without_a_cloud_app_is_told_how_to_get_one()
    {
        Kairos.App.Text.Language = "ar";
        var s = new Kairos.Settings.AppSettings();
        s.Normalize();
        Kairos.Ui.SettingsViewModel.Clouds = () => [];
        try
        {
            var vm = new Kairos.Ui.SettingsViewModel();
            vm.SeedSettings(s);
            Assert.True(!vm.HasCloud && vm.CloudChoices.Count == 0, "no buttons: the line saying how to get one shows instead");
            foreach (var language in Kairos.App.Text.Languages)
            {
                Kairos.App.Text.Language = language.Code;
                string hint = Kairos.App.Text.Get("general.cloudNone");
                Assert.True(hint.Contains("OneDrive") && hint.Contains("Google Drive") && hint.Contains("Dropbox"), language.Code + ": the three named");
            }
        }
        finally
        {
            Kairos.Ui.SettingsViewModel.Clouds = Kairos.App.CloudFolders.OnThisPc;
            Kairos.App.Text.Language = "en";
        }
    }
}
