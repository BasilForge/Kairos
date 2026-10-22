using Kairos.App;
using Kairos.Engine.Share;
using Kairos.Settings;
using Kairos.Ui;

namespace Kairos.Tests;

public static class ShareViewModelTests
{
    const string Clip = @"D:\Clips\ELDEN RING\ELDEN RING 2026-10-12 21-04-55.mp4";

    sealed class FakeHost : IShareHost
    {
        public MediaInfo Info = new(300, 2560, 1440, 60, "av1", "aac", 192_000, 3, Bytes: 333_000_000);
        public readonly List<(string Source, string Target, double Start, double End)> Cuts = [];
        public Func<SharePlan, ShareResult?>? OnTranscode;
        public readonly List<(string Source, string Target, SharePlan Plan, long MaxBytes)> Transcodes = [];
        public (string? File, string? Text)? Clipboard;
        public readonly List<string> Launched = [];
        public readonly List<(SharePlatform Platform, string File, string Caption)> Sites = [];
        public readonly List<SharePlatform> Revealed = [];
        public PublishOutcome Outcome = new(PublishResult.Posted, "https://x.com/kairos/status/1");
        public Func<SharePlatform, PublishOutcome>? OutcomeFor;
        public Action<SharePlatform>? OnPublish;
        public string[] Statuses = ["share.pub.opening", "share.pub.uploading", "share.pub.posting"];
        public List<string> SeenStatus = [];
        public ShareSettings? Remembered;

        public int Probes;
        public MediaInfo Probe(string clip)
        {
            Probes++;
            return Info;
        }
        public SharePicture? Picture = new(null, 2560, 1440, 2_000_000);
        public SharePicture? ReadPicture(string path) => Picture;
        public readonly List<(string Source, string Target, long MaxBytes)> Jpegs = [];
        public ShareResult MakeJpeg(string source, string target, long maxBytes)
        {
            Jpegs.Add((source, target, maxBytes));
            return new ShareResult(target, maxBytes - 1, "jpeg");
        }
        public int Jobs() => Jpegs.Count;
        public readonly List<string> CopiedPictures = [];
        public void CopyPicture(string path) => CopiedPictures.Add(path);

        public ShareResult Transcode(string source, string target, SharePlan plan, long maxBytes, IProgress<double> progress, CancellationToken ct)
        {
            Transcodes.Add((source, target, plan, maxBytes));
            if (WriteTargets)
                File.WriteAllText(target, "copy");
            progress.Report(0.5);
            return OnTranscode?.Invoke(plan) ?? new ShareResult(target, 18_200_000, "h264_nvenc");
        }

        public ShareResult Cut(string source, string target, double start, double end, IProgress<double> progress, CancellationToken ct)
        {
            Cuts.Add((source, target, start, end));
            progress.Report(0.5);
            return new ShareResult(target, 33_000_000, "copy");
        }

        public void CopyToClipboard(string? file, string? text) => Clipboard = (file, text);
        public void Launch(string target) => Launched.Add(target);
        public Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct)
        {
            Sites.Add((platform, file, caption));
            foreach (string key in Statuses)
            {
                status(key);
                if (key == "share.pub.uploading")
                    OnPublish?.Invoke(platform);
            }
            return Task.FromResult(ct.IsCancellationRequested ? new PublishOutcome(PublishResult.Cancelled) : OutcomeFor?.Invoke(platform) ?? Outcome);
        }
        public void RevealSite(SharePlatform platform) => Revealed.Add(platform);
        /// <summary>Each preview picture asked for: exact, or the nearest key frame.</summary>
        public readonly List<bool> PreviewExact = [];
        public void GrabFrames(string clip, double[] times, int width, bool exact, Action<int, System.Windows.Media.Imaging.BitmapSource?> done)
        {
            if (times.Length == 1)
                PreviewExact.Add(exact);
        }
        public void ReleaseFrames() { }
        public readonly List<(double From, double To)> Plays = [];
        public int Stops;
        public Action<double>? Position;
        public Action<string?>? Ended;
        public System.Windows.Media.Imaging.BitmapSource? Play(string clip, double from, double to, int width, int height,
            Action<double> position, Action<string?> ended)
        {
            Plays.Add((from, to));
            Position = position;
            Ended = ended;
            return null;
        }
        public void StopPlaying() => Stops++;
        public double Volume = -1;
        public void SetPlaybackVolume(double volume) => Volume = volume;
        readonly List<Action> _later = [];
        public void After(TimeSpan delay, Action action) => _later.Add(action);
        /// <summary>Runs what was put off (the "copied" check going away).</summary>
        public void RunLater()
        {
            var due = _later.ToList();
            _later.Clear();
            due.ForEach(a => a());
        }
        public void ShowInFolder(string path) { }
        public void Remember(ShareSettings prefs) => Remembered = prefs;
        public void Post(Action action) => action();
        public void Run(Action work) => work();
        public string Folder => @"C:\Share";
        public string Shared = @"D:\Clips\Shared";
        public string SharedFolder => Shared;
        /// <summary>Transcodes write their target (a copy's file on disk, for what reads it back).</summary>
        public bool WriteTargets;
    }

    /// <summary>Presses Copy on a video and picks <paramref name="mb"/> (the size kept from last time if none).</summary>
    static void Copy(ShareViewModel vm, int? mb = null)
    {
        int asked = 0;
        void Ask() => asked++;
        vm.CopySizeAsked += Ask;
        vm.CopyCommand.Execute(null);
        vm.CopySizeAsked -= Ask;
        Assert.Equal(1, asked, "Copy asks the size");
        vm.CopyAtCommand.Execute(mb ?? vm.CopyMb);
    }

    static (ShareViewModel Vm, FakeHost Host) Open(Action<ShareSettings>? prefs = null, MediaInfo? info = null, SharePlatform? platform = null)
    {
        Text.Language = "en";
        var host = new FakeHost();
        if (info != null)
            host.Info = info;
        var saved = new ShareSettings();
        prefs?.Invoke(saved);
        var vm = new ShareViewModel(host, () => saved);
        vm.Open(Clip);
        if (platform != null)
            vm.Platform = platform;
        return (vm, host);
    }

    /// <summary>Opened, with the cut moved to the last 30 seconds of the 5:00 clip.</summary>
    static (ShareViewModel Vm, FakeHost Host) OpenCut()
    {
        var (vm, host) = Open(platform: SharePlatform.TikTok);
        vm.TrimStart = 270;
        return (vm, host);
    }

    [Test]
    public static void Opening_a_clip_picks_no_platform_and_offers_nothing_to_post_until_one_is_chosen()
    {
        var (vm, _) = Open(p => p.Tags = "#clips");
        Assert.True(vm.IsOpen && vm.IsChoosing, "open, choosing");
        Assert.Equal(null, vm.Platform, "nothing picked");
        Assert.True(!vm.PrepareCommand.CanExecute(null) && vm.PrepareLabel == "Choose where to share", vm.PrepareLabel);
        Assert.True(!vm.HasPlatform && vm.SizeEstimate == "", "no size before a platform");
        Assert.Equal((0.0, 300.0), (vm.TrimStart, vm.TrimEnd), "the whole clip");
        vm.Platform = SharePlatform.X;
        Assert.True(vm.PrepareCommand.CanExecute(null) && vm.HasPlatform, "ready");
        Assert.Equal((160.0, 300.0), (vm.TrimStart, vm.TrimEnd), "X's 2:20");
        Assert.Equal("#EldenRing #clips", vm.Caption, "caption");
        Assert.Equal("ELDEN RING", vm.Game, "game");
    }

    [Test]
    public static void Everywhere_posts_to_the_three_sites_with_one_caption_and_one_file_per_size()
    {
        var (vm, host) = Open(platform: SharePlatform.All);
        Assert.Equal("Post everywhere", vm.PrepareLabel, "button");
        Assert.Equal((160.0, 300.0), (vm.TrimStart, vm.TrimEnd), "the strictest limit: X's 2:20");
        vm.Caption = "Clutch #EldenRing";
        vm.PrepareCommand.Execute(null);
        Assert.Equal("1920x1080 2560x1440", string.Join(" ", host.Transcodes.Select(t => $"{t.Plan.Width}x{t.Plan.Height}")),
            "X at 1080p, TikTok and Instagram share the clip's own size");
        Assert.Equal("X TikTok Instagram", string.Join(" ", host.Sites.Select(s => s.Platform)), "all three posted");
        Assert.True(host.Sites.All(s => s.Caption == "Clutch #EldenRing"), "one caption");
        Assert.Equal(host.Sites[1].File, host.Sites[2].File, "TikTok and Instagram post the same file");
        Assert.True(vm.IsDone && vm.IsMulti, "done");
        Assert.Equal("X TikTok Instagram", string.Join(" ", vm.Jobs.Select(j => j.Name)), "a row per site");
        Assert.True(vm.Jobs.All(j => j.IsPosted && j.StatusText == "Posted"), string.Join(", ", vm.Jobs.Select(j => j.StatusText)));
        vm.Jobs[0].OpenCommand.Execute(null);
        Assert.Equal("https://x.com/kairos/status/1", host.Launched.Single(), "opens the X post");
    }

    [Test]
    public static void Everywhere_goes_on_when_one_site_needs_the_user_or_fails()
    {
        var (vm, host) = Open(platform: SharePlatform.All);
        host.OutcomeFor = p => p == SharePlatform.Instagram ? new PublishOutcome(PublishResult.NeedsUser, Detail: "share") : host.Outcome;
        host.OnTranscode = plan => plan.Width == 1920 ? throw new ShareException("No H.264 encoder could be started.") : null;
        vm.PrepareCommand.Execute(null);
        var (x, tiktok, instagram) = (vm.Jobs[0], vm.Jobs[1], vm.Jobs[2]);
        Assert.True(x.IsFailed && x.Detail.Contains("H.264"), "X failed: " + x.Detail);
        Assert.True(tiktok.IsPosted, "TikTok posted anyway");
        Assert.True(instagram.NeedsUser && instagram.CanOpen, "Instagram waits for the user");
        instagram.OpenCommand.Execute(null);
        Assert.Equal(SharePlatform.Instagram, host.Revealed.Single(), "its window comes up");
        Assert.True(vm.IsDone, "done with what could be done");
    }

    [Test]
    public static void Everywhere_shows_each_sites_progress_while_it_posts()
    {
        var (vm, host) = Open(platform: SharePlatform.All);
        var seen = new List<string>();
        host.OnPublish = p => seen.Add($"{p}: " + string.Join(" | ", vm.Jobs.Select(j => j.StatusText)));
        vm.PrepareCommand.Execute(null);
        Assert.True(seen.Count == 3 && seen[0].StartsWith("X: Uploading"), string.Join(" / ", seen));
    }

    [Test]
    public static void Everywhere_with_premium_takes_instagrams_three_minutes_and_one_9_16_choice()
    {
        var (vm, host) = Open(p => p.XPremium = true, platform: SharePlatform.All);
        Assert.Equal((120.0, 300.0), (vm.TrimStart, vm.TrimEnd), "Instagram's 3:00 is now the strictest");
        vm.Vertical = true;
        vm.PrepareCommand.Execute(null);
        Assert.True(host.Transcodes.All(t => t.Plan.Crop != null), "9:16 everywhere");
        Assert.Equal("All", string.Join(" ", host.Remembered!.VerticalFor), "remembered for everywhere");
    }

    [Test]
    public static void The_cut_opens_as_long_as_the_platform_takes_and_follows_a_platform_change_until_moved()
    {
        var (vm, _) = Open(platform: SharePlatform.X);
        Assert.Equal((160.0, 300.0), (vm.TrimStart, vm.TrimEnd), "X: the last 2:20");
        vm.Platform = SharePlatform.TikTok;
        Assert.Equal((0.0, 300.0), (vm.TrimStart, vm.TrimEnd), "TikTok: the whole clip");
        vm.Platform = SharePlatform.Instagram;
        Assert.Equal((120.0, 300.0), (vm.TrimStart, vm.TrimEnd), "Instagram: the last 3:00");
        vm.TrimStart = 200;
        vm.Platform = SharePlatform.TikTok;
        Assert.Equal((200.0, 300.0), (vm.TrimStart, vm.TrimEnd), "a cut the user moved stays");
        var (shortVm, _) = Open(info: new MediaInfo(20, 1920, 1080, 60, "h264", "aac", 160_000, 1), platform: SharePlatform.X);
        Assert.Equal((0.0, 20.0), (shortVm.TrimStart, shortVm.TrimEnd), "a short clip whole");
    }

    [Test]
    public static void Dragging_the_trim_handles_moves_the_cut_and_the_plan_follows()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        vm.TrimStart = 200;
        vm.TrimEnd = 212.5;
        Assert.Equal("3:20.0 – 3:32.5", vm.TrimRange, "the cut");
        vm.PrepareCommand.Execute(null);
        var plan = host.Transcodes.Single().Plan;
        Assert.Equal((200.0, 12.5), (plan.Start, plan.Duration), "the trimmed part is what gets converted");
    }

    [Test]
    public static void X_free_accounts_get_two_twenty_and_premium_lifts_it()
    {
        var (vm, _) = Open();
        Assert.Equal((0.0, 300.0), (vm.TrimStart, vm.TrimEnd), "the whole clip before a platform is picked");
        vm.Platform = SharePlatform.X;
        Assert.Equal((160.0, 300.0), (vm.TrimStart, vm.TrimEnd), "X free: last 2:20");
        vm.TrimStart = 0;
        Assert.Equal((160.0, 300.0), (vm.TrimStart, vm.TrimEnd), "the handle stops at the limit");
        var (premium, _) = Open(p => { p.XPremium = true; }, platform: SharePlatform.X);
        Assert.Equal((0.0, 300.0), (premium.TrimStart, premium.TrimEnd), "Premium: all five minutes");
    }

    [Test]
    public static void Copy_puts_the_whole_clip_itself_on_the_clipboard_at_once_and_says_copied_for_a_moment()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        vm.TrimStart = 0;
        vm.Platform = SharePlatform.TikTok;
        vm.TrimStart = 0;
        Copy(vm);
        Assert.Equal((0, 0), (host.Transcodes.Count, host.Cuts.Count), "nothing converted or cut");
        Assert.Equal((Clip, (string?)null), host.Clipboard!.Value, "the clip itself on the clipboard");
        Assert.True(vm.IsCopied && vm.IsChoosing, "copied, and still choosing");
        Assert.Equal(SharePlatform.TikTok, vm.Platform, "the picked platform stays picked");
        host.RunLater();
        Assert.True(!vm.IsCopied, "the check goes away after a moment");
    }

    [Test]
    public static void Copy_of_a_cut_is_cut_without_converting()
    {
        var (vm, host) = OpenCut();
        Copy(vm);
        Assert.Equal(0, host.Transcodes.Count, "not converted");
        var cut = host.Cuts.Single();
        Assert.Equal((Clip, 270.0, 300.0), (cut.Source, cut.Start, cut.End), "the part");
        Assert.Equal(@"D:\Clips\Shared\ELDEN RING 2026-10-12 21-04-55 (4.30-5.00).mp4", cut.Target, "kept in the clips folder's Shared, named by its part");
        Assert.Equal((cut.Target, (string?)null), host.Clipboard!.Value, "on the clipboard");
        Assert.True(vm.IsCopied, "copied");
    }

    [Test]
    public static void Copy_works_without_picking_a_platform()
    {
        var (vm, host) = Open();
        Assert.True(vm.CopyCommand.CanExecute(null), "available at once");
        Copy(vm);
        Assert.Equal(Clip, host.Clipboard!.Value.File, "copied");
        Assert.Equal(null, vm.Platform, "still nothing picked");
    }

    [Test]
    public static void TikTok_gets_a_vertical_cut_and_the_caption_rides_along_with_the_file()
    {
        var (vm, host) = Open(p => { p.VerticalFor = [SharePlatform.TikTok]; p.CropPosition = 0.25; }, platform: SharePlatform.TikTok);
        vm.Caption = "Insane #EldenRing #fromsoft";
        vm.PrepareCommand.Execute(null);
        var plan = host.Transcodes.Single().Plan;
        Assert.True(plan.Crop != null && plan.Height > plan.Width, $"vertical {plan.Width}x{plan.Height}");
        Assert.Equal((SharePlatform.TikTok, host.Transcodes[0].Target, "Insane #EldenRing #fromsoft"), host.Sites.Single(), "published by Kairos");
        Assert.Equal(0, host.Launched.Count, "not the outside browser");
        Assert.Equal(null, host.Clipboard, "nothing to paste");
        Assert.Equal("#fromsoft", host.Remembered!.Tags, "user's own tags remembered");
        Assert.Equal(0.25, host.Remembered!.CropPosition, "crop remembered");
    }

    [Test]
    public static void X_gets_the_caption_and_at_most_two_minutes_twenty()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        vm.PrepareCommand.Execute(null);
        Assert.Equal(140.0, host.Transcodes.Single().Plan.Duration, "last 2:20");
        Assert.Equal((SharePlatform.X, host.Transcodes[0].Target, "#EldenRing #gaming"), host.Sites.Single(), "X published by Kairos with the caption");
    }

    [Test]
    public static void A_failure_explains_itself_and_try_again_returns_to_the_choices()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        host.OnTranscode = _ => throw new ShareException("No H.264 encoder could be started.");
        vm.PrepareCommand.Execute(null);
        Assert.True(vm.IsFailed && vm.ErrorDetail.Contains("H.264"), vm.ErrorDetail);
        Assert.Equal(null, host.Clipboard, "clipboard untouched");
        vm.RetryCommand.Execute(null);
        Assert.True(vm.IsChoosing, "back to choosing");
    }

    [Test]
    public static void Cancelling_returns_to_the_choices_without_an_error()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        host.OnTranscode = _ => throw new OperationCanceledException();
        vm.PrepareCommand.Execute(null);
        Assert.True(vm.IsChoosing && !vm.IsFailed, "choosing again");
    }

    [Test]
    public static void After_sharing_picking_another_platform_starts_a_new_share()
    {
        var (vm, host) = Open(platform: SharePlatform.TikTok);
        vm.PrepareCommand.Execute(null);
        Assert.True(vm.IsDone && vm.CanEdit, "choices stay usable when done");
        vm.Platform = SharePlatform.X;
        Assert.True(vm.IsChoosing, "back to choosing");
        vm.PrepareCommand.Execute(null);
        Assert.Equal(2, host.Transcodes.Count, "second share");
    }

    [Test]
    public static void An_unreadable_clip_hides_the_choices_and_offers_to_try_again()
    {
        Text.Language = "en";
        var host = new FakeHost();
        var vm = new ShareViewModel(new ThrowingProbe(host), () => new ShareSettings());
        vm.Open(Clip);
        Assert.True(vm.IsFailed && !vm.HasClip, "failed without a clip");
        Assert.Equal("Try again", vm.RetryLabel, "label");
        Assert.True(vm.ErrorDetail.Contains("not found"), vm.ErrorDetail);
    }

    const string Shot = @"D:\Clips\ELDEN RING\ELDEN RING 2026-10-15 21-30-00.png";

    static (ShareViewModel Vm, FakeHost Host) OpenShot(long bytes = 2_000_000, SharePlatform? platform = null)
    {
        Text.Language = "en";
        var host = new FakeHost { Picture = new SharePicture(null, 2560, 1440, bytes) };
        var vm = new ShareViewModel(host, () => new ShareSettings());
        vm.Open(Shot);
        if (platform != null)
            vm.Platform = platform;
        return (vm, host);
    }

    [Test]
    public static void A_screenshot_opens_as_a_picture_without_trim_or_player()
    {
        var (vm, host) = OpenShot();
        Assert.True(vm.IsPicture && vm.HasClip && !vm.HasVideo, "a picture to share, not a video");
        Assert.Equal(0, host.Probes, "no video reading");
        Assert.Near(16.0 / 9, vm.PreviewAspect, 0.001, "shown in its own shape");
        Assert.True(!vm.PlayCommand.CanExecute(null), "nothing to play");
        Assert.True(vm.IsChoosing, "ready to choose where");
        Assert.Equal("Share screenshot", vm.Title, "the panel says what it shares");
    }

    [Test]
    public static void A_screenshot_is_posted_as_it_is_when_the_site_takes_its_size()
    {
        var (vm, host) = OpenShot(bytes: 2_000_000, platform: SharePlatform.X);
        vm.PrepareCommand.Execute(null);
        Assert.Equal(0, host.Jobs(), "no copy made");
        Assert.Equal((SharePlatform.X, Shot), (host.Sites.Single().Platform, host.Sites.Single().File), "the PNG itself goes to X");
        Assert.True(vm.IsDone, "posted");
    }

    [Test]
    public static void A_screenshot_over_a_sites_limit_goes_as_a_jpeg_copy()
    {
        var (vm, host) = OpenShot(bytes: 6_000_000, platform: SharePlatform.X);
        vm.PrepareCommand.Execute(null);
        Assert.Equal(5_000_000L, host.Jpegs.Single().MaxBytes, "X takes pictures up to 5 MB");
        Assert.True(host.Jpegs.Single().Target.EndsWith(".jpg", StringComparison.Ordinal), host.Jpegs.Single().Target);
        Assert.Equal(host.Jpegs.Single().Target, host.Sites.Single().File, "the copy is what is posted");
    }

    [Test]
    public static void A_screenshot_goes_everywhere_each_site_with_what_it_takes()
    {
        var (vm, host) = OpenShot(bytes: 6_000_000, platform: SharePlatform.All);
        vm.PrepareCommand.Execute(null);
        var files = host.Sites.ToDictionary(s => s.Platform, s => s.File);
        Assert.Equal(3, files.Count, "X, TikTok and Instagram");
        Assert.True(files[SharePlatform.X].EndsWith(".jpg", StringComparison.Ordinal), "X: a JPEG under 5 MB");
        Assert.Equal(Shot, files[SharePlatform.Instagram], "Instagram takes 6 MB as it is");
        Assert.Equal(Shot, files[SharePlatform.TikTok], "TikTok takes up to 50 MB a photo");
        Assert.True(vm.IsDone, "all done");
    }

    [Test]
    public static void Copying_a_screenshot_puts_the_picture_itself_on_the_clipboard()
    {
        var (vm, host) = OpenShot();
        int asked = 0;
        vm.CopySizeAsked += () => asked++;
        vm.CopyCommand.Execute(null);
        Assert.Equal(0, asked, "a screenshot is copied as it is: no size asked");
        Assert.Equal(Shot, host.CopiedPictures.Single(), "image and file, at once");
        Assert.Equal(0, host.Cuts.Count + host.Transcodes.Count, "nothing to cut or convert");
        Assert.True(vm.IsCopied, "the check shows");
    }

    sealed class ThrowingProbe(FakeHost inner) : IShareHost
    {
        public string SharedFolder => inner.SharedFolder;
        public MediaInfo Probe(string clip) => throw new ShareException("The clip was not found: " + clip);
        public SharePicture? ReadPicture(string path) => inner.ReadPicture(path);
        public ShareResult MakeJpeg(string source, string target, long maxBytes) => inner.MakeJpeg(source, target, maxBytes);
        public void CopyPicture(string path) => inner.CopyPicture(path);
        public ShareResult Transcode(string source, string target, SharePlan plan, long maxBytes, IProgress<double> progress, CancellationToken ct) =>
            inner.Transcode(source, target, plan, maxBytes, progress, ct);
        public ShareResult Cut(string source, string target, double start, double end, IProgress<double> progress, CancellationToken ct) =>
            inner.Cut(source, target, start, end, progress, ct);
        public void CopyToClipboard(string? file, string? text) => inner.CopyToClipboard(file, text);
        public void Launch(string target) => inner.Launch(target);
        public Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct) =>
            inner.Publish(platform, file, caption, status, ct);
        public void RevealSite(SharePlatform platform) { }
        public void GrabFrames(string clip, double[] times, int width, bool exact, Action<int, System.Windows.Media.Imaging.BitmapSource?> done) { }
        public void ReleaseFrames() { }
        public System.Windows.Media.Imaging.BitmapSource? Play(string clip, double from, double to, int width, int height,
            Action<double> position, Action<string?> ended) => null;
        public void StopPlaying() { }
        public void SetPlaybackVolume(double volume) { }
        public void After(TimeSpan delay, Action action) { }
        public void ShowInFolder(string path) { }
        public void Remember(ShareSettings prefs) { }
        public void Post(Action action) => action();
        public void Run(Action work) => work();
        public string Folder => inner.Folder;
    }

    [Test]
    public static void The_button_says_where_it_posts_and_the_panel_follows_the_posting()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        Assert.Equal("Post to X", vm.PrepareLabel, "button");
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShareViewModel.PublishText) && vm.IsPublishing) seen.Add(vm.PublishText); };
        vm.PrepareCommand.Execute(null);
        Assert.True(seen.Contains("Uploading…"), "status shown: " + string.Join(" | ", seen));
        Assert.True(vm.IsDone && vm.ResultTitle == "Posted to X", vm.ResultTitle);
        Assert.Equal("Open the post", vm.OpenAgainLabel, "post link");
        vm.OpenAgainCommand.Execute(null);
        Assert.Equal("https://x.com/kairos/status/1", host.Launched.Single(), "opens the new post");
    }

    [Test]
    public static void Without_a_post_address_open_goes_to_the_site()
    {
        var (vm, host) = Open(platform: SharePlatform.TikTok);
        host.Outcome = new PublishOutcome(PublishResult.Posted);
        vm.PrepareCommand.Execute(null);
        Assert.Equal("Posted to TikTok", vm.ResultTitle, "posted");
        vm.OpenAgainCommand.Execute(null);
        Assert.True(host.Launched.Single().StartsWith("https://www.tiktok.com/"), host.Launched.Single());
    }

    [Test]
    public static void When_the_site_needs_the_user_the_window_is_one_click_away()
    {
        var (vm, host) = Open(platform: SharePlatform.Instagram);
        host.Outcome = new PublishOutcome(PublishResult.NeedsUser, Detail: "share");
        vm.PrepareCommand.Execute(null);
        Assert.True(vm.IsDone && vm.ResultTitle == "Needs a step from you", vm.ResultTitle);
        Assert.True(vm.ResultHint.Contains("window"), vm.ResultHint);
        vm.OpenAgainCommand.Execute(null);
        Assert.Equal(SharePlatform.Instagram, host.Revealed.Single(), "shows the window");
        Assert.Equal(0, host.Launched.Count, "no browser");
    }

    [Test]
    public static void A_publish_cancelled_from_the_window_returns_to_the_choices()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        host.Outcome = new PublishOutcome(PublishResult.Cancelled);
        vm.PrepareCommand.Execute(null);
        Assert.True(vm.IsChoosing, "choosing again");
    }

    [Test]
    public static void Closing_hides_the_panel()
    {
        var (vm, _) = Open();
        vm.CloseCommand.Execute(null);
        Assert.True(!vm.IsOpen, "closed");
    }

    [Test]
    public static void Vertical_is_offered_everywhere_off_by_default_and_remembered_per_platform()
    {
        var (vm, host) = Open(platform: SharePlatform.X);
        Assert.True(!vm.Vertical && !vm.ShowCrop, "off at first");
        vm.Vertical = true;
        Assert.True(vm.ShowCrop, "the 9:16 frame shows on the picture");
        vm.Platform = SharePlatform.TikTok;
        Assert.True(!vm.Vertical, "only for X");
        vm.Platform = SharePlatform.X;
        vm.PrepareCommand.Execute(null);
        var plan = host.Transcodes.Single().Plan;
        Assert.True(plan.Crop != null && plan.Height > plan.Width, $"X vertical {plan.Width}x{plan.Height}");
        Assert.Equal("X", string.Join(" ", host.Remembered!.VerticalFor), "remembered for X only");
    }

    static (int Width, int Height) PreparedSize(SharePlatform platform)
    {
        var (vm, host) = Open(platform: platform);
        vm.PrepareCommand.Execute(null);
        var plan = host.Transcodes.Single().Plan;
        return (plan.Width, plan.Height);
    }

    [Test]
    public static void The_picture_keeps_the_clips_own_size_except_on_X_or_when_the_size_limit_needs_less()
    {
        Assert.Equal((2560, 1440), PreparedSize(SharePlatform.TikTok), "TikTok");
        Assert.Equal((2560, 1440), PreparedSize(SharePlatform.Instagram), "Instagram");
        Assert.Equal((1920, 1080), PreparedSize(SharePlatform.X), "X: 1080p");
    }

    [Test]
    public static void One_short_line_says_where_the_cut_is_and_a_rounded_size()
    {
        var (vm, _) = OpenCut();
        Assert.Equal("4:30.0 – 5:00.0", vm.TrimRange, "the cut");
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(vm.SizeEstimate, @"^\d+ MB$"), "a plain rounded number: " + vm.SizeEstimate);
    }

    [Test]
    public static void The_preview_volume_turns_the_playback_down_or_off_and_is_kept_for_next_time()
    {
        var (vm, host) = OpenCut();
        Assert.True(vm.PreviewVolume == 1 && !vm.IsMuted, "full at first");
        vm.PlayCommand.Execute(null);
        Assert.Equal(1.0, host.Volume, "the player is told");
        vm.PreviewVolume = 0.4;
        Assert.Equal(0.4, host.Volume, "turned down while playing");
        vm.MuteCommand.Execute(null);
        Assert.True(vm.IsMuted && host.Volume == 0, "muted");
        vm.MuteCommand.Execute(null);
        Assert.True(!vm.IsMuted && host.Volume == 0.4, "back to where it was");
        vm.PreviewVolume = 0;
        Assert.True(vm.IsMuted, "all the way down is muted");
        vm.MuteCommand.Execute(null);
        Assert.True(!vm.IsMuted && vm.PreviewVolume > 0, "unmuting from zero gives some sound back");
        vm.PreviewVolume = 0.3;
        vm.MuteCommand.Execute(null);
        vm.CloseCommand.Execute(null);
        Assert.Equal((0.3, true), (host.Remembered!.PreviewVolume, host.Remembered.PreviewMuted), "kept for next time");
        var (again, _) = Open(p => { p.PreviewVolume = 0.3; p.PreviewMuted = true; });
        Assert.True(again.PreviewVolume == 0.3 && again.IsMuted, "opens as it was left");
    }

    [Test]
    public static void Posting_keeps_the_preview_volume_setting()
    {
        var (vm, host) = Open(p => { p.PreviewVolume = 0.5; }, platform: SharePlatform.X);
        vm.PrepareCommand.Execute(null);
        Assert.Equal(0.5, host.Remembered!.PreviewVolume, "the other choices saved with it");
    }

    [Test]
    public static void An_emoji_shows_in_the_caption_box_at_once()
    {
        var (vm, _) = Open(platform: SharePlatform.X);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.InsertEmoji("🔥", 0, 0);
        Assert.True(changed.Contains(nameof(ShareViewModel.Caption)), "the box is told: " + string.Join(", ", changed));
    }

    [Test]
    public static void An_emoji_goes_where_the_caret_is_and_replaces_a_selection()
    {
        var (vm, _) = Open(platform: SharePlatform.X);
        vm.Caption = "Clutch #EldenRing";
        int caret = vm.InsertEmoji("🔥", 6, 0);
        Assert.Equal(("Clutch🔥 #EldenRing", 8), (vm.Caption, caret), "at the caret, which moves past it");
        caret = vm.InsertEmoji("💀", 0, 6);
        Assert.Equal(("💀🔥 #EldenRing", 2), (vm.Caption, caret), "a selection is replaced");
        caret = vm.InsertEmoji("😂", 99, 0);
        Assert.Equal(("💀🔥 #EldenRing😂", vm.Caption.Length), (vm.Caption, caret), "past the end: at the end");
    }

    [Test]
    public static void The_emoji_picker_offers_pictures_for_each_of_its_emoji()
    {
        Text.Language = "en";
        Assert.Equal("Popular Faces Hands Games and sports Hearts and symbols Animals and food",
            string.Join(" ", EmojiPicks.Categories.Select(c => c.Name)), "tabs, popular first");
        Assert.True(EmojiPicks.Categories.All(c => c.Emoji.Count >= 40 && c.Emoji.Count == c.Emoji.Distinct().Count()),
            "at least forty in each tab, none twice in a tab");
        Assert.Equal(40, EmojiPicks.Categories[0].Emoji.Count, "forty popular ones");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "Kairos.sln")))
            root = root.Parent!;
        foreach (var pick in EmojiPicks.All)
        {
            string file = Path.Combine(root.FullName, "src", "Kairos", "Ui", "Assets", "Emoji", pick.File);
            Assert.True(File.Exists(file), $"{pick.Emoji}: {pick.File}");
        }
        Assert.Equal("🔥", EmojiPicks.Categories[0].Emoji[0].Emoji, "fire first");
    }

    [Test]
    public static void A_click_on_the_picture_plays_and_its_play_mark_shows_only_while_paused()
    {
        var (vm, host) = OpenCut();
        Assert.True(vm.ShowPlayMark, "play mark on the paused picture");
        vm.PlayCommand.Execute(null);
        Assert.True(!vm.ShowPlayMark, "hidden while playing");
        host.Ended!(null);
        Assert.True(vm.ShowPlayMark, "back at the end");
        vm.PrepareCommand.Execute(null);
        Assert.True(vm.IsDone && vm.ShowPlayMark, "after sharing the picture is still a player");
    }

    [Test]
    public static void Play_runs_the_cut_from_its_start_and_the_playhead_follows()
    {
        var (vm, host) = OpenCut();
        vm.PlayCommand.Execute(null);
        Assert.Equal((270.0, 300.0), host.Plays.Single(), "the cut");
        Assert.True(vm.IsPlaying, "playing");
        host.Position!(281.5);
        Assert.Equal(281.5, vm.Playhead, "playhead follows");
        host.Position!(300);
        host.Ended!(null);
        Assert.True(!vm.IsPlaying, "stopped at the end");
        vm.PlayCommand.Execute(null);
        Assert.Equal((270.0, 300.0), host.Plays[1], "from the start again");
    }

    [Test]
    public static void Pausing_and_playing_again_continues_where_it_was()
    {
        var (vm, host) = OpenCut();
        vm.PlayCommand.Execute(null);
        host.Position!(284);
        vm.PlayCommand.Execute(null);
        Assert.True(!vm.IsPlaying && host.Stops == 1, "paused");
        vm.PlayCommand.Execute(null);
        Assert.Equal((284.0, 300.0), host.Plays[1], "continues");
    }

    [Test]
    public static void Clicking_the_strip_moves_the_playhead_inside_the_cut()
    {
        var (vm, host) = OpenCut();
        vm.Playhead = 290;
        vm.PlayCommand.Execute(null);
        Assert.Equal((290.0, 300.0), host.Plays.Single(), "from the clicked point");
        vm.Playhead = 10;
        Assert.Equal(270.0, vm.Playhead, "kept inside the cut");
        Assert.Equal(2, host.Plays.Count, "seeking while playing plays on from there");
    }

    [Test]
    public static void Moving_the_trim_closing_or_converting_stops_the_playback()
    {
        var (vm, host) = OpenCut();
        vm.PlayCommand.Execute(null);
        vm.TrimStart = 260;
        Assert.True(!vm.IsPlaying && host.Stops == 1, "trim moved");
        vm.PlayCommand.Execute(null);
        vm.PrepareCommand.Execute(null);
        Assert.True(!vm.IsPlaying && host.Stops == 2, "converting");
        var (other, otherHost) = Open();
        other.PlayCommand.Execute(null);
        other.CloseCommand.Execute(null);
        Assert.True(!other.IsPlaying && otherHost.Stops == 1, "closed");
    }

    [Test]
    public static void The_playhead_shows_while_playing_or_once_moved_but_not_on_the_start_handle()
    {
        var (vm, host) = OpenCut();
        Assert.True(!vm.ShowPlayhead, "hidden at the start of the cut");
        vm.PlayCommand.Execute(null);
        Assert.True(vm.ShowPlayhead, "shown while playing");
        host.Position!(284);
        vm.PlayCommand.Execute(null);
        Assert.True(vm.ShowPlayhead, "shown where it was paused");
        vm.TrimStart = 275;
        Assert.True(!vm.ShowPlayhead, "back on the start handle after a trim");
        vm.Playhead = 290;
        Assert.True(vm.ShowPlayhead, "moved by a click on the strip");
    }

    [Test]
    public static void The_end_of_an_old_playback_does_not_stop_a_newer_one()
    {
        var (vm, host) = OpenCut();
        vm.PlayCommand.Execute(null);
        var oldEnded = host.Ended!;
        vm.Playhead = 290;
        oldEnded(null);
        Assert.True(vm.IsPlaying, "the newer playback goes on");
    }

    [Test]
    public static void Edit_opens_the_editor_on_the_shared_clip()
    {
        Text.Language = "en";
        var vm = new ShareViewModel(new FakeHost(), () => new ShareSettings());
        vm.Open(Clip);
        string? asked = null;
        vm.EditRequested += path => asked = path;
        vm.EditCommand.Execute(null);
        Assert.Equal(Clip, asked, "Edit opens the editor on this clip");
    }

    /// <summary>
    /// An edit saved from the share panel's editor (a real clip: a new copy, or the original replaced) is what the panel
    /// shares from then on, and it is never removed: not when the panel closes, not when it moves on to another clip.
    /// </summary>
    [Test]
    public static void A_saved_edit_is_shared_and_kept()
    {
        Text.Language = "en";
        var vm = new ShareViewModel(new FakeHost(), () => new ShareSettings());
        vm.Open(Clip);
        string saved = Path.Combine(Path.GetTempPath(), "kairos-saved-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(saved, "edit");
        try
        {
            Assert.True(vm.UseSaved(saved, Clip), "taken");
            Assert.Equal(saved, vm.ClipPath, "the panel shares the saved edit");
            Assert.Equal("ELDEN RING", vm.Game, "still named after the game");
            vm.Close();
            Assert.True(File.Exists(saved), "a saved edit stays");
            Assert.True(!vm.UseSaved(saved, Clip) && File.Exists(saved), "too late (the panel closed): not shown, still kept");
        }
        finally
        {
            File.Delete(saved);
        }
    }

    /// <summary>
    /// A share edit is saved after the user closed the panel or moved on to another clip: it is not shown (the panel does
    /// not pop open, and another clip is never swapped for it), and the saved clip stays.
    /// </summary>
    [Test]
    public static void A_saved_edit_that_finishes_too_late_is_not_shown()
    {
        Text.Language = "en";
        var vm = new ShareViewModel(new FakeHost(), () => new ShareSettings());
        string saved = Path.Combine(Path.GetTempPath(), "kairos-saved-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(saved, "edit");
        try
        {
            vm.Open(Clip);
            vm.Close();
            Assert.True(!vm.UseSaved(saved, Clip), "refused");
            Assert.True(!vm.IsOpen, "the panel stays closed");

            const string other = @"D:\Clips\ELDEN RING\ELDEN RING 2026-10-12 22-10-00.mp4";
            vm.Open(other);
            Assert.True(!vm.UseSaved(saved, Clip), "refused");
            Assert.Equal(other, vm.ClipPath, "the other clip stays");
            Assert.True(File.Exists(saved), "the saved clip is the user's: kept");
        }
        finally
        {
            File.Delete(saved);
        }
    }

    /// <summary>Dragging the playhead or a handle shows the nearest key frame (fast); the exact one when the drag stops.</summary>
    [Test]
    public static void Scrubbing_shows_key_frames_then_the_exact_one()
    {
        var (vm, host) = Open();
        host.PreviewExact.Clear();
        vm.Scrubbing = true;
        vm.TrimStart = 2;
        vm.TrimStart = 2.5;
        Assert.Equal("False,False", string.Join(",", host.PreviewExact), "while dragging: key frames");
        host.RunLater();
        Assert.Equal("False,False,True", string.Join(",", host.PreviewExact), "a pause of 150 ms: the exact frame");
        vm.TrimStart = 3;
        vm.Scrubbing = false;
        Assert.Equal(true, host.PreviewExact[^1], "let go: the exact frame");
        host.RunLater();
        Assert.Equal(5, host.PreviewExact.Count, "a pause asked for after the drag ended asks for nothing more");
    }

    [Test]
    public static void Preparing_keeps_the_other_share_settings()
    {
        var (vm, host) = Open(p => p.CopyMb = 10, platform: SharePlatform.X);
        vm.PrepareCommand.Execute(null);
        Assert.Equal(10, host.Remembered!.CopyMb, "the copy size is kept");
        Assert.True(host.Remembered.Tags.Length > 0, "the hashtags are remembered as before");
    }

    /// <summary>A 5:00 HEVC clip: Discord, many phones and a Windows without its extension play no HEVC.</summary>
    static MediaInfo Hevc() => new(300, 2560, 1440, 60, "hevc", "aac", 192_000, 3, Bytes: 333_000_000);

    [Test]
    public static void An_hevc_clip_is_copied_and_dragged_as_h264_at_its_own_size()
    {
        // HEVC goes out as H.264, so it plays wherever it lands.
        var (vm, host) = Open(info: Hevc());
        Assert.Equal(null, vm.DragFile, "nothing to drag before its H.264 copy is made");
        Copy(vm);
        Assert.Equal(0, host.Cuts.Count, "never handed on as it is");
        var made = host.Transcodes.Single();
        Assert.True(made.Plan.Width == 2560 && made.Plan.Height == 1440 && made.Plan.Fps == 60, "at its own size and rate");
        Assert.Equal(@"D:\Clips\Shared\ELDEN RING 2026-10-12 21-04-55 (0.00-5.00).mp4", made.Target, "kept with the other copies");
        Assert.Equal(made.Target, host.Clipboard?.File, "the H.264 copy on the clipboard");
        Assert.Equal(made.Target, vm.DragFile, "and dragged");
    }

    [Test]
    public static void A_cut_of_an_hevc_clip_is_converted_not_cut()
    {
        var (vm, host) = Open(info: Hevc());
        vm.TrimStart = 270;
        Copy(vm);
        Assert.Equal((0, 1), (host.Cuts.Count, host.Transcodes.Count), "converted, not cut");
    }

    [Test]
    public static void Dragging_an_hevc_copy_tile_makes_its_copy_for_the_next_drag()
    {
        var (vm, host) = Open(info: Hevc());
        vm.PrepareDrag();
        Assert.Equal(1, host.Transcodes.Count, "the drag started its copy");
        Assert.Equal(host.Transcodes[0].Target, vm.DragFile, "the next drag takes it");
        vm.PrepareDrag();
        Assert.Equal(1, host.Transcodes.Count, "made once");
        var (av1, av1Host) = Open();
        Assert.Equal(Clip, av1.DragFile, "AV1 plays where it lands: the clip itself");
        av1.PrepareDrag();
        Assert.Equal(0, av1Host.Transcodes.Count, "nothing to make");
    }

    [Test]
    public static void The_library_drags_an_hevc_clip_as_its_h264_copy_made_on_the_first_drag()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kairos-tests", "copies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "Shared"));
        string clip = Path.Combine(folder, "ELDEN RING 2026-10-12 21-04-55.mp4");
        File.WriteAllText(clip, "hevc");
        File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(-5));
        var host = new FakeHost { Info = Hevc(), Shared = Path.Combine(folder, "Shared"), WriteTargets = true };
        var copies = new PlayableCopies(host);
        Assert.Equal(null, copies.FileFor(clip), "not made yet: nothing to drag");
        var progress = new List<double>();
        copies.Make(clip, new InlineProgress(progress.Add), CancellationToken.None);
        var t = host.Transcodes.Single();
        Assert.True(t.Plan.Width == 2560 && t.Plan.Height == 1440, "at its own size");
        Assert.Equal(Path.Combine(folder, "Shared", "ELDEN RING 2026-10-12 21-04-55 (0.00-5.00).mp4"), t.Target, "the name the share panel's copy has too");
        Assert.Equal(t.Target, copies.FileFor(clip), "the next drag takes it");
        Assert.True(progress.Count > 0, "its progress for the card's ring");
        File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(1)); // an edit saved over the clip
        Assert.Equal(null, copies.FileFor(clip), "an older copy is never dragged: it is made again");
        File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddMinutes(-5));
        File.Delete(t.Target);
        Assert.Equal(null, copies.FileFor(clip), "a copy deleted since: made again, not a drag of nothing");
        host.Info = new MediaInfo(300, 2560, 1440, 60, "h264", "aac", 192_000, 3);
        Assert.Equal(@"D:\Other.mp4", new PlayableCopies(host).FileFor(@"D:\Other.mp4"), "H.264 (or AV1): the clip itself");
        Assert.True(SharePlanner.PlaysEverywhere("av1") && SharePlanner.PlaysEverywhere("h264") && !SharePlanner.PlaysEverywhere("hevc"), "which go as they are");
    }

    static MediaInfo Minute(long bytes, double seconds = 60) => new(seconds, 2560, 1440, 60, "av1", "aac", 192_000, 3, Bytes: bytes);

    [Test]
    public static void Copy_at_a_size_converts_the_cut_and_copies_the_file()
    {
        var (vm, host) = Open(p => p.CopyMb = 10, info: Minute(120_000_000));
        Copy(vm, 10);
        Assert.Equal(1, host.Transcodes.Count, "converted (the whole clip no longer skips the size)");
        Assert.Equal(10_000_000L, host.Transcodes[0].MaxBytes, "made to fit 10 MB");
        Assert.Near(60, host.Transcodes[0].Plan.Duration, 0.01, "the cut");
        Assert.Equal(host.Transcodes[0].Target, host.Clipboard?.File, "the converted file on the clipboard");
        Assert.True(vm.IsCopied, "the check shows");
    }

    [Test]
    public static void A_cut_that_fits_is_copied_as_it_is()
    {
        var (vm, host) = Open(p => p.CopyMb = 10, info: Minute(6_000_000, 30));
        Copy(vm, 10);
        Assert.Equal(0, host.Transcodes.Count, "no conversion");
        Assert.Equal(Clip, host.Clipboard?.File, "the clip itself");
    }

    [Test]
    public static void Cancelling_a_sized_copy_copies_nothing()
    {
        var (vm, host) = Open(p => p.CopyMb = 10, info: Minute(120_000_000));
        host.OnTranscode = _ =>
        {
            Assert.True(vm.IsCopyConverting, "the ring shows while it converts");
            vm.CopyCommand.Execute(null); // pressed again: stops it
            return null;
        };
        Copy(vm, 10);
        Assert.Equal(null, host.Clipboard, "nothing copied after Copy was pressed again");
        Assert.True(!vm.IsCopyConverting, "the ring is gone");

        host.OnTranscode = _ =>
        {
            vm.CloseCommand.Execute(null);
            return null;
        };
        vm.Open(Clip);
        Copy(vm, 10);
        Assert.Equal(null, host.Clipboard, "nothing copied after the panel closed");
    }

    /// <summary>
    /// Copy is a tile like the ones beside it, with no size under it (that made the row taller, a gap under X, TikTok
    /// and Instagram, 2026-10-22): pressed, it asks the size, and the one picked is copied and kept for next time.
    /// </summary>
    [Test]
    public static void Pressing_copy_asks_the_size_then_copies_at_the_one_picked()
    {
        var (vm, host) = Open(info: Minute(120_000_000));
        int asked = 0;
        vm.CopySizeAsked += () => asked++;
        vm.CopyCommand.Execute(null);
        Assert.True(asked == 1 && host.Clipboard == null && host.Transcodes.Count == 0, "asks the size; nothing copied yet");
        Assert.Equal("Original,10 MB,50 MB,500 MB", string.Join(",", vm.CopySizeChoices.Select(o => o.Label)), "the choices");
        Assert.Equal(",Discord,Nitro Basic,Nitro", string.Join(",", vm.CopySizeChoices.Select(o => o.For)), "what each is for");
        vm.CopyAtCommand.Execute(10);
        Assert.Equal(10_000_000L, host.Transcodes.Single().MaxBytes, "made to fit 10 MB");
        Assert.Equal(host.Transcodes[0].Target, host.Clipboard?.File, "and copied");
        Assert.Equal(10, host.Remembered?.CopyMb, "kept for next time (a cut too long for it says so)");
    }

    [Test]
    public static void Too_long_for_the_size_picked_offers_a_trim()
    {
        var (vm, host) = Open(p => p.CopyMb = 10, info: Minute(333_000_000, 600));
        Assert.Equal(null, vm.CopyTooLong, "no note before a size is picked (the size is no longer on show)");
        Copy(vm, 10);
        double fit = SharePlanner.SecondsThatFit(SharePlanner.CopySpec(10), 2560, 1440, false);
        Assert.Equal($"Only the last {(int)fit} s fit in 10 MB.", vm.CopyTooLong, "the note");
        Assert.Equal(0, host.Transcodes.Count, "nothing made: it would not fit");
        vm.TrimToFitCommand.Execute(null);
        Assert.Near(Math.Floor(fit), vm.TrimEnd - vm.TrimStart, 0.01, "trimmed to what fits");
        Assert.Near(600, vm.TrimEnd, 0.01, "ending where it ended");
        Assert.Equal(null, vm.CopyTooLong, "the note is gone");
        Copy(vm, 10);
        Assert.Equal(1, host.Transcodes.Count, "and it copies");
        Copy(vm, 0);
        Assert.Equal(null, vm.CopyTooLong, "Original always fits");
    }

    [Test]
    public static void The_original_size_copies_as_before()
    {
        var (vm, host) = Open(info: Minute(120_000_000));
        Copy(vm, 0);
        Assert.Equal(Clip, host.Clipboard?.File, "the clip itself");
        vm.CopyAtCommand.Execute(50);
        Assert.Equal(50, host.Remembered?.CopyMb, "a picked size is remembered");
    }

    [Test]
    public static void The_drag_gives_the_prepared_file_only_while_it_matches()
    {
        var (vm, host) = Open(p => p.CopyMb = 10, info: Minute(120_000_000));
        Assert.Equal(Clip, vm.DragFile, "before a copy: the clip");
        Copy(vm, 10);
        string made = host.Transcodes[0].Target;
        Assert.Equal(made, vm.DragFile, "the copy made to fit");
        vm.TrimStart = 10;
        Assert.Equal(Clip, vm.DragFile, "the cut changed: the clip");
        vm.TrimStart = 0;
        Copy(vm, 10);
        Assert.Equal(made, vm.DragFile, "made again");
        Copy(vm, 50);
        Assert.True(host.Transcodes[^1].MaxBytes == 50_000_000 && vm.DragFile == host.Transcodes[^1].Target, "another size: its own copy");
        vm.Open(Shot);
        Assert.Equal(Shot, vm.DragFile, "a screenshot: itself");
    }
}
