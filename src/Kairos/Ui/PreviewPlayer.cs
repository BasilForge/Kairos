using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairos.Engine.Share;

namespace Kairos.Ui;

/// <summary>
/// Plays part of a clip in the share panel. A background thread decodes ahead: pictures into a small pool, sound
/// straight into the sound card's queue. <see cref="Tick"/> (each screen refresh, on the thread that made the player)
/// shows the picture due by the sound's clock and reports the time; it also reports the end or a failure, so every
/// callback runs on that thread. Without sound (or a sound card) a stopwatch is the clock.
/// </summary>
sealed class PreviewPlayer : IDisposable
{
    /// <summary>Pictures decoded ahead: about a third of a second at 60 fps.</summary>
    const int PoolSize = 20;
    /// <summary>What they may take (20 pictures at 1280×720, about 74 MB): a larger picture keeps fewer ahead.</summary>
    const long PoolBytes = PoolSize * 1280L * 720 * 4;

    /// <summary>How many pictures are decoded ahead at this size: within <see cref="PoolBytes"/>, at least six (a tenth of a second).</summary>
    internal static int PoolFor(int width, int height) => (int)Math.Clamp(PoolBytes / ((long)width * height * 4), 6, PoolSize);
    /// <summary>Sound queued before the clock starts, and the least kept queued while pictures wait for room.</summary>
    const double SoundLead = 0.15;

    /// <summary>Set when a picture's buffer comes back (or playing stops): the reading thread goes on at once.</summary>
    readonly AutoResetEvent _room = new(false);
    void Wake()
    {
        if (!_disposed)
            _room.Set();
    }

    /// <summary>The longest rest with every buffer full; the sound ahead is far longer than this.</summary>
    const int RestMs = 20;

    int _waits;
    bool _disposed;
    /// <summary>How many times the reading thread rested with every buffer full (tests: it does not spin).</summary>
    internal int Waits => Volatile.Read(ref _waits);

    readonly string _clip;
    readonly double _from, _to;
    readonly int _width, _height;
    readonly bool _mute;
    readonly Action<double> _position;
    readonly Action<string?> _ended;
    readonly Thread _thread;
    readonly object _lock = new();
    readonly Queue<(double Time, byte[] Pixels)> _ready = new();
    readonly Stack<byte[]> _free = new();
    readonly Stopwatch _watch = new();
    volatile SoundOut? _sound;
    int _late, _noRoom, _replaced;
    volatile bool _stop;
    volatile bool _decoded;
    volatile bool _clockStarted;
    /// <summary>Made to wait (<see cref="Release"/>); set and read under <see cref="_clockLock"/> with the clock's start.</summary>
    volatile bool _held;
    readonly object _clockLock = new();
    volatile string? _failure;
    bool _shownFirst, _finished;

    /// <param name="rate">Play faster or slower (the editor's speed): the sound at that speed and the same pitch.</param>
    /// <param name="filter">Changes each picture on the decoding thread before it is shown (the editor's colours and blur),
    /// with its time in the clip.</param>
    /// <param name="mix">Several sound tracks mixed at their gains (null: the first track, the full mix).</param>
    /// <param name="held">Reads ahead but shows, plays and reports nothing until <see cref="Release"/> (the editor's next part).</param>
    public PreviewPlayer(string clip, double from, double to, int width, int height, bool mute, Action<double> position, Action<string?> ended,
        double rate = 1, Action<byte[], double>? filter = null, TrackMix? mix = null, bool held = false)
    {
        _mix = mix;
        _rate = rate;
        _filter = filter;
        _clip = clip;
        _from = from;
        _to = to;
        _width = Math.Max(2, width & ~1);
        _height = Math.Max(2, height & ~1);
        _mute = mute;
        _position = position;
        _ended = ended;
        _held = held;
        Picture = new WriteableBitmap(_width, _height, 96, 96, PixelFormats.Bgra32, null);
        _thread = new Thread(Decode) { IsBackground = true, Name = "Kairos preview", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Loudness of what is heard (0..2, above 1 louder than recorded); applies to the sound decoded from now on
    /// (a fraction of a second ahead).</summary>
    public double Volume
    {
        get => Volatile.Read(ref _volume);
        set => Volatile.Write(ref _volume, Math.Clamp(value, 0, 2));
    }

    double _volume = 1;

    /// <summary>
    /// Plays a player made held: its first picture shows on the next <see cref="Tick"/> and its sound starts (it was
    /// read ahead meanwhile, so the part goes on at once from the one before).
    /// </summary>
    public void Release()
    {
        lock (_clockLock)
            _held = false;
        if (_decoded)
            StartClockWhenReady(force: true); // a short part read to its end while it waited: the reading thread has ended
        Wake();
    }

    /// <summary>The picture the player draws into.</summary>
    public WriteableBitmap Picture { get; }

    /// <summary>Pictures shown and pictures skipped because they came too late (for the self-test).</summary>
    public int Shown { get; private set; }
    public int Skipped => _late + _noRoom + _replaced;
    /// <summary>Why pictures were skipped: decoded after their time, no free buffer while the sound needed reading,
    /// or overtaken on screen by a newer one before a refresh came.</summary>
    public string SkipReasons => $"late {_late}, no room {_noRoom}, overtaken {_replaced}";

    // What was heard and how smoothly pictures came (for the self-check).
    readonly TrackMix? _mix;
    long _soundBytes;
    int _soundPeak;
    bool _hasAudio;
    readonly System.Diagnostics.Stopwatch _sinceShown = new();
    public double SoundSeconds => Interlocked.Read(ref _soundBytes) / (double)(ClipPlayback.SampleRate * ClipPlayback.BytesPerFrame);
    /// <summary>Loudest sample of the sound read (0 = silence), before any volume.</summary>
    public int SoundPeak => Volatile.Read(ref _soundPeak);
    public bool HasAudio => Volatile.Read(ref _hasAudio);
    /// <summary>The sound card is playing it (false: no sound output, the pictures run on the watch).</summary>
    public bool SoundOpen => _sound != null;
    /// <summary>Longest time between two pictures shown, and how many waits were over 50 ms.</summary>
    public double LongestGapMs { get; private set; }
    public int LongGaps { get; private set; }

    /// <summary>Seconds of the clip played since the clock started: the sound card's position (at the speed), or the watch
    /// when there is no sound.</summary>
    double Played() => (_sound != null ? _sound.PlayedSeconds : _watch.Elapsed.TotalSeconds) * _rate;

    readonly double _rate;
    /// <summary>The sound at another speed (decoding thread only).</summary>
    Engine.Edit.SoundTempo? _tempo;
    readonly Action<byte[], double>? _filter;

    double Clock() => _clockStarted ? _from + Played() : _from;

    void Decode()
    {
        // Made here, not on the window's thread (the editor's next part is made while one plays), and outside the lock
        // the window's Tick takes: tens of MB to clear.
        var pool = new byte[PoolFor(_width, _height)][];
        for (int i = 0; i < pool.Length; i++)
            pool[i] = new byte[_width * _height * 4];
        lock (_lock)
            foreach (var buffer in pool)
                _free.Push(buffer);
        try
        {
            using var playback = new ClipPlayback(_clip, _width, _height, mix: _mix);
            Volatile.Write(ref _hasAudio, playback.HasAudio);
            if (playback.HasAudio)
            {
                try
                {
                    if (Math.Abs(_rate - 1) > 1e-9)
                        _tempo = new Engine.Edit.SoundTempo(_rate);
                    _sound = new SoundOut(ClipPlayback.SampleRate, 2, _mute);
                }
                catch (Exception e) when (e is InvalidOperationException or ShareException)
                {
                    Engine.ObsLog.Warn("preview: no sound: " + e.Message);
                    _tempo?.Dispose();
                    _tempo = null;
                    _sound = null; // no sound card (or no speed filter): pictures only, on the watch
                }
            }
            playback.Seek(_from);
            bool picturesDone = false, soundDone = _sound == null;
            double frame = playback.FrameSeconds;
            while (!_stop && !(picturesDone && soundDone))
            {
                // The sound is the clock: keep it ahead first (read apart from the pictures, so this never costs one).
                if (!soundDone && SoundQueued() < 2 * SoundLead)
                    soundDone = !ReadSound(playback);
                else if (!picturesDone && FreeCount() > 0)
                    picturesDone = !ReadPicture(playback, frame);
                else if (!_clockStarted && !_held)
                    StartClockWhenReady(force: true); // every picture buffer filled before enough sound: start anyway
                else if (!soundDone && SoundQueued() < 6 * SoundLead)
                    soundDone = !ReadSound(playback); // idle: read a little further ahead
                else
                {
                    // Every buffer is full: rest until a picture is shown (its buffer comes back) or a little of the
                    // sound has been heard (the sound ahead never runs low: it is kept 0.6 s and more ahead here).
                    Interlocked.Increment(ref _waits);
                    _room.WaitOne(_held ? Timeout.Infinite : RestMs); // held: nothing changes until Release (or Dispose) wakes it
                }
                StartClockWhenReady();
            }
        }
        catch (Exception e) when (e is ShareException or IOException or InvalidOperationException)
        {
            Engine.ObsLog.Warn("preview: stopped: " + e.Message);
            _failure = e.Message;
        }
        finally
        {
            _tempo?.Dispose();
            _tempo = null;
            _decoded = true;
            StartClockWhenReady(force: true);
        }
    }

    int FreeCount()
    {
        lock (_lock)
            return _free.Count;
    }

    /// <summary>Seconds of sound queued and not yet heard (all of it before the clock starts).</summary>
    double SoundQueued() => _sound == null ? 0 : _sound.WrittenSeconds - (_clockStarted ? _sound.PlayedSeconds : 0);

    /// <summary>Queues the next sound piece of the cut; false when the cut's sound is all queued.</summary>
    bool ReadSound(ClipPlayback playback)
    {
        if (_sound == null)
            return false;
        double left = playback.NextSound() ? _to - playback.SoundTime : 0;
        if (left <= 0)
        {
            FlushTempo();
            return false;
        }
        var piece = playback.Sound;
        int keep = Math.Min(piece.Length, (int)(left * ClipPlayback.SampleRate) * ClipPlayback.BytesPerFrame);
        _sound.Write(_tempo != null ? _tempo.Push(piece[..keep]) : piece[..keep], Volume);
        Interlocked.Add(ref _soundBytes, keep);
        int peak = Peak(piece[..keep]);
        if (peak > _soundPeak)
            Volatile.Write(ref _soundPeak, peak);
        if (keep < piece.Length)
            FlushTempo();
        return keep == piece.Length;
    }

    /// <summary>The end of the cut: the sound the speed filter held back is queued too.</summary>
    void FlushTempo()
    {
        if (_tempo != null && _sound != null)
            _sound.Write(_tempo.Flush(), Volume);
    }

    /// <summary>Decodes the next picture of the cut into a free buffer; false when the cut's pictures are done.</summary>
    bool ReadPicture(ClipPlayback playback, double frame)
    {
        if (!playback.NextPicture() || playback.PictureTime >= _to)
            return false;
        // Late already: skip the conversion (the clock has moved on).
        if (_clockStarted && playback.PictureTime + frame < Clock())
        {
            Interlocked.Increment(ref _late);
            return true;
        }
        byte[] pixels;
        lock (_lock)
        {
            if (_free.Count == 0)
            {
                Interlocked.Increment(ref _noRoom);
                return true;
            }
            pixels = _free.Pop();
        }
        playback.CopyPicture(pixels);
        _filter?.Invoke(pixels, playback.PictureTime);
        lock (_lock)
            _ready.Enqueue((playback.PictureTime, pixels));
        return true;
    }

    /// <summary>The clock starts once a picture and a little sound are ready (or decoding has finished), unless held.</summary>
    void StartClockWhenReady(bool force = false)
    {
        if (_clockStarted || _held)
            return;
        lock (_clockLock) // the reading thread and Release may both start it
        {
            if (_clockStarted || _held)
                return;
            bool picture;
            lock (_lock)
                picture = _ready.Count > 0;
            bool sound = _sound == null || _sound.WrittenSeconds >= SoundLead;
            if (!force && !(picture && sound))
                return;
            _clockStarted = true;
            if (_sound != null)
                _sound.Start();
            else
                _watch.Start();
        }
    }

    /// <summary>Shows the picture due now and reports the time; reports the end once. Call on the owner thread.</summary>
    public void Tick()
    {
        if (_finished || _held)
            return;
        if (_failure is { } failure)
        {
            Finish(failure);
            return;
        }
        // One reading of the sound for both the playhead and the end: a second, later reading could say the sound is
        // over while the playhead was last put a picture before the end.
        double played = _clockStarted ? Played() : 0;
        double clock = Math.Min(_from + played, _to);
        (double Time, byte[] Pixels)? due = null;
        bool empty;
        lock (_lock)
        {
            // The first picture shows at once; after that, in order, catching up after a stall.
            var (drop, show) = _shownFirst ? Choose(_ready.Select(r => r.Time).ToArray(), clock) : (0, _ready.Count > 0);
            for (int i = 0; i < drop; i++)
            {
                _free.Push(_ready.Dequeue().Pixels);
                Interlocked.Increment(ref _replaced);
            }
            if (drop > 0)
                Wake();
            if (show)
            {
                due = _ready.Dequeue();
                _shownFirst = true;
            }
            empty = _ready.Count == 0;
        }
        if (due is { } now)
        {
            Picture.WritePixels(new Int32Rect(0, 0, _width, _height), now.Pixels, _width * 4, 0);
            if (_sinceShown.IsRunning)
            {
                double gap = _sinceShown.Elapsed.TotalMilliseconds;
                LongestGapMs = Math.Max(LongestGapMs, gap);
                if (gap > 50)
                    LongGaps++;
            }
            _sinceShown.Restart();
            lock (_lock)
                _free.Push(now.Pixels);
            Wake();
            Shown++;
        }
        if (_shownFirst)
            _position(clock);
        bool soundOver = _sound == null || _sound.PlayedSeconds >= _sound.WrittenSeconds - 0.001;
        // Read after _decoded: a failure the decoding thread gave just after the check above still reaches the end.
        if (clock >= _to - 0.001 || (_decoded && empty && soundOver && _clockStarted))
            Finish(_failure);
    }

    static int Peak(ReadOnlySpan<byte> pcm)
    {
        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm);
        int peak = 0;
        foreach (short s in samples)
            peak = Math.Max(peak, Math.Abs((int)s));
        return peak;
    }

    /// <summary>Video may trail the sound this much before pictures are dropped (unnoticeable: under lip-sync detection).</summary>
    const double MaxLag = 0.045;

    /// <summary>
    /// Which ready pictures (times, oldest first) to drop and whether to show the next one, at <paramref name="clock"/>.
    /// Pictures are shown in order, one per refresh, so a short stall is caught up instead of skipped; only pictures
    /// further behind the sound than <see cref="MaxLag"/> are dropped.
    /// </summary>
    internal static (int Drop, bool Show) Choose(IReadOnlyList<double> ready, double clock)
    {
        int drop = 0;
        // Keep the newest due picture even when it is late: there is nothing newer to show instead.
        while (drop < ready.Count - 1 && ready[drop] < clock - MaxLag && ready[drop + 1] <= clock + 0.004)
            drop++;
        bool show = drop < ready.Count && ready[drop] <= clock + 0.004;
        return (drop, show);
    }

    void Finish(string? failure)
    {
        _finished = true;
        Dispose();
        _ended(failure);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop = true;
        _room.Set();
        if (Thread.CurrentThread != _thread && _thread.Join(2000))
            _room.Dispose(); // the reading thread has ended: its wake-up is let go now, not whenever the GC runs
        _sound?.Dispose();
    }
}
