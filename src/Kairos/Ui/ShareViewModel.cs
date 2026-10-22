using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Share;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>A screenshot opened for sharing: the picture to show (null if unreadable), its size in pixels and in bytes.</summary>
public sealed record SharePicture(BitmapSource? Image, int Width, int Height, long Bytes);

/// <summary>What the share panel needs from the outside world (FFmpeg, clipboard, browser, settings, threads).</summary>
public interface IShareHost
{
    /// <summary>Where Copy keeps its copies: the clips folder's "Shared" (<see cref="ShareFiles.SharedIn"/>).</summary>
    string SharedFolder { get; }
    MediaInfo Probe(string clip);
    /// <summary>Reads a picture for the panel; null when it cannot be read.</summary>
    SharePicture? ReadPicture(string path);
    /// <summary>A JPEG copy of the picture at <paramref name="target"/>, no bigger than <paramref name="maxBytes"/>.</summary>
    ShareResult MakeJpeg(string source, string target, long maxBytes);
    /// <summary>Puts the picture on the clipboard as an image and as the file.</summary>
    void CopyPicture(string path);
    ShareResult Transcode(string source, string target, SharePlan plan, long maxBytes, IProgress<double> progress, CancellationToken ct);
    /// <summary>Copies <paramref name="start"/>..<paramref name="end"/> of the clip as it is (no conversion; from the key frame at or before the start).</summary>
    ShareResult Cut(string source, string target, double start, double end, IProgress<double> progress, CancellationToken ct);
    void CopyToClipboard(string? file, string? text);
    /// <summary>Opens a web page in the normal browser.</summary>
    void Launch(string target);
    /// <summary>
    /// Posts the file with the caption on X, TikTok or Instagram through the site's page in Kairos's own (off-screen)
    /// window. <paramref name="status"/> gets text keys as it goes. Runs on the UI thread.
    /// </summary>
    Task<PublishOutcome> Publish(SharePlatform platform, string file, string caption, Action<string> status, CancellationToken ct);
    /// <summary>Brings up the site window left open for the user to finish a post.</summary>
    void RevealSite(SharePlatform platform);
    void ShowInFolder(string path);
    /// <summary>Saves the share choices right away (they are not part of the Save/Revert bar).</summary>
    void Remember(ShareSettings prefs);
    void Post(Action action);
    void Run(Action work);
    /// <summary>Reads frames of the clip at <paramref name="times"/> (seconds), <paramref name="width"/> pixels wide, in the
    /// background; <paramref name="done"/> gets each one (index, image or null) on the UI thread. A newer call replaces
    /// older pending ones. <paramref name="exact"/>: the exact frame (else the nearest key frame, much faster).</summary>
    void GrabFrames(string clip, double[] times, int width, bool exact, Action<int, BitmapSource?> done);
    /// <summary>The panel closed: let go of the clip file.</summary>
    void ReleaseFrames();
    /// <summary>
    /// Plays the clip from <paramref name="from"/> to <paramref name="to"/> (seconds) with its sound, the picture
    /// <paramref name="width"/>×<paramref name="height"/>. Returns the picture it keeps drawing into. On the UI thread,
    /// <paramref name="position"/> gets the time shown and <paramref name="ended"/> runs once at the end (null) or on a
    /// failure (the reason). A newer call or <see cref="StopPlaying"/> stops it without calling <paramref name="ended"/>.
    /// </summary>
    BitmapSource? Play(string clip, double from, double to, int width, int height, Action<double> position, Action<string?> ended);
    void StopPlaying();
    /// <summary>Loudness of the preview playback only (0..1), now and for the next playbacks.</summary>
    void SetPlaybackVolume(double volume);
    /// <summary>Runs <paramref name="action"/> on the UI thread after <paramref name="delay"/>.</summary>
    void After(TimeSpan delay, Action action);
    string Folder { get; }
}

/// <summary>
/// The share panel: pick a platform, trim the clip (and watch the cut), a picture size and an optional 9:16 crop.
/// The clip is converted to what that platform takes; X, TikTok and Instagram then get it posted by Kairos through
/// their own pages (the user signs in once). Copy makes an MP4 every app plays and puts it on the clipboard. No
/// service APIs.
/// </summary>
public sealed partial class ShareViewModel : INotifyPropertyChanged
{
    enum Phase { Choosing, Working, Publishing, Done, Failed }

    /// <summary>Width in pixels of the big picture (frames read for it and the playback), sharp on scaled screens.</summary>
    public const int PreviewPixels = 960;

    readonly IShareHost _host;
    readonly Func<ShareSettings> _prefs;
    string _clip = "";
    MediaInfo? _info;
    /// <summary>Set when a screenshot is open instead of a video.</summary>
    SharePicture? _picture;
    Phase _phase;
    bool _isOpen;
    /// <summary>Nothing until the user picks where to share.</summary>
    SharePlatform? _platform;
    double _trimStart, _trimEnd;
    bool? _xPremium;
    double _previewTime;
    BitmapSource? _previewFrame;
    bool _clipCopying, _clipCopied;
    /// <summary>Copy made to fit this many MB (0: as it is).</summary>
    int _copyMb;
    /// <summary>A copy being made to fit a size; pressing Copy again or closing the panel stops it.</summary>
    CancellationTokenSource? _copyCts;
    double _copyProgress;
    /// <summary>The last copy made to fit a size, and for which size and cut (what a drag takes while they still match).</summary>
    (int Mb, double Start, double End, string Path)? _prepared;
    double _volume = 1;
    bool _muted;
    int _copiedShow;
    bool _trimMoved;
    readonly HashSet<SharePlatform> _verticalFor = [];
    bool _playing;
    int _playSession;
    double _playhead;
    double _crop = 0.5;
    string _caption = "";
    double _progress;
    string _errorDetail = "";
    bool _copied;
    ShareResult? _result;
    SharePlatform _resultPlatform;
    string _resultCaption = "";
    CancellationTokenSource? _cts;
    BitmapSource? _thumbnail;
    string _publishStatus = "share.pub.opening";
    PublishOutcome? _outcome;

    public ShareViewModel(IShareHost host, Func<ShareSettings> prefs)
    {
        _host = host;
        _prefs = prefs;
        PrepareCommand = new RelayCommand(_ => Prepare(), _ => IsChoosing && HasPlatform);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel());
        CloseCommand = new RelayCommand(_ => Close());
        RetryCommand = new RelayCommand(_ => Retry());
        CopyCaptionCommand = new RelayCommand(_ => { _host.CopyToClipboard(null, Caption); _copied = true; Raise(nameof(CopiedVisible)); });
        CopyCommand = new RelayCommand(_ => PressCopy(), _ => CanCopy && (_copyCts != null || !_clipCopying));
        CopyAtCommand = new RelayCommand(p => { if (p is int mb) CopyAt(mb); }, _ => CanCopy && !_clipCopying);
        TrimToFitCommand = new RelayCommand(_ => TrimToFit(), _ => _copySizePicked && TooLongForSize && CanEdit);
        MuteCommand = new RelayCommand(_ => ToggleMute());
        CopyFileCommand = new RelayCommand(_ => { if (_result != null) _host.CopyToClipboard(_result.Path, null); });
        ShowFileCommand = new RelayCommand(_ => { if (_result != null) _host.ShowInFolder(_result.Path); });
        OpenAgainCommand = new RelayCommand(_ => OpenDestination());
        PlayCommand = new RelayCommand(_ => { if (_playing) StopPlayback(); else StartPlayback(); }, _ => HasVideo && CanEdit);
        EditCommand = new RelayCommand(_ =>
        {
            StopPlayback();
            _host.ReleaseFrames();
            EditRequested?.Invoke(_clip);
        }, _ => CanEdit && (HasVideo || IsPicture));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public ICommand PrepareCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CopyCaptionCommand { get; }
    public ICommand CopyFileCommand { get; }
    public ICommand ShowFileCommand { get; }
    public ICommand OpenAgainCommand { get; }
    public ICommand PlayCommand { get; }
    /// <summary>
    /// Copy pressed: a video asks its size (<see cref="CopySizeAsked"/>), a screenshot is copied as it is; pressed again
    /// while a copy is made, it stops.
    /// </summary>
    public ICommand CopyCommand { get; }
    /// <summary>
    /// Copies the cut at the size picked (the MB as the parameter, kept for next time): as it is (the clip itself, or the
    /// part cut without converting), or made to fit the size.
    /// </summary>
    public ICommand CopyAtCommand { get; }
    /// <summary>Copy was pressed on a video: the panel shows the sizes (<see cref="CopySizeChoices"/>) under it.</summary>
    public event Action? CopySizeAsked;
    /// <summary>Shortens the cut to what fits the size, keeping its end.</summary>
    public ICommand TrimToFitCommand { get; }
    /// <summary>Mutes or unmutes the preview (the clip's own sound is never changed).</summary>
    public ICommand MuteCommand { get; }

    /// <summary>The clip (or edited copy) the panel shares.</summary>
    public string ClipPath => _clip;

    /// <summary>Edit was pressed: the window opens the editor on this clip; what it saves is shared from then on.</summary>
    public event Action<string>? EditRequested;
    public ICommand EditCommand { get; }

    /// <summary>
    /// Shares <paramref name="savedPath"/>, an edit of <paramref name="forClip"/> saved from this panel's editor (a real clip:
    /// a new copy, or the original replaced), from now on; the game stays the clip's. It is never removed (it is the
    /// user's clip); one that finished after the panel closed or moved on is just not shown.
    /// </summary>
    /// <returns>Whether the panel took it.</returns>
    public bool UseSaved(string savedPath, string forClip)
    {
        if (!_isOpen || !string.Equals(ClipPath, forClip, StringComparison.OrdinalIgnoreCase))
            return false;
        string game = Game;
        var thumbnail = Thumbnail;
        Open(savedPath, thumbnail);
        Game = game;
        _caption = SharePlanner.Caption(Game, _prefs().Tags);
        RaiseAll();
        return true;
    }

    /// <summary>Lets go of the clip (its preview and frames), so it can be replaced by its edit; it opens again with it.</summary>
    public void Release()
    {
        StopPlayback();
        _host.ReleaseFrames();
    }

    public void Open(string clipPath, BitmapSource? thumbnail = null)
    {
        if (_phase is Phase.Working or Phase.Publishing && _isOpen)
            return; // one share at a time; the panel already shows it
        StopPlayback();
        var prefs = _prefs();
        _clip = clipPath;
        Thumbnail = thumbnail;
        _platform = null;
        _clipCopied = false;
        _copyCts?.Cancel();
        _copyCts = null;
        _clipCopying = false;
        _prepared = null;
        _copyMb = prefs.CopyMb;
        _copySizePicked = false;
        Jobs.Clear();
        _volume = prefs.PreviewVolume;
        _muted = prefs.PreviewMuted;
        ApplyVolume();
        _xPremium = prefs.XPremium;
        _verticalFor.Clear();
        _verticalFor.UnionWith(prefs.VerticalFor);
        _crop = prefs.CropPosition;
        Game = SharePlanner.GameFromFile(clipPath);
        _caption = SharePlanner.Caption(Game, prefs.Tags);
        _result = null;
        _outcome = null;
        _errorDetail = "";
        _progress = 0;
        _copied = false;
        _info = null;
        _picture = null;
        if (ClipLibrary.KindOf(clipPath) == ClipKind.Picture)
        {
            // A screenshot: no trim, no player; the picture itself is the preview.
            _picture = _host.ReadPicture(clipPath);
            _previewFrame = null;
            Filmstrip.Clear();
            if (_picture == null)
            {
                _errorDetail = Text.Get("share.pictureUnreadable");
                _phase = Phase.Failed;
            }
            else
                _phase = Phase.Choosing;
            _isOpen = true;
            RaiseAll();
            return;
        }
        try
        {
            _info = _host.Probe(clipPath);
            _phase = Phase.Choosing;
            (_trimStart, _trimEnd) = SharePlanner.DefaultTrim(_info.Duration, MaxSeconds);
            _trimMoved = false;
            _previewTime = _playhead = _trimStart;
            _previewFrame = null;
            Filmstrip.Clear();
            RequestFrames();
        }
        catch (Exception e)
        {
            _info = null;
            _errorDetail = e.Message;
            _phase = Phase.Failed;
        }
        _isOpen = true;
        RaiseAll();
    }

    public void Close()
    {
        _cts?.Cancel();
        _copyCts?.Cancel();
        StopPlayback();
        RememberVolume();
        _host.ReleaseFrames();
        _isOpen = false;
        RaiseAll();
    }

    // Panel state
    public bool IsOpen => _isOpen;
    public bool IsChoosing => _phase == Phase.Choosing;
    public bool IsWorking => _phase == Phase.Working;
    public bool IsDone => _phase == Phase.Done;
    public bool IsFailed => _phase == Phase.Failed;
    /// <summary>Choices stay usable after a share: changing one starts the next share.</summary>
    public bool CanEdit => IsChoosing || IsDone;
    /// <summary>Something to share: a video or a screenshot.</summary>
    public bool HasClip => _info != null || _picture != null;
    /// <summary>A video: trim, playback, sound and 9:16 apply.</summary>
    public bool HasVideo => _info != null;
    public bool IsPicture => _picture != null;
    public string Title => Text.Get(IsPicture ? "share.titlePicture" : "share.title");
    /// <summary>The preview's loudness shows for a video that can be played now.</summary>
    public bool CanPlay => HasVideo && CanEdit;
    /// <summary>9:16 is for videos once a platform is chosen.</summary>
    public bool ShowVertical => HasPlatform && HasVideo;

    // The clip
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; Raise(nameof(Thumbnail)); Raise(nameof(PreviewFrame)); }
    }
    public string Game { get; private set; } = "";

    // Choices
    public SharePlatform? Platform
    {
        get => _platform;
        set { _platform = value; _copied = false; FitTrimToPlatform(); StopPlayback(); Edited(); }
    }

    public bool HasPlatform => _platform != null;

    // Trim: the part of the clip that is shared
    public double ClipSeconds => _info?.Duration ?? 0;

    /// <summary>Seconds into the clip where the shared part starts.</summary>
    public double TrimStart
    {
        get => _trimStart;
        // A start handle pushed against the end stops a second before it.
        set { double start = Math.Min(value, _trimEnd - SharePlanner.MinTrimSeconds); SetTrim(start, _trimEnd, start); }
    }

    /// <summary>Seconds into the clip where the shared part ends.</summary>
    public double TrimEnd
    {
        get => _trimEnd;
        // An end handle pulled back to the start stops a second after it.
        set { double end = Math.Max(value, _trimStart + SharePlanner.MinTrimSeconds); SetTrim(_trimStart, end, end); }
    }

    /// <summary>Bound to the trim bar's middle: setting it moves the whole cut.</summary>
    public double TrimMoveStart
    {
        get => _trimStart;
        set => MoveTrim(value);
    }

    /// <summary>Moves the whole cut (dragging its middle), keeping its length.</summary>
    public void MoveTrim(double newStart)
    {
        double length = _trimEnd - _trimStart;
        newStart = Math.Clamp(newStart, 0, Math.Max(0, ClipSeconds - length));
        SetTrim(newStart, newStart + length, newStart);
    }

    // The line under the trim bar: where the cut is and about how big the file comes out
    public string TrimRange => _info == null ? "" : $"{ClockTenths(_trimStart)} – {ClockTenths(_trimEnd)}";
    /// <summary>
    /// An estimate, so a plain rounded number: "213 MB", "8.4 MB", "1.2 GB". Everywhere: the biggest of the files that
    /// go (TikTok's and Instagram's are the clip's own size).
    /// </summary>
    public string SizeEstimate => _info == null || _platform == null ? ""
        : RoundedSize(_platform == SharePlatform.All ? SiteAccounts.Sites.Max(s => EstimatedBytes(PlanFor(s))) : EstimatedBytes(CurrentPlan()));

    // Preview loudness: only what is heard here, never the clip
}
