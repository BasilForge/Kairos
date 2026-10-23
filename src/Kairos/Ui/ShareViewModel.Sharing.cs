using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Ui;

public sealed partial class ShareViewModel
{
    void Prepare()
    {
        if (!HasClip || _phase != Phase.Choosing || _platform is not { } platform)
            return;
        StopPlayback();
        // What the panel shows now; the rest (the copy size) as it was.
        var prefs = _prefs().Copy();
        prefs.XPremium = _xPremium;
        prefs.VerticalFor = [.. _verticalFor];
        prefs.CropPosition = _crop;
        prefs.Tags = SharePlanner.RememberTags(_caption, Game);
        prefs.PreviewVolume = _volume;
        prefs.PreviewMuted = _muted;
        _host.Remember(prefs);
        _resultPlatform = platform;
        _resultCaption = _caption;
        _outcome = null;
        Jobs.Clear();
        if (platform == SharePlatform.All)
        {
            PostEverywhere(_cts = new CancellationTokenSource());
            return;
        }
        if (_picture != null)
        {
            PreparePicture(platform, _cts = new CancellationTokenSource());
            return;
        }

        var spec = CurrentSpec;
        var plan = CurrentPlan();
        string source = _clip;
        string target = ShareFiles.Target(_host.Folder, platform, _clip);
        var cts = _cts = new CancellationTokenSource();
        _progress = 0;
        _phase = Phase.Working;
        RaiseAll();

        _host.Run(() =>
        {
            try
            {
                var progress = new InlineProgress(p => _host.Post(() =>
                {
                    _progress = p;
                    Raise(nameof(ProgressPercent));
                    Raise(nameof(ProgressText));
                }));
                var result = _host.Transcode(source, target, plan, spec.MaxBytes, progress, cts.Token);
                _host.Post(() => Finish(result, cts));
            }
            catch (OperationCanceledException)
            {
                _host.Post(() => { if (_cts == cts) { _phase = Phase.Choosing; RaiseAll(); } });
            }
            catch (Exception e)
            {
                _host.Post(() => { if (_cts == cts) { _errorDetail = e.Message; _phase = Phase.Failed; RaiseAll(); } });
            }
        });
    }

    void Finish(ShareResult result, CancellationTokenSource cts)
    {
        if (_cts != cts || cts.IsCancellationRequested)
        {
            _phase = Phase.Choosing;
            RaiseAll();
            return;
        }
        _result = result;
        if (IsSite(_resultPlatform))
        {
            Publish(result, cts);
            return;
        }
        _host.CopyToClipboard(result.Path, null);
        _phase = Phase.Done;
        RaiseAll();
    }

    /// <summary>A screenshot: copied at once, or posted as it is (a JPEG copy only when it is over the site's limit).</summary>
    async void PreparePicture(SharePlatform platform, CancellationTokenSource cts)
    {
        if (platform == SharePlatform.Copy)
        {
            _host.CopyPicture(_clip);
            _result = new ShareResult(_clip, _picture!.Bytes, "picture");
            _phase = Phase.Done;
            RaiseAll();
            return;
        }
        _progress = 0;
        _phase = Phase.Working;
        RaiseAll();
        ShareResult file;
        try
        {
            file = await PictureFor(platform);
        }
        catch (Exception e)
        {
            if (_cts == cts)
            {
                _errorDetail = e.Message;
                _phase = Phase.Failed;
                RaiseAll();
            }
            return;
        }
        Finish(file, cts);
    }

    /// <summary>The screenshot as <paramref name="site"/> takes it: the file itself, or a JPEG copy under its limit.</summary>
    Task<ShareResult> PictureFor(SharePlatform site)
    {
        long bytes = _picture!.Bytes;
        if (!SharePlanner.PictureNeedsJpeg(site, bytes))
            return Task.FromResult(new ShareResult(_clip, bytes, "picture"));
        var done = new TaskCompletionSource<ShareResult>();
        string source = _clip;
        string target = ShareFiles.Target(_host.Folder, site, _clip, ".jpg");
        long limit = SharePlanner.PictureLimit(site);
        _host.Run(() =>
        {
            try
            {
                var result = _host.MakeJpeg(source, target, limit);
                _host.Post(() => done.TrySetResult(result));
            }
            catch (Exception e)
            {
                _host.Post(() => done.TrySetException(e));
            }
        });
        return done.Task;
    }

    /// <summary>X, TikTok, Instagram: Kairos posts it through the site's page; the panel follows along.</summary>
    async void Publish(ShareResult result, CancellationTokenSource cts)
    {
        _publishStatus = "share.pub.opening";
        _phase = Phase.Publishing;
        RaiseAll();
        PublishOutcome outcome;
        try
        {
            outcome = await _host.Publish(_resultPlatform, result.Path, _resultCaption, key =>
            {
                _publishStatus = key;
                Raise(nameof(PublishText));
            }, cts.Token);
        }
        catch (Exception e)
        {
            if (_cts == cts)
            {
                _errorDetail = e.Message;
                _phase = Phase.Failed;
                RaiseAll();
            }
            return;
        }
        if (_cts != cts)
            return;
        NoteXPremium(outcome);
        if (outcome.Result == PublishResult.Cancelled)
            _phase = Phase.Choosing;
        else
        {
            _outcome = outcome;
            _phase = Phase.Done;
        }
        RaiseAll();
    }

    /// <summary>X's page tells whether the account has Premium (longer videos); remembered for next time.</summary>
    void NoteXPremium(PublishOutcome outcome)
    {
        if (outcome.XPremium is not { } premium || premium == _xPremium)
            return;
        _xPremium = premium;
        var prefs = _prefs().Copy();
        prefs.XPremium = premium;
        _host.Remember(prefs);
    }

    /// <summary>
    /// X, TikTok and Instagram with one caption. Each picture size is made once (X's 1080p, and the clip's own size
    /// that TikTok and Instagram share), and each site starts posting as soon as its file is ready, the others
    /// alongside. A site that fails or needs the user does not stop the rest.
    /// </summary>
    async void PostEverywhere(CancellationTokenSource cts)
    {
        foreach (var site in SiteAccounts.Sites)
            Jobs.Add(new SiteJob(site, OpenJob));
        _phase = Phase.Publishing;
        RaiseAll();
        var posts = new List<Task>();
        if (_picture != null)
        {
            foreach (var job in Jobs)
            {
                ShareResult picture;
                try
                {
                    picture = await PictureFor(job.Platform);
                }
                catch (Exception e)
                {
                    job.Failed(e.Message);
                    continue;
                }
                posts.Add(PostOne(job, picture, cts.Token));
            }
            await Task.WhenAll(posts);
            if (_cts == cts)
            {
                _phase = Phase.Done;
                RaiseAll();
            }
            return;
        }
        foreach (var group in SiteAccounts.Sites.GroupBy(PlanFor))
        {
            var jobs = group.Select(s => Jobs.Single(j => j.Platform == s)).ToList();
            if (cts.IsCancellationRequested)
            {
                jobs.ForEach(j => j.Cancelled());
                continue;
            }
            jobs.ForEach(j => j.Preparing(0));
            ShareResult file;
            try
            {
                long maxBytes = group.Min(s => SharePlanner.Spec(s, _xPremium == true).MaxBytes);
                file = await ConvertAsync(ShareFiles.Target(_host.Folder, group.First(), _clip), group.Key, maxBytes,
                    p => jobs.ForEach(j => j.Preparing(p)), cts.Token);
            }
            catch (OperationCanceledException)
            {
                jobs.ForEach(j => j.Cancelled());
                continue;
            }
            catch (Exception e)
            {
                jobs.ForEach(j => j.Failed(e.Message));
                continue;
            }
            posts.AddRange(jobs.Select(j => PostOne(j, file, cts.Token)));
        }
        await Task.WhenAll(posts);
        if (_cts != cts)
            return;
        _phase = Phase.Done;
        RaiseAll();
    }

    Task<ShareResult> ConvertAsync(string target, SharePlan plan, long maxBytes, Action<double> progress, CancellationToken ct)
    {
        var done = new TaskCompletionSource<ShareResult>();
        string source = _clip;
        _host.Run(() =>
        {
            try
            {
                var result = _host.Transcode(source, target, plan, maxBytes, new InlineProgress(p => _host.Post(() => progress(p))), ct);
                _host.Post(() => done.TrySetResult(result));
            }
            catch (OperationCanceledException)
            {
                _host.Post(() => done.TrySetCanceled());
            }
            catch (Exception e)
            {
                _host.Post(() => done.TrySetException(e));
            }
        });
        return done.Task;
    }

    async Task PostOne(SiteJob job, ShareResult file, CancellationToken ct)
    {
        job.Status("share.pub.opening");
        try
        {
            var outcome = await _host.Publish(job.Platform, file.Path, _resultCaption, job.Status, ct);
            NoteXPremium(outcome);
            job.Finished(outcome);
        }
        catch (OperationCanceledException)
        {
            job.Cancelled();
        }
        catch (Exception e)
        {
            job.Failed(e.Message);
        }
    }

    /// <summary>A posted site's post (or the site); a site that needs the user: its window.</summary>
    void OpenJob(SiteJob job)
    {
        if (job.NeedsUser)
            _host.RevealSite(job.Platform);
        else if (job.IsPosted)
            _host.Launch(job.PostUrl ?? SiteHome(job.Platform));
    }

    static bool IsSite(SharePlatform p) => p is SharePlatform.X or SharePlatform.TikTok or SharePlatform.Instagram;

    /// <summary>The site's own address to look at the result when the post's address is not known.</summary>
    static string SiteHome(SharePlatform p) => p switch
    {
        SharePlatform.X => "https://x.com/home",
        SharePlatform.TikTok => "https://www.tiktok.com/tiktokstudio/content",
        _ => "https://www.instagram.com/",
    };

    /// <summary>Posted: the post (or the site). Handed over: the site window.</summary>
    void OpenDestination()
    {
        if (!IsSite(_resultPlatform))
            return;
        if (_outcome?.Result == PublishResult.NeedsUser)
            _host.RevealSite(_resultPlatform);
        else
            _host.Launch(_outcome?.PostUrl ?? SiteHome(_resultPlatform));
    }

    void Edited()
    {
        if (_phase == Phase.Done)
        {
            _phase = Phase.Choosing;
            Jobs.Clear();
        }
        RaiseAll();
    }

    void Retry()
    {
        if (_info == null)
            Open(_clip, Thumbnail);
        else
        {
            _phase = Phase.Choosing;
            RaiseAll();
        }
    }

    /// <summary>"4:29.5": the trim moves in tenths of a second.</summary>
    static string ClockTenths(double seconds)
    {
        double t = Math.Round(Math.Max(0, seconds), 1);
        int whole = (int)t;
        return $"{whole / 60}:{whole % 60:00}.{(int)Math.Round((t - whole) * 10) % 10}";
    }

    static string Size(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0.0} MB";

    static string RoundedSize(long bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB"
        : bytes >= 10_000_000 ? $"{Math.Round(bytes / 1e6):0} MB" : $"{bytes / 1e6:0.0} MB";
}
