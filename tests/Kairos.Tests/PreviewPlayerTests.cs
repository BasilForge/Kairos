using System.Diagnostics;
using Kairos.Engine.Share;
using Kairos.Ui;

namespace Kairos.Tests;

/// <summary>The share panel's player, for real: decoding, the sound card's clock (playing silence) and the pictures.</summary>
public static class PreviewPlayerTests
{
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share");

    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint milliseconds);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] static extern uint timeEndPeriod(uint milliseconds);

    /// <summary>
    /// Runs <paramref name="body"/> on a UI-style (STA) thread like the settings window's. The app shows pictures on
    /// each screen refresh; here a loop of short sleeps stands in for it, so the timer is made precise for the run
    /// (Windows' default makes a 4 ms sleep last 15.6 ms, which would drop frames the app never drops).
    /// </summary>
    static void OnUiThread(Action body)
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            timeBeginPeriod(1);
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
            finally
            {
                timeEndPeriod(1);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error == null, error ?? "");
    }

    [Test]
    public static void A_faster_part_plays_at_its_speed_with_every_picture_filtered()
    {
        // The editor's preview: two seconds of the clip at double speed take about one, and each picture passes the filter.
        TestFfmpeg.Load();
        OnUiThread(() =>
        {
            string result = "running";
            int filtered = 0;
            var watch = Stopwatch.StartNew();
            using var player = new PreviewPlayer(Path.Combine(Fixtures, "clip-av1-2tracks.mp4"), 1.0, 3.0, 320, 180, mute: true,
                _ => { }, failure => result = failure ?? "ended", rate: 2, filter: (pixels, time) =>
                {
                    Interlocked.Increment(ref filtered);
                    Assert.True(time >= 0.9 && time < 3.0, $"its time in the clip: {time}");
                });
            while (result == "running" && watch.Elapsed.TotalSeconds < 8)
            {
                player.Tick();
                Thread.Sleep(4);
            }
            Assert.Equal("ended", result, "reached the end");
            Assert.True(watch.Elapsed.TotalSeconds < 1.7, $"twice as fast: {watch.Elapsed.TotalSeconds:0.00} s");
            Assert.True(filtered > 20, $"pictures filtered: {filtered}");
            // Its sound at double speed (silent here) is the clock: all two seconds of it were read.
            Assert.True(player.HasAudio, "the clip has sound");
            Assert.Near(2.0, player.SoundSeconds, 0.1, "the cut's sound read at double speed");
        });
    }

    [Test]
    public static void The_cut_plays_in_real_time_with_its_pictures_and_the_playhead_reaches_the_end()
    {
        TestFfmpeg.Load();
        OnUiThread(() =>
        {
            string result = "running";
            double last = 0;
            var watch = new Stopwatch();
            using var player = new PreviewPlayer(Path.Combine(Fixtures, "clip-av1-2tracks.mp4"), 2.0, 4.0, 640, 360, mute: true,
                t =>
                {
                    if (!watch.IsRunning)
                        watch.Start();
                    last = t;
                }, failure => result = failure ?? "ended");
            var limit = Stopwatch.StartNew();
            while (result == "running" && limit.Elapsed.TotalSeconds < 8)
            {
                player.Tick();
                Thread.Sleep(4);
            }
            Assert.Equal("ended", result, "reached the end");
            Assert.Near(2.0, watch.Elapsed.TotalSeconds, 0.15, "in real time");
            Assert.Near(4.0, last, 0.001, "the playhead reached the end of the cut");
            Assert.True(player.Shown >= 110, $"pictures shown: {player.Shown} of 120 (skipped {player.Skipped})");
            Assert.Equal((640, 360), (player.Picture.PixelWidth, player.Picture.PixelHeight), "picture size");
            // With every buffer full, the reading thread waits for a picture to be shown (or a little sound to be heard)
            // instead of looking again every 2 ms: about 1000 looks in 2 s before, a few hundred at most now.
            Assert.True(player.Waits < 300, $"the reading thread rests: {player.Waits} waits in 2 s");
        });
    }

    [Test]
    public static void A_file_that_keeps_its_sound_in_long_runs_still_plays_every_picture()
    {
        TestFfmpeg.Load();
        // Like Kairos's own recordings (and OBS replays): pictures and sound stored in runs of a second or more, so the
        // sound for a moment sits far from its pictures in the file.
        string fragmented = Path.Combine(Path.GetTempPath(), $"kairos-runs-{Guid.NewGuid():N}.mp4");
        try
        {
            string source = Path.Combine(Fixtures, "clip-h264.mp4");
            var info = ClipTranscoder.Probe(source);
            ClipCutter.Cut(source, fragmented, 0, info.Duration, null, CancellationToken.None, "+frag_keyframe+empty_moov+default_base_moof");
            double seconds = Math.Min(3.0, info.Duration - 0.2);
            OnUiThread(() =>
            {
                string result = "running";
                using var player = new PreviewPlayer(fragmented, 0, seconds, 640, 360, mute: true, _ => { }, failure => result = failure ?? "ended");
                var limit = Stopwatch.StartNew();
                while (result == "running" && limit.Elapsed.TotalSeconds < seconds + 4)
                {
                    player.Tick();
                    Thread.Sleep(4);
                }
                double expected = seconds * info.Fps;
                Assert.Equal("ended", result, "reached the end");
                Assert.True(player.Shown >= expected * 0.95, $"pictures shown: {player.Shown} of {expected:F0} ({player.SkipReasons})");
                Assert.True(player.LongestGapMs < 80, $"longest wait between pictures: {player.LongestGapMs:F0} ms");
                Assert.Near(seconds, player.SoundSeconds, 0.05, "all the sound");
            });
        }
        finally
        {
            File.Delete(fragmented);
        }
    }

    [Test]
    public static void Pictures_are_shown_in_order_and_dropped_only_when_far_behind_the_sound()
    {
        const double frame = 1.0 / 60;
        Assert.Equal((0, false), PreviewPlayer.Choose([1.0], 0.99), "not due yet");
        Assert.Equal((0, true), PreviewPlayer.Choose([1.0], 1.0), "due");
        Assert.Equal((0, true), PreviewPlayer.Choose([1.0, 1.0 + frame, 1.0 + 2 * frame], 1.0 + 2 * frame),
            "after a short stall: the oldest first, the others on the next refreshes");
        Assert.Equal((2, true), PreviewPlayer.Choose([0.9, 0.95, 1.0], 1.0), "pictures far behind the sound are dropped");
        Assert.Equal((0, false), PreviewPlayer.Choose([], 1.0), "nothing ready");
    }

    [Test]
    public static void The_preview_volume_scales_the_sound_and_zero_is_silence()
    {
        short[] samples = [1000, -1000, 32767, -32768];
        byte[] Pcm() => samples.SelectMany(BitConverter.GetBytes).ToArray();
        short[] Read(byte[] b) => Enumerable.Range(0, b.Length / 2).Select(i => BitConverter.ToInt16(b, i * 2)).ToArray();
        var half = Pcm();
        SoundOut.Scale(half, 0.5);
        Assert.Equal("500 -500 16383 -16384", string.Join(" ", Read(half)), "half");
        var full = Pcm();
        SoundOut.Scale(full, 1);
        Assert.Equal(string.Join(" ", samples), string.Join(" ", Read(full)), "full is untouched");
        var off = Pcm();
        SoundOut.Scale(off, 0);
        Assert.True(Read(off).All(v => v == 0), "silence");
    }

    [Test]
    public static void Above_full_volume_the_sound_is_louder_and_never_wraps()
    {
        short[] samples = [1000, -1000, 20000, -20000];
        var pcm = samples.SelectMany(BitConverter.GetBytes).ToArray();
        SoundOut.Scale(pcm, 2);
        var read = Enumerable.Range(0, pcm.Length / 2).Select(i => BitConverter.ToInt16(pcm, i * 2));
        Assert.Equal("2000 -2000 32767 -32768", string.Join(" ", read), "twice as loud, held at the ends");
    }

    [Test]
    public static void Stopping_halfway_is_quick_and_reports_no_end()
    {
        TestFfmpeg.Load();
        OnUiThread(() =>
        {
            bool ended = false;
            var player = new PreviewPlayer(Path.Combine(Fixtures, "clip-h264.mp4"), 1.0, 5.0, 640, 360, mute: true, _ => { }, _ => ended = true);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 0.5)
            {
                player.Tick();
                Thread.Sleep(4);
            }
            var stop = Stopwatch.StartNew();
            player.Dispose();
            player.Tick();
            Assert.True(stop.Elapsed.TotalSeconds < 0.5, $"stopped in {stop.Elapsed.TotalMilliseconds:F0} ms");
            Assert.True(!ended, "a stop is not an end");
        });
    }

    /// <summary>
    /// The editor's next part, read ahead held: nothing shown, heard or ended while it waits, however long (a short part
    /// is read to its end meanwhile); let go, its first picture shows on the very next refresh and it plays in real time.
    /// </summary>
    [Test]
    public static void A_part_read_ahead_waits_then_shows_its_first_picture_at_once()
    {
        TestFfmpeg.Load();
        OnUiThread(() =>
        {
            foreach (var (to, seconds) in new[] { (3.0, 1.0), (2.1, 0.1) })
            {
                string result = "running";
                double? first = null;
                using var player = new PreviewPlayer(Path.Combine(Fixtures, "clip-av1-2tracks.mp4"), 2.0, to, 320, 180, mute: true,
                    t => first ??= t, failure => result = failure ?? "ended", held: true);
                var waiting = Stopwatch.StartNew();
                while (waiting.Elapsed.TotalSeconds < 1.5)
                {
                    player.Tick();
                    Thread.Sleep(4);
                }
                Assert.True(player.Shown == 0 && first == null && result == "running", $"{seconds} s part held: nothing shown, no time, no end");
                player.Release();
                player.Tick();
                Assert.Equal(1, player.Shown, $"{seconds} s part: its first picture at once");
                Assert.Near(2.0, first ?? -1, 0.02, "from the part's start");
                var watch = Stopwatch.StartNew();
                while (result == "running" && watch.Elapsed.TotalSeconds < 5)
                {
                    player.Tick();
                    Thread.Sleep(4);
                }
                Assert.Equal("ended", result, $"{seconds} s part played to its end");
                Assert.True(watch.Elapsed.TotalSeconds > seconds - 0.1 && watch.Elapsed.TotalSeconds < seconds + 0.4,
                    $"{seconds} s part in real time once let go: {watch.Elapsed.TotalSeconds:0.00} s");
            }
        });
    }

    [Test]
    public static void A_clip_that_cannot_be_read_reports_why()
    {
        TestFfmpeg.Load();
        OnUiThread(() =>
        {
            string? failure = null;
            using var player = new PreviewPlayer(Path.Combine(Fixtures, "missing.mp4"), 0, 2, 640, 360, mute: true, _ => { }, f => failure = f ?? "ended");
            var watch = Stopwatch.StartNew();
            while (failure == null && watch.Elapsed.TotalSeconds < 3)
            {
                player.Tick();
                Thread.Sleep(4);
            }
            Assert.True(failure != null && failure.Contains("not found"), failure ?? "no report");
        });
    }

    /// <summary>Pictures decoded ahead take the same memory at any size: 20 at 1280×720, fewer when larger, never under six.</summary>
    [Test]
    public static void The_pictures_kept_ahead_fit_one_memory_budget()
    {
        Assert.Equal(20, PreviewPlayer.PoolFor(1280, 720), "the editor's size: as before");
        Assert.Equal(20, PreviewPlayer.PoolFor(640, 360), "smaller: no more than before");
        Assert.Equal(7, PreviewPlayer.PoolFor(2078, 1168), "the Library player in a large window: about the same 74 MB, not 194");
        Assert.Equal(6, PreviewPlayer.PoolFor(2560, 1440), "at least a tenth of a second");
    }
}
