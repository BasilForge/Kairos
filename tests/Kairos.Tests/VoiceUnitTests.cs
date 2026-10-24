using Kairos.Engine.Voice;
using Kairos.Settings;

namespace Kairos.Tests;

public static class VoiceUnitTests
{
    static float[] Sine(double hz, int rate, double seconds, double amp = 0.5) =>
        [.. Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate)))];

    static double Rms(ReadOnlySpan<float> x)
    {
        double s = 0;
        foreach (float v in x)
            s += v * v;
        return Math.Sqrt(s / Math.Max(1, x.Length));
    }

    static int ZeroCrossings(ReadOnlySpan<float> x)
    {
        int n = 0;
        for (int i = 1; i < x.Length; i++)
            if ((x[i - 1] < 0) != (x[i] < 0))
                n++;
        return n;
    }

    static float[][] RandomSeq(int n, int seed)
    {
        var rnd = new Random(seed);
        return [.. Enumerable.Range(0, n).Select(_ => Enumerable.Range(0, 96).Select(_ => (float)(rnd.NextDouble() * 2 - 1)).ToArray())];
    }

    [Test]
    public static void Resampler_keeps_speech_and_divides_the_rate_by_three()
    {
        var r = new Resampler48To16();
        var input = Sine(1000, 48000, 1.0);
        var output = new List<float>();
        for (int i = 0; i < input.Length; i += 480)   // libobs delivers ~10 ms blocks
            output.AddRange(r.Process(input.AsSpan(i, 480)));
        Assert.Equal(16000, output.Count, "one second in, one second out");
        var tail = output.Skip(1000).ToArray();
        Assert.Near(0.5 / Math.Sqrt(2), Rms(tail), 0.02, "1 kHz passes");
        Assert.Near(2000.0 * tail.Length / 16000, ZeroCrossings(tail), 4, "still 1 kHz");
    }

    [Test]
    public static void Resampler_removes_tones_above_eight_kilohertz()
    {
        var output = new Resampler48To16().Process(Sine(12000, 48000, 1.0));
        Assert.True(Rms(output.AsSpan(1000)) < 0.5 / Math.Sqrt(2) * 0.03, "12 kHz attenuated by at least 30 dB");
    }

    [Test]
    public static void Wsola_changes_length_but_not_pitch()
    {
        var x = Sine(220, 16000, 1.0);
        foreach (double rate in new[] { 0.9, 1.1 })
        {
            var y = Wsola.Stretch(x, rate);
            Assert.Near(16000 / rate, y.Length, 2, $"length at {rate}");
            var middle = y.AsSpan(800, y.Length - 1600);
            Assert.Near(220, ZeroCrossings(middle) / 2.0 / (middle.Length / 16000.0), 6, $"pitch at {rate}");
        }
    }

    [Test]
    public static void Matcher_finds_the_phrase_inside_a_longer_sequence()
    {
        var phrase = RandomSeq(15, 1);
        var query = RandomSeq(10, 2).Concat(phrase).Concat(RandomSeq(10, 3)).ToArray();
        Assert.Near(0, PhraseMatcher.Cost(phrase, query), 1e-6, "exact copy inside");
        Assert.True(PhraseMatcher.Cost(phrase, RandomSeq(35, 4)) > 0.5, "unrelated sequence costs a lot");
        Assert.True(double.IsPositiveInfinity(PhraseMatcher.Cost(phrase, RandomSeq(5, 9))), "far too short to hold the phrase");
    }

    [Test]
    public static void Matcher_can_ask_the_phrase_to_start_near_the_beginning()
    {
        // Listening checks the start of what was said: words, then the phrase further in, are talk, not the phrase said first.
        var phrase = RandomSeq(15, 1);
        var first = phrase.Concat(RandomSeq(20, 2)).ToArray();
        var later = RandomSeq(40, 3).Concat(phrase).ToArray();
        Assert.Near(0, PhraseMatcher.Cost(phrase, first, maxStart: 5), 1e-6, "the phrase first: found");
        Assert.True(PhraseMatcher.Cost(phrase, later, maxStart: 5) > 0.3, "the phrase after 40 frames of words: out of reach (unrelated)");
        Assert.Near(0, PhraseMatcher.Cost(phrase, later), 1e-6, "anywhere, as before, when no start is asked");
    }

    [Test]
    public static void Matcher_tolerates_speaking_faster_or_slower()
    {
        var phrase = RandomSeq(16, 5);
        var slower = phrase.SelectMany((f, i) => i % 2 == 0 ? new[] { f, f } : [f]).ToArray();
        var faster = phrase.Where((_, i) => i % 3 != 2).ToArray();
        Assert.True(PhraseMatcher.Cost(phrase, slower) < 0.01, "slower");
        Assert.True(PhraseMatcher.Cost(phrase, faster) < 0.01, "faster");
    }

    [Test]
    public static void Score_is_the_mean_of_the_two_closest_templates()
    {
        var a = RandomSeq(12, 6);
        var b = RandomSeq(12, 7);
        Assert.Near(0, PhraseMatcher.Score([a, a, b], a), 1e-6, "two exact templates");
        Assert.Near(PhraseMatcher.Cost(b, a) / 2, PhraseMatcher.Score([a, b], a), 1e-6, "one exact, one far");
    }

    [Test]
    public static void Match_strength_is_full_well_below_the_threshold_and_empty_far_above()
    {
        Assert.Near(1, PhraseMatcher.Strength(0.03, 0.06), 1e-9, "strong");
        Assert.Near(0.6, PhraseMatcher.Strength(0.06, 0.06), 1e-9, "at the threshold");
        Assert.Near(0, PhraseMatcher.Strength(0.2, 0.06), 1e-9, "none");
    }

    static List<float[]> Feed(VoiceSegmenter seg, int chunks, float prob)
    {
        var found = new List<float[]>();
        var chunk = new float[VoiceSegmenter.Chunk];
        for (int i = 0; i < chunks; i++)
            found.AddRange(seg.Push(chunk, prob));
        return found;
    }

    // Say the phrase and keep talking, and it saves; Medal-like listening to all talk saved clips
    // mid-conversation (a user, 1.0.1), so only the start of what someone says (after a silence or a breath) is checked,
    // as long as the phrase: the phrase inside a sentence never is.
    const int Hangover = VoiceSegmenter.HangoverChunks;

    [Test]
    public static void Segmenter_checks_a_phrase_said_on_its_own()
    {
        var seg = new VoiceSegmenter();
        Assert.Equal(0, Feed(seg, 10, 0.05f).Count + Feed(seg, 30, 0.9f).Count, "nothing until the speaker pauses");
        Assert.Equal(0, Feed(seg, Hangover - 1, 0.05f).Count, "a short breath is not the end yet");
        var found = Feed(seg, 1, 0.05f);
        Assert.Equal(1, found.Count, "one segment");
        Assert.Equal((VoiceSegmenter.PreRollChunks + 30 + Hangover) * VoiceSegmenter.Chunk, found[0].Length,
            "the utterance alone: pre-roll + phrase + pause, no words from before");
    }

    [Test]
    public static void Segmenter_keeps_a_phrase_with_a_comma_pause_together()
    {
        var seg = new VoiceSegmenter();
        var found = Feed(seg, 10, 0.05f).Concat(Feed(seg, 15, 0.9f)).Concat(Feed(seg, 18, 0.05f))
            .Concat(Feed(seg, 15, 0.9f)).Concat(Feed(seg, 40, 0.05f)).ToList();
        Assert.Equal((VoiceSegmenter.PreRollChunks + 15 + 18 + 15 + Hangover) * VoiceSegmenter.Chunk, found[0].Length,
            "“Kairos, ... clip it” arrives whole in the first segment");
        Assert.Equal(2, found.Count, "and the part after the pause is checked on its own too (it could be a breath)");
    }

    [Test]
    public static void Segmenter_checks_the_start_of_talk_so_the_phrase_said_first_counts()
    {
        var seg = new VoiceSegmenter { HeadChunks = 60 };
        Feed(seg, 10, 0.05f);
        var head = Feed(seg, 200, 0.9f);
        Assert.Equal(1, head.Count, "the phrase then talk at once: its start is checked, while the talk goes on");
        Assert.Equal((VoiceSegmenter.PreRollChunks + 60) * VoiceSegmenter.Chunk, head[0].Length, "pre-roll + as long as the phrase");
        Assert.Equal(0, Feed(seg, 100, 0.9f).Count, "nothing more while the talk goes on");
    }

    [Test]
    public static void Segmenter_starts_again_after_a_breath_but_not_inside_a_sentence()
    {
        var seg = new VoiceSegmenter { HeadChunks = 60 };
        Feed(seg, 10, 0.05f);
        Feed(seg, 120, 0.9f);
        Assert.Equal(0, Feed(seg, VoiceSegmenter.BreathChunks - 1, 0.05f).Count + Feed(seg, 80, 0.9f).Count,
            "a gap shorter than a breath (inside a sentence): not a new start");
        Feed(seg, VoiceSegmenter.BreathChunks, 0.05f);
        var after = Feed(seg, 80, 0.9f);
        Assert.Equal(1, after.Count, "after a breath (“guys… Kairos clip it”): the phrase is checked");
        Assert.Equal((VoiceSegmenter.BreathChunks + 60) * VoiceSegmenter.Chunk, after[0].Length, "from the breath on, no words before it");
        Assert.Equal(0, Feed(seg, 40, 0.05f).Count, "the talk ends: already checked, nothing more");
    }

    [Test]
    public static void Segmenter_keeps_a_breath_when_a_doubtful_chunk_comes_before_the_next_words()
    {
        // Silero gives ~0.4 to a chunk as speech starts again: between quiet (0.35) and speech (0.5), it is no new talk.
        var seg = new VoiceSegmenter { HeadChunks = 60 };
        Feed(seg, 10, 0.05f);
        Feed(seg, 120, 0.9f);
        Feed(seg, VoiceSegmenter.BreathChunks + 2, 0.05f);
        Feed(seg, 1, 0.4f);
        var after = Feed(seg, 80, 0.9f);
        Assert.Equal(1, after.Count, "still a breath: the words after it are checked");
        Assert.Equal((VoiceSegmenter.PreRollChunks + 60) * VoiceSegmenter.Chunk, after[0].Length, "the doubtful chunk in its pre-roll");
    }

    [Test]
    public static void Enrollment_segmenter_hands_over_a_long_sample_so_the_page_can_say_it_is_too_long()
    {
        var seg = new VoiceSegmenter(enrolling: true);
        Feed(seg, 10, 0.05f);
        int during = Feed(seg, 150, 0.9f).Count;
        var found = Feed(seg, 40, 0.05f);
        Assert.Equal(0, during, "nothing while speaking");
        Assert.Equal(1, found.Count, "the long sample comes out at the pause");
    }

    [Test]
    public static void Enrollment_segmenter_keeps_a_comma_pause_inside_one_sample()
    {
        var seg = new VoiceSegmenter(enrolling: true);
        int count = Feed(seg, 10, 0.05f).Count + Feed(seg, 15, 0.9f).Count + Feed(seg, 18, 0.05f).Count + Feed(seg, 15, 0.9f).Count;
        var found = Feed(seg, 40, 0.05f);
        Assert.Equal(0, count, "no cut at the pause");
        Assert.Equal(1, found.Count, "one sample");
        int expected = (VoiceSegmenter.PreRollChunks + 15 + 18 + 15 + Hangover) * VoiceSegmenter.Chunk;
        Assert.Equal(expected, found[0].Length, "pre-roll + phrase + hangover");
    }

    [Test]
    public static void An_accepted_phrase_always_keeps_strict_below_the_cap()
    {
        // A user's samples were 0.074 apart: every sensitivity became 0.100, so Strict did nothing.
        Assert.True(VoiceSettings.Threshold(VoiceSensitivity.Strict, VoicePhrase.MaxSpread) < VoiceSettings.MaxThreshold,
            "the most a kept phrase may vary still leaves Strict stricter");
        Assert.True(VoicePhrase.MaxSpread >= 0.064, "the real voices measured (0.045-0.064) are kept");
        Assert.True(VoicePhrase.MaxSpread < 0.074, "a user's loose samples are not");
    }

    /// <summary>
    /// Samples 0.065 apart were kept (a user, 1.1.0): Normal then sat at the cap and ordinary talk while playing saved four
    /// clips in 13 minutes. Samples that loose are asked again (the furthest one) up to twice; kept after that, so a voice
    /// that varies is never stuck, and the user is told the phrase may save on talk. Close samples are kept at once.
    /// </summary>
    [Test]
    public static void Loose_samples_are_asked_again_twice_then_kept_with_a_warning()
    {
        Assert.Equal(EnrollVerdict.Keep, VoicePhrase.Verdict(0.045, retries: 0), "close: kept");
        Assert.Equal(EnrollVerdict.SayAgain, VoicePhrase.Verdict(0.065, retries: 0), "loose: the furthest said again");
        Assert.Equal(EnrollVerdict.SayAgain, VoicePhrase.Verdict(0.065, retries: 1), "still loose: once more");
        Assert.Equal(EnrollVerdict.KeepLoose, VoicePhrase.Verdict(0.065, retries: 2), "after two tries: kept, with a warning");
        Assert.Equal(EnrollVerdict.SayAgain, VoicePhrase.Verdict(0.074, retries: 5), "beyond the most: never kept");
        Assert.True(VoiceSettings.Threshold(VoiceSensitivity.Normal, VoicePhrase.TightSpread) <= VoiceSettings.MaxThreshold,
            "a phrase kept without a warning leaves Normal at or under the cap");
    }

    [Test]
    public static void Smart_save_is_off_by_default_with_normal_sensitivity()
    {
        var s = new AppSettings();
        Assert.Equal(false, s.Voice.Enabled, "off");
        Assert.Equal(VoiceSensitivity.Normal, s.Voice.Sensitivity, "normal");
        Assert.Near(0.060, VoiceSettings.Threshold(VoiceSensitivity.Normal), 1e-9, "normal threshold from the spike");
        Assert.True(VoiceSettings.Threshold(VoiceSensitivity.Strict) < VoiceSettings.Threshold(VoiceSensitivity.Easy), "strict is stricter");
    }

    [Test]
    public static void Threshold_follows_how_much_the_users_own_samples_vary()
    {
        Assert.Near(0.060, VoiceSettings.Threshold(VoiceSensitivity.Normal, spread: 0.02), 1e-9, "steady voice keeps the floor");
        // A real voice (2026-10-12): samples 0.045-0.052 apart, attempts 0.060-0.081, normal talk 0.114+.
        double normal = VoiceSettings.Threshold(VoiceSensitivity.Normal, spread: 0.052);
        Assert.True(normal > 0.085 && normal < 0.105, $"normal {normal:F3} sits between the attempts and normal talk");
        double strict = VoiceSettings.Threshold(VoiceSensitivity.Strict, 0.052), easy = VoiceSettings.Threshold(VoiceSensitivity.Easy, 0.052);
        Assert.True(strict < normal && normal < easy, "strict < normal < easy");
        Assert.True(VoiceSettings.Threshold(VoiceSensitivity.Easy, 0.2) <= VoiceSettings.MaxThreshold, "never so loose that talk matches");
        // A second real recording varied more (spread 0.064); the cap must still keep clear of normal talk (0.114).
        Assert.True(VoiceSettings.Threshold(VoiceSensitivity.Easy, 0.064) <= 0.100, "every sensitivity keeps a margin below normal talk");
    }

    [Test]
    public static void Turning_smart_save_on_rebuilds_but_sensitivity_does_not()
    {
        var before = new AppSettings();
        AppSettings Changed(Action<AppSettings> change)
        {
            var s = SettingsStore.Deserialize(SettingsStore.Serialize(before));
            change(s);
            return s;
        }
        Assert.Equal(true, SettingsDiff.NeedsRebuild(before, Changed(s => s.Voice.Enabled = true)), "listening needs the mic source");
        Assert.Equal(false, SettingsDiff.NeedsRebuild(before, Changed(s =>
        {
            s.Voice.Sensitivity = VoiceSensitivity.Strict;
            s.Voice.HintDismissed = true;
        })), "sensitivity is live");
    }
}
