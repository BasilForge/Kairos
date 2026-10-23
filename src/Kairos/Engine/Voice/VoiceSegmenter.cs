namespace Kairos.Engine.Voice;

/// <summary>
/// Turns 16 kHz audio chunks plus a speech probability per chunk into the speech worth checking.
/// Listening: the start of what someone says, after a silence or a breath (<see cref="BreathChunks"/>), as long as the
/// phrase can be (<see cref="HeadChunks"/>), then nothing until the next breath. The phrase said first counts even when
/// talk follows at once; the phrase inside a sentence never does (listening to all talk, as Medal
/// does, saved clips mid-conversation: a user's report on 1.0.1). A breath starts a new start beside the one still being taken, so
/// "guys… Kairos clip it" is checked from the breath, and a phrase with a comma pause ("Kairos, ... clip it") still
/// arrives whole in the first, the way it was recorded. A start that ends in a ~0.8 s pause arrives at the pause.
/// Enrolling: every utterance up to its pause, so the page can say a sample is too long.
/// </summary>
public sealed class VoiceSegmenter
{
    public const int Chunk = 512;                       // 32 ms, Silero VAD's frame
    public const float StartProbability = 0.5f;
    public const float EndProbability = 0.35f;
    public const int PreRollChunks = 10;                // ~0.3 s before speech starts
    public const int HangoverChunks = 25;               // ~0.8 s of quiet ends an utterance
    public const int BreathChunks = 8;                  // ~0.26 s of quiet: a breath, after which talk starts afresh
    public const int MaxSpeechChunks = 94;              // ~3 s: a phrase is at most 2.5 s

    /// <summary>
    /// Listening: the phrase starts within this much of a checked segment (its ~0.3 s pre-roll included), so words and
    /// then the phrase further in do not count as the phrase said first.
    /// </summary>
    public const double PhraseStartsWithin = 1.6;
    const int KeepChunks = PreRollChunks + MaxSpeechChunks + HangoverChunks;

    /// <summary>Starts being taken at once, at most (a breath every half second never piles them up).</summary>
    const int MaxStarts = 3;

    readonly bool _enrolling;
    readonly Queue<float[]> _recent = new();
    readonly Queue<float[]> _speech = new();
    /// <summary>Listening: the starts being taken, each its chunks so far and its length after its pre-roll.</summary>
    readonly List<(List<float[]> Chunks, int Length)> _starts = [];
    bool _inSpeech;
    int _quiet, _spoken;
    /// <summary>Listening: quiet chunks, and all chunks, since the last clearly spoken one (a doubtful one between keeps the breath).</summary>
    int _quietSinceSpoken, _sinceSpoken;
    int _headChunks = MaxSpeechChunks;

    public VoiceSegmenter(bool enrolling = false) => _enrolling = enrolling;

    /// <summary>Listening: how much of a start is checked, in chunks (<see cref="HeadFor"/>; at most <see cref="MaxSpeechChunks"/>).</summary>
    public int HeadChunks
    {
        get => _headChunks;
        set => _headChunks = Math.Clamp(value, 1, MaxSpeechChunks);
    }

    /// <summary>The start to check for a phrase whose longest sample is <paramref name="phraseSeconds"/>: said a little slower, and a margin.</summary>
    public static int HeadFor(double phraseSeconds) =>
        Math.Clamp((int)Math.Ceiling((phraseSeconds * 1.15 + 0.35) * 16000 / Chunk), 16, MaxSpeechChunks);

    public List<float[]> Push(ReadOnlySpan<float> chunk, float probability)
    {
        var copy = chunk.ToArray();
        return _enrolling ? PushWhole(copy, probability) : PushListening(copy, probability);
    }

    List<float[]> PushListening(float[] copy, float probability)
    {
        var found = new List<float[]>();
        bool spoken = probability >= StartProbability;
        // A breath is enough quiet since the last clearly spoken chunk: a doubtful chunk as speech starts again (0.4)
        // does not undo it (it did in the 48 kHz path, so "guys… Kairos clip it" was never checked from the breath).
        int quietBefore = _quietSinceSpoken, sinceBefore = _sinceSpoken;
        _quiet = probability < EndProbability ? _quiet + 1 : 0;
        if (probability < EndProbability)
            _quietSinceSpoken++;
        _sinceSpoken++;
        if (spoken)
            _quietSinceSpoken = _sinceSpoken = 0;
        bool startsNow = spoken && (!_inSpeech || quietBefore >= BreathChunks);
        for (int i = 0; i < _starts.Count; i++)
        {
            var (chunks, length) = _starts[i];
            chunks.Add(copy);
            _starts[i] = (chunks, ++length);
            if (_quiet >= HangoverChunks || length >= _headChunks)
            {
                found.Add(Join(chunks));
                _starts.RemoveAt(i--);
            }
        }
        if (startsNow && _starts.Count < MaxStarts)
        {
            // From the silence or the breath on: no words from before it.
            int preRoll = _inSpeech ? Math.Min(sinceBefore, PreRollChunks) : PreRollChunks;
            _starts.Add(([.. _recent.Skip(Math.Max(0, _recent.Count - preRoll)), copy], 1));
        }
        if (spoken)
            _inSpeech = true;
        else if (_quiet >= HangoverChunks)
            _inSpeech = false;
        _recent.Enqueue(copy);
        if (_recent.Count > PreRollChunks)
            _recent.Dequeue();
        return found;
    }

    List<float[]> PushWhole(float[] copy, float probability)
    {
        var found = new List<float[]>();
        if (!_inSpeech)
        {
            if (probability >= StartProbability)
            {
                _inSpeech = true;
                _quiet = 0;
                _spoken = 1;
                foreach (var c in _recent)
                    Keep(c);
                _recent.Clear();
                Keep(copy);
            }
            else
            {
                _recent.Enqueue(copy);
                if (_recent.Count > PreRollChunks)
                    _recent.Dequeue();
            }
            return found;
        }

        Keep(copy);
        _spoken++;
        _quiet = probability < EndProbability ? _quiet + 1 : 0;
        if (_quiet >= HangoverChunks)
        {
            found.Add(Join(_speech));
            _inSpeech = false;
            _speech.Clear();
        }
        return found;
    }

    void Keep(float[] chunk)
    {
        _speech.Enqueue(chunk);
        if (_speech.Count > KeepChunks)
            _speech.Dequeue();
    }

    static float[] Join(IReadOnlyCollection<float[]> chunks)
    {
        var all = new float[chunks.Count * Chunk];
        int i = 0;
        foreach (var c in chunks)
            c.CopyTo(all, i++ * Chunk);
        return all;
    }
}
