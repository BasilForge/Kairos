using System.Drawing;
using Kairos.App;
using Kairos.Settings;

namespace Kairos.Tests;

public static class SaveFeedbackTests
{
    [Test]
    public static void Sound_and_notification_are_on_by_default()
    {
        var g = new GeneralSettings();
        Assert.Equal(true, g.SoundOnSave, "sound");
        Assert.Equal(true, g.ToastOnSave, "notification");
    }

    [Test]
    public static void Chime_is_a_short_48khz_mono_wav()
    {
        byte[] wav = Chime.Wav();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4), "riff");
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4), "wave");
        Assert.Equal(48000, BitConverter.ToInt32(wav, 24), "sample rate");
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22), "mono");
        int dataBytes = BitConverter.ToInt32(wav, 40);
        Assert.Equal(wav.Length - 44, dataBytes, "data size");
        double seconds = dataBytes / 2.0 / 48000;
        Assert.True(seconds is > 0.25 and < 0.6, $"length {seconds:F2}s");
    }

    [Test]
    public static void Chime_is_gentle_and_click_free()
    {
        byte[] wav = Chime.Wav();
        int samples = (wav.Length - 44) / 2;
        short Sample(int i) => BitConverter.ToInt16(wav, 44 + i * 2);
        int peak = 0;
        for (int i = 0; i < samples; i++)
            peak = Math.Max(peak, Math.Abs((int)Sample(i)));
        Assert.True(peak < 32767 * 0.45, $"peak {peak} should stay under -7 dBFS");
        Assert.True(Math.Abs((int)Sample(0)) < 200 && Math.Abs((int)Sample(samples - 1)) < 200, "starts and ends at silence");
    }

    [Test]
    public static void Chime_is_a_low_soft_bubble()
    {
        byte[] wav = Chime.Wav();
        int samples = (wav.Length - 44) / 2;
        short Sample(int i) => BitConverter.ToInt16(wav, 44 + i * 2);
        int window = 48000 * 120 / 1000, crossings = 0, peak = 0;
        for (int i = 1; i < window; i++)
            if ((Sample(i - 1) < 0) != (Sample(i) < 0))
                crossings++;
        for (int i = 0; i < samples; i++)
            peak = Math.Max(peak, Math.Abs((int)Sample(i)));
        double hz = crossings / 2.0 / 0.12;
        Assert.True(hz < 700, $"pitch {hz:F0} Hz should stay low");
        Assert.True(peak < 32767 * 0.32, $"peak {peak} should be quieter than the old chime");
    }

    [Test]
    public static void Toast_sits_in_the_top_right_of_the_work_area()
    {
        var p = SaveToast.Place(new Rectangle(0, 0, 2560, 1400), new Size(320, 72), margin: 16);
        Assert.Equal(new Point(2560 - 320 - 16, 16), p, "position");
        var second = SaveToast.Place(new Rectangle(-2560, 0, 2560, 1400), new Size(320, 72), margin: 16);
        Assert.Equal(new Point(-320 - 16, 16), second, "left monitor");
    }

    [Test]
    public static void Toast_fades_in_holds_and_fades_out()
    {
        Assert.Near(0, SaveToast.Opacity(0), 0.001, "start");
        Assert.True(SaveToast.Opacity(SaveToast.FadeIn / 2) is > 0 and < 1, "fading in");
        Assert.Near(1, SaveToast.Opacity(SaveToast.FadeIn + 0.5), 0.001, "held");
        Assert.True(SaveToast.Opacity(SaveToast.Total - SaveToast.FadeOut / 2) is > 0 and < 1, "fading out");
        Assert.Near(0, SaveToast.Opacity(SaveToast.Total), 0.001, "gone");
    }

    /// <summary>A tip over a game must not startle: it comes in slower and from further, and stays long enough to read.</summary>
    [Test]
    public static void A_tip_arrives_calmly_and_stays_to_be_read()
    {
        var calm = ToastTiming.Calm;
        Assert.True(calm.FadeIn >= ToastTiming.Usual.FadeIn * 2 && calm.SlidePx > ToastTiming.Usual.SlidePx, "slower and from further than a save");
        Assert.True(calm.Hold >= 6, $"held {calm.Hold} s: two lines read at ease");
        Assert.True(SaveToast.Opacity(calm.FadeIn / 2, calm) is > 0 and < 1, "fading in");
        Assert.Near(1, SaveToast.Opacity(calm.FadeIn + 1, calm), 0.001, "held");
        Assert.Near(0, SaveToast.Opacity(calm.Total, calm), 0.001, "gone");
        Assert.Near(SaveToast.Opacity(0.1), SaveToast.Opacity(0.1, ToastTiming.Usual), 0.0001, "a save as before");
    }

    [Test]
    public static void A_tip_shows_the_soft_rose_mark()
    {
        using var tip = SaveToast.Paint("A tip", "Set the game to Borderless", rtl: false, scale: 1f, ToastIcon.Tip);
        var c = tip.GetPixel(22, 32);
        Assert.True(c.R > 220 && c.G is > 110 and < 160 && c.B is > 140 and < 190, $"a tip: Kairos's rose ({c})");
    }

    [Test]
    public static void The_tip_sound_is_soft_and_not_the_save_sound()
    {
        byte[] wav = Chime.TipWav();
        int samples = (wav.Length - 44) / 2;
        short Sample(int i) => BitConverter.ToInt16(wav, 44 + i * 2);
        int peak = 0;
        for (int i = 0; i < samples; i++)
            peak = Math.Max(peak, Math.Abs((int)Sample(i)));
        Assert.True(peak < 32767 * 0.25, $"peak {peak}: softer than the save sound");
        Assert.True(Math.Abs((int)Sample(0)) < 200 && Math.Abs((int)Sample(samples - 1)) < 200, "starts and ends at silence");
        Assert.True(samples / 48000.0 is > 0.3 and < 1.2, "a short chime");
        Assert.True(!wav.AsSpan().SequenceEqual(Chime.Wav()), "not the save sound");
    }

    [Test]
    public static void A_problem_shows_an_amber_mark_not_the_green_check()
    {
        // The icon sits 16 px in, 30 px wide, centred in the 64 px height; this point is on its disc, off the mark.
        using (var ok = SaveToast.Paint("Saved", "Game", rtl: false, scale: 1f))
        {
            var c = ok.GetPixel(22, 32);
            Assert.True(c.G > 190 && c.R < 100, $"a save: green ({c})");
        }
        using (var problem = SaveToast.Paint("Alt+F10 doesn't work", "Another app is using it", rtl: false, scale: 1f, ToastIcon.Warning))
        {
            var c = problem.GetPixel(22, 32);
            Assert.True(c.R > 230 && c.G is > 140 and < 200 && c.B < 80, $"a problem: amber ({c})");
        }
    }

    [Test]
    public static void A_replay_that_could_not_start_says_so_with_the_hint()
    {
        Text.Language = "en";
        Assert.Equal((Text.Get("toast.notSaved.failed"), Text.Get("fix.closeRecorders")), EngineApp.NotSavedToast("toast.notSaved.failed"), "failed: what happened, then what to do");
        Assert.Equal((Text.Get("toast.notSaved"), Text.Get("toast.notSaved.paused")), EngineApp.NotSavedToast("toast.notSaved.paused"), "the other reasons as before");
    }

    [Test]
    public static void Every_toast_shows_whole_in_every_language()
    {
        // Every card Kairos shows, with sample values in its blanks: the title on one line, the line under it on up to two.
        static IEnumerable<(string Title, string Detail)> Cards()
        {
            yield return (Text.Get("toast.saved"), "Apex Legends • 0:30");
            yield return (Text.Get("toast.shot"), "Apex Legends • " + Text.Get("toast.byVoice"));
            yield return (Text.Get("toast.shotFailed"), "");
            yield return EngineApp.TakenToast(["Alt+Shift+F10"]);
            yield return EngineApp.TakenToast(["Alt+F10", "Alt+Shift+F10", "Alt+F9"]);
            yield return (Text.Get("toast.keysPicked"), Text.Format("toast.keysPickedSave", "Alt+Shift+F10"));
            yield return (Text.Get("toast.recordStarted"), Text.Format("toast.recordHint", "Alt+Shift+F9"));
            yield return (Text.Get("toast.recordPaused"), "12:34 • 1.2 GB");
            yield return (Text.Get("toast.recordResumed"), "12:34 • 1.2 GB");
            yield return (Text.Get("toast.recordSaved"), "Apex Legends • 12:34 • 1.2 GB");
            yield return EngineApp.RecordFailedToast();
            foreach (string reason in new[] { "off", "paused", "waiting", "starting", "write", "disk", "failed" })
                yield return EngineApp.NotSavedToast("toast.notSaved." + reason);
            yield return (Text.Get("toast.lowDisk"), Text.Format("toast.lowDiskBody", "8.2 GB", "C:"));
            yield return (Text.Format("toast.update", "1.1.0"), Text.Get("toast.updateDetail"));
            yield return EngineApp.BorderlessToast("Wuthering Waves");
            yield return EngineApp.BorderlessToast("Arknights: Endfield");
        }
        var cut = new List<string>();
        foreach (var language in Text.Languages)
            LanguageTests.WithLanguage(language.Code, () =>
            {
                foreach (var (title, detail) in Cards())
                    foreach (float scale in new[] { 1f, 1.25f, 1.5f })
                        if (!SaveToast.Fits(title, detail, scale))
                            cut.Add($"{language.Code} at {scale:P0}: {title} / {detail}");
            });
        Assert.True(cut.Count == 0, "cut short: " + string.Join("; ", cut));
    }

    [Test]
    public static void A_long_line_makes_the_card_taller_not_shorter_text()
    {
        using var shortCard = SaveToast.Paint("Clip not saved", "The disk is full", rtl: false, scale: 1f, ToastIcon.Warning);
        using var longCard = SaveToast.Paint("Clip not saved", "Instant replay is starting, try again in a moment", rtl: false, scale: 1f, ToastIcon.Warning);
        Assert.Equal(64, shortCard.Height, "one line: the usual card");
        Assert.True(longCard.Height > shortCard.Height && longCard.Width == shortCard.Width, $"two lines: taller ({longCard.Height}), same width");
    }

    [Test]
    public static void A_recording_that_cannot_start_gives_a_hint_in_the_users_language()
    {
        Text.Language = "ar";
        try
        {
            var (title, detail) = EngineApp.RecordFailedToast();
            Assert.Equal(Text.Get("toast.recordFailed"), title, "title");
            Assert.Equal(Text.Get("fix.closeRecorders"), detail, "the hint, not OBS's English");
            Assert.True(detail != title && detail != "fix.closeRecorders" && !detail.Contains("Recording could not start"), "no repeated, missing or English line");
        }
        finally
        {
            Text.Language = "en";
        }
    }
}
