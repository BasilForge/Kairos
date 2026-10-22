using System.Buffers.Binary;
using Kairos.Engine.Share;

namespace Kairos.Tests;

/// <summary>Real conversions with the FFmpeg that OBS ships (read from the OBS install, never changed).</summary>
public static class ShareTranscodeTests
{
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share");
    static string H264 => Path.Combine(Fixtures, "clip-h264.mp4");
    static string Av1 => Path.Combine(Fixtures, "clip-av1-2tracks.mp4");

    static string Temp(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-share-tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.Delete(path);
        return path;
    }

    /// <summary>Top-level MP4 boxes in file order.</summary>
    static List<string> Boxes(string path)
    {
        var boxes = new List<string>();
        using var f = File.OpenRead(path);
        Span<byte> head = stackalloc byte[16];
        while (f.Position + 8 <= f.Length)
        {
            long at = f.Position;
            f.ReadExactly(head[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(head);
            string type = System.Text.Encoding.ASCII.GetString(head[4..8]);
            if (size == 1)
            {
                f.ReadExactly(head[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[8..16]);
            }
            else if (size == 0)
                size = f.Length - at;
            boxes.Add(type);
            f.Position = at + size;
        }
        return boxes;
    }

    [Test]
    public static void Probe_reads_size_rate_length_and_tracks()
    {
        TestFfmpeg.Load();
        var h = ClipTranscoder.Probe(H264);
        Assert.Equal((640, 360), (h.Width, h.Height), "size");
        Assert.Near(60, h.Fps, 0.01, "fps");
        Assert.Near(6, h.Duration, 0.1, "seconds");
        Assert.Equal(("h264", "aac", 1), (h.VideoCodec, h.AudioCodec, h.AudioStreams), "streams");
        var a = ClipTranscoder.Probe(Av1);
        Assert.Equal(("av1", 2), (a.VideoCodec, a.AudioStreams), "AV1 with two tracks");
        Assert.Near(96_000, a.AudioBitrate, 8_000, "first track's bitrate");
    }

    [Test]
    public static void Discord_copy_is_h264_aac_cut_from_the_end_and_starts_fast()
    {
        TestFfmpeg.Load();
        string output = Temp("discord.mp4");
        var plan = new SharePlan(Start: 3, Duration: 3, Width: 640, Height: 360, Crop: null, Fps: 60, VideoBitrate: 1_500_000);
        double last = 0;
        var result = ClipTranscoder.Transcode(H264, output, plan, maxBytes: 10_000_000, new SyncProgress(p => last = p), CancellationToken.None);
        var o = ClipTranscoder.Probe(output);
        Assert.Equal(("h264", "aac", 1), (o.VideoCodec, o.AudioCodec, o.AudioStreams), "codecs");
        Assert.Equal((640, 360), (o.Width, o.Height), "size");
        Assert.Near(3, o.Duration, 0.15, "last 3 s");
        Assert.Near(60, o.Fps, 0.5, "fps kept");
        var boxes = Boxes(output);
        Assert.True(boxes.IndexOf("moov") >= 0 && boxes.IndexOf("moov") < boxes.IndexOf("mdat"), "moov before mdat: " + string.Join(',', boxes));
        Assert.Equal(new FileInfo(output).Length, result.Bytes, "reported size");
        Assert.Near(1, last, 0.01, "progress reached the end");
        Assert.True(result.Encoder.StartsWith("h264_") || result.Encoder == "libx264", "encoder " + result.Encoder);
    }

    /// <summary>NVENC on the decoder's D3D11 device (no second, CUDA, context) writes exactly the same file as before.</summary>
    [Test]
    public static void The_encoder_on_the_decoders_device_writes_the_same_file()
    {
        TestFfmpeg.Load();
        var frame = SharePlanner.Frame(640, 360, vertical: false, cropPosition: 0.5, shortSide: 0);
        var plan = new SharePlan(1, 3, frame.Width, frame.Height, frame.Crop, 60, 2_000_000);
        string shared = Temp("shared.mp4"), own = Temp("own.mp4");
        try
        {
            ClipTranscoder.UseDecoderDevice = true;
            ClipTranscoder.Transcode(Av1, shared, plan, long.MaxValue, null, CancellationToken.None);
            ClipTranscoder.UseDecoderDevice = false;
            ClipTranscoder.Transcode(Av1, own, plan, long.MaxValue, null, CancellationToken.None);
        }
        finally
        {
            ClipTranscoder.UseDecoderDevice = true;
        }
        Assert.True(File.ReadAllBytes(shared).SequenceEqual(File.ReadAllBytes(own)), "byte for byte the same share copy");
    }

    [Test]
    public static void An_av1_clip_becomes_vertical_h264_with_only_the_full_mix_track()
    {
        TestFfmpeg.Load();
        string output = Temp("vertical.mp4");
        var frame = SharePlanner.Frame(640, 360, vertical: true, cropPosition: 0.25, shortSide: 0);
        var plan = new SharePlan(0, 6, frame.Width, frame.Height, frame.Crop, 30, 800_000);
        ClipTranscoder.Transcode(Av1, output, plan, long.MaxValue, null, CancellationToken.None);
        var o = ClipTranscoder.Probe(output);
        Assert.Equal("h264", o.VideoCodec, "video");
        Assert.Equal((frame.Width, frame.Height), (o.Width, o.Height), "9:16 picture");
        Assert.Near(30, o.Fps, 0.5, "fps lowered");
        Assert.Equal(1, o.AudioStreams, "one audio track");
        Assert.Near(96_000, o.AudioBitrate, 10_000, "the first (full mix) track, copied");
        Assert.Near(6, o.Duration, 0.15, "whole clip");
    }

    [Test]
    public static void A_copy_can_be_made_bigger_than_the_clip()
    {
        TestFfmpeg.Load();
        string output = Temp("upscaled.mp4");
        var plan = SharePlanner.Plan(SharePlanner.Spec(SharePlatform.Copy), 1, 2, 640, 360, 60, 128_000, false, 0.5, 720);
        Assert.Equal((1280, 720), (plan.Width, plan.Height), "planned at 720p");
        ClipTranscoder.Transcode(H264, output, plan, long.MaxValue, null, CancellationToken.None);
        var o = ClipTranscoder.Probe(output);
        Assert.Equal((1280, 720), (o.Width, o.Height), "upscaled");
        Assert.Near(2, o.Duration, 0.15, "the cut");
    }

    [Test]
    public static void An_encoder_overshoot_is_retried_until_the_file_fits()
    {
        TestFfmpeg.Load();
        string output = Temp("tight.mp4");
        // Asks for ~6 MB of video in a 400 kB limit: the first pass overshoots, the retry must bring it under.
        var plan = new SharePlan(0, 6, 640, 360, null, 60, 8_000_000);
        var result = ClipTranscoder.Transcode(H264, output, plan, maxBytes: 400_000, null, CancellationToken.None);
        Assert.True(result.Bytes <= 400_000, $"{result.Bytes} bytes");
        Assert.True(new FileInfo(output).Length == result.Bytes, "file on disk");
    }

    [Test]
    public static void The_software_encoder_is_a_working_fallback()
    {
        TestFfmpeg.Load();
        string output = Temp("x264.mp4");
        var plan = new SharePlan(0, 2, 320, 180, null, 30, 500_000);
        var result = ClipTranscoder.Transcode(H264, output, plan, long.MaxValue, null, CancellationToken.None, encoders: ["libx264"]);
        Assert.Equal("libx264", result.Encoder, "encoder");
        var o = ClipTranscoder.Probe(output);
        Assert.Equal((320, 180, "h264"), (o.Width, o.Height, o.VideoCodec), "scaled");
    }

    [Test]
    public static void Cancelling_stops_and_leaves_no_half_file()
    {
        TestFfmpeg.Load();
        string output = Temp("cancel.mp4");
        using var cts = new CancellationTokenSource();
        var plan = new SharePlan(0, 6, 640, 360, null, 60, 1_000_000);
        bool threw = false;
        try
        {
            ClipTranscoder.Transcode(H264, output, plan, long.MaxValue, new SyncProgress(p => { if (p > 0.1) cts.Cancel(); }), cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }
        Assert.True(threw, "cancelled");
        Assert.True(!File.Exists(output), "no output");
        Assert.True(!Directory.EnumerateFiles(Path.GetDirectoryName(output)!, "cancel*").Any(), "no temp file");
    }

    [Test]
    public static void Cpu_decoding_still_works_when_the_graphics_card_cannot_decode()
    {
        TestFfmpeg.Load();
        foreach (string source in new[] { H264, Av1 })
        {
            string output = Temp("cpu-" + Path.GetFileName(source));
            var plan = new SharePlan(4, 2, 320, 180, null, 30, 400_000);
            ClipTranscoder.Transcode(source, output, plan, long.MaxValue, null, CancellationToken.None, gpuDecode: false);
            var o = ClipTranscoder.Probe(output);
            Assert.Equal((320, 180, "h264"), (o.Width, o.Height, o.VideoCodec), Path.GetFileName(source));
            Assert.Near(2, o.Duration, 0.15, Path.GetFileName(source) + " length");
        }
    }

    [Test]
    public static void A_locked_older_copy_gets_a_numbered_name_instead_of_failing()
    {
        TestFfmpeg.Load();
        string output = Temp("locked.mp4");
        File.Delete(Path.Combine(Path.GetDirectoryName(output)!, "locked (2).mp4"));
        File.WriteAllText(output, "an older copy that is still being uploaded");
        var plan = new SharePlan(0, 1, 320, 180, null, 30, 400_000);
        using (new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = ClipTranscoder.Transcode(H264, output, plan, long.MaxValue, null, CancellationToken.None);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(output)!, "locked (2).mp4"), result.Path, "next free name");
            Assert.True(File.Exists(result.Path), "written");
        }
    }

    [Test]
    public static void Arabic_folder_and_file_names_work()
    {
        TestFfmpeg.Load();
        string dir = Path.Combine(Path.GetTempPath(), "kairos-share-tests", "لقطات تجريبية");
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "لعبة 2026-10-12 21-04-55.mp4");
        File.Copy(H264, source, overwrite: true);
        string output = Path.Combine(dir, "مشاركة.mp4");
        File.Delete(output);
        var plan = new SharePlan(0, 1, 320, 180, null, 30, 400_000);
        var result = ClipTranscoder.Transcode(source, output, plan, long.MaxValue, null, CancellationToken.None);
        Assert.True(File.Exists(output) && result.Bytes > 0, "written");
        Assert.Equal("h264", ClipTranscoder.Probe(output).VideoCodec, "readable");
    }

    [Test]
    public static void This_pc_uses_its_graphics_card_to_encode()
    {
        TestFfmpeg.Load();
        string output = Temp("gpu.mp4");
        var plan = new SharePlan(0, 1, 640, 360, null, 60, 1_000_000);
        var result = ClipTranscoder.Transcode(H264, output, plan, long.MaxValue, null, CancellationToken.None);
        // The developer PC has an NVIDIA card; elsewhere any hardware encoder is fine.
        Assert.True(result.Encoder != "libx264", "fell back to the CPU encoder");
        Console.WriteLine($"    encoder: {result.Encoder}");
    }

    [Test]
    public static void A_missing_or_broken_clip_gives_a_clear_error()
    {
        TestFfmpeg.Load();
        bool threw = false;
        try
        {
            ClipTranscoder.Probe(Path.Combine(Fixtures, "missing.mp4"));
        }
        catch (ShareException)
        {
            threw = true;
        }
        Assert.True(threw, "ShareException");
    }

    sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    [Test]
    public static void A_copy_made_to_fit_stays_under_its_size()
    {
        TestFfmpeg.Load();
        string output = Temp("fit.mp4");
        var spec = SharePlanner.Spec(SharePlatform.Copy) with { MaxBytes = 300_000 };
        var source = ClipTranscoder.Probe(H264);
        var plan = SharePlanner.Plan(spec, 0, source.Duration, source.Width, source.Height, source.Fps, source.AudioBitrate, false, 0.5, 0)
            with { AudioBitrate = 64_000 };
        var result = ClipTranscoder.Transcode(H264, output, plan, spec.MaxBytes, null, CancellationToken.None);
        var o = ClipTranscoder.Probe(output);
        Assert.True(result.Bytes <= 300_000, $"{result.Bytes} bytes");
        Assert.Equal("aac", o.AudioCodec, "AAC");
        Assert.True(o.AudioBitrate is >= 48_000 and <= 80_000, $"the sound at {o.AudioBitrate} b/s");
        Assert.Near(source.Duration, o.Duration, 0.2, "the whole clip");
    }

    [Test]
    public static void A_vertical_clip_is_shared_vertical()
    {
        TestFfmpeg.Load();
        string vertical = Path.Combine(Fixtures, "clip-vertical.mp4");
        var info = ClipTranscoder.Probe(vertical);
        var spec = SharePlanner.CopySpec(10);
        var plan = SharePlanner.Plan(spec, 0, info.Duration, info.Width, info.Height, info.Fps, info.AudioBitrate, vertical: true, 0.5, 0);
        Assert.Equal((360, 640, (CropRect?)new CropRect(0, 0, 360, 640)), (plan.Width, plan.Height, plan.Crop), "9:16 kept, nothing cut away");
        var result = ClipTranscoder.Transcode(vertical, Temp("vertical.mp4"), plan, spec.MaxBytes, null, CancellationToken.None);
        var o = ClipTranscoder.Probe(result.Path);
        Assert.Equal((360, 640), (o.Width, o.Height), "the copy is vertical");
    }
}
