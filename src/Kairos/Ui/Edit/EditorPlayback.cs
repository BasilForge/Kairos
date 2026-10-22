using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kairos.Engine.Edit;
using Kairos.Engine.Share;

namespace Kairos.Ui.Edit;

/// <summary>What the preview plays: one sound track at a volume, or several mixed, each at its gain (0-based tracks).</summary>
public sealed record SoundPlan(IReadOnlyList<int> Tracks, IReadOnlyList<double> Gains)
{
    public bool Mixed => Tracks.Count > 1;

    /// <summary>The player's volume: one track's level, or 1 for tracks mixed at their own gains.</summary>
    public double Volume => Mixed ? 1 : Gains[0];

    public static SoundPlan One(int track, double volume) => new([track], [volume]);
}

/// <param name="OutputStart">Where the range starts in the edited clip.</param>
/// <param name="Part">The kept part it plays (its speed and sound).</param>
public sealed record PlayRange(double SourceStart, double SourceEnd, double Factor, double OutputStart, ClipSpan Part);

/// <summary>
/// The editor's preview. Playing goes through the kept parts one after another (a cut is skipped), each at its speed and
/// its sound level (the sound at the part's speed, same pitch, as saved), with the colour look and the blurred boxes on
/// every picture; the next part is read ahead, so it goes on straight from the one before. Paused, it shows the picture
/// at the playhead the same way. A screenshot is its own picture. Runs only while the editor is open.
/// </summary>
public sealed class EditorPlayback : IDisposable
{
    const int Width = 1280;
    readonly EditorViewModel _vm;
    readonly Action<BitmapSource?> _show;
    readonly Action<bool> _showPlaying;
    readonly IPlayerSession _session;
    readonly DispatcherTimer _stillTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    readonly int _height;
    FrameGrabber? _grabber;
    FrameFilter? _stillColour;
    /// <summary>
    /// Reads still pictures: decoding a moment takes tens of milliseconds, too long for the window's thread. One request
    /// at a time, a newer one replacing one not read yet. Once started it owns the frame reader and the still's colour filter.
    /// </summary>
    Thread? _stillThread;
    readonly AutoResetEvent _stillWanted = new(false);
    readonly object _stillLock = new();
    (EditProject Project, double Source, int Run)? _stillRequest;
    int _stillRun;
    volatile bool _stillStop;
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    byte[]? _picture;
    int _pictureWidth, _pictureHeight;
    List<PlayRange> _queue = [];
    int _run;
    bool _playing;
    /// <summary>It was playing when the user started moving the playhead: it plays on from there when they let go.</summary>
    bool _resume;

    public EditorPlayback(EditorViewModel vm, Action<BitmapSource?> show, Action<bool> showPlaying)
        : this(vm, show, showPlaying, new PlayerSession())
    {
    }

    internal EditorPlayback(EditorViewModel vm, Action<BitmapSource?> show, Action<bool> showPlaying, IPlayerSession session)
    {
        _session = session;
        _vm = vm;
        _show = show;
        _showPlaying = showPlaying;
        _height = Math.Max(2, (int)Math.Round(Width / Math.Max(0.1, vm.Aspect)) & ~1);
        _stillTimer.Tick += (_, _) =>
        {
            _stillTimer.Stop();
            ShowStill();
        };
        vm.PlayToggleRequested += Toggle;
        vm.PropertyChanged += OnChanged;
        vm.SeekStarted += OnSeekStarted;
        vm.SeekEnded += OnSeekEnded;
        if (vm.IsPicture)
            LoadPicture(vm.Project.Source);
        ScheduleStill();
    }

    /// <summary>A still picture was read, as it is before any look or blur, shrunk for the look tiles.</summary>
    public event Action<byte[]>? StillRead;

    /// <summary>The thread the last still picture was read on (tests: never the window's).</summary>
    internal int StillReadOn { get; private set; }

    /// <summary>Pictures the player has shown, and how the last play went (the measuring harness).</summary>
    internal int FramesShown => _session.FramesShown;
    internal string PlayReport => _session.Report;

    void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Project))
        {
            if (_playing)
                PlayOnWith(_vm.Project);
            else
                ScheduleStill();
        }
        else if (e.PropertyName == nameof(EditorViewModel.PlayheadOutput) && !_playing)
            ScheduleStill();
    }

    /// <summary>A click, drag or arrow key on the playhead: while it moves, the preview shows still pictures.</summary>
    void OnSeekStarted()
    {
        if (!_playing)
            return;
        _resume = true;
        Pause();
    }

    void OnSeekEnded()
    {
        if (!_resume)
            return;
        _resume = false;
        // Already playing again (Space during the drag), or let go at the very end: nothing to resume.
        if (_playing || _vm.PlayheadOutput >= _vm.Project.OutputDuration - 0.05)
            return;
        Play();
    }

    // ---------------------------------------------------------------- pure parts (tested)

    /// <summary>The kept parts from the playhead on, each with where it plays and how fast.</summary>
    public static IReadOnlyList<PlayRange> Ranges(EditProject p, double fromOutput)
    {
        var ranges = new List<PlayRange>();
        double at = 0;
        foreach (var (span, factor) in p.Pieces())
        {
            double length = span.Length / factor;
            if (at + length > fromOutput + 1e-9)
            {
                double skip = Math.Max(0, fromOutput - at);
                ranges.Add(new PlayRange(span.Start + skip * factor, span.End, factor, at + skip, span));
            }
            at += length;
        }
        return ranges;
    }

    /// <summary>The colour look (through <paramref name="colour"/>) and the blurred boxes showing at <paramref name="source"/>.</summary>
    public static void Filter(EditProject p, FrameFilter? colour, byte[] bgra, int width, int height, double source)
    {
        colour?.Apply(bgra);
        foreach (var blur in p.Items.OfType<BlurItem>())
            if (EditGeometry.ShowsAt(p, blur, source))
                FrameFilter.Blur(bgra, width, height, new Int32Rect((int)(blur.Box.X * width), (int)(blur.Box.Y * height),
                    (int)(blur.Box.Width * width), (int)(blur.Box.Height * height)));
    }

    /// <summary>
    /// What the preview plays for a part: the clip's own tracks mixed as the saved clip mixes them, each at the clip's level
    /// times the part's (a muted one at 0), so any level is heard at once without starting again; a one-track clip: its
    /// sound at its level.
    /// </summary>
    public static SoundPlan PreviewSound(SoundLevels s, ClipSpan part, Engine.Audio.TrackLayout layout)
    {
        var tracks = layout.PartitionTracks;
        return tracks.Count == 0 ? SoundPlan.One(0, s.All * part.Levels[0])
            : new SoundPlan(tracks, [.. Enumerable.Range(0, tracks.Count).Select(j => s.Tracks.Heard(j) * part.Levels[j])]);
    }

    /// <summary>The playing range's part as it is in <paramref name="p"/> (its sound may have changed).</summary>
    static ClipSpan PartIn(EditProject p, PlayRange range) =>
        p.Kept.FirstOrDefault(k => Math.Abs(k.Start - range.Part.Start) < 1e-9 && Math.Abs(k.End - range.Part.End) < 1e-9) ?? range.Part;

    // ---------------------------------------------------------------- playing

    public void Toggle()
    {
        _resume = false; // the user chose: play or pause stays as they said
        if (_playing)
            Pause();
        else
            Play();
    }

    void Play()
    {
        if (_vm.IsPicture)
            return;
        double from = _vm.PlayheadOutput >= _vm.Project.OutputDuration - 0.05 ? 0 : _vm.PlayheadOutput;
        _queue = Ranges(_vm.Project, from).ToList();
        if (_queue.Count == 0)
            return;
        _playing = true;
        _showPlaying(true);
        PlayRange(++_run, 0);
    }

    /// <summary>The edit the playing pictures are made with (read by the player's decoding thread).</summary>
    EditProject? _live;

    /// <summary>
    /// An edit while playing keeps it playing: texts and drawings are drawn over the picture as they are, a colour look or
    /// blur box is in the next picture, a sound level is heard at once. Only new cuts, speeds or another sound track
    /// start the parts again, from the playhead.
    /// </summary>
    void PlayOnWith(EditProject next)
    {
        var playing = _live;
        Volatile.Write(ref _live, next);
        bool replan = playing == null || _part == null
            || !playing.Kept.Select(k => (k.Start, k.End, k.Speed)).SequenceEqual(next.Kept.Select(k => (k.Start, k.End, k.Speed)));
        if (!replan)
        {
            var before = SoundOf(playing!, _part!.Index);
            var now = SoundOf(next, _part.Index);
            if (before.Tracks.SequenceEqual(now.Tracks))
            {
                Hear(now); // the part read ahead takes its levels as they are when it goes on (GoneOn)
                return;
            }
        }
        _run++;
        Play();
    }

    /// <summary>
    /// A part given to the player: its tracks mixed, and the colour look its pictures are made with on that player's
    /// decoding thread (the part playing and the one read ahead never share one: a look takes one caller at a time).
    /// </summary>
    sealed class Part(int index, SoundPlan sound) : IDisposable
    {
        public int Index { get; } = index;
        /// <summary>The tracks mixed (their gains change while they play), or null for one track.</summary>
        public TrackMix? Mix { get; } = sound.Mixed ? new TrackMix(sound.Tracks, sound.Gains) : null;
        public FrameFilter? Colour;

        public void Dispose()
        {
            Colour?.Dispose(); // its player has stopped (Stop waits for its decoding thread)
            Colour = null;
        }
    }

    /// <summary>The part playing, and the one read ahead to go on from it.</summary>
    Part? _part, _nextPart;

    /// <summary>Seconds before a part ends that the next one is read ahead (opening the clip and reading up to the cut
    /// take up to half a second).</summary>
    const double ReadAhead = 2;

    /// <summary>The levels of what plays now: one track's volume, or each mixed track's gain.</summary>
    void Hear(SoundPlan sound)
    {
        if (_part?.Mix is { } mix && sound.Mixed)
            mix.Gains = sound.Gains;
        _session.Volume = sound.Volume;
    }

    SoundPlan SoundOf(EditProject p, int index) => PreviewSound(p.Sound, PartIn(p, _queue[index]), _vm.Layout);

    /// <summary>A playing picture of <paramref name="part"/> with the edit as it is now (on its player's decoding thread).</summary>
    void FilterLive(Part part, byte[] pixels, double time)
    {
        var project = Volatile.Read(ref _live);
        if (project == null)
            return;
        string? look = LookFor(project, time);
        if (look != null)
        {
            part.Colour ??= new FrameFilter(Width, _height, look);
            part.Colour.SetFilter(look);
        }
        Filter(project, look == null ? null : part.Colour, pixels, Width, _height, time);
    }

    void PlayRange(int run, int index)
    {
        _session.Stop(); // the old players' decoding threads may be using their colour looks: stop them before freeing those
        DisposeParts();
        Volatile.Write(ref _live, _vm.Project);
        _part = Give(run, index, next: false);
        if (_part == null)
            Pause();
    }

    /// <summary>
    /// Gives part <paramref name="index"/> to the player: to play now, or (<paramref name="next"/>) read ahead to go on when
    /// the playing one ends. Null when the player cannot take it.
    /// </summary>
    Part? Give(int run, int index, bool next)
    {
        var project = _vm.Project;
        var range = _queue[index];
        var sound = SoundOf(project, index);
        var part = new Part(index, sound);
        Action<WriteableBitmap> show = picture =>
        {
            if (run == _run)
                _show(picture);
        };
        Action<double> position = t =>
        {
            if (run != _run)
                return;
            _vm.PlayheadOutput = range.OutputStart + (t - range.SourceStart) / range.Factor;
            if (_nextPart == null && index + 1 < _queue.Count && (range.SourceEnd - t) / range.Factor <= ReadAhead)
                _nextPart = Give(run, index + 1, next: true);
        };
        Action<string?> ended = failure =>
        {
            if (run != _run)
                return;
            if (failure != null)
                Pause();
            else if (_nextPart != null)
                GoneOn();
            else if (index + 1 < _queue.Count)
                PlayRange(run, index + 1);
            else
                Pause();
        };
        Action<byte[], double> filter = (pixels, time) => FilterLive(part, pixels, time);
        bool given;
        if (next)
            given = _session.Queue(project.Source, range.SourceStart, range.SourceEnd, Width, _height, show, position, ended, range.Factor, filter, part.Mix, sound.Volume);
        else
        {
            _session.Volume = sound.Volume; // before it starts: its first sound is read at once
            given = _session.Start(project.Source, range.SourceStart, range.SourceEnd, Width, _height, show, position, ended, range.Factor, filter, part.Mix);
        }
        if (given)
            return part;
        part.Dispose();
        return null;
    }

    /// <summary>The playing part ended and the one read ahead went on (the player switched to it), at its levels as they are now.</summary>
    void GoneOn()
    {
        _part?.Dispose();
        (_part, _nextPart) = (_nextPart, null);
        Hear(SoundOf(_vm.Project, _part!.Index));
    }

    void Pause()
    {
        _run++;
        _session.Stop();
        DisposeParts();
        if (_playing)
        {
            _playing = false;
            _showPlaying(false);
        }
        ScheduleStill();
    }

    /// <summary>Frees the parts' colour looks once their players have stopped.</summary>
    void DisposeParts()
    {
        _part?.Dispose();
        _nextPart?.Dispose();
        _part = _nextPart = null;
    }

    // ---------------------------------------------------------------- the still picture

    void ScheduleStill()
    {
        _stillTimer.Stop();
        _stillTimer.Start(); // a burst of changes (a slider, a drag) shows once it settles
    }

    void ShowStill()
    {
        if (_playing)
            return;
        var p = _vm.Project;
        if (_vm.IsPicture)
        {
            if (_picture == null)
                return;
            var pixels = (byte[])_picture.Clone();
            StillRead?.Invoke(LookThumbs.Shrink(pixels, _pictureWidth, _pictureHeight));
            Colour(_pictureWidth, _pictureHeight, LookFor(p, 0));
            Filter(p, _stillColour, pixels, _pictureWidth, _pictureHeight, 0);
            _show(Bitmap(pixels, _pictureWidth, _pictureHeight));
            return;
        }
        try
        {
            ClipTranscoder.RequireLoaded();
        }
        catch (ShareException)
        {
            return; // FFmpeg is missing: the picture the editor opened with stays
        }
        // Read on its own thread (decoding a moment takes tens of milliseconds); the newest moment asked for wins.
        lock (_stillLock)
            _stillRequest = (p, p.ToSource(_vm.PlayheadOutput) ?? 0, ++_stillRun);
        if (_stillThread == null)
        {
            _stillThread = new Thread(ReadStills) { IsBackground = true, Name = "Kairos editor still" };
            _stillThread.Start();
        }
        _stillWanted.Set();
    }

    static string? LookFor(EditProject p, double source)
    {
        var (look, strength) = p.LookAt(source); // a part may have its own look
        return ColourLooks.Filter(look, strength);
    }

    /// <summary>The still-reading thread: each moment's picture read, coloured and blurred; the window's thread shows it.</summary>
    void ReadStills()
    {
        try
        {
            while (true)
            {
                _stillWanted.WaitOne();
                if (_stillStop)
                    return;
                (EditProject Project, double Source, int Run) request;
                lock (_stillLock)
                {
                    if (_stillRequest is not { } wanted)
                        continue;
                    request = wanted;
                    _stillRequest = null;
                }
                try
                {
                    _grabber ??= new FrameGrabber(request.Project.Source);
                    if (_grabber.Grab(request.Source, Width, exact: true) is not { } frame)
                        continue;
                    StillReadOn = Environment.CurrentManagedThreadId;
                    var small = LookThumbs.Shrink(frame.Bgra, frame.Width, frame.Height);
                    Colour(frame.Width, frame.Height, LookFor(request.Project, request.Source));
                    Filter(request.Project, _stillColour, frame.Bgra, frame.Width, frame.Height, request.Source);
                    var picture = Bitmap(frame.Bgra, frame.Width, frame.Height);
                    int run = request.Run;
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (run != _stillRun || _playing || _stillStop)
                            return; // another moment was asked for since, or it is playing: not this one
                        StillRead?.Invoke(small);
                        _show(picture);
                    });
                }
                catch (Exception e) when (e is ShareException or IOException or InvalidOperationException)
                {
                    // The file is gone or cannot be read: the picture shown stays.
                }
            }
        }
        finally
        {
            _grabber?.Dispose();
            _grabber = null;
            _stillColour?.Dispose();
            _stillColour = null;
            _stillWanted.Dispose();
        }
    }

    void Colour(int width, int height, string? look)
    {
        if (_stillColour == null)
            _stillColour = new FrameFilter(width, height, look);
        else
            _stillColour.SetFilter(look);
    }

    void LoadPicture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            _pictureWidth = converted.PixelWidth;
            _pictureHeight = converted.PixelHeight;
            _picture = new byte[_pictureWidth * _pictureHeight * 4];
            converted.CopyPixels(_picture, _pictureWidth * 4, 0);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException)
        {
            _picture = null;
        }
    }

    static BitmapSource Bitmap(byte[] bgra, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return; // saving lets go of the preview, then the editor closing does again
        _disposed = true;
        _run++;
        _stillTimer.Stop();
        _vm.PlayToggleRequested -= Toggle;
        _vm.PropertyChanged -= OnChanged;
        _vm.SeekStarted -= OnSeekStarted;
        _vm.SeekEnded -= OnSeekEnded;
        _session.Stop();
        DisposeParts();
        _stillStop = true;
        _stillRun++; // a still read after this is not shown
        if (_stillThread != null)
        {
            _stillWanted.Set(); // the reading thread ends and lets go of the frame reader and the still's filter
            return;
        }
        _stillColour?.Dispose();
        _stillColour = null;
        _grabber?.Dispose();
        _grabber = null;
        _stillWanted.Dispose();
    }
}
