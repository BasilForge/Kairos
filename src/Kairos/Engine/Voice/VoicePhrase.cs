using Kairos.Settings;

namespace Kairos.Engine.Voice;

public enum EnrollProblem { None, TooShort, TooLong, Different }

/// <summary>What the three samples make: a phrase, the furthest sample said again, or a phrase kept with a warning.</summary>
public enum EnrollVerdict { Keep, SayAgain, KeepLoose }

/// <summary>The user's recorded phrase: three samples plus slower and faster copies, as embedding sequences.</summary>
public sealed class VoicePhrase
{
    const double MinSeconds = 0.4, MaxSeconds = 2.5;
    /// <summary>Enrollment samples must agree at least this well (1.6 × the Easy threshold).</summary>
    static readonly double MaxEnrollCost = 1.6 * VoiceSettings.Threshold(VoiceSensitivity.Easy);

    /// <summary>
    /// The most the three samples may differ (<see cref="Spread"/>) for the phrase to be kept: beyond it every sensitivity
    /// reaches <see cref="VoiceSettings.MaxThreshold"/> and ordinary words come close (a user's 0.074 saved clips while he
    /// talked). Real voices measured 0.045-0.064.
    /// </summary>
    public const double MaxSpread = 0.066;

    /// <summary>
    /// Samples further apart than this put Normal at its cap (1.8 × the spread passes 0.100), where ordinary talk can
    /// come close (a user's 0.065 saved four clips in 13 minutes of play): the furthest is asked again, up to
    /// <see cref="LooseRetries"/> times, then kept with a warning (a voice that varies is never stuck).
    /// </summary>
    public const double TightSpread = 0.055;
    public const int LooseRetries = 2;

    /// <summary>What three samples <paramref name="spread"/> apart make, after <paramref name="retries"/> loose tries.</summary>
    public static EnrollVerdict Verdict(double spread, int retries) =>
        spread > MaxSpread ? EnrollVerdict.SayAgain
        : spread <= TightSpread ? EnrollVerdict.Keep
        : retries < LooseRetries ? EnrollVerdict.SayAgain
        : EnrollVerdict.KeepLoose;

    public IReadOnlyList<float[][]> Templates { get; }

    /// <summary>How far each sample scores against the other samples' templates (the worst one): the user's own variation.</summary>
    public double Spread { get; }

    /// <summary>The sample furthest from the other two (the one that sets <see cref="Spread"/>): the one to say again.</summary>
    public int WorstSample { get; }

    /// <summary>The longest sample, in seconds: how much of what is said listening checks (<see cref="VoiceSegmenter.HeadFor"/>).</summary>
    public double Seconds { get; }

    VoicePhrase(List<float[][]> templates, double spread, int worst, double seconds)
    {
        Templates = templates;
        Spread = spread;
        WorstSample = worst;
        Seconds = seconds;
    }

    public static VoicePhrase Build(SpeechModels models, IReadOnlyList<float[]> samples)
    {
        var perSample = new List<List<float[][]>>();
        var plain = new List<float[][]>();
        double seconds = 0;
        foreach (var raw in samples)
        {
            var s = Trim(raw);
            seconds = Math.Max(seconds, s.Length / (double)SpeechModels.SampleRate);
            var e = models.Embed(s);
            plain.Add(e);
            perSample.Add([e, models.Embed(Wsola.Stretch(s, 0.9)), models.Embed(Wsola.Stretch(s, 1.1))]);
        }
        double spread = 0;
        int worst = -1;
        if (samples.Count > 1)
            for (int i = 0; i < samples.Count; i++)
            {
                double score = PhraseMatcher.Score([.. perSample.Where((_, j) => j != i).SelectMany(t => t)], plain[i]);
                if (score > spread)
                    (spread, worst) = (score, i);
            }
        return new VoicePhrase([.. perSample.SelectMany(t => t)], spread, worst, seconds);
    }

    /// <summary>Matching cost of <paramref name="audio"/>; below the sensitivity threshold means the phrase was said.</summary>
    public double Score(SpeechModels models, ReadOnlySpan<float> audio) => PhraseMatcher.Score(Templates, models.Embed(audio));

    /// <summary>
    /// Matching cost of the phrase said at the start of <paramref name="audio"/>: beginning within its first
    /// <paramref name="startWithinSeconds"/> (listening: what someone says first, <see cref="VoiceSegmenter.PhraseStartsWithin"/>).
    /// </summary>
    public double ScoreAtStart(SpeechModels models, ReadOnlySpan<float> audio, double startWithinSeconds)
    {
        var query = models.Embed(audio);
        double seconds = Math.Max(1e-3, audio.Length / (double)SpeechModels.SampleRate);
        int maxStart = Math.Max(1, (int)Math.Ceiling(query.Length * startWithinSeconds / seconds));
        return PhraseMatcher.Score(Templates, query, maxStart);
    }

    /// <summary>Whether <paramref name="sample"/> can join the enrollment: the right length and like the earlier samples.</summary>
    public static EnrollProblem Check(SpeechModels models, float[] sample, IReadOnlyList<float[]> earlier)
    {
        var s = Trim(sample);
        double seconds = s.Length / (double)SpeechModels.SampleRate;
        if (seconds < MinSeconds || Rms(s) < 0.003)
            return EnrollProblem.TooShort;
        if (seconds > MaxSeconds)
            return EnrollProblem.TooLong;
        if (earlier.Count == 0)
            return EnrollProblem.None;
        var embedding = models.Embed(s);
        double cost = earlier.Select(e => PhraseMatcher.Cost(models.Embed(Trim(e)), embedding)).Min();
        return cost <= MaxEnrollCost ? EnrollProblem.None : EnrollProblem.Different;
    }

    /// <summary>Cuts leading and trailing quiet (more than 30 dB below the loudest 128 ms frame).</summary>
    public static float[] Trim(float[] audio)
    {
        const int frame = 2048, hop = 512;
        int frames = audio.Length / hop + 1;
        var db = new double[frames];
        double max = double.NegativeInfinity;
        for (int f = 0; f < frames; f++)
        {
            int center = f * hop, start = Math.Max(0, center - frame / 2), end = Math.Min(audio.Length, center + frame / 2);
            double sum = 0;
            for (int i = start; i < end; i++)
                sum += audio[i] * audio[i];
            db[f] = 10 * Math.Log10(sum / frame + 1e-12);
            max = Math.Max(max, db[f]);
        }
        int first = Array.FindIndex(db, d => d > max - 30), last = Array.FindLastIndex(db, d => d > max - 30);
        if (first < 0)
            return audio;
        int from = first * hop, to = Math.Min(audio.Length, (last + 1) * hop);
        return to - from > SpeechModels.SampleRate / 10 ? audio[from..to] : audio;
    }

    static double Rms(float[] x) => x.Length == 0 ? 0 : Math.Sqrt(x.Sum(v => (double)v * v) / x.Length);

    // ------------------------------------------------------------------ persistence

    /// <summary>phrase1..3.wav for the clip phrase, shot1..3.wav for the screenshot phrase.</summary>
    static string SamplePath(string dir, int i, VoiceAction action) =>
        Path.Combine(dir, $"{(action == VoiceAction.Shot ? "shot" : "phrase")}{i + 1}.wav");

    public static void SaveSamples(string dir, IReadOnlyList<float[]> samples, VoiceAction action = VoiceAction.Clip)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < samples.Count; i++)
            WavFile.Write(SamplePath(dir, i, action), samples[i]);
    }

    public static List<float[]> LoadSamples(string dir, VoiceAction action = VoiceAction.Clip)
    {
        var samples = new List<float[]>();
        for (int i = 0; i < 3; i++)
        {
            string p = SamplePath(dir, i, action);
            if (!File.Exists(p))
                return [];
            samples.Add(WavFile.Read(p));
        }
        return samples;
    }

    public static bool HasSamples(string dir, VoiceAction action = VoiceAction.Clip) =>
        Enumerable.Range(0, 3).All(i => File.Exists(SamplePath(dir, i, action)));

    public static void DeleteSamples(string dir, VoiceAction action = VoiceAction.Clip)
    {
        for (int i = 0; i < 3; i++)
            File.Delete(SamplePath(dir, i, action));
    }
}
