using Kairos.App;
using Kairos.Engine.Share;

namespace Kairos.Ui;

public sealed partial class ShareViewModel
{
    /// <summary>A 9:16 cut for the chosen platform (each platform, and everywhere, remembers its own; off at first).</summary>
    public bool Vertical
    {
        get => _platform is { } p && _verticalFor.Contains(p);
        set
        {
            if (_platform is not { } p)
                return;
            if (value)
                _verticalFor.Add(p);
            else
                _verticalFor.Remove(p);
            Edited();
        }
    }

    /// <summary>No platform yet: no limits (the whole clip can be looked at and trimmed).</summary>
    ShareSpec CurrentSpec => _platform is { } p ? SharePlanner.Spec(p, _xPremium == true) : SharePlanner.Spec(SharePlatform.Copy);

    /// <summary>The picture size: the clip's own (a copy enlarged when asked), or less when the platform needs it.</summary>
    SizeStep CurrentSize
    {
        get
        {
            if (_info == null)
                return new SizeStep(0, true);
            return SharePlanner.DefaultSize(CurrentSpec, _trimEnd - _trimStart, _info.Width, _info.Height, _info.Fps, AudioBitrate, Vertical);
        }
    }

    /// <summary>0 = the 9:16 window at the left edge, 1 = at the right edge.</summary>
    public double CropPosition
    {
        get => _crop;
        set { _crop = Math.Clamp(value, 0, 1); Edited(); }
    }

    public string Caption
    {
        get => _caption;
        set { _caption = value ?? ""; _copied = false; Raise(nameof(CopiedVisible)); Raise(nameof(CaptionFlow)); }
    }

    public bool CopiedVisible => _copied;

    /// <summary>Like dir="auto": the caption box follows the first letter typed (Arabic right-to-left, else left-to-right).</summary>
    public System.Windows.FlowDirection CaptionFlow
    {
        get
        {
            foreach (char c in _caption)
            {
                if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ')
                    return System.Windows.FlowDirection.RightToLeft;
                if (char.IsLetter(c))
                    return System.Windows.FlowDirection.LeftToRight;
            }
            return Text.IsRightToLeft ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight;
        }
    }
    public bool ShowCrop => Vertical && _info != null;
    public bool ShowCaption => _platform is { } p && (IsSite(p) || p == SharePlatform.All);

    // Picture preview with the 9:16 frame drawn to scale (as shares of the picture's width)
    public double PreviewAspect => _info is { Width: > 0, Height: > 0 } i ? (double)i.Width / i.Height
        : _picture is { Width: > 0, Height: > 0 } p ? (double)p.Width / p.Height : 16.0 / 9;
    /// <summary>Width of the 9:16 frame as a share of the picture's width.</summary>
    public double CropFraction => Math.Min(1, 9.0 / 16 / PreviewAspect);
    public System.Windows.GridLength CropLeft => new((1 - CropFraction) * _crop, System.Windows.GridUnitType.Star);
    public System.Windows.GridLength CropWindow => new(CropFraction, System.Windows.GridUnitType.Star);
    public System.Windows.GridLength CropRight => new((1 - CropFraction) * (1 - _crop), System.Windows.GridUnitType.Star);

    long EstimatedBytes(SharePlan plan) => (long)((plan.VideoBitrate + AudioBitrate) * plan.Duration / 8);

    public string PrepareLabel => _platform switch
    {
        null => Text.Get("share.choose"),
        SharePlatform.Copy => Text.Get("share.goCopy"),
        SharePlatform.All => Text.Get("share.postAll"),
        { } p => Text.Format("share.publishTo", PlatformName(p)),
    };

    // Working
    public double ProgressPercent => Math.Round(_progress * 100);
    public string ProgressText => Text.Format("share.preparing", (int)ProgressPercent);

    // Publishing (X, TikTok, Instagram)
    public bool IsPublishing => _phase == Phase.Publishing;
    public string PublishText => Text.Get(_publishStatus);

    // Everywhere: a row per site while it goes and after
    public System.Collections.ObjectModel.ObservableCollection<SiteJob> Jobs { get; } = [];
    public bool IsMulti => _resultPlatform == SharePlatform.All && (IsWorking || IsPublishing || IsDone) && Jobs.Count > 0;
    public bool IsSingleWorking => IsWorking && !IsMulti;
    public bool IsSinglePublishing => IsPublishing && !IsMulti;
    public bool IsSingleDone => IsDone && !IsMulti;
    public bool IsMultiRunning => IsMulti && !IsDone;

    // Done
    public string ResultTitle => _outcome?.Result switch
    {
        PublishResult.Posted => Text.Format("share.posted", PlatformName(_resultPlatform)),
        PublishResult.NeedsUser => Text.Get("share.needsUser"),
        _ => _result == null ? "" : Text.Format("share.ready", Text.Ltr(Size(_result.Bytes))),
    };
    public string ResultHint => _outcome?.Result switch
    {
        PublishResult.Posted => Text.Get(_resultPlatform == SharePlatform.TikTok ? "share.posted.tiktok" : "share.posted.hint"),
        PublishResult.NeedsUser => Text.Get("share.pub.manual"),
        _ => Text.Get("share.hint." + _resultPlatform.ToString().ToLowerInvariant()),
    };
    public bool ShowOpenAgain => _resultPlatform != SharePlatform.Copy;
    public string OpenAgainLabel => _outcome?.Result switch
    {
        PublishResult.Posted when _outcome.PostUrl != null => Text.Get("share.openPost"),
        PublishResult.NeedsUser => Text.Get("share.showWindow"),
        _ => Text.Format("share.openAgain", PlatformName(_resultPlatform)),
    };

    // Failed
    public string ErrorDetail => _errorDetail;
    public string RetryLabel => Text.Get(_info == null ? "share.tryAgain" : "share.retry");

    public static string PlatformName(SharePlatform p) => p switch
    {
        SharePlatform.X => "X",
        SharePlatform.Copy => Text.Get("share.copy"),
        SharePlatform.All => Text.Get("share.all"),
        _ => p.ToString(),
    };

    double MaxSeconds => CurrentSpec.MaxSeconds;

    void SetTrim(double start, double end, double previewAt)
    {
        if (_info == null)
            return;
        StopPlayback();
        _trimMoved = true;
        (_trimStart, _trimEnd) = SharePlanner.ClampTrim(start, end, _info.Duration, MaxSeconds);
        _previewTime = _playhead = Math.Clamp(previewAt, _trimStart, _trimEnd);
        RequestPreview();
        Edited();
    }

    /// <summary>
    /// A cut the user has not moved opens as long as the new platform takes; a moved one is kept and only made to
    /// fit (too long: it keeps its end and starts later).
    /// </summary>
    void FitTrimToPlatform()
    {
        if (_info == null)
            return;
        (_trimStart, _trimEnd) = _trimMoved
            ? SharePlanner.ClampTrim(_trimStart, _trimEnd, _info.Duration, MaxSeconds)
            : SharePlanner.DefaultTrim(_info.Duration, MaxSeconds);
        _playhead = Math.Clamp(_playhead, _trimStart, _trimEnd);
    }

    void RequestFrames()
    {
        if (_info == null)
            return;
        string clip = _clip;
        var times = Enumerable.Range(0, FilmstripFrames).Select(i => (i + 0.5) * _info.Duration / FilmstripFrames).ToArray();
        foreach (var _ in times)
            Filmstrip.Add(null);
        _host.GrabFrames(clip, times, 96, exact: false, (i, frame) =>
        {
            if (clip == _clip && i < Filmstrip.Count)
                Filmstrip[i] = frame;
        });
        RequestPreview();
    }

    /// <summary>
    /// The playhead or a handle is being dragged: each picture comes from the nearest key frame (a decode of up to a
    /// second of frames per move was slow on CPU decoding and high frame rates); the exact one 150 ms after the drag
    /// pauses, and when it ends.
    /// </summary>
    public bool Scrubbing
    {
        get => _scrubbing;
        set
        {
            if (_scrubbing == value)
                return;
            _scrubbing = value;
            if (!value)
                RequestPreview();
        }
    }

    bool _scrubbing;
    int _settle;

    void RequestPreview() => RequestPreview(exact: !_scrubbing);

    void RequestPreview(bool exact)
    {
        if (_info == null)
            return;
        if (!exact)
        {
            int settle = ++_settle;
            _host.After(TimeSpan.FromMilliseconds(150), () =>
            {
                if (settle == _settle && _scrubbing)
                    RequestPreview(exact: true);
            });
        }
        string clip = _clip;
        double at = Math.Min(_previewTime, Math.Max(0, _info.Duration - 0.05));
        _host.GrabFrames(clip, [at], PreviewPixels, exact, (_, frame) =>
        {
            if (clip != _clip || frame == null || _playing)
                return;
            _previewFrame = frame;
            Raise(nameof(PreviewFrame));
        });
    }

    long AudioBitrate => _info == null ? 0 : SharePlanner.AudioBitrateOf(_info);

    SharePlan CurrentPlan() => SharePlanner.Plan(CurrentSpec, _trimStart, _trimEnd - _trimStart, _info!.Width, _info.Height, _info.Fps,
        AudioBitrate, Vertical, _crop, CurrentSize.ShortSide);

    /// <summary>Everywhere: what one site gets of the shared cut (its own picture size, the shared 9:16 choice).</summary>
    SharePlan PlanFor(SharePlatform site) => PlanFor(SharePlanner.Spec(site, _xPremium == true), Vertical, _crop);

    /// <summary>The cut for <paramref name="spec"/> at the biggest picture that stays clean in its size.</summary>
    SharePlan PlanFor(ShareSpec spec, bool vertical, double crop)
    {
        var size = SharePlanner.DefaultSize(spec, _trimEnd - _trimStart, _info!.Width, _info.Height, _info.Fps, AudioBitrate, vertical);
        return SharePlanner.Plan(spec, _trimStart, _trimEnd - _trimStart, _info.Width, _info.Height, _info.Fps, AudioBitrate,
            vertical, crop, size.ShortSide);
    }
}
