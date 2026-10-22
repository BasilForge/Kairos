using System.Windows.Media.Imaging;
using Kairos.App;
using Kairos.Engine.Share;

namespace Kairos.Ui;

public sealed partial class ShareViewModel
{
    public double PreviewVolume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_volume > 0)
                _muted = false;
            ApplyVolume();
            RaiseVolume();
        }
    }

    public bool IsMuted => _muted || _volume <= 0;

    void ToggleMute()
    {
        if (IsMuted)
        {
            _muted = false;
            if (_volume <= 0)
                _volume = 0.5; // all the way down: unmuting gives some sound back
        }
        else
            _muted = true;
        ApplyVolume();
        RaiseVolume();
    }

    void ApplyVolume() => _host.SetPlaybackVolume(IsMuted ? 0 : _volume);

    void RaiseVolume()
    {
        Raise(nameof(PreviewVolume));
        Raise(nameof(IsMuted));
    }

    /// <summary>The panel closes: the loudness is kept for next time (when it changed).</summary>
    void RememberVolume()
    {
        var prefs = _prefs();
        if (prefs.PreviewVolume == _volume && prefs.PreviewMuted == _muted)
            return;
        var copy = prefs.Copy();
        copy.PreviewVolume = _volume;
        copy.PreviewMuted = _muted;
        _host.Remember(copy);
    }

    // Copy: one press, a check for a moment, nothing else
    public bool IsCopying => _clipCopying;
    public bool IsCopied => _clipCopied;
    bool WholeClip => _info != null && _trimStart <= 0.05 && _trimEnd >= _info.Duration - 0.05;

    /// <summary>Copy's size in MB (0: the clip's own); kept for next time.</summary>
    public int CopyMb
    {
        get => _copyMb;
        set
        {
            if (value == _copyMb || !SharePlanner.CopySizes.Contains(value))
                return;
            _copyMb = value;
            var prefs = _prefs().Copy();
            prefs.CopyMb = value;
            _host.Remember(prefs);
            RaiseCopy();
        }
    }

    static string SizeLabel(int mb) => mb == 0 ? Text.Get("share.copyOriginal") : Text.Ltr(Text.Format("share.copyMb", mb));
    /// <summary>A size in Copy's list: its name and what it is for (Discord's limits).</summary>
    public sealed record CopySize(string Label, string For, int Mb);

    public IReadOnlyList<CopySize> CopySizeChoices => [.. SharePlanner.CopyTiers.Select(t => new CopySize(SizeLabel(t.Mb), t.For, t.Mb))];

    /// <summary>A size was picked in this panel: only then does a cut too long for it say so.</summary>
    bool _copySizePicked;

    bool CanCopy => HasClip && !IsWorking && !IsPublishing;

    void PressCopy()
    {
        if (_picture != null)
        {
            _host.CopyPicture(_clip); // a screenshot: as it is
            ShowCopied();
        }
        else if (_copyCts != null)
            _copyCts.Cancel(); // pressed again: stop making it
        else
            CopySizeAsked?.Invoke();
    }

    void CopyAt(int mb)
    {
        _copySizePicked = true;
        CopyMb = mb;
        RaiseCopy(); // the note, also when the size stayed the same
        CopyClip();
    }
    /// <summary>
    /// What dragging Copy gives: the copy made to fit the size while the size and cut still match it, else the clip
    /// itself (a screenshot: itself).
    /// </summary>
    public string? DragFile => !HasClip ? null
        : _prepared is { } p && p.Mb == _copyMb && Math.Abs(p.Start - _trimStart) < 0.001 && Math.Abs(p.End - _trimEnd) < 0.001 ? p.Path
        : NeedsH264 ? null
        : _clip;

    /// <summary>The clip is HEVC: copied and dragged as H.264 (<see cref="SharePlanner.PlaysEverywhere"/>), never as it is.</summary>
    bool NeedsH264 => _info != null && _picture == null && !SharePlanner.PlaysEverywhere(_info.VideoCodec);

    /// <summary>A drag with nothing to drag yet (an HEVC clip): its H.264 copy is made, with the tile's ring, for the next drag.</summary>
    public void PrepareDrag()
    {
        if (NeedsH264 && _copyCts == null && !_clipCopying && DragFile == null)
            CopyClip();
    }

    public bool IsCopyConverting => _copyCts != null;
    public double CopyProgress => _copyProgress;
    public string CopyPercent => $"{(int)(_copyProgress * 100)}%";

    ShareSpec CopySpec => SharePlanner.CopySpec(_copyMb);

    /// <summary>The longest cut any picture fits in the size (kept until the size or the clip changes: read on every drag move).</summary>
    double CopyFits
    {
        get
        {
            if (_fits.Mb != _copyMb || !ReferenceEquals(_fits.Info, _info))
                _fits = (_copyMb, _info, _info == null ? double.PositiveInfinity : SharePlanner.SecondsThatFit(CopySpec, _info.Width, _info.Height, false));
            return _fits.Seconds;
        }
    }
    (int Mb, MediaInfo? Info, double Seconds) _fits = (-1, null, 0);

    /// <summary>The cut's share of the file: about what it takes as it is.</summary>
    bool FitsAsItIs => _info != null
        && (_trimEnd - _trimStart) / Math.Max(0.001, _info.Duration) * _info.Bytes <= CopySpec.MaxBytes * SharePlanner.SizeMargin;

    /// <summary>The cut is longer than any picture fits in the size.</summary>
    bool TooLongForSize => HasVideo && !FitsAsItIs && _trimEnd - _trimStart > CopyFits;

    /// <summary>"Only the last N s fit", once that size was picked (null when it fits).</summary>
    public string? CopyTooLong => _copySizePicked && TooLongForSize
        ? Text.Format("share.copyTooLong", (int)CopyFits, SizeLabel(_copyMb))
        : null;

    void TrimToFit()
    {
        double fit = Math.Floor(CopyFits);
        SetTrim(_trimEnd - fit, _trimEnd, _trimEnd - fit);
    }

    /// <summary>The video's cut on the clipboard at Copy's size (a cut too long for it waits for a shorter one).</summary>
    void CopyClip()
    {
        if (_info == null || _clipCopying)
            return;
        if (!FitsAsItIs || NeedsH264)
        {
            if (!TooLongForSize)
                CopyAtSize();
            return;
        }
        string source = _clip;
        if (WholeClip)
        {
            // The whole clip: the file itself goes on the clipboard, like copying it in File Explorer.
            _host.CopyToClipboard(source, null);
            ShowCopied();
            return;
        }
        double start = _trimStart, end = _trimEnd;
        string target = ShareFiles.CopyTarget(_host.SharedFolder, _clip, start, end);
        _clipCopying = true;
        RaiseCopy();
        _host.Run(() =>
        {
            try
            {
                var result = _host.Cut(source, target, start, end, new InlineProgress(_ => { }), CancellationToken.None);
                _host.Post(() =>
                {
                    _clipCopying = false;
                    _host.CopyToClipboard(result.Path, null);
                    ShowCopied();
                });
            }
            catch (Exception e)
            {
                _host.Post(() =>
                {
                    _clipCopying = false;
                    _errorDetail = e.Message;
                    _phase = Phase.Failed;
                    RaiseAll();
                });
            }
        });
    }

    /// <summary>Converts the cut to fit the size (the picture and sound made smaller), then puts it on the clipboard.</summary>
    async void CopyAtSize()
    {
        double start = _trimStart, end = _trimEnd;
        int mb = _copyMb;
        var spec = CopySpec;
        var cts = _copyCts = new CancellationTokenSource();
        _copyProgress = 0;
        _clipCopying = true;
        RaiseCopy();
        try
        {
            var result = await ConvertAsync(ShareFiles.CopyTarget(_host.SharedFolder, _clip, start, end, mb), SharePlanner.CopyPlan(mb, _info!, start, end - start),
                spec.MaxBytes, p =>
                {
                    if (_copyCts != cts)
                        return;
                    _copyProgress = p;
                    Raise(nameof(CopyProgress));
                    Raise(nameof(CopyPercent));
                }, cts.Token);
            if (!EndCopy(cts))
                return;
            _prepared = (mb, start, end, result.Path);
            _host.CopyToClipboard(result.Path, null);
            ShowCopied();
        }
        catch (OperationCanceledException)
        {
            EndCopy(cts);
        }
        catch (Exception e)
        {
            if (!EndCopy(cts))
                return;
            _errorDetail = e.Message;
            _phase = Phase.Failed;
            RaiseAll();
        }
    }

    /// <summary>A copy to a size ended; true when it still counts (not stopped, the panel not moved on).</summary>
    bool EndCopy(CancellationTokenSource cts)
    {
        if (_copyCts != cts)
            return false;
        _copyCts = null;
        _clipCopying = false;
        RaiseCopy();
        return !cts.IsCancellationRequested;
    }

    void ShowCopied()
    {
        int show = ++_copiedShow;
        _clipCopied = true;
        RaiseCopy();
        _host.After(TimeSpan.FromSeconds(1.8), () =>
        {
            if (show != _copiedShow)
                return;
            _clipCopied = false;
            RaiseCopy();
        });
    }

    void RaiseCopy()
    {
        Raise(nameof(IsCopying));
        Raise(nameof(IsCopied));
        Raise(nameof(IsCopyConverting));
        Raise(nameof(CopyProgress));
        Raise(nameof(CopyPercent));
        Raise(nameof(CopyTooLong));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Puts <paramref name="emoji"/> in the caption over the selection (or at the caret); returns the caret after it.</summary>
    public int InsertEmoji(string emoji, int selectionStart, int selectionLength)
    {
        int start = Math.Clamp(selectionStart, 0, _caption.Length);
        int length = Math.Clamp(selectionLength, 0, _caption.Length - start);
        Caption = _caption[..start] + emoji + _caption[(start + length)..];
        // The box shows it now (typing in the box needs no such call: the box is where the text came from).
        Raise(nameof(Caption));
        return start + emoji.Length;
    }

    // Watching the cut
    public bool IsPlaying => _playing;
    public string PlayLabel => Text.Get(_playing ? "share.pause" : "share.play");
    /// <summary>The playhead line: while playing, or once it has moved off the start of the cut.</summary>
    public bool ShowPlayhead => _info != null && (_playing || _playhead > _trimStart + 0.05);
    /// <summary>The round play mark in the middle of the paused picture (a click on the picture plays).</summary>
    public bool ShowPlayMark => _info != null && CanEdit && !_playing;

    /// <summary>Where the playback is (or starts): seconds into the clip, always inside the cut. Setting it seeks.</summary>
    public double Playhead
    {
        get => _playhead;
        set
        {
            if (_info == null)
                return;
            _playhead = Math.Clamp(value, _trimStart, _trimEnd);
            if (_playing)
            {
                StopPlayback();
                StartPlayback();
            }
            else
            {
                _previewTime = _playhead;
                RequestPreview();
            }
            RaisePlayhead();
        }
    }

    void RaisePlayhead()
    {
        Raise(nameof(Playhead));
        Raise(nameof(ShowPlayhead));
    }

    /// <summary>From the playhead to the end of the cut; from the start when the playhead is at the end.</summary>
    void StartPlayback()
    {
        if (_info == null || _playing)
            return;
        double from = _playhead >= _trimStart && _playhead < _trimEnd - 0.05 ? _playhead : _trimStart;
        int session = ++_playSession;
        _playing = true;
        _playhead = from;
        int height = (int)Math.Round(PreviewPixels / PreviewAspect) & ~1;
        BitmapSource? picture = null;
        picture = _host.Play(_clip, from, _trimEnd, PreviewPixels, height, t =>
        {
            if (session != _playSession)
                return;
            // The player's picture replaces the still once its first frame is in (no black flash).
            if (picture != null && _previewFrame != picture)
            {
                _previewFrame = picture;
                Raise(nameof(PreviewFrame));
            }
            _playhead = Math.Clamp(t, _trimStart, _trimEnd);
            RaisePlayhead();
        }, _ =>
        {
            if (session != _playSession)
                return;
            _playing = false;
            RaisePlayback();
        });
        RaisePlayback();
    }

    void StopPlayback()
    {
        if (!_playing)
            return;
        _playSession++;
        _playing = false;
        _host.StopPlaying();
        RaisePlayback();
    }

    void RaisePlayback()
    {
        Raise(nameof(IsPlaying));
        Raise(nameof(PlayLabel));
        Raise(nameof(ShowPlayMark));
        RaisePlayhead();
    }

    /// <summary>Small frames along the whole clip under the trim bar (filled in as they are read).</summary>
    public System.Collections.ObjectModel.ObservableCollection<BitmapSource?> Filmstrip { get; } = [];
    public const int FilmstripFrames = 10;

    /// <summary>The frame at the handle being moved (or the start), shown in the big preview.</summary>
    public BitmapSource? PreviewFrame => _picture?.Image ?? _previewFrame ?? _thumbnail;
}
