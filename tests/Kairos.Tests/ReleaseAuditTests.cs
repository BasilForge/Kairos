using System.Globalization;
using System.IO.Pipes;
using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Regression tests for the problems found in the 2026-10-12 pre-release audit (see docs/audits/2026-10-12-release-audit-1.4.md).</summary>
public static class ReleaseAuditTests
{
    static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"))).FullName;

    static string MakeFile(string dir, string name, int kb, DateTime time)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[kb * 1024]);
        File.SetLastWriteTime(path, time);
        return path;
    }

    // A-01: the size limit must never touch videos Kairos did not save.
    [Test]
    public static void Size_limit_only_removes_clips_Kairos_saved()
    {
        string dir = TempDir();
        var index = new ClipIndex(Path.Combine(dir, "clips.txt"));
        var day = new DateTime(2026, 1, 1);
        string wedding = MakeFile(Path.Combine(dir, "Family"), "Wedding.mp4", 300, day);
        string obsReplay = MakeFile(dir, "Replay 2026-01-01 09-00-00.mp4", 300, day.AddHours(1));
        string oldClip = MakeFile(Path.Combine(dir, "Game"), "Game 2026-01-02 10-00-00.mp4", 300, day.AddDays(1));
        string newClip = MakeFile(Path.Combine(dir, "Game"), "Game 2026-01-03 10-00-00.mp4", 300, day.AddDays(2));
        index.Add(oldClip);
        index.Add(newClip);

        var recycled = new List<string>();
        int removed = StorageJanitor.Enforce(dir, limitBytes: 400 * 1024, index, recycled.Add);

        Assert.Equal(1, removed, "one clip over the limit");
        Assert.Equal(oldClip, recycled.Single(), "the oldest Kairos clip");
        Assert.True(File.Exists(wedding) && File.Exists(obsReplay), "other videos untouched");
    }

    [Test]
    public static void Clip_index_keeps_unicode_paths_and_forgets_missing_files()
    {
        string dir = TempDir();
        var index = new ClipIndex(Path.Combine(dir, "clips.txt"));
        string arabic = MakeFile(Path.Combine(dir, "لعبة 1"), "لعبة 1 2026-01-02 10-00-00.mp4", 1, DateTime.Now);
        string gone = MakeFile(dir, "Game 2026-01-01 10-00-00.mp4", 1, DateTime.Now);
        index.Add(arabic);
        index.Add(gone);
        File.Delete(gone);
        index.Prune();
        var known = index.Load();
        Assert.True(known.Contains(arabic.ToUpperInvariant()), "Arabic path kept, case-insensitive");
        Assert.Equal(1, known.Count, "missing file forgotten");
    }

    // A-02: a clip too big for the Recycle Bin must not vanish silently when the user asked to recycle it.
    [Test]
    public static void Recycling_asks_before_anything_would_be_deleted_for_good()
    {
        var user = RecycleBin.UserFlags;
        Assert.True(user.HasFlag(RecycleBin.Op.AllowUndo) && user.HasFlag(RecycleBin.Op.WantNukeWarning), "user recycling warns before a permanent delete");
        var background = RecycleBin.BackgroundFlags;
        Assert.True(background.HasFlag(RecycleBin.Op.AllowUndo) && background.HasFlag(RecycleBin.Op.NoErrorUi) && background.HasFlag(RecycleBin.Op.Silent),
            "background clean-up never shows a window while you play");
    }

    // A-03: clip and log names use the Gregorian calendar and Latin digits on every Windows region.
    [Test]
    public static void Clip_and_log_names_are_the_same_on_every_region()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var t = new DateTime(2026, 10, 12, 21, 9, 50);
            Assert.Equal(Path.Combine(@"C:\Clips", "Game 2026-10-12 21-09-50.mp4"), ClipOrganizer.TargetPath(@"C:\Clips", "Game", t, false), "clip");
            Assert.Equal("kairos-2026-10-12 21-09-50.log", ObsLog.FileName(t), "log");
            Assert.Equal("settings-2026-10-12 21-09-50.log", ObsLog.FileName(t, "settings"), "the settings window's own log");
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // A-05: a connection that never sends a command must not block the settings window from reaching the engine.
    [Test]
    public static void Silent_pipe_connections_are_dropped_so_others_are_served()
    {
        string name = "kairos-test-" + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(command => "ok:" + command, () => [], name, TimeSpan.FromMilliseconds(300));
        var silent = new List<NamedPipeClientStream>();
        for (int i = 0; i < 8; i++)
        {
            var c = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            c.Connect(2000);
            silent.Add(c);
        }
        Thread.Sleep(1200);
        string? reply = PipeClient.Send("status", 2000, name);
        silent.ForEach(c => c.Dispose());
        Assert.Equal("ok:status", reply, "served after the silent connections timed out");
    }

    // A-12: a relative clips folder would land inside the program folder and be lost on update.
    [Test]
    public static void Relative_clips_folder_falls_back_to_the_default()
    {
        var s = new AppSettings();
        s.Output.Folder = "clips";
        s.Normalize();
        Assert.Equal(OutputSettings.DefaultFolder, s.Output.Folder, "relative path replaced");
        s.Output.Folder = @"D:\Clips";
        s.Normalize();
        Assert.Equal(@"D:\Clips", s.Output.Folder, "absolute path kept");
    }

    // A-13: missing model files must be reported as such, not as "turn Smart Save on".
    [Test]
    public static void Smart_save_reports_missing_model_files()
    {
        string dir = TempDir();
        using var v = new Engine.Voice.VoiceTrigger(Path.Combine(dir, "no-models"), Path.Combine(dir, "voice"));
        v.SetMicConnected(true);
        var st = v.Status();
        Assert.Equal(false, st.ModelsFound, "models missing");
        Assert.Equal(false, st.Available, "not available");
    }

    // A-14: a settings file that is briefly locked must not be read as "no settings" (Apply would then overwrite it).
    [Test]
    public static void Settings_survive_a_briefly_locked_file()
    {
        string path = Path.Combine(TempDir(), "settings.json");
        var saved = new AppSettings();
        saved.Output.Folder = @"D:\Locked test";
        SettingsStore.Save(path, saved);
        var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(250); locked.Dispose(); });
        var loaded = SettingsStore.Load(path);
        Assert.Equal(@"D:\Locked test", loaded.Output.Folder, "real settings, not defaults");
    }

    // A-04: helpers start Explorer from the Windows folder, not from whatever the search path finds first.
    [Test]
    public static void Explorer_is_started_by_full_path()
    {
        Assert.True(Path.IsPathFullyQualified(ShellPaths.Explorer) && File.Exists(ShellPaths.Explorer), ShellPaths.Explorer);
    }

    // A-11: listening logs only near matches, so the log does not record every sentence the user says.
    [Test]
    public static void Voice_log_keeps_tests_and_near_matches_only()
    {
        Assert.Equal(true, Engine.Voice.VoiceTrigger.ShouldLog("testing", 0.3, 0.06), "testing always");
        Assert.Equal(true, Engine.Voice.VoiceTrigger.ShouldLog("idle", 0.08, 0.06), "near match");
        Assert.Equal(false, Engine.Voice.VoiceTrigger.ShouldLog("idle", 0.15, 0.06), "ordinary talk");
    }
}
