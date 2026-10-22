using System.IO.Compression;
using Kairos.App;
using Kairos.Engine.Share;
using Kairos.Engine.Updates;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>The security review's fixes (2026-10-21): what Kairos opens, sends and trusts.</summary>
public static class SecurityTests
{
    [Test]
    public static void A_post_address_opens_only_when_it_is_the_sites_own_https_page()
    {
        Assert.Equal("https://x.com/kairos/status/123", PublishRecipe.PostAddress(SharePlatform.X, "https://x.com/kairos/status/123"), "X's own post");
        Assert.Equal("https://mobile.twitter.com/a/status/1", PublishRecipe.PostAddress(SharePlatform.X, "https://mobile.twitter.com/a/status/1"), "its old name too");
        Assert.Equal("https://www.tiktok.com/@a/video/1", PublishRecipe.PostAddress(SharePlatform.TikTok, "https://www.tiktok.com/@a/video/1"), "TikTok's own");
        Assert.Equal("https://www.instagram.com/p/abc/", PublishRecipe.PostAddress(SharePlatform.Instagram, "https://www.instagram.com/p/abc/"), "Instagram's own");
        foreach (string? bad in new[]
                 {
                     null, "", "not a link", "http://x.com/a/status/1", "file:///C:/Windows/System32/calc.exe", @"\\server\share\run.exe",
                     "ms-settings:privacy", "javascript:alert(1)", "https://x.com.evil.example/a", "https://evil.example/x.com",
                     "https://user@x.com/a", "https://x.com:8443/a", "https://www.tiktok.com/@a/video/1",
                 })
            Assert.Equal(null, PublishRecipe.PostAddress(SharePlatform.X, bad), $"refused: {bad}");
    }

    [Test]
    public static void A_report_carries_no_user_folder_and_no_window_title()
    {
        string data = Folder(), desktop = Folder();
        const string profile = @"C:\Users\Basil";
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        // A log from before logs were cleaned (1.0.1), with the user's folder in both slashes.
        File.WriteAllText(Path.Combine(data, "logs", "kairos-old.log"), @"output folder C:\Users\Basil\Videos\Kairos" + "\n" + "module c:/users/basil/x.dll\n");
        File.WriteAllText(Path.Combine(data, "settings.json"), """
            {"capture":{"window":"Secret DM - Discord:Chrome_WidgetWin_1:Discord.exe","apps":["Bank#3A login:Chrome_WidgetWin_1:chrome.exe"]},
             "audio":{"sources":[{"target":"Private chat:Qt5QWindowIcon:app.exe","label":"Chat"}]},
             "output":{"folder":"C:\\Users\\Basil\\Videos\\Kairos"},"hotkeys":{"save":"Alt+F8"}}
            """);
        string zip = Report.Save(data, desktop, new DateTime(2026, 10, 21, 9, 0, 0),
            """{"lastClip":"C:\\Users\\Basil\\Videos\\Kairos\\a.mp4","capturing":"Secret DM - Discord:Chrome_WidgetWin_1:Discord.exe"}""", profile);
        using var archive = ZipFile.OpenRead(zip);
        string all = string.Join("\n", archive.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
        Assert.True(!all.Contains("Basil", StringComparison.OrdinalIgnoreCase), "no user name: " + all);
        Assert.True(!all.Contains("Secret") && !all.Contains("Bank") && !all.Contains("Private chat"), "no window title: " + all);
        Assert.True(all.Contains(":Chrome_WidgetWin_1:Discord.exe") && all.Contains(":Qt5QWindowIcon:app.exe"), "the app is still known");
        Assert.True(all.Contains("%USERPROFILE%") && all.Contains("Alt+F8") && all.Contains("\"Chat\""), "the rest as it was");
    }

    [Test]
    public static void Updates_come_only_from_a_folder_on_this_PC()
    {
        Assert.True(UpdateRules.IsLocalFolder(@"C:\feeds\kairos"), "a folder on a drive");
        foreach (string bad in new[] { @"\\server\share\feed", @"//server/share/feed", @"\\?\UNC\server\share", @"\\.\pipe\x", "feeds", @"C:feeds", "" })
            Assert.True(!UpdateRules.IsLocalFolder(bad), $"refused: {bad}");
        string file = Path.Combine(Folder(), "update-source.txt");
        File.WriteAllText(file, @"\\server\share\feed");
        Assert.Equal(null, UpdateRules.LocalSource(file), "a network folder is never asked for (no connection is made)");
    }

    [Test]
    public static void Winget_runs_from_its_own_folder_never_one_found_on_the_search_path()
    {
        Assert.True(Path.IsPathFullyQualified(ShellPaths.Winget), "a full path");
        Assert.True(ShellPaths.Winget.EndsWith(@"\Microsoft\WindowsApps\winget.exe", StringComparison.OrdinalIgnoreCase), ShellPaths.Winget);
    }

    [Test]
    public static void Each_Windows_session_has_its_own_pipe()
    {
        Assert.True(AppInfo.PipeFor(1) != AppInfo.PipeFor(2), "two people signed in at once each reach their own Kairos");
        Assert.Equal(AppInfo.PipeFor(System.Diagnostics.Process.GetCurrentProcess().SessionId), AppInfo.PipeName, "this session's");
    }

    [Test]
    public static void Signing_out_of_a_site_also_forgets_the_sign_in_service_used_for_it()
    {
        Assert.True(SiteAccounts.SignOutRemoves(SharePlatform.X, ".x.com"), "the site's own");
        foreach (string service in new[] { ".google.com", "accounts.google.com", ".youtube.com", "appleid.apple.com", ".facebook.com", "access.line.me", ".kakao.com" })
            Assert.True(SiteAccounts.SignOutRemoves(SharePlatform.X, service), $"{service}: signing in again asks again");
        Assert.True(!SiteAccounts.SignOutRemoves(SharePlatform.X, ".tiktok.com") && !SiteAccounts.SignOutRemoves(SharePlatform.X, ".instagram.com"),
            "the other sites stay signed in");
        Assert.True(!SiteAccounts.SignOutRemoves(SharePlatform.TikTok, ".x.com"), "X stays when TikTok goes");
        Assert.True(!SiteAccounts.SignOutRemoves(SharePlatform.X, "notgoogle.com"), "a lookalike is neither");
    }

    [Test]
    public static void Ffmpeg_reads_only_files_on_this_PC()
    {
        TestFfmpeg.Load();
        string clip = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share", "clip-h264.mp4");
        // FFmpeg's own rule for a file opened by its path (checked on 8.1.2): whatever it opens from inside the file is
        // limited to these, so a file in the clips folder can never make it go online. A newer FFmpeg must keep it.
        using (var input = new ClipTranscoder.Input(clip))
            Assert.True(WhitelistOf(input)?.Split(',').All(p => p is "file" or "crypto" or "data") == true, "local protocols only: " + WhitelistOf(input));
        // Text named like a clip can be a stream description (SDP): FFmpeg picks it by its content, whatever the name,
        // and would open network ports and send to the address it names. It is refused at once, offline.
        string fake = Path.Combine(Folder(), "clip.mp4");
        File.WriteAllText(fake, "v=0\r\no=- 0 0 IN IP4 127.0.0.1\r\ns=x\r\nc=IN IP4 127.0.0.1\r\nt=0 0\r\nm=video 5004 RTP/AVP 96\r\na=rtpmap:96 H264/90000\r\n");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool opened = true;
        try { ClipTranscoder.Probe(fake); } catch (ShareException) { opened = false; }
        Assert.True(!opened && clock.Elapsed < TimeSpan.FromSeconds(3), $"refused without going online ({clock.ElapsedMilliseconds} ms)");
    }

    [Test]
    public static void An_installed_Kairos_loads_FFmpeg_and_the_captions_engine_only_from_its_own_folders()
    {
        string installed = Folder(), build = Folder(), elsewhere = Folder();
        File.WriteAllText(Path.Combine(installed, "avformat-62.dll"), "");
        Assert.Equal(installed, ClipTranscoder.FfmpegFolder(installed, elsewhere), "installed: a folder named by KAIROS_FFMPEG or --captions is ignored");
        Assert.Equal(null, Kairos.Engine.Captions.CaptionProcess.RuntimeFolder(installed, elsewhere), "installed: the downloaded engine, never one named");
        Assert.Equal(elsewhere, ClipTranscoder.FfmpegFolder(build, elsewhere), "a development build (no FFmpeg of its own) may be pointed elsewhere");
        Assert.Equal(elsewhere, Kairos.Engine.Captions.CaptionProcess.RuntimeFolder(build, elsewhere), "and so may its captions engine (tests)");
        Assert.Equal(build, ClipTranscoder.FfmpegFolder(build, null), "nothing named: its own folder");
    }

    static unsafe string? WhitelistOf(ClipTranscoder.Input input) =>
        System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)input.Format->protocol_whitelist);

    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", "security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
