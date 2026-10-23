using System.Windows;
using Kairos.Engine.Audio;
using Kairos.Engine.Captions;
using Kairos.Engine.Voice;
using Kairos.Engine.Edit;
using Kairos.Ui;
using Kairos.Ui.Edit;

namespace Kairos.Tests;

/// <summary>Auto captions: lines to text items, the model download, and (when the model is on this PC) real transcriptions.</summary>
public static class CaptionTests
{
    static string Voice(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Voice", name + ".wav");

    /// <summary>Stands in for WPF: the box a line needs, each character 1.5% of the picture wide, 8% high.</summary>
    static Size Measure(string text) => new(text.Length * 0.015, 0.08);

    static IReadOnlyList<TextItem> Items(params Caption[] captions) =>
        Captions.ToItems(captions, Measure, "#FFD54A", 0.05, TextEffect.Box, "almarai");

    /// <summary>The whisper DLLs built into the repository (third_party\\whisper\\bin): the tests do not download them.</summary>
    static readonly string Whisper = FindWhisper();

    static string FindWhisper()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "third_party", "whisper", "bin");
            if (File.Exists(Path.Combine(candidate, "whisper.dll")))
                return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "whisper");
    }

    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-caption-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public static void Captions_become_timed_text_items()
    {
        var items = Items(new Caption(1.0, 2.5, " Hello there "), new Caption(3, 4.25, "مرحبا يا شباب"));
        Assert.Equal(2, items.Count, "items");
        var a = items[0];
        Assert.Equal("Hello there", a.Text, "trimmed text");
        Assert.Near(1.0, a.Start, 1e-9, "start");
        Assert.Near(2.5, a.End, 1e-9, "end");
        Assert.Near(0.5, a.Box.X + a.Box.Width / 2, 1e-9, "centred");
        Assert.True(a.Box.Bottom is > 0.85 and <= 0.95, $"near the bottom ({a.Box.Bottom:F3})");
        Assert.Near(Measure("Hello there").Width, a.Box.Width, 1e-9, "the box the text needs");
        Assert.Near(Measure("Hello there").Height, a.Box.Height, 1e-9, "its height");
        Assert.True(a.Effect == TextEffect.Box && a.Font == "almarai" && a.ColorHex == "#FFD54A" && a.SizeRel == 0.05, "the chosen text style");
        Assert.Equal("مرحبا يا شباب", items[1].Text, "Arabic as spoken");
        Assert.Near(4.25, items[1].End, 1e-9, "second end");
        Assert.True(items[0].Id != items[1].Id, "each its own item");
    }

    [Test]
    public static void Long_lines_are_split()
    {
        const string line = "this is a rather long spoken line that keeps on going"; // 53 characters
        var items = Items(new Caption(10, 16, line));
        Assert.Equal(2, items.Count, "two items");
        Assert.True(items.All(i => i.Text.Length <= Captions.MaxLine), "each fits on one line");
        Assert.Equal(line, items[0].Text + " " + items[1].Text, "every word kept, in order");
        Assert.Near(10, items[0].Start, 1e-9, "first starts with the line");
        Assert.Near(items[0].End, items[1].Start, 1e-9, "second follows the first");
        Assert.Near(16, items[1].End, 1e-9, "second ends with the line");
        double share = items[0].Text.Length / (double)(items[0].Text.Length + items[1].Text.Length);
        Assert.Near(10 + 6 * share, items[0].End, 1e-9, "time follows the share of characters");
    }

    [Test]
    public static void Sounds_that_are_not_speech_are_left_out()
    {
        var items = Items(new Caption(0, 1, "[Music]"), new Caption(1, 2, " (laughs) "), new Caption(2, 3, "  "),
            new Caption(3, 4, "♪ ♪"), new Caption(4, 5, "[BLANK_AUDIO]"), new Caption(5, 6, "GG"));
        Assert.Equal(1, items.Count, "only the spoken line");
        Assert.Equal("GG", items[0].Text, "the spoken line");
    }

    /// <summary>
    /// Speech is found from the speech detector's chances (one per 32 ms): short pauses stay inside a line, a click is not
    /// speech, and each part gets a little room before and after.
    /// </summary>
    [Test]
    public static void Speech_is_found_between_silences()
    {
        const double frame = 0.032;
        var chances = new List<float>();
        void Add(int frames, float chance) => chances.AddRange(Enumerable.Repeat(chance, frames));
        Add(100, 0.02f); // 3.2 s quiet
        Add(40, 0.9f);   // 1.28 s talking
        Add(10, 0.1f);   // a 0.32 s breath
        Add(30, 0.9f);   // talking on
        Add(150, 0.05f); // 4.8 s quiet
        Add(3, 0.95f);   // a 0.1 s click
        Add(150, 0.05f);
        Add(60, 0.8f);   // 1.92 s talking
        Add(20, 0.0f);
        var parts = SpeechRegions.From(chances, frame);
        Assert.Equal(2, parts.Count, "two spoken parts (the breath joins, the click is not speech)");
        Assert.Near(100 * frame - SpeechRegions.Before, parts[0].Start, 1e-6, "starts just before the talking");
        Assert.Near(180 * frame + SpeechRegions.After, parts[0].End, 1e-6, "ends just after it");
        Assert.Near((100 + 80 + 150 + 3 + 150) * frame - SpeechRegions.Before, parts[1].Start, 1e-6, "the second part");
    }

    /// <summary>
    /// The spoken parts are joined into one array made once (a long recording's sound took 3 to 4 times its size while
    /// the parts were gathered and copied).
    /// </summary>
    [Test]
    public static void Joining_the_speech_makes_one_array_once()
    {
        var samples = new float[TrackAudio.Rate * 600];
        var parts = Enumerable.Range(0, 100).Select(i => (Start: i * 6.0, End: i * 6.0 + 5)).ToList();
        SpeechMap.Join(samples, parts.Take(1).ToList()); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        var map = SpeechMap.Join(samples, parts);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long speech = map.Samples.Length * (long)sizeof(float);
        Assert.Equal((int)(TrackAudio.Rate * (500 + 99 * SpeechMap.Gap)), map.Samples.Length, "every part with its gaps");
        Assert.True(allocated < speech * 1.1, $"one array: {allocated / 1048576} MB made for {speech / 1048576} MB of speech");
    }

    /// <summary>Finding the speech moves the progress on (it sat at 10% while a long recording was searched).</summary>
    [Test]
    public static void Finding_the_speech_moves_the_progress()
    {
        var seen = new List<double>();
        SpeechRegions.Find(new float[TrackAudio.Rate * 30], CancellationToken.None, new InlineProgress(seen.Add));
        Assert.True(seen.Count >= 10, $"many steps ({seen.Count})");
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second >= p.First), "never backwards");
        Assert.True(seen[0] < 0.2 && seen[^1] > 0.95, $"from the start to the end ({seen[0]:0.00} .. {seen[^1]:0.00})");
    }

    /// <summary>Finding the speech loads only the speech detector, not Smart Save's other two models.</summary>
    [Test]
    public static void Finding_the_speech_needs_only_the_speech_detector()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", "vad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Path.Combine(SpeechModels.DefaultDirectory, "silero_vad.onnx"), Path.Combine(dir, "silero_vad.onnx"));
            var parts = SpeechRegions.Find(new float[TrackAudio.Rate * 5], CancellationToken.None, null, dir);
            Assert.Equal(0, parts.Count, "silence has no speech, with the detector alone in the folder");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Test]
    public static void Speech_only_audio_maps_back_to_clip_time()
    {
        var samples = new float[TrackAudio.Rate * 20];
        var parts = new List<(double Start, double End)> { (4, 6), (12, 14.5) };
        var map = SpeechMap.Join(samples, parts);
        Assert.Equal((int)(TrackAudio.Rate * (2 + SpeechMap.Gap + 2.5)), map.Samples.Length, "only the speech, with a short gap");
        Assert.Near(4, map.ToSource(0), 1e-6, "the first part starts where it was said");
        Assert.Near(5.5, map.ToSource(1.5), 1e-6, "inside the first part");
        Assert.Near(6, map.ToSource(2 + SpeechMap.Gap / 2), 1e-6, "the gap belongs to the end of the part before it");
        Assert.Near(12.5, map.ToSource(2 + SpeechMap.Gap + 0.5), 1e-6, "inside the second part");
    }

    /// <summary>
    /// Whisper often starts a line where the one before it ended, which in the joined speech is the quiet gap between
    /// two parts: that line starts with its own part, not seconds early at the end of the part before.
    /// </summary>
    [Test]
    public static void A_line_after_a_pause_starts_with_its_words()
    {
        var map = SpeechMap.Join(new float[TrackAudio.Rate * 20], [(4, 6), (12, 14.5)]);
        var (start, end) = map.ToSource(2.1, 3.4); // starts in the gap, ends in the second part
        Assert.Near(12, start, 1e-6, "with the second part");
        Assert.Near(12 + (3.4 - (2 + SpeechMap.Gap)), end, 1e-6, "ends where it ends");
        var (start2, end2) = map.ToSource(1.9, 2.6); // starts at the first part's tail, ends in the second
        Assert.Near(12, start2, 1e-6, "a line whose words are in the next part starts there");
        Assert.True(end2 > start2, "and ends after it starts");
        var (inside, insideEnd) = map.ToSource(0.5, 1.5);
        Assert.Near(4.5, inside, 1e-6, "inside one part: as it is");
        Assert.Near(5.5, insideEnd, 1e-6, "inside one part: as it is");
    }

    /// <summary>Whisper's line limit counts bytes: an Arabic letter takes two, so the limit leaves Arabic lines room.</summary>
    [Test]
    public static void Arabic_lines_get_as_many_letters_as_english()
    {
        Assert.True(Transcriber.LineBytes >= 2 * Captions.MaxLine, $"{Transcriber.LineBytes} bytes for {Captions.MaxLine} letters");
    }

    [Test]
    public static void A_repeated_line_is_shown_once()
    {
        var items = Items(new Caption(1, 2, "push left"), new Caption(2, 3, "Push left."), new Caption(3, 4, "he is one shot"),
            new Caption(9, 10, "push left"));
        Assert.Equal("push left|he is one shot|push left", string.Join("|", items.Select(i => i.Text)), "the repeat right after is dropped");
        Assert.Near(3, items[0].End, 1e-9, "the line stays up while it is repeated");
    }

    /// <summary>
    /// Silence before, between and after two sentences: no lines in the silence (Whisper writes things it never heard
    /// into long quiet parts) and each line starts when its sentence does, not earlier.
    /// </summary>
    [Test]
    public static void Captions_start_when_the_speaking_starts()
    {
        if (!CaptionModel.Default.IsPresent)
        {
            Console.WriteLine("  (skipped: the captions model is not on this PC)");
            return;
        }
        TestFfmpeg.Load();
        var first = WavFile.Read(Voice("chat_v1_1"));
        var second = WavFile.Read(Voice("chat_v1_2"));
        float[] Quiet(double seconds) => new float[(int)(seconds * 16000)];
        var audio = Quiet(4).Concat(first).Concat(Quiet(5)).Concat(second).Concat(Quiet(3)).ToArray();
        string clip = Path.Combine(Folder(), "talk.wav");
        WavFile.Write(clip, audio);
        double firstStarts = 4, secondStarts = 4 + first.Length / 16000.0 + 5;
        var captions = Transcriber.Run(clip, [0], null, CancellationToken.None, runtimeFolder: Whisper);
        foreach (var c in captions)
            Console.WriteLine($"  {c.Start:F2}-{c.End:F2} {c.Text}");
        Assert.True(captions.Count >= 2, "both sentences");
        Assert.True(captions[0].Start >= firstStarts - 0.5, $"the first line waits for the talking ({captions[0].Start:F2} s)");
        Assert.True(captions.Any(c => c.Start >= secondStarts - 0.5), "a line for the second sentence");
        Assert.True(captions.All(c => c.Start >= secondStarts - 0.5 || c.End <= secondStarts - 4), "nothing in the silence between");
    }

    [Test]
    public static void The_voice_tracks_are_used_when_there_are_any()
    {
        string Streams(TrackLayout layout) => string.Join(",", Captions.Streams(layout));
        Assert.Equal("2", Streams(TrackLayout.Of([], 3)), "1.0.1's clips: track 3 is the mic");
        Assert.Equal("0", Streams(TrackLayout.Of([], 4)), "four tracks without names: nothing is known, the mix");
        Assert.Equal("0", Streams(TrackLayout.Of([], 2)), "no separate mic: the mix");
        Assert.Equal("0", Streams(TrackLayout.Of([], 1)), "one track");
        Assert.Equal("2,3", Streams(TrackLayout.Of(["Mix", "Game", "Discord", "Microphone", "Other sounds"], 5)), "Discord and the mic");
        Assert.Equal("0", Streams(TrackLayout.Of(["Mix", "Game", "Other sounds"], 3)), "no voice of its own: the mix");
    }

    /// <summary>Two voice tracks (Discord, the mic) are heard as one: their sum, in step.</summary>
    [Test]
    public static void Two_voice_tracks_are_listened_to_together()
    {
        TestFfmpeg.Load();
        // Discord speaks before 1 s, the mic after (Fixtures/Share/clip-h264-5tracks.mp4).
        string clip = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share", "clip-h264-5tracks.mp4");
        double Loud(TrackSound sound, double from, double to)
        {
            int a = (int)((from - sound.Start) * TrackAudio.Rate), b = (int)((to - sound.Start) * TrackAudio.Rate);
            return sound.Samples.Skip(a).Take(b - a).Max(Math.Abs);
        }
        var both = TrackAudio.Read(clip, [2, 3], null, CancellationToken.None);
        Assert.True(Loud(both, 0.1, 0.9) > 0.1 && Loud(both, 1.2, 2.5) > 0.1, "Discord, then the mic");
        var mic = TrackAudio.Read(clip, [3], null, CancellationToken.None);
        Assert.True(Loud(mic, 0.1, 0.9) < 0.01 && Loud(mic, 1.2, 2.5) > 0.1, "one track alone as before");
        Assert.Near(mic.Samples.Length, both.Samples.Length, TrackAudio.Rate / 50, "as long as each track");
    }

    [Test]
    public static void The_model_is_not_downloaded_without_consent()
    {
        string folder = Path.Combine(Folder(), "models");
        var model = new CaptionModel(folder);
        bool refused = false;
        try
        {
            model.Download(consent: false, null, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        Assert.True(refused, "refused without consent");
        Assert.True(!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any(), "nothing written");
        Assert.True(!model.IsPresent, "not present");
    }

    [Test]
    public static void A_partial_model_is_not_present_and_removing_deletes_it()
    {
        string folder = Folder();
        var model = new CaptionModel(folder);
        File.WriteAllBytes(model.Path, new byte[1024]);
        Assert.True(!model.IsPresent, "a file of the wrong size is not the model");
        model.Remove();
        Assert.True(!File.Exists(model.Path), "removed");
        model.Remove(); // nothing there: no error
    }

    /// <summary>An interrupted download (the window closed) leaves a big part file; Remove and the next start clear it.</summary>
    [Test]
    public static void Unfinished_downloads_are_cleared()
    {
        string folder = Folder();
        var model = new CaptionModel(folder);
        string part = model.Path + "." + Guid.NewGuid().ToString("N") + ".part";
        File.WriteAllBytes(part, new byte[1024]);
        string other = Path.Combine(folder, "notes.txt");
        File.WriteAllText(other, "mine");
        model.RemoveParts();
        Assert.True(!File.Exists(part), "the part file is gone");
        Assert.True(File.Exists(other), "other files stay");
        File.WriteAllBytes(part, new byte[1024]);
        model.Remove();
        Assert.True(!File.Exists(part), "Remove clears it too");
    }

    [Test]
    public static void A_sound_track_becomes_16_khz_mono()
    {
        TestFfmpeg.Load();
        var wav = WavFile.Read(Voice("chat_v1_1"));
        var sound = TrackAudio.Read(Voice("chat_v1_1"), 0, null, CancellationToken.None);
        Assert.Near(wav.Length, sound.Samples.Length, 16, "same length");
        Assert.Near(0, sound.Start, 1e-6, "starts with the clip");
        int n = Math.Min(wav.Length, sound.Samples.Length);
        Assert.True(Enumerable.Range(0, n).All(i => Math.Abs(wav[i] - sound.Samples[i]) < 2e-3f), "same samples");
    }

    [Test]
    public static void A_real_transcription_finds_words()
    {
        if (!CaptionModel.Default.IsPresent)
        {
            Console.WriteLine("  (skipped: the captions model is not on this PC)");
            return;
        }
        TestFfmpeg.Load();
        double seconds = WavFile.Read(Voice("chat_v1_1")).Length / 16000.0;
        double reached = 0;
        var captions = Transcriber.Run(Voice("chat_v1_1"), [0], new InlineProgress(v => reached = v), CancellationToken.None, runtimeFolder: Whisper);
        string text = string.Join(" ", captions.Select(c => c.Text));
        Console.WriteLine($"  heard: {text}");
        Assert.True(captions.Count > 0, "lines");
        Assert.True(text.Count(char.IsLetter) >= 10, "words");
        Assert.True(captions.All(c => c.Start >= 0 && c.End > c.Start && c.End <= seconds + 0.5), "times within the clip");
        Assert.Near(1, reached, 1e-9, "progress reached the end");
    }

    [Test]
    public static void Captions_run_in_their_own_process()
    {
        if (!CaptionModel.Default.IsPresent)
        {
            Console.WriteLine("  (skipped: the captions model is not on this PC)");
            return;
        }
        string ffmpeg = TestFfmpeg.Folder ?? throw new InvalidOperationException("FFmpeg 8 DLLs from OBS not found");
        double reached = 0;
        var captions = CaptionProcess.Run(Path.Combine(AppContext.BaseDirectory, "Kairos.exe"), Voice("chat_v1_1"), [0],
            new InlineProgress(v => reached = v), CancellationToken.None, ffmpeg, Whisper).GetAwaiter().GetResult();
        Assert.True(captions.Count > 0 && captions.Any(c => c.Text.Any(char.IsLetter)), "lines came back");
        Assert.Near(1, reached, 1e-9, "progress came back");
    }

    [Test]
    public static void A_cancelled_caption_process_stops()
    {
        if (!CaptionModel.Default.IsPresent)
        {
            Console.WriteLine("  (skipped: the captions model is not on this PC)");
            return;
        }
        string ffmpeg = TestFfmpeg.Folder ?? throw new InvalidOperationException("FFmpeg 8 DLLs from OBS not found");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;
        try
        {
            CaptionProcess.Run(Path.Combine(AppContext.BaseDirectory, "Kairos.exe"), Voice("chat_v1_1"), [0], null, cts.Token, ffmpeg, Whisper)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Assert.True(cancelled, "cancelled");
        Assert.True(watch.Elapsed.TotalSeconds < 3, $"stopped quickly ({watch.Elapsed.TotalSeconds:F1}s)");
    }

    // ---------------------------------------------------------------- the editor's Auto captions button

    /// <summary>Stands in for Whisper and the download (no model, no process).</summary>
    sealed class FakeMaker(bool present, params Caption[] lines) : ICaptionMaker
    {
        public bool Ready { get; private set; } = present;
        public long DownloadBytes => Ready ? 0 : CaptionModel.Bytes + CaptionRuntime.Bytes;
        public int Downloads, Listens;
        public string Streams = "";
        public Exception? Fails;

        public Task Download(IProgress<double> progress, CancellationToken ct)
        {
            Downloads++;
            Ready = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Caption>> Listen(string clip, IReadOnlyList<int> streams, IProgress<double> progress, CancellationToken ct)
        {
            Listens++;
            Streams = string.Join(",", streams);
            return Fails != null ? Task.FromException<IReadOnlyList<Caption>>(Fails) : Task.FromResult<IReadOnlyList<Caption>>(lines);
        }
    }

    static EditorViewModel Editor(ICaptionMaker maker, int audioStreams = 3, bool picture = false) =>
        new(EditProject.For(picture ? @"C:\Clips\b.png" : @"C:\Clips\a.mp4", picture ? 0 : 30), picture, EditorMode.Library,
            aspect: 16 / 9.0, audioStreams, maker);

    /// <summary>Captions pressed, then Start on the explainer every press shows.</summary>
    static void Press(EditorViewModel vm)
    {
        vm.CaptionsCommand.Execute(null);
        Assert.True(vm.ShowingIntro, "every press explains first");
        vm.IntroStartCommand.Execute(null);
    }

    [Test]
    public static void Auto_captions_ask_before_downloading_the_model()
    {
        Kairos.App.Text.Language = "en";
        var maker = new FakeMaker(present: false, new Caption(1, 2, "nice shot"));
        var vm = Editor(maker);
        vm.CaptionsCommand.Execute(null);
        Assert.True(vm.ShowingIntro, "asks first");
        Assert.True(vm.DownloadQuestion.Contains("561 MB"), vm.DownloadQuestion); // the engine and the model, asked once
        Assert.Equal(0, maker.Downloads, "nothing downloaded yet");
        Assert.Equal(0, maker.Listens, "nothing heard yet");
        vm.IntroStartCommand.Execute(null);
        Assert.True(!vm.ShowingIntro, "answered");
        Assert.Equal(1, maker.Downloads, "downloaded once agreed");
        Assert.Equal(1, maker.Listens, "then listened");
        Assert.Equal(1, vm.Project.Items.OfType<TextItem>().Count(), "the line is a text item");
    }

    [Test]
    public static void Every_press_explains_first_and_once_made_asks_to_make_them_again()
    {
        Kairos.App.Text.Language = "en";
        var maker = new FakeMaker(present: true, new Caption(1, 2, "nice shot"));
        var vm = Editor(maker);
        vm.CaptionsCommand.Execute(null);
        Assert.True(vm.ShowingIntro && !vm.NeedsDownload, "explains first, with nothing to download");
        Assert.Equal("Start", vm.IntroButton, "the button starts");
        Assert.Equal(0, maker.Listens, "nothing heard yet");
        vm.NotNowCommand.Execute(null);
        vm.CaptionsCommand.Execute(null);
        Assert.True(vm.ShowingIntro, "every press, nothing remembered");
        vm.IntroStartCommand.Execute(null);
        Assert.Equal(1, maker.Listens, "then listened");
        vm.DismissCaptionsNote();
        vm.CaptionsCommand.Execute(null);
        Assert.True(vm.AskingRecaption && !vm.ShowingIntro, "made already: asks to make them again instead");
        vm.RecaptionCommand.Execute(null);
        Assert.True(!vm.ShowingIntro && maker.Listens == 2, "made again at once, no second question");
    }

    [Test]
    public static void Making_them_again_without_the_model_asks_for_the_download()
    {
        var maker = new FakeMaker(present: false);
        var vm = Editor(maker);
        var caption = new TextItem(Guid.NewGuid(), 1, 2, "gg", new Rect(0.1, 0.8, 0.3, 0.08), "#FFFFFF", 0.06, TextEffect.Box, Group: Guid.NewGuid());
        vm.Apply(vm.Project.With(caption));
        vm.CaptionsCommand.Execute(null);
        vm.RecaptionCommand.Execute(null);
        Assert.True(vm.ShowingIntro && vm.NeedsDownload && maker.Downloads == 0, "the download is asked for, not started");
    }

    [Test]
    public static void Not_now_downloads_nothing()
    {
        var maker = new FakeMaker(present: false);
        var vm = Editor(maker);
        vm.CaptionsCommand.Execute(null);
        vm.NotNowCommand.Execute(null);
        Assert.True(!vm.ShowingIntro && !vm.CaptionsBusy, "back to editing");
        Assert.Equal(0, maker.Downloads, "no download");
        Assert.Equal(0, maker.Listens, "nothing heard");
        Assert.True(vm.Project.IsUnchanged, "no change");
    }

    [Test]
    public static void Captions_are_added_as_one_step_to_undo()
    {
        Kairos.App.Text.Language = "en";
        var maker = new FakeMaker(present: true, new Caption(1, 2, "push left"), new Caption(3, 4, "he is one shot"));
        var vm = Editor(maker);
        vm.TextColor = "#FFD54A";
        vm.TextFont = "lalezar";
        Press(vm);
        Assert.Equal("2", maker.Streams, "Kairos's clip: the mic track");
        var items = vm.Project.Items.OfType<TextItem>().OrderBy(i => i.Start).ToList();
        Assert.Equal("push left|he is one shot", string.Join("|", items.Select(i => i.Text)), "the lines");
        Assert.True(items.All(i => i.ColorHex == "#FFD54A" && i.Font == "lalezar"), "in the text tool's colour and font");
        Assert.Near(3, items[1].Start, 1e-9, "at their time");
        Assert.True(vm.CaptionsStatus.Contains('2'), vm.CaptionsStatus);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Project.IsUnchanged, "one undo takes them all away");
    }

    /// <summary>
    /// After auto captions every line is chosen: one change restyles them all (font, colour, size, style) as one step to
    /// undo, the boxes growing with their text; one line chosen alone changes alone.
    /// </summary>
    [Test]
    public static void Captions_come_chosen_so_one_change_restyles_them_all()
    {
        Kairos.App.Text.Language = "en";
        var maker = new FakeMaker(present: true, new Caption(1, 2, "push left"), new Caption(3, 4, "he is one shot"), new Caption(5, 6, "nice"));
        var vm = Editor(maker);
        Press(vm);
        List<TextItem> Lines() => [.. vm.Project.Items.OfType<TextItem>().OrderBy(i => i.Start)];
        var before = Lines();
        Assert.True(before.Count == 3 && before.All(i => i.Group != null), "three captions");
        Assert.True(vm.SelectedItems.Count == 3 && vm.PageTitle == "Captions" && vm.PageNote == "3 selected", "all chosen");
        Assert.Near(vm.Project.ToOutput(1), vm.PlayheadOutput, 1e-6, "the first line on screen");

        vm.TextColor = "#3DDC84";
        vm.Fonts.Single(f => f.Key == "lalezar").IsSelected = true;
        vm.TextSize = 0.09;
        vm.TextEffect = TextEffect.Outline;
        var after = Lines();
        Assert.True(after.All(i => i.ColorHex == "#3DDC84" && i.Font == "lalezar" && i.SizeRel == 0.09 && i.Effect == TextEffect.Outline),
            "every line restyled");
        Assert.True(after.Zip(before).All(p => p.First.Box.Width > p.Second.Box.Width && p.First.Text == p.Second.Text), "bigger boxes, same words");
        Assert.True(after.Zip(before).All(p => Math.Abs(p.First.Box.Bottom - p.Second.Box.Bottom) < 1e-9), "still standing on the same line");

        vm.Select(Lines()[1]);
        vm.TextColor = "#FF5A5A";
        Assert.Equal(1, Lines().Count(i => i.ColorHex == "#FF5A5A"), "one chosen: only that line");
    }

    /// <summary>Restyling all, only what changed applies: a new font keeps each line's own colour; undo shows the old one.</summary>
    [Test]
    public static void Restyling_many_changes_only_what_was_changed()
    {
        Kairos.App.Text.Language = "en";
        var vm = Editor(new FakeMaker(present: true, new Caption(1, 2, "push left"), new Caption(3, 4, "GG")));
        Press(vm);
        List<TextItem> Lines() => [.. vm.Project.Items.OfType<TextItem>().OrderBy(i => i.Start)];
        vm.Select(Lines()[1]);
        vm.TextColor = "#FF5A5A";
        vm.SelectRow(TimelineRow.Text);
        vm.Fonts.Single(f => f.Key == "lalezar").IsSelected = true;
        Assert.True(Lines().All(l => l.Font == "lalezar"), "every line takes the font");
        Assert.Equal("#FF5A5A", Lines()[1].ColorHex, "the red line stays red");
        vm.UndoCommand.Execute(null);
        Assert.Equal(TextFonts.Default, vm.TextFont, "the settings show the font as it is after the undo");
        vm.TextColor = "#3DDC84";
        Assert.True(Lines().All(l => l.Font == TextFonts.Default && l.ColorHex == "#3DDC84"), "the undone font does not come back");
    }

    /// <summary>Captions and texts you typed are all texts: one row, and its name chooses every one of them.</summary>
    [Test]
    public static void The_text_row_holds_captions_and_typed_texts_alike()
    {
        Kairos.App.Text.Language = "en";
        var vm = Editor(new FakeMaker(present: true));
        TextItem Made(string words, double y, Guid? group = null) =>
            new(Guid.NewGuid(), 1, 3, words, new Rect(0.1, y, 0.3, 0.08), "#FFFFFF", 0.06, TextEffect.Box, "segoe", group);
        var group = Guid.NewGuid();
        TextItem title = Made("Title", 0.1), line1 = Made("push left", 0.8, group), line2 = Made("nice", 0.8, group);
        vm.Apply(vm.Project.With(title).With(line1).With(line2));
        Assert.True(vm.Project.Items.All(i => EditorViewModel.RowOf(i) == TimelineRow.Text), "one row for all texts, captions and typed alike");
        vm.SelectRow(TimelineRow.Text);
        Assert.True(vm.SelectedItems.Count == 3 && vm.PageTitle == "Texts", "all three chosen");
        vm.Fonts.Single(f => f.Key == "rakkas").IsSelected = true;
        Assert.True(vm.Project.Items.OfType<TextItem>().All(t => t.Font == "rakkas"), "every text restyled");
        vm.Select(line1);
        Assert.Equal(Kairos.App.Text.Get("editor.hint.allTexts"), vm.PageHint, "one chosen: how to choose them all");
    }

    /// <summary>
    /// Captions made again are asked about first (a press by mistake must not throw edits away), and the new lines take
    /// the old ones' place instead of piling up; texts typed by hand stay.
    /// </summary>
    [Test]
    public static void Captions_again_asks_first_and_replaces_the_old_ones()
    {
        Kairos.App.Text.Language = "en";
        var maker = new FakeMaker(present: true, new Caption(1, 2, "push left"), new Caption(3, 4, "GG"));
        var vm = Editor(maker);
        var typed = new TextItem(Guid.NewGuid(), 5, 8, "Title", new Rect(0.1, 0.1, 0.3, 0.08), "#FFFFFF", 0.06, TextEffect.Box);
        vm.Apply(vm.Project.With(typed));
        Press(vm);
        Assert.True(!vm.AskingRecaption && maker.Listens == 1, "the first time: no question");
        vm.DismissCaptionsNote();
        vm.CaptionsCommand.Execute(null);
        Assert.True(vm.AskingRecaption && maker.Listens == 1, "again: asked first, nothing heard yet");
        vm.KeepCaptionsCommand.Execute(null);
        Assert.True(!vm.AskingRecaption && maker.Listens == 1 && vm.Project.Items.Count == 3, "kept: nothing changed");
        vm.CaptionsCommand.Execute(null);
        vm.RecaptionCommand.Execute(null);
        Assert.True(!vm.ShowingIntro, "no second question");
        Assert.Equal(2, maker.Listens, "made again when agreed");
        Assert.Equal(2, vm.Project.Items.OfType<TextItem>().Count(t => t.Group != null), "the new captions in the old ones' place, not four");
        Assert.True(vm.Project.Items.Any(i => i.Id == typed.Id), "the typed text stays");
        vm.UndoCommand.Execute(null);
        Assert.Equal(2, vm.Project.Items.OfType<TextItem>().Count(t => t.Group != null), "one undo brings the old captions back");
    }

    /// <summary>The Captions button shows how it went for a moment, then comes back.</summary>
    [Test]
    public static void The_captions_button_tells_how_it_went_then_comes_back()
    {
        Kairos.App.Text.Language = "en";
        var vm = Editor(new FakeMaker(present: true, new Caption(1, 2, "push left")));
        Assert.True(vm.ShowsCaptionsButton && !vm.HasCaptionsNote, "the button");
        Press(vm);
        Assert.True(vm.HasCaptionsNote && !vm.ShowsCaptionsButton, "how it went");
        vm.DismissCaptionsNote();
        Assert.True(vm.ShowsCaptionsButton, "the button again");
    }

    /// <summary>Over the picture: the usual arrow to choose and move, the pen to draw, the shapes pointer for shapes.</summary>
    [Test]
    public static void The_picture_shows_the_arrow_unless_drawing()
    {
        Assert.Equal(System.Windows.Input.Cursors.Arrow, EditCanvas.CursorFor(EditMode.None), "choosing: the arrow");
        Assert.True(EditCanvas.CursorFor(EditMode.Draw) == Kairos.Ui.ThemeCursors.Get(Kairos.Ui.ThemeCursors.Shape.Pen), "drawing: the pen");
        Assert.True(EditCanvas.CursorFor(EditMode.Shapes) == Kairos.Ui.ThemeCursors.Get(Kairos.Ui.ThemeCursors.Shape.ShapeTool), "shapes: their own");
    }

    [Test]
    public static void A_clip_without_a_mic_track_is_heard_from_the_mix()
    {
        var maker = new FakeMaker(present: true, new Caption(1, 2, "gg"));
        Press(Editor(maker, audioStreams: 2));
        Assert.Equal("0", maker.Streams, "the mix");
    }

    [Test]
    public static void Hearing_nothing_or_failing_says_so()
    {
        Kairos.App.Text.Language = "en";
        var silent = Editor(new FakeMaker(present: true, new Caption(0, 6, ".")));
        Press(silent);
        Assert.True(silent.Project.IsUnchanged, "nothing added");
        Assert.Equal(Kairos.App.Text.Get("editor.captions.none"), silent.CaptionsStatus, "says nothing was heard");

        var maker = new FakeMaker(present: true) { Fails = new CaptionException("The captions model could not be loaded.") };
        var failing = Editor(maker);
        Press(failing);
        Assert.True(failing.CaptionsStatus.Contains("could not be loaded"), failing.CaptionsStatus);
        Assert.True(!failing.CaptionsBusy, "ready to try again");
    }

    [Test]
    public static void Pictures_and_silent_clips_have_no_auto_captions()
    {
        var maker = new FakeMaker(present: true);
        Assert.True(Editor(maker).CanCaption, "a clip with sound");
        Assert.True(!Editor(maker, audioStreams: 0).CanCaption, "no sound");
        Assert.True(!Editor(maker, audioStreams: 0, picture: true).CanCaption, "a picture");
        Assert.True(!new EditorViewModel(EditProject.For(@"C:\Clips\a.mp4", 30), false, EditorMode.Library, 16 / 9.0, 3).CanCaption,
            "no caption maker given");
    }

    [Test]
    public static void The_captions_model_can_be_removed_in_settings()
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Kairos.App.Text.Language = "en";
                var model = new CaptionModel(Folder());
                var vm = new SettingsViewModel { CaptionModel = model, CaptionRuntime = new CaptionRuntime(Path.Combine(Folder(), "runtime"), "unused") };
                Assert.True(!vm.CaptionModelOnDisk, "not downloaded");
                Assert.Equal(Kairos.App.Text.Get("general.captionsNone"), vm.CaptionModelText, "says so");
                File.WriteAllBytes(model.Path, new byte[3 * 1024 * 1024]);
                vm.RefreshCaptionModel();
                Assert.True(vm.CaptionModelOnDisk && vm.CaptionModelText.Contains("3 MB"), vm.CaptionModelText);
                vm.RemoveCaptionModelCommand.Execute(null);
                Assert.True(!File.Exists(model.Path) && !vm.CaptionModelOnDisk, "removed");
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error == null, error ?? "");
    }

}
