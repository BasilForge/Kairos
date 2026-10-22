using Kairos.Engine.Voice;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>
/// Smart Save on a larger spoken set than the fixtures: two Windows voices, each saying the phrase at five speeds and
/// about 105 s of gaming talk with near words ("that clip was crazy", "clip that guy", "Cairo"), made silently by
/// tools\make-voice-eval.ps1. Runs only when KAIROS_VOICE_EVAL names that folder (the audio is not kept in the repo).
/// It must neither miss the phrase nor take any talk for it.
/// </summary>
public static class VoiceEvalTests
{
    [Test]
    public static void The_phrase_is_caught_said_first_or_after_a_breath_and_talk_never_is()
    {
        if (Environment.GetEnvironmentVariable("KAIROS_VOICE_EVAL") is not { Length: > 0 } dir)
            return;
        var m = new SpeechModels(SpeechModels.DefaultDirectory);
        foreach (string voice in new[] { "david", "zira" })
        {
            float[] Wav(string name) => WavFile.Read(Path.Combine(dir, $"{voice}-{name}.wav"));
            var phrase = VoicePhrase.Build(m, [Wav("phrase-r1"), Wav("phrase-r2"), Wav("phrase-r3")]);
            double strict = VoiceSettings.Threshold(VoiceSensitivity.Strict, phrase.Spread);
            double easy = VoiceSettings.Threshold(VoiceSensitivity.Easy, phrase.Spread);
            foreach (string name in new[] { "phrase-r0", "phrase-r4", "first-then-talk", "after-breath", "after-long-breath" })
            {
                double best = Scores(m, Wav(name), phrase).Min();
                Console.WriteLine($"{voice} {name}: {best:F3} (strict {strict:F3})");
                Assert.True(best < strict, $"{voice} {name}: caught even at Strict ({best:F3})");
            }
            foreach (string name in new[] { "inside-sentence", "talk" })
            {
                double best = Scores(m, Wav(name), phrase).Min();
                Console.WriteLine($"{voice} {name}: {best:F3} (easy {easy:F3})");
                Assert.True(best > easy, $"{voice} {name}: never taken for the phrase, even at Easy ({best:F3})");
            }
        }
    }

    /// <summary>Each start listening checks in <paramref name="at16"/> (heard as the microphone gives it: 48 kHz, resampled), scored.</summary>
    static List<double> Scores(SpeechModels m, float[] at16, VoicePhrase phrase)
    {
        var resampler = new Resampler48To16();
        var at48 = at16.Concat(new float[24000]).SelectMany(x => new[] { x, x, x }).ToArray();
        var audio = new List<float>();
        for (int b = 0; b + 480 <= at48.Length; b += 480)
            audio.AddRange(resampler.Process(at48.AsSpan(b, 480)));
        var segmenter = new VoiceSegmenter { HeadChunks = VoiceSegmenter.HeadFor(phrase.Seconds) };
        var vad = m.NewVad();
        var chunk = new float[VoiceSegmenter.Chunk];
        var scores = new List<double> { double.PositiveInfinity };
        for (int i = 0; i + VoiceSegmenter.Chunk <= audio.Count; i += VoiceSegmenter.Chunk)
        {
            audio.CopyTo(i, chunk, 0, VoiceSegmenter.Chunk);
            foreach (var segment in segmenter.Push(chunk, m.SpeechProbability(vad, chunk)))
                scores.Add(phrase.ScoreAtStart(m, segment, VoiceSegmenter.PhraseStartsWithin));
        }
        return scores;
    }
}
