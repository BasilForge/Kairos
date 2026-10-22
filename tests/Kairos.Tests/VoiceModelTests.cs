using Kairos.Engine.Voice;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Runs the real ONNX models on synthetic-voice fixtures (generated silently with Windows TTS).</summary>
public static class VoiceModelTests
{
    static readonly Lazy<SpeechModels> Models = new(() => new SpeechModels(SpeechModels.DefaultDirectory));
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Voice", name + ".wav");
    static float[] Load(string name) => WavFile.Read(Fixture(name));
    static readonly double Normal = VoiceSettings.Threshold(VoiceSensitivity.Normal);

    /// <summary>
    /// Smart Save asks the speech detector 31 times a second while it listens: a question makes no copy of the sound
    /// (it made one, 2 KB, every time), and the answers are the same.
    /// </summary>
    [Test]
    public static void Asking_the_speech_detector_makes_no_copy_of_the_sound()
    {
        var m = Models.Value;
        var chunk = new float[512];
        for (int i = 0; i < chunk.Length; i++)
            chunk[i] = 0.3f * MathF.Sin(i * 0.07f);
        var state = m.NewVad();
        m.SpeechProbability(state, chunk); // warm up
        var again = m.NewVad();
        float first = m.SpeechProbability(again, chunk);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            m.SpeechProbability(state, chunk);
        long perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        Assert.True(perCall < 2048, $"no copy of the 2 KB of sound: {perCall} bytes per question");
        Assert.Equal(first, m.SpeechProbability(m.NewVad(), chunk), "the same answer for the same sound");
    }

    [Test]
    public static void Wav_files_round_trip_as_16_khz_mono()
    {
        var audio = Load("pos_en_v1_r2");
        Assert.True(audio.Length > 8000 && audio.Max(Math.Abs) > 0.05f, "speech loaded");
        string path = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N") + ".wav");
        WavFile.Write(path, audio);
        var back = WavFile.Read(path);
        Assert.Equal(audio.Length, back.Length, "length");
        Assert.True(audio.Zip(back).All(p => Math.Abs(p.First - p.Second) < 1e-3f), "samples");
    }

    [Test]
    public static void Embeddings_are_96_numbers_every_80_ms()
    {
        var e = Models.Value.Embed(new float[16000]);
        Assert.Equal(96, e[0].Length, "dimensions");
        // 1 s + 0.8 s padding = 180 mel frames -> (180 - 76) / 8 + 1 windows
        Assert.True(e.Length is >= 13 and <= 15, $"{e.Length} windows");
    }

    [Test]
    public static void Voice_detector_hears_speech_and_ignores_silence()
    {
        var m = Models.Value;
        static float Max(SpeechModels m, float[] audio)
        {
            var vad = m.NewVad();
            float max = 0;
            for (int i = 0; i + 512 <= audio.Length; i += 512)
                max = Math.Max(max, m.SpeechProbability(vad, audio.AsSpan(i, 512)));
            return max;
        }
        Assert.True(Max(m, Load("chat_v1_1")) > 0.8f, "speech");
        Assert.True(Max(m, new float[16000]) < 0.2f, "silence");
    }

    [Test]
    public static void Trim_drops_the_silence_around_a_phrase()
    {
        var raw = Load("pos_en_v1_r2");
        var trimmed = VoicePhrase.Trim(raw);
        Assert.True(trimmed.Length < raw.Length && trimmed.Length > 8000, $"{raw.Length} -> {trimmed.Length}");
    }

    static VoicePhrase Enroll(string lang, string voice) =>
        VoicePhrase.Build(Models.Value, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_{lang}_{voice}_r{r}"))]);

    [Test]
    public static void Enrolled_phrase_is_recognised_faster_slower_and_inside_a_sentence()
    {
        var m = Models.Value;
        var phrase = Enroll("en", "v1");
        foreach (string name in new[] { "pos_en_v1_r0", "pos_en_v1_r4" })
            Assert.True(phrase.Score(m, Load(name)) < Normal, $"{name}: {phrase.Score(m, Load(name)):F3}");
        var gap = new float[2400];
        var sentence = Load("chat_v1_1")[^16000..].Concat(gap).Concat(VoicePhrase.Trim(Load("pos_en_v1_r0"))).Concat(gap).Concat(Load("chat_v1_2")[..16000]).ToArray();
        Assert.True(phrase.Score(m, sentence) < Normal, $"inside a sentence: {phrase.Score(m, sentence):F3}");

        var arabic = Enroll("ar", "v2");
        foreach (string name in new[] { "pos_ar_v2_r0", "pos_ar_v2_r4" })
            Assert.True(arabic.Score(m, Load(name)) < Normal, $"{name}: {arabic.Score(m, Load(name)):F3}");
    }

    [Test]
    public static void Spread_measures_how_far_each_sample_is_from_the_other_two()
    {
        var phrase = Enroll("en", "v1");
        Assert.True(phrase.Spread > 0 && phrase.Spread < 0.04, $"synthetic voice is very steady: {phrase.Spread:F3}");
    }

    [Test]
    public static void Samples_too_unlike_each_other_name_the_one_to_say_again()
    {
        var m = Models.Value;
        var steady = Enroll("en", "v1");
        Assert.True(steady.Spread <= VoicePhrase.MaxSpread, $"one voice saying it the same way is kept: {steady.Spread:F3}");
        var mixed = VoicePhrase.Build(m, [Load("pos_en_v1_r1"), Load("pos_en_v1_r2"), Load("pos_ar_v2_r1")]);
        Assert.True(mixed.Spread > VoicePhrase.MaxSpread, $"a third sample unlike the other two: {mixed.Spread:F3}");
        Assert.Equal(2, mixed.WorstSample, "the one unlike the others is the one said again");
    }

    [Test]
    public static void Similar_words_and_chatter_do_not_trigger()
    {
        var m = Models.Value;
        var phrase = Enroll("en", "v1");
        foreach (string name in new[] { "near_v1_1", "near_v1_2", "near_v1_3", "near_v1_4", "near_v1_5", "near_v1_12", "chat_v1_1", "chat_v1_2", "chat_v2_3" })
            Assert.True(phrase.Score(m, Load(name)) > Normal, $"{name}: {phrase.Score(m, Load(name)):F3}");
    }

    [Test]
    public static void Enrollment_samples_are_checked_for_length_and_consistency()
    {
        var m = Models.Value;
        Assert.Equal(EnrollProblem.TooShort, VoicePhrase.Check(m, new float[3000], []), "0.2 s");
        Assert.Equal(EnrollProblem.TooLong, VoicePhrase.Check(m, Load("chat_v1_1").Concat(Load("chat_v1_2")).ToArray(), []), "two sentences");
        var first = Load("pos_en_v1_r1");
        Assert.Equal(EnrollProblem.None, VoicePhrase.Check(m, first, []), "first sample");
        Assert.Equal(EnrollProblem.None, VoicePhrase.Check(m, Load("pos_en_v1_r2"), [first]), "same phrase again");
        Assert.Equal(EnrollProblem.Different, VoicePhrase.Check(m, Load("near_v1_4"), [first]), "a different phrase");
        Assert.Equal(EnrollProblem.Different, VoicePhrase.Check(m, Load("near_v1_4"), [first, Load("pos_en_v1_r2")]),
            "like none of the earlier ones");
    }

    [Test]
    public static void Enrollment_samples_are_saved_and_loaded_back()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        var samples = new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}")).ToList();
        VoicePhrase.SaveSamples(dir, samples);
        var loaded = VoicePhrase.LoadSamples(dir);
        Assert.Equal(3, loaded.Count, "three samples");
        VoicePhrase.DeleteSamples(dir);
        Assert.Equal(0, VoicePhrase.LoadSamples(dir).Count, "deleted");
    }
}
