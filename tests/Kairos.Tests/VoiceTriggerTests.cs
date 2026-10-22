using Kairos.Engine.Voice;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Feeds fixture speech through the whole listener (48 kHz in, worker thread, VAD, matching).</summary>
public static class VoiceTriggerTests
{
    static float[] Load(string name) => WavFile.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Voice", name + ".wav"));

    /// <summary>16 kHz fixture → 48 kHz blocks of 480 frames, the way libobs delivers microphone audio.</summary>
    static void Say(VoiceTrigger v, float[] audio16k, double silenceAfter = 0.8)
    {
        var at48 = new float[(audio16k.Length + (int)(silenceAfter * 16000)) * 3];
        for (int i = 0; i < audio16k.Length; i++)
            at48[3 * i] = at48[3 * i + 1] = at48[3 * i + 2] = audio16k[i];
        for (int i = 0, n = 0; i + 480 <= at48.Length; i += 480, n++)
        {
            v.Feed(at48.AsSpan(i, 480));
            // The worker loads the models on the first sound (0.4-1.8 s on a test PC): fed faster than it listens, the
            // queue's 4 s dropped the start. So at most 1 s of sound waits for it (a block is 160 samples at 16 kHz).
            if (n % 50 == 49)
                WaitFor(() => v.BlocksReceived - v.ChunksAnalysed * VoiceSegmenter.Chunk / 160 <= 100);
        }
    }

    static bool WaitFor(Func<bool> condition, int ms = 15000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    static VoiceTrigger New(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        var v = new VoiceTrigger(SpeechModels.DefaultDirectory, dir);
        v.SetMicConnected(true);
        return v;
    }

    [Test]
    public static void Nothing_listens_until_smart_save_is_on_or_a_phrase_is_being_recorded()
    {
        using var v = New(out _);
        Assert.Equal(false, v.Listening, "off by default");
        v.Configure(enabled: true, VoiceSensitivity.Normal);
        Assert.Equal(false, v.Listening, "on, but no phrase yet");
        v.StartEnroll();
        Assert.Equal(true, v.Listening, "recording a phrase");
        v.Cancel();
        Assert.Equal(false, v.Listening, "cancelled");
    }

    [Test]
    public static void Recording_three_samples_saves_the_phrase_then_saying_it_triggers_once()
    {
        using var v = New(out string dir);
        v.Configure(enabled: true, VoiceSensitivity.Normal);
        v.StartEnroll();
        for (int r = 1; r <= 3; r++)
        {
            Say(v, Load($"pos_en_v1_r{r}"));
            int expected = r;
            Assert.True(WaitFor(() => v.Status().Samples >= expected || v.Status().HasPhrase), $"sample {r} accepted ({v.Status().Message})");
        }
        Assert.True(WaitFor(() => v.Status().HasPhrase), "phrase ready");
        Assert.Equal("idle", v.Status().Mode, "back to listening");
        Assert.Equal(3, VoicePhrase.LoadSamples(dir).Count, "samples on disk");

        int triggers = 0;
        v.Triggered += _ => Interlocked.Increment(ref triggers);
        Say(v, Load("near_v1_5"));
        Say(v, Load("chat_v1_1"));
        Assert.True(WaitFor(() => v.Status().LastScore != null), "heard something");
        Thread.Sleep(500);
        Assert.Equal(0, triggers, "similar words and chatter do not save");

        Say(v, Load("pos_en_v1_r0"));
        Assert.True(WaitFor(() => triggers == 1), "the phrase saves");
        Say(v, Load("pos_en_v1_r4"));
        Thread.Sleep(1500);
        Assert.Equal(1, triggers, "cooldown stops a second save right away");
    }

    [Test]
    public static void The_phrase_inside_talk_does_not_save_but_said_on_its_own_it_does()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        v.Configure(enabled: true, VoiceSensitivity.Easy);
        int triggers = 0;
        v.Triggered += _ => Interlocked.Increment(ref triggers);
        var gap = new float[2400]; // 0.15 s: a breath between words
        var talk = VoicePhrase.Trim(Load("chat_v1_1"))[^24000..].Concat(gap).Concat(VoicePhrase.Trim(Load("pos_en_v1_r0"))).Concat(gap)
            .Concat(VoicePhrase.Trim(Load("chat_v1_2"))[..24000]).ToArray();
        Say(v, talk, silenceAfter: 1.5);
        Thread.Sleep(1500);
        Assert.Equal(0, triggers, "said in the middle of talking: not saved");
        Say(v, Load("pos_en_v1_r4"), silenceAfter: 1.5);
        Assert.True(WaitFor(() => triggers == 1), $"said on its own: saved ({v.Status().LastScore})");
    }

    [Test]
    public static void The_phrase_said_first_saves_even_when_talk_follows_at_once()
    {
        // A user: "sometimes I say it and keep talking".
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        v.Configure(enabled: true, VoiceSensitivity.Normal);
        int triggers = 0;
        v.Triggered += _ => Interlocked.Increment(ref triggers);
        var talk = VoicePhrase.Trim(Load("pos_en_v1_r0")).Concat(VoicePhrase.Trim(Load("chat_v1_1"))).Concat(VoicePhrase.Trim(Load("chat_v1_2"))).ToArray();
        Say(v, talk, silenceAfter: 1.5);
        Assert.True(WaitFor(() => triggers == 1), $"the phrase, then talk with no pause: saved ({v.Status().LastScore})");
    }

    [Test]
    public static void The_phrase_after_a_breath_saves()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        v.Configure(enabled: true, VoiceSensitivity.Normal);
        int triggers = 0;
        v.Triggered += _ => Interlocked.Increment(ref triggers);
        var breath = new float[5600]; // 0.35 s: "guys… Kairos clip it"
        var talk = VoicePhrase.Trim(Load("chat_v1_1"))[^24000..].Concat(breath).Concat(VoicePhrase.Trim(Load("pos_en_v1_r4")))
            .Concat(VoicePhrase.Trim(Load("chat_v1_2"))).ToArray();
        Say(v, talk, silenceAfter: 1.5);
        Assert.True(WaitFor(() => triggers == 1), $"after a breath, talk on both sides: saved ({v.Status().LastScore})");
    }

    [Test]
    public static void Talk_alone_never_saves()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        v.Configure(enabled: true, VoiceSensitivity.Normal);
        int triggers = 0;
        v.Triggered += _ => Interlocked.Increment(ref triggers);
        var breath = new float[5600];
        // near_v1_2 or near_v1_1 just before near_v1_3 is the phrase said in two parts (0.037 and 0.059 against 0.060), and
        // the 0.65 s between them may be taken as a comma: near_v1_3 comes first, so no two near-misses make the phrase.
        foreach (string name in new[] { "chat_v1_1", "chat_v1_2", "chat_v2_3", "near_v1_3", "near_v1_1", "near_v1_2", "near_v1_4", "near_v1_5", "near_v1_12" })
            Say(v, VoicePhrase.Trim(Load(name)).Concat(breath).ToArray(), silenceAfter: 0.3);
        Say(v, [], silenceAfter: 1.5);
        Thread.Sleep(1500);
        Assert.Equal(0, triggers, "talk and near-misses, each after a breath (each checked), at Normal: never saved");
    }

    [Test]
    public static void Test_mode_reports_a_match_without_saving()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        v.Configure(enabled: false, VoiceSensitivity.Normal);
        Assert.Equal(true, v.Status().HasPhrase, "phrase found on disk");
        int triggers = 0;
        v.Triggered += _ => triggers++;
        v.StartTest();
        Say(v, Load("pos_en_v1_r0"));
        Assert.True(WaitFor(() => v.Status().LastMatch), $"matched ({v.Status().LastScore})");
        Assert.Equal(0, triggers, "testing never saves");
        Assert.Equal("testing", v.Status().Mode, "still testing");
    }

    [Test]
    public static void A_different_second_sample_is_rejected_and_can_be_said_again()
    {
        using var v = New(out _);
        v.StartEnroll();
        Say(v, Load("pos_en_v1_r1"));
        Assert.True(WaitFor(() => v.Status().Samples == 1), "first sample");
        Say(v, Load("near_v1_4"));
        Assert.True(WaitFor(() => v.Status().Message == "voice.err.different"), $"rejected ({v.Status().Message})");
        Assert.Equal(1, v.Status().Samples, "still one sample");
        Say(v, Load("pos_en_v1_r2"));
        Assert.True(WaitFor(() => v.Status().Samples == 2), "second sample after retry");
    }

    [Test]
    public static void The_screenshot_phrase_takes_a_screenshot_and_the_clip_phrase_saves_a_clip()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_ar_v2_r{r}"))], VoiceAction.Shot);
        v.Configure(enabled: true, VoiceSensitivity.Normal);
        Assert.True(v.Status().HasPhrase && v.Status().HasShotPhrase, "both phrases found on disk");
        var heard = new List<VoiceAction>();
        v.Triggered += a => { lock (heard) heard.Add(a); };

        Say(v, Load("pos_ar_v2_r0"));
        Assert.True(WaitFor(() => { lock (heard) return heard.Count == 1; }), $"the screenshot phrase acts ({v.Status().LastScore})");
        Say(v, Load("pos_en_v1_r0"));
        Assert.True(WaitFor(() => { lock (heard) return heard.Count == 2; }), $"the clip phrase acts too, its own cooldown ({v.Status().LastScore})");
        lock (heard)
            Assert.Equal("Shot Clip", string.Join(" ", heard), "each phrase its own action, once");
    }

    [Test]
    public static void A_screenshot_phrase_like_the_clip_phrase_is_refused_while_recording()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        v.Configure(enabled: true, VoiceSensitivity.Normal);
        v.StartEnroll(VoiceAction.Shot);
        Assert.Equal("shot", v.Status().EnrollingFor, "recording the screenshot phrase");
        Say(v, Load("pos_en_v1_r4"));
        Assert.True(WaitFor(() => v.Status().Message == "voice.err.tooClose"), $"refused: it is the clip phrase ({v.Status().Message})");
        Assert.Equal(0, v.Status().Samples, "not kept");
        Say(v, Load("pos_ar_v2_r1"));
        Assert.True(WaitFor(() => v.Status().Samples == 1), $"a different phrase is taken ({v.Status().Message})");
    }

    [Test]
    public static void The_screenshot_phrase_can_be_tested_on_its_own_and_the_test_says_which_phrase_matched()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_ar_v2_r{r}"))], VoiceAction.Shot);
        v.Configure(enabled: false, VoiceSensitivity.Normal);
        Assert.True(!v.Status().HasPhrase && v.Status().HasShotPhrase, "only a screenshot phrase");
        v.StartTest();
        Assert.Equal("testing", v.Status().Mode, "testing with just that phrase");
        Say(v, Load("pos_ar_v2_r0"));
        Assert.True(WaitFor(() => v.Status().LastMatch), $"matched ({v.Status().LastScore})");
        Assert.Equal("shot", v.Status().LastPhrase, "the screenshot phrase is the one heard");
    }

    [Test]
    public static void Deleting_the_screenshot_phrase_keeps_the_clip_phrase()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_ar_v2_r{r}"))], VoiceAction.Shot);
        v.DeletePhrase(VoiceAction.Shot);
        Assert.True(v.Status().HasPhrase && !v.Status().HasShotPhrase, "only the screenshot phrase is gone");
    }

    [Test]
    public static void Deleting_the_phrase_removes_the_samples()
    {
        using var v = New(out string dir);
        VoicePhrase.SaveSamples(dir, [.. new[] { 1, 2, 3 }.Select(r => Load($"pos_en_v1_r{r}"))]);
        Assert.Equal(true, v.Status().HasPhrase, "before");
        v.DeletePhrase();
        Assert.Equal(false, v.Status().HasPhrase, "after");
        Assert.Equal(0, VoicePhrase.LoadSamples(dir).Count, "files gone");
    }

    [Test]
    public static void Status_says_unavailable_without_a_microphone()
    {
        using var v = New(out _);
        v.SetMicConnected(false);
        Assert.Equal(false, v.Status().Available, "no mic source");
        Assert.Near(VoiceSettings.Threshold(VoiceSensitivity.Normal), v.Status().Threshold, 1e-9, "threshold reported");
    }
}
