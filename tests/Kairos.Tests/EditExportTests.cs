using FFmpeg.AutoGen;
using Kairos.Engine;
using Kairos.Engine.Edit;
using Kairos.Engine.Share;
using Kairos.Settings;
using Kairos.Ui.Edit;

namespace Kairos.Tests;

/// <summary>Real exports of edits with the FFmpeg that OBS ships (fixtures only, never the user's clips).</summary>
public static unsafe class EditExportTests
{
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share");
    static string Av1 => Path.Combine(Fixtures, "clip-av1-2tracks.mp4");
    static string H264 => Path.Combine(Fixtures, "clip-h264.mp4");

    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-edit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static EncoderPlan Plan(MediaInfo info) =>
        EncoderPresets.Plan(VideoCodec.Av1, QualityPreset.High, info.Width, info.Height, (int)Math.Round(info.Fps), 22, 40, nvencAvailable: true);

    static ShareResult Export(string source, EditProject p, string target, IReadOnlyDictionary<Guid, Overlay>? pngs = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = ClipTranscoder.Probe(source);
        var graph = EditGraph.Build(p, info.Width, info.Height, info.Fps, info.AudioStreams, pngs ?? new Dictionary<Guid, Overlay>());
        return EditExporter.Export(source, target, graph, Plan(info), progress, ct);
    }

    [Test]
    public static void Cutting_shortens_the_clip()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(Av1);
        var p = EditProject.For(Av1, info.Duration).SplitAt(1.5).SplitAt(3).DeleteSegmentAt(2);
        var result = Export(Av1, p, Path.Combine(dir, "cut.mp4"));
        var output = ClipTranscoder.Probe(result.Path);
        Assert.Near(info.Duration - 1.5, output.Duration, 0.15, "the middle is gone");
        Assert.Equal(1, output.AudioStreams, "one sound track: the mix");
        Assert.True(output.Width == info.Width && output.Height == info.Height, "same size");
    }

    /// <summary>
    /// Saving a short part of a long clip reads only that part: the export jumps to the first kept moment, stops soon
    /// after the last, and its progress runs over the part (a 10-second end of a 5-minute clip took over a minute).
    /// </summary>
    [Test]
    public static void Saving_a_short_part_reads_only_that_part()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        foreach (string source in new[] { H264, Av1 })
        {
            var info = ClipTranscoder.Probe(source);
            int total = (int)Math.Round(info.Duration * info.Fps);
            string name = Path.GetFileNameWithoutExtension(source);

            var first = EditProject.For(source, info.Duration) with { Kept = [new ClipSpan(0, 1)] };
            var start = Export(source, first, Path.Combine(dir, name + "-first.mp4"));
            Assert.Near(1, ClipTranscoder.Probe(start.Path).Duration, 0.1, $"{name}: the first second");
            Assert.True(EditExporter.LastDecodedFrames < total * 0.6,
                $"{name}: reading stops after the part ({EditExporter.LastDecodedFrames} of {total} pictures decoded)");

            var values = new List<double>();
            var last = EditProject.For(source, info.Duration) with { Kept = [new ClipSpan(info.Duration - 1, info.Duration)] };
            var end = Export(source, last, Path.Combine(dir, name + "-last.mp4"), progress: new InlineProgress(values.Add));
            Assert.Near(1, ClipTranscoder.Probe(end.Path).Duration, 0.1, $"{name}: the last second");
            // From a keyframe 2 s before the part (so its sound is all there): on a 6-second fixture, under 80%.
            Assert.True(EditExporter.LastDecodedFrames < total * 0.8,
                $"{name}: reading starts near the part ({EditExporter.LastDecodedFrames} of {total} pictures decoded)");
            Assert.True(values.Count > 2 && values[0] < 0.3,
                $"{name}: progress runs over the part, from 0 ({string.Join(", ", values.Select(v => v.ToString("0.00")))})");
        }
    }

    /// <summary>
    /// NVENC on the decoder's D3D11 device (no second, CUDA, context: about a quarter of a gigabyte less while saving)
    /// writes exactly the same file as before.
    /// </summary>
    [Test]
    public static void The_encoder_on_the_decoders_device_writes_the_same_file()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(Av1);
        var p = EditProject.For(Av1, info.Duration).SplitAt(2) with { Look = ColourLook.Vivid, LookStrength = 1 };
        string shared, own;
        try
        {
            EditExporter.UseDecoderDevice = true;
            shared = Export(Av1, p, Path.Combine(dir, "shared.mp4")).Path;
            EditExporter.UseDecoderDevice = false;
            own = Export(Av1, p, Path.Combine(dir, "own.mp4")).Path;
        }
        finally
        {
            EditExporter.UseDecoderDevice = true;
        }
        Assert.True(File.ReadAllBytes(shared).SequenceEqual(File.ReadAllBytes(own)), "byte for byte the same edit");
    }

    [Test]
    public static void Double_speed_halves_it()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(Av1);
        var p = EditProject.For(Av1, info.Duration).SetSpeed(0, info.Duration, 2);
        var output = ClipTranscoder.Probe(Export(Av1, p, Path.Combine(dir, "fast.mp4")).Path);
        Assert.Near(info.Duration / 2, output.Duration, 0.15, "half as long");
        Assert.Near(info.Fps, output.Fps, 1, "same frame rate");
    }

    [Test]
    public static void A_text_item_is_in_the_picture()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(Av1);
        var text = new TextItem(Guid.NewGuid(), 0, info.Duration, "WWWW", new System.Windows.Rect(0.05, 0.05, 0.6, 0.3), "#FFFFFF", 0.25, TextEffect.Plain);
        Overlay? png = null;
        var sta = new Thread(() => png = ItemRenderer.RenderPng(text, info.Width, info.Height, dir));
        if (OperatingSystem.IsWindows())
            sta.SetApartmentState(ApartmentState.STA); // WPF draws on an STA thread; Linux has no apartments
        sta.Start();
        sta.Join();
        var result = Export(Av1, EditProject.For(Av1, info.Duration).With(text), Path.Combine(dir, "text.mp4"),
            new Dictionary<Guid, Overlay> { [text.Id] = png! });
        double before = Brightness(Av1), after = Brightness(result.Path);
        Assert.True(after > before + 20, $"white text in the top-left: {before:0} -> {after:0}");
    }

    /// <summary>
    /// Blur boxes of any size save (a gamertag or one chat line is small), at the picture's edge too, and the blurred area
    /// keeps its colours (the decoded picture may be NV12).
    /// </summary>
    [Test]
    public static void Small_blur_boxes_save_blurred_in_their_colours()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(H264);
        var small = new BlurItem(Guid.NewGuid(), 0, info.Duration, new System.Windows.Rect(0.30, 0.40, 0.04, 0.04));
        var edge = new BlurItem(Guid.NewGuid(), 0, info.Duration, new System.Windows.Rect(0.97, 0.0, 0.03, 0.2));
        var big = new BlurItem(Guid.NewGuid(), 0, info.Duration, new System.Windows.Rect(0.45, 0.25, 0.3, 0.5));
        var result = Export(H264, EditProject.For(H264, info.Duration).With(small).With(edge).With(big), Path.Combine(dir, "blur.mp4"));
        var before = Frame(H264);
        var after = Frame(result.Path);
        var box = new System.Windows.Int32Rect((int)(before.Width * 0.47), (int)(before.Height * 0.3), (int)(before.Width * 0.26), (int)(before.Height * 0.4));
        double sharpBefore = Sharpness(before, box), sharpAfter = Sharpness(after, box);
        Assert.True(sharpAfter < sharpBefore * 0.6, $"blurred: edges {sharpBefore:0.0} -> {sharpAfter:0.0}");
        var (b0, g0, r0) = Mean(before, box);
        var (b1, g1, r1) = Mean(after, box);
        Assert.True(Math.Abs(b0 - b1) < 14 && Math.Abs(g0 - g1) < 14 && Math.Abs(r0 - r1) < 14,
            $"same colours: {r0:0},{g0:0},{b0:0} -> {r1:0},{g1:0},{b1:0}");
    }

    /// <summary>A part's own look is on only in that part (FFmpeg switches it on and off with its time).</summary>
    [Test]
    public static void A_part_look_colours_only_that_part()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(H264);
        double half = info.Duration / 2;
        var p = EditProject.For(H264, info.Duration).SplitAt(half).WithParts([1], k => k with { Look = ColourLook.Mono });
        var result = Export(H264, p, Path.Combine(dir, "look.mp4"));
        using var grabber = new FrameGrabber(result.Path);
        var early = grabber.Grab(half * 0.4, 320, exact: true)!;
        var late = grabber.Grab(half + (info.Duration - half) * 0.6, 320, exact: true)!;
        Assert.True(Colourful(early) > 40, $"the first part keeps its colours ({Colourful(early):0})");
        Assert.True(Colourful(late) < 8, $"the second is black and white ({Colourful(late):0})");
    }

    /// <summary>How far a picture's pixels are from grey, on average (0 for black and white).</summary>
    static double Colourful(GrabbedFrame f)
    {
        double sum = 0;
        for (int i = 0; i < f.Bgra.Length; i += 4)
            sum += Math.Max(f.Bgra[i], Math.Max(f.Bgra[i + 1], f.Bgra[i + 2])) - Math.Min(f.Bgra[i], Math.Min(f.Bgra[i + 1], f.Bgra[i + 2]));
        return sum / (f.Bgra.Length / 4);
    }

    static GrabbedFrame Frame(string clip)
    {
        using var grabber = new FrameGrabber(clip);
        return grabber.Grab(1.0, ClipTranscoder.Probe(clip).Width, exact: true)!;
    }

    /// <summary>How much neighbouring pixels differ across the box, squared (a softened edge spreads its step thin).</summary>
    static double Sharpness(GrabbedFrame f, System.Windows.Int32Rect box)
    {
        double sum = 0;
        int count = 0;
        for (int y = box.Y; y < box.Y + box.Height; y++)
            for (int x = box.X; x < box.X + box.Width - 1; x++)
            {
                int i = (y * f.Width + x) * 4;
                for (int c = 0; c < 3; c++)
                    sum += Math.Pow(f.Bgra[i + c] - f.Bgra[i + 4 + c], 2);
                count++;
            }
        return sum / count;
    }

    static (double B, double G, double R) Mean(GrabbedFrame f, System.Windows.Int32Rect box)
    {
        double b = 0, g = 0, r = 0;
        int count = 0;
        for (int y = box.Y; y < box.Y + box.Height; y++)
            for (int x = box.X; x < box.X + box.Width; x++)
            {
                int i = (y * f.Width + x) * 4;
                b += f.Bgra[i];
                g += f.Bgra[i + 1];
                r += f.Bgra[i + 2];
                count++;
            }
        return (b / count, g / count, r / count);
    }

    /// <summary>Auto captions on a talkative clip: many text items in one export, each a small picture.</summary>
    [Test]
    public static void Fifty_caption_items_export()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(H264);
        var p = EditProject.For(H264, info.Duration);
        double step = info.Duration / 50;
        var items = Enumerable.Range(0, 50).Select(i => new TextItem(Guid.NewGuid(), i * step, (i + 1) * step, $"line {i} of what was said",
            new System.Windows.Rect(0.2, 0.8, 0.6, 0.1), "#FFFFFF", 0.05, TextEffect.Box)).ToList();
        foreach (var item in items)
            p = p.With(item);
        var pngs = new Dictionary<Guid, Overlay>();
        var sta = new Thread(() =>
        {
            foreach (var item in items)
                pngs[item.Id] = ItemRenderer.RenderPng(item, info.Width, info.Height, dir);
        });
        if (OperatingSystem.IsWindows())
            sta.SetApartmentState(ApartmentState.STA); // WPF draws on an STA thread; Linux has no apartments
        sta.Start();
        sta.Join();
        long pictures = pngs.Values.Sum(o => new FileInfo(o.Path).Length);
        var result = Export(H264, p, Path.Combine(dir, "captions.mp4"), pngs);
        var output = ClipTranscoder.Probe(result.Path);
        Assert.Near(info.Duration, output.Duration, 0.15, "the whole clip");
        Assert.True(pngs.Values.All(o => o.Y >= info.Height * 0.75), "each laid at the bottom");
        Console.WriteLine($"  50 captions: pictures {pictures / 1024} KB in all");
    }

    /// <summary>
    /// While a save runs, all of the window's process runs below normal (FFmpeg's own decode, filter and encode threads
    /// too, not only the thread that started it), so a game in front keeps its CPU; it is back to normal afterwards.
    /// </summary>
    [Test]
    public static void A_save_runs_below_normal_priority()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(H264);
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var before = self.PriorityClass;
        var seen = new List<System.Diagnostics.ProcessPriorityClass>();
        var progress = new InlineProgress(_ =>
        {
            self.Refresh();
            seen.Add(self.PriorityClass);
        });
        Export(H264, EditProject.For(H264, info.Duration).SplitAt(1).DeleteSegmentAt(0.5), Path.Combine(dir, "low.mp4"), progress: progress);
        self.Refresh();
        Assert.True(seen.Count > 0 && seen.All(p => p == System.Diagnostics.ProcessPriorityClass.BelowNormal),
            "below normal while saving: " + string.Join(",", seen.Distinct()));
        Assert.Equal(before, self.PriorityClass, "back as it was");
    }


    /// <summary>Mean brightness of the top-left area (where the test's text sits) at one second.</summary>
    static double Brightness(string clip)
    {
        using var grabber = new FrameGrabber(clip);
        var frame = grabber.Grab(1.0, 320, exact: true)!;
        double sum = 0;
        int count = 0;
        for (int y = (int)(frame.Height * 0.1); y < frame.Height * 0.3; y++)
            for (int x = (int)(frame.Width * 0.1); x < frame.Width * 0.6; x++)
            {
                int i = (y * frame.Width + x) * 4;
                sum += (frame.Bgra[i] + frame.Bgra[i + 1] + frame.Bgra[i + 2]) / 3.0;
                count++;
            }
        return sum / count;
    }

    [Test]
    public static void Cancelling_leaves_nothing()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(Av1);
        using var cts = new CancellationTokenSource();
        bool cancelled = false;
        try
        {
            Export(Av1, EditProject.For(Av1, info.Duration).SetSpeed(0, 1, 0.5), Path.Combine(dir, "never.mp4"),
                progress: new SyncProgress(_ => cts.Cancel()), ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Assert.True(cancelled, "stopped");
        Assert.Equal(0, Directory.GetFiles(dir).Length, "no file and no part file left");
    }

    [Test]
    public static void A_one_track_clip_exports()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(H264);
        var p = EditProject.For(H264, info.Duration) with { Sound = new SoundLevels(All: 0.5) };
        var output = ClipTranscoder.Probe(Export(H264, p, Path.Combine(dir, "one.mp4")).Path);
        Assert.Near(info.Duration, output.Duration, 0.15, "whole clip");
        Assert.Equal(info.AudioStreams > 0 ? 1 : 0, output.AudioStreams, "its sound");
    }

    /// <summary>
    /// An edited Kairos clip keeps the clip's layout (track 1 the mix, 2 the game, 3 the mic), each cut and set like the
    /// mix, so it can be edited again with game and mic apart (it used to come out with the mix only).
    /// </summary>
    [Test]
    public static void Game_and_mic_tracks_are_mixed_and_kept_apart()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        string threeTracks = ThreeTracks(Av1, Path.Combine(dir, "three.mp4"));
        var info = ClipTranscoder.Probe(threeTracks);
        Assert.Equal(3, info.AudioStreams, "fixture: mix, game, mic");
        var p = EditProject.For(threeTracks, info.Duration).SplitAt(2).SplitAt(4).DeleteSegmentAt(3)
            with { Sound = new SoundLevels(Game: 0.6, MicMuted: true) };
        string path = Export(threeTracks, p, Path.Combine(dir, "mixed.mp4")).Path;
        var output = ClipTranscoder.Probe(path);
        Assert.Equal(3, output.AudioStreams, "mix, game and mic, like the clip");
        Assert.Near(info.Duration - 2, output.Duration, 0.15, "the middle is cut from every track");
        var tracks = Enumerable.Range(0, 3).Select(i => Kairos.Engine.Captions.TrackAudio.Read(path, i, null, default).Samples).ToList();
        float Peak(int i) => tracks[i].Length == 0 ? 0 : tracks[i].Max(Math.Abs);
        Assert.True(Peak(0) > 0.01, $"the mix sounds ({Peak(0):0.000})");
        Assert.True(Peak(1) > 0.01, $"the game track sounds ({Peak(1):0.000})");
        Assert.True(Peak(2) < 0.002, $"the muted mic track is silent ({Peak(2):0.0000})");
        foreach (var t in tracks)
            Assert.Near(output.Duration, t.Length / 16000.0, 0.15, "every track as long as the picture");
    }

    /// <summary>A one-track clip (not Kairos's) still comes out with its one track.</summary>
    [Test]
    public static void A_one_track_clip_keeps_one_track()
    {
        TestFfmpeg.Load();
        string dir = Folder();
        var info = ClipTranscoder.Probe(H264);
        var output = ClipTranscoder.Probe(Export(H264, EditProject.For(H264, info.Duration).SplitAt(2), Path.Combine(dir, "one.mp4")).Path);
        Assert.Equal(1, output.AudioStreams, "one track");
    }

    /// <summary>A copy of a two-track clip with its second track again as a third (Kairos's mix, game, mic layout).</summary>
    static string ThreeTracks(string source, string target)
    {
        AVFormatContext* input = null;
        ffmpeg.avformat_open_input(&input, source, null, null);
        ffmpeg.avformat_find_stream_info(input, null);
        AVFormatContext* output = null;
        ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", target);
        var map = new List<(int From, int To)>();
        int lastAudio = -1;
        for (int i = 0; i < input->nb_streams; i++)
        {
            AVStream* s = ffmpeg.avformat_new_stream(output, null);
            ffmpeg.avcodec_parameters_copy(s->codecpar, input->streams[i]->codecpar);
            s->codecpar->codec_tag = 0;
            s->time_base = input->streams[i]->time_base;
            map.Add((i, s->index));
            if (input->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                lastAudio = i;
        }
        AVStream* extra = ffmpeg.avformat_new_stream(output, null);
        ffmpeg.avcodec_parameters_copy(extra->codecpar, input->streams[lastAudio]->codecpar);
        extra->codecpar->codec_tag = 0;
        extra->time_base = input->streams[lastAudio]->time_base;
        ffmpeg.avio_open(&output->pb, target, ffmpeg.AVIO_FLAG_WRITE);
        ffmpeg.avformat_write_header(output, null);
        AVPacket* packet = ffmpeg.av_packet_alloc();
        while (ffmpeg.av_read_frame(input, packet) >= 0)
        {
            int from = packet->stream_index;
            if (from == lastAudio)
            {
                AVPacket* copy = ffmpeg.av_packet_clone(packet);
                copy->stream_index = extra->index;
                copy->pos = -1;
                ffmpeg.av_packet_rescale_ts(copy, input->streams[from]->time_base, extra->time_base);
                ffmpeg.av_interleaved_write_frame(output, copy);
                ffmpeg.av_packet_free(&copy);
            }
            packet->stream_index = map[from].To;
            packet->pos = -1;
            ffmpeg.av_packet_rescale_ts(packet, input->streams[from]->time_base, output->streams[map[from].To]->time_base);
            ffmpeg.av_interleaved_write_frame(output, packet);
            ffmpeg.av_packet_unref(packet);
        }
        ffmpeg.av_write_trailer(output);
        ffmpeg.av_packet_free(&packet);
        ffmpeg.avio_closep(&output->pb);
        ffmpeg.avformat_free_context(output);
        ffmpeg.avformat_close_input(&input);
        return target;
    }

    /// <summary>Reports on the exporting thread (Progress&lt;T&gt; would post to another thread, too late to cancel).</summary>
    sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
