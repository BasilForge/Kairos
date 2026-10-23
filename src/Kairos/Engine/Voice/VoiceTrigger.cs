using Kairos.Settings;

namespace Kairos.Engine.Voice;

/// <summary>What the settings window shows about Smart Save.</summary>
/// <param name="Mode">"idle", "enrolling" or "testing".</param>
/// <param name="Message">A <c>Text</c> key for the last enrollment result, or null.</param>
/// <param name="HasShotPhrase">A second phrase that takes a screenshot is recorded.</param>
/// <param name="EnrollingFor">"clip" or "shot": which phrase a recording in progress is for.</param>
/// <param name="LastPhrase">"clip" or "shot": the phrase the last utterance came closest to (what a test names).</param>
public sealed record VoiceStatus(
    bool Available, bool HasPhrase, string Mode, int Samples, string? Message,
    double? LastScore, bool LastMatch, double Threshold, bool ModelsFound = true,
    bool HasShotPhrase = false, string EnrollingFor = "clip", string? LastPhrase = null);

/// <summary>
/// Smart Save's listener. The microphone tap calls <see cref="Feed"/> on libobs's audio thread; a
/// below-normal worker thread resamples, runs the voice detector and, at the end of each utterance,
/// compares it with the recorded phrases (one saves a clip, the other takes a screenshot). Nothing runs unless Smart Save is on with a phrase, or the
/// user is recording or testing one.
/// </summary>
public sealed class VoiceTrigger : IDisposable
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(90);
    const int MaxQueuedBlocks = 400;                    // ~4 s of 10 ms blocks; older audio is dropped

    readonly string _modelsDir, _dataDir;
    readonly object _gate = new();
    readonly Queue<float[]> _queue = new();
    readonly AutoResetEvent _wake = new(false);
    readonly List<float[]> _samples = [];
    Thread? _worker;
    volatile bool _stopping;

    SpeechModels? _models;
    VoicePhrase? _phrase, _shotPhrase;
    bool _phraseOnDisk, _shotOnDisk;
    VoiceAction _enrollFor = VoiceAction.Clip;
    bool _enabled, _micConnected;
    VoiceSensitivity _sensitivity = VoiceSensitivity.Normal;
    string _mode = "idle";
    string? _message;
    double? _lastScore, _lastThreshold;
    string? _lastPhrase;
    bool _lastMatch;
    DateTime _lastMatchAt;

    /// <summary>A match stays shown this long: a later start inside the same words (a pause in the phrase) does not hide it.</summary>
    static readonly TimeSpan MatchShown = TimeSpan.FromSeconds(2);
    readonly Dictionary<VoiceAction, DateTime> _lastTrigger = new() { [VoiceAction.Clip] = DateTime.MinValue, [VoiceAction.Shot] = DateTime.MinValue };
    DateTime _testStarted;
    int _generation;                                    // bumps on every mode change so stale audio is ignored

    long _blocks, _chunks;
    /// <summary>Microphone blocks accepted and 32 ms chunks analysed (self-test figures).</summary>
    public long BlocksReceived => Interlocked.Read(ref _blocks);
    public long ChunksAnalysed => Interlocked.Read(ref _chunks);

    /// <summary>Test mode logs every score; listening logs only near matches, not every sentence the user says.</summary>
    public static bool ShouldLog(string mode, double score, double threshold) => mode == "testing" || score < 1.5 * threshold;

    /// <summary>Raised on the worker thread when a phrase is heard outside its cooldown, with what it does.</summary>
    public event Action<VoiceAction>? Triggered;

    public VoiceTrigger(string modelsDir, string dataDir)
    {
        _modelsDir = modelsDir;
        _dataDir = dataDir;
        _phraseOnDisk = VoicePhrase.HasSamples(dataDir);
        _shotOnDisk = VoicePhrase.HasSamples(dataDir, VoiceAction.Shot);
    }

    /// <summary>Whether microphone audio is wanted right now.</summary>
    public bool Listening
    {
        get
        {
            lock (_gate)
                return _mode != "idle" || (_enabled && (_phraseOnDisk || _shotOnDisk));
        }
    }

    public void Configure(bool enabled, VoiceSensitivity sensitivity)
    {
        lock (_gate)
        {
            _phraseOnDisk = VoicePhrase.HasSamples(_dataDir);
            _shotOnDisk = VoicePhrase.HasSamples(_dataDir, VoiceAction.Shot);
            _enabled = enabled;
            _sensitivity = sensitivity;
        }
        EnsureWorker();
    }

    public void SetMicConnected(bool connected)
    {
        lock (_gate)
            _micConnected = connected;
    }

    /// <summary>Microphone audio at 48 kHz (one channel). Cheap; safe to call from libobs's audio thread.</summary>
    public void Feed(ReadOnlySpan<float> samples48k)
    {
        if (!Listening)
            return;
        var copy = samples48k.ToArray();
        Interlocked.Increment(ref _blocks);
        lock (_gate)
        {
            _queue.Enqueue(copy);
            if (_queue.Count > MaxQueuedBlocks)
                _queue.Dequeue();
        }
        _wake.Set();
    }

    /// <summary>Records the phrase for <paramref name="action"/> (three samples).</summary>
    public void StartEnroll(VoiceAction action = VoiceAction.Clip)
    {
        lock (_gate)
            _enrollFor = action;
        SetMode("enrolling", clearSamples: true);
    }

    public void StartTest()
    {
        lock (_gate)
        {
            _phraseOnDisk = VoicePhrase.HasSamples(_dataDir);
            _shotOnDisk = VoicePhrase.HasSamples(_dataDir, VoiceAction.Shot);
            if (!_phraseOnDisk && !_shotOnDisk)
                return;
            _testStarted = DateTime.UtcNow;
        }
        SetMode("testing", clearSamples: false);
    }

    public void Cancel() => SetMode("idle", clearSamples: true);

    public void DeletePhrase(VoiceAction action = VoiceAction.Clip)
    {
        lock (_gate)
        {
            VoicePhrase.DeleteSamples(_dataDir, action);
            if (action == VoiceAction.Shot)
            {
                _shotPhrase = null;
                _shotOnDisk = false;
            }
            else
            {
                _phrase = null;
                _phraseOnDisk = false;
            }
        }
        SetMode("idle", clearSamples: true);
    }

    public VoiceStatus Status()
    {
        lock (_gate)
        {
            if (_mode == "testing" && DateTime.UtcNow - _testStarted > TestTimeout)
                _mode = "idle";
            if (_mode != "enrolling")
            {
                _phraseOnDisk = VoicePhrase.HasSamples(_dataDir);
                _shotOnDisk = VoicePhrase.HasSamples(_dataDir, VoiceAction.Shot);
            }
            bool models = SpeechModels.Available(_modelsDir);
            return new VoiceStatus(_micConnected && models, _phraseOnDisk, _mode,
                _samples.Count, _message, _lastScore, _lastMatch, _lastThreshold ?? Threshold(_phrase ?? _shotPhrase), models,
                _shotOnDisk, _enrollFor == VoiceAction.Shot ? "shot" : "clip", _lastPhrase);
        }
    }

    void SetMode(string mode, bool clearSamples)
    {
        lock (_gate)
        {
            _mode = mode;
            _message = null;
            _lastScore = null;
            _lastThreshold = null;
            _lastPhrase = null;
            _lastMatch = false;
            _generation++;
            _queue.Clear();
            if (clearSamples)
                _samples.Clear();
        }
        EnsureWorker();
    }

    void EnsureWorker()
    {
        if (_worker != null || !Listening)
            return;
        _worker = new Thread(Run) { IsBackground = true, Name = "Smart Save", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    void Run()
    {
        var resampler = new Resampler48To16();
        var segmenter = new VoiceSegmenter();
        bool headSet = false;
        VadState? vad = null;
        var pending = new List<float>();
        var chunk = new float[VoiceSegmenter.Chunk]; // filled for each chunk (the segmenter keeps its own copies)
        int generation = -1;
        while (!_stopping)
        {
            _wake.WaitOne(500);
            while (true)
            {
                float[] block;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                        break;
                    block = _queue.Dequeue();
                    if (_generation != generation)
                    {
                        // New mode: forget half-heard speech from before.
                        generation = _generation;
                        resampler = new Resampler48To16();
                        segmenter = _mode == "enrolling"
                            ? new VoiceSegmenter(enrolling: true)
                            : new VoiceSegmenter();
                        headSet = false;
                        vad = null;
                        pending.Clear();
                    }
                }
                try
                {
                    var models = Models();
                    if (models == null)
                        continue;
                    vad ??= models.NewVad();
                    if (!headSet)
                    {
                        // Listening checks the start of what is said, as long as the longest recorded phrase.
                        segmenter.HeadChunks = HeadChunks(models);
                        headSet = true;
                    }
                    pending.AddRange(resampler.Process(block));
                    int used = 0;
                    for (; used + VoiceSegmenter.Chunk <= pending.Count; used += VoiceSegmenter.Chunk)
                    {
                        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending).Slice(used, VoiceSegmenter.Chunk).CopyTo(chunk);
                        Interlocked.Increment(ref _chunks);
                        foreach (var segment in segmenter.Push(chunk, models.SpeechProbability(vad, chunk)))
                            Handle(models, segment, generation);
                    }
                    pending.RemoveRange(0, used);
                }
                catch (Exception e)
                {
                    ObsLog.Warn($"smart save: {e.Message}");
                }
            }
        }
    }

    SpeechModels? Models()
    {
        if (_models == null && SpeechModels.Available(_modelsDir))
            _models = new SpeechModels(_modelsDir);
        return _models;
    }

    void Handle(SpeechModels models, float[] segment, int generation)
    {
        string mode;
        lock (_gate)
        {
            if (generation != _generation)
                return;
            mode = _mode;
        }

        if (mode == "enrolling")
        {
            List<float[]> earlier, three;
            VoiceAction target;
            lock (_gate)
            {
                earlier = [.. _samples];
                target = _enrollFor;
            }
            var problem = VoicePhrase.Check(models, segment, earlier);
            // A sample the other phrase already matches (even at Easy) could not be told apart from it.
            bool tooClose = problem == EnrollProblem.None && PhraseFor(models, Other(target)) is { } other
                            && VoiceRouting.TooClose(other.Score(models, segment), VoiceSettings.Threshold(VoiceSensitivity.Easy, other.Spread));
            lock (_gate)
            {
                if (generation != _generation)
                    return;
                if (problem != EnrollProblem.None || tooClose)
                {
                    _message = tooClose ? "voice.err.tooClose" : problem switch
                    {
                        EnrollProblem.TooShort => "voice.err.short",
                        EnrollProblem.TooLong => "voice.err.long",
                        _ => "voice.err.different",
                    };
                    return;
                }
                _samples.Add(VoicePhrase.Trim(segment));
                _message = null;
                if (_samples.Count < 3)
                    return;
                three = [.. _samples];
            }
            var built = VoicePhrase.Build(models, three);
            lock (_gate)
            {
                if (generation != _generation)
                    return;
                if (built.Spread > VoicePhrase.MaxSpread)
                {
                    // Three samples too unlike each other: the one furthest from the other two is said again.
                    ObsLog.Info($"voice phrase: samples {built.Spread:F3} apart (most {VoicePhrase.MaxSpread:F3}): sample {built.WorstSample + 1} again");
                    _samples.RemoveAt(built.WorstSample);
                    _message = "voice.err.notAlike";
                    return;
                }
                VoicePhrase.SaveSamples(_dataDir, _samples, target);
                if (target == VoiceAction.Shot)
                {
                    _shotPhrase = built;
                    _shotOnDisk = true;
                }
                else
                {
                    _phrase = built;
                    _phraseOnDisk = true;
                }
                LogPhrase(built);
                _samples.Clear();
                _mode = "idle";
                _message = "voice.enrolled";
                _generation++;
            }
            return;
        }

        var clipPhrase = PhraseFor(models, VoiceAction.Clip);
        var shotPhrase = PhraseFor(models, VoiceAction.Shot);
        if (clipPhrase == null && shotPhrase == null)
            return;
        double? clipScore = clipPhrase?.ScoreAtStart(models, segment, VoiceSegmenter.PhraseStartsWithin);
        double? shotScore = shotPhrase?.ScoreAtStart(models, segment, VoiceSegmenter.PhraseStartsWithin);
        VoiceAction? trigger = null;
        lock (_gate)
        {
            if (generation != _generation)
                return;
            double clipThreshold = Threshold(clipPhrase), shotThreshold = Threshold(shotPhrase);
            var heard = VoiceRouting.Pick(clipScore, clipThreshold, shotScore, shotThreshold);
            // What the test shows: the phrase this came closest to; a match stays shown for a moment.
            bool shotCloser = shotScore is { } s && (clipScore is not { } c || s / shotThreshold < c / clipThreshold);
            double score = (shotCloser ? shotScore : clipScore)!.Value, threshold = shotCloser ? shotThreshold : clipThreshold;
            if (heard != null || !_lastMatch || DateTime.UtcNow - _lastMatchAt >= MatchShown)
            {
                _lastScore = score;
                _lastThreshold = threshold;
                _lastPhrase = shotCloser ? "shot" : "clip";
                _lastMatch = heard != null;
                if (heard != null)
                    _lastMatchAt = DateTime.UtcNow;
            }
            // Numbers only (no audio): lets the thresholds be tuned against real voices.
            if (ShouldLog(mode, score, threshold))
                ObsLog.Info($"voice {mode}: {(shotCloser ? "screenshot" : "clip")} phrase score {score:F3} (threshold {threshold:F3}) " +
                            $"over {segment.Length / (double)SpeechModels.SampleRate:F1} s");
            if (mode == "idle" && _enabled && heard is { } action && VoiceRouting.Ready(_lastTrigger[action], DateTime.UtcNow))
            {
                _lastTrigger[action] = DateTime.UtcNow;
                trigger = action;
            }
        }
        if (trigger is { } act)
            Triggered?.Invoke(act);
    }

    static VoiceAction Other(VoiceAction action) => action == VoiceAction.Shot ? VoiceAction.Clip : VoiceAction.Shot;

    /// <summary>How much of what is said listening checks: as long as the longest recorded phrase (none yet: the most).</summary>
    int HeadChunks(SpeechModels models)
    {
        double seconds = Math.Max(PhraseFor(models, VoiceAction.Clip)?.Seconds ?? 0, PhraseFor(models, VoiceAction.Shot)?.Seconds ?? 0);
        return seconds > 0 ? VoiceSegmenter.HeadFor(seconds) : VoiceSegmenter.MaxSpeechChunks;
    }

    /// <summary>The phrase for <paramref name="action"/>, built from its samples on first use; null when it is not recorded.</summary>
    VoicePhrase? PhraseFor(SpeechModels models, VoiceAction action)
    {
        bool shot = action == VoiceAction.Shot;
        lock (_gate)
        {
            if (!(shot ? _shotOnDisk : _phraseOnDisk))
                return null;
            if ((shot ? _shotPhrase : _phrase) is { } cached)
                return cached;
        }
        var samples = VoicePhrase.LoadSamples(_dataDir, action);
        var built = samples.Count == 3 ? VoicePhrase.Build(models, samples) : null;
        if (built != null)
            LogPhrase(built);
        lock (_gate)
            return shot ? _shotPhrase ??= built : _phrase ??= built;
    }

    /// <summary>The cut-off for the current sensitivity, widened for a voice whose samples vary more. Call under the lock.</summary>
    double Threshold(VoicePhrase? phrase) => VoiceSettings.Threshold(_sensitivity, phrase?.Spread ?? 0);

    void LogPhrase(VoicePhrase phrase) =>
        ObsLog.Info($"voice phrase: spread {phrase.Spread:F3}; thresholds strict {VoiceSettings.Threshold(VoiceSensitivity.Strict, phrase.Spread):F3}, " +
                    $"normal {VoiceSettings.Threshold(VoiceSensitivity.Normal, phrase.Spread):F3}, easy {VoiceSettings.Threshold(VoiceSensitivity.Easy, phrase.Spread):F3}");

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _worker?.Join(2000);
        _models?.Dispose();
        _wake.Dispose();
    }
}
