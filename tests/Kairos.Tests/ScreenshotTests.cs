using Kairos.App;
using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Screenshots: the key, where and how they are saved, and how they count toward the folder size limit.</summary>
public static class ScreenshotTests
{
    static string TempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"))).FullName;

    static string MakeFile(string dir, string name, int kb, DateTime time)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[kb * 1024]);
        File.SetLastWriteTime(path, time);
        return path;
    }

    [Test]
    public static void The_shutter_sound_is_short_quiet_and_not_the_clip_chime()
    {
        byte[] wav = ShotSound.Wav();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4), "a WAV file");
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4), "a WAV file");
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22), "mono");
        Assert.Equal(48000, BitConverter.ToInt32(wav, 24), "48 kHz");
        int samples = BitConverter.ToInt32(wav, 40) / 2;
        Assert.True(samples / 48000.0 < 0.25, $"short: {samples / 48000.0:F2} s");
        double peak = 0;
        for (int i = 0; i < samples; i++)
            peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(wav, 44 + i * 2) / 32768.0));
        Assert.True(peak > 0.05 && peak <= 0.3, $"heard but not loud: peak {peak:F2}");
        Assert.True(!wav.AsSpan().SequenceEqual(Chime.Wav()), "a different sound from the clip's");
    }

    [Test]
    public static void Screenshot_hotkey_defaults_to_alt_f12_also_for_old_settings()
    {
        Assert.Equal("Alt+F12", new AppSettings().Hotkeys.Screenshot, "new settings");
        Assert.Equal("Alt+F12", SettingsStore.Deserialize("""{"hotkeys":{"save":"Alt+F10"}}""").Hotkeys.Screenshot, "settings from before");
    }

    [Test]
    public static void A_screenshot_is_named_and_placed_like_a_clip()
    {
        var t = new DateTime(2026, 10, 15, 21, 30, 0);
        Assert.Equal(@"D:\Clips\Elden Ring\Elden Ring 2026-10-15 21-30-00.png",
            ClipOrganizer.TargetPath(@"D:\Clips", "Elden Ring", t, true, ".png"), "a folder per game");
        Assert.Equal(@"D:\Clips\Elden Ring 2026-10-15 21-30-00.png",
            ClipOrganizer.TargetPath(@"D:\Clips", "Elden Ring", t, false, ".png"), "all in one folder");
        Assert.Equal(@"D:\Clips\Elden Ring\Elden Ring 2026-10-15 21-30-00.mp4",
            ClipOrganizer.TargetPath(@"D:\Clips", "Elden Ring", t, true), "clips as before");
    }

    [Test]
    public static void The_writer_saves_an_opaque_png_of_the_frame()
    {
        string dir = TempDir();
        try
        {
            // 4x2 BGRA: red, green, blue, white / then see-through black (the canvas where nothing is drawn).
            byte[] bgra =
            [
                0, 0, 255, 255,  0, 255, 0, 255,  255, 0, 0, 255,  255, 255, 255, 255,
                0, 0, 0, 0,      0, 0, 0, 0,      0, 0, 0, 0,      0, 0, 0, 0,
            ];
            string path = ScreenshotWriter.Write(Path.Combine(dir, "Game", "Game 2026-10-15 21-30-00.png"), new ScreenshotFrame(bgra, 4, 2));

            Assert.Equal(Path.Combine(dir, "Game", "Game 2026-10-15 21-30-00.png"), path, "where it was asked to go");
            using var png = new System.Drawing.Bitmap(path);
            Assert.Equal((4, 2), (png.Width, png.Height), "the frame's size");
            Assert.Equal(System.Drawing.Color.FromArgb(255, 255, 0, 0), png.GetPixel(0, 0), "red stays red");
            Assert.Equal(System.Drawing.Color.FromArgb(255, 0, 0, 255), png.GetPixel(2, 0), "blue stays blue");
            Assert.Equal(System.Drawing.Color.FromArgb(255, 0, 0, 0), png.GetPixel(1, 1), "no see-through pixels: opaque black");
            Assert.Equal(1, Directory.GetFiles(Path.Combine(dir, "Game")).Length, "no temp file left behind");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void A_half_written_screenshot_left_by_a_crash_is_cleared_away()
    {
        string dir = TempDir();
        try
        {
            string stale = Path.Combine(dir, ".Game 2026-10-15 20-00-00.png.part");
            string fresh = Path.Combine(dir, ".Game 2026-10-15 21-29-59.png.part");
            File.WriteAllBytes(stale, [1, 2, 3]);
            File.SetLastWriteTime(stale, DateTime.Now.AddMinutes(-10));
            File.WriteAllBytes(fresh, [1, 2, 3]); // another one being written right now

            string png = ScreenshotWriter.Write(Path.Combine(dir, "Game 2026-10-15 21-30-00.png"), new ScreenshotFrame(new byte[2 * 2 * 4], 2, 2));

            Assert.True(!File.Exists(stale), "the leftover from before is gone");
            Assert.True(File.Exists(fresh), "one being written now is left alone");
            var left = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToList();
            Assert.Equal(".Game 2026-10-15 21-29-59.png.part | Game 2026-10-15 21-30-00.png", string.Join(" | ", left),
                "the new picture, and no part file of its own (they are dot files the library never lists)");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void A_second_screenshot_in_the_same_second_gets_a_number()
    {
        string dir = TempDir();
        try
        {
            string first = Path.Combine(dir, "Game 2026-10-15 21-30-00.png");
            var frame = new ScreenshotFrame(new byte[2 * 2 * 4], 2, 2);
            Assert.Equal(first, ScreenshotWriter.Write(first, frame), "the first one");
            Assert.Equal(Path.Combine(dir, "Game 2026-10-15 21-30-00 (2).png"), ScreenshotWriter.Write(first, frame), "the second one");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void Copying_a_picture_offers_the_image_and_the_file()
    {
        string dir = TempDir();
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                string png = ScreenshotWriter.Write(Path.Combine(dir, "Game 2026-10-15 21-30-00.png"), new ScreenshotFrame(new byte[8 * 4 * 4], 8, 4));
                // Built, not put on the clipboard: tests leave the user's clipboard alone.
                var data = Kairos.Ui.PictureClipboard.Data(png);
                Assert.True(data.GetDataPresent(System.Windows.DataFormats.Bitmap), "the image itself (Paint, WhatsApp)");
                var files = data.GetFileDropList();
                Assert.True(files.Count == 1 && files[0] == png, "and the file (Discord, Explorer)");
            }
            catch (Exception e)
            {
                error = e.ToString();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Directory.Delete(dir, recursive: true);
        Assert.True(error == null, error ?? "");
    }

    [Test]
    public static void A_jpeg_copy_fits_under_the_sites_limit()
    {
        string dir = TempDir();
        try
        {
            // Noise: the worst case for size (a busy game scene), about 2.7 MB as a PNG.
            var noise = new Random(3);
            var bgra = new byte[1280 * 720 * 4];
            noise.NextBytes(bgra);
            string png = ScreenshotWriter.Write(Path.Combine(dir, "Game 2026-10-15 21-30-00.png"), new ScreenshotFrame(bgra, 1280, 720));
            string jpg = Path.Combine(dir, "X", "Game 2026-10-15 21-30-00.jpg");

            var result = Kairos.Engine.Share.PictureJpeg.Make(png, jpg, 400_000);

            Assert.True(result.Bytes <= 400_000 && new FileInfo(jpg).Length == result.Bytes, $"under the limit: {result.Bytes} bytes ({result.Encoder})");
            byte[] head = File.ReadAllBytes(jpg)[..2];
            Assert.True(head[0] == 0xFF && head[1] == 0xD8, "a JPEG");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static void OnSta(Action test)
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
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

    static StatusDto Status(Kairos.Engine.Voice.VoiceStatus? voice = null, string? lastShot = null, bool shotKeyFree = true) =>
        new(null, new Dictionary<string, bool> { ["screenshot"] = shotKeyFree }, null, null, false, false, "1.24.0", voice, lastShot);

    [Test]
    public static void The_settings_window_shows_the_screenshot_key_and_the_last_screenshot() => OnSta(() =>
    {
        Text.Language = "en";
        var vm = new Kairos.Ui.SettingsViewModel();
        string key = vm.HkScreenshot;
        Assert.True(key.Length > 0, "a screenshot key (Alt+F12 unless changed)");
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SeedStatus(Status(lastShot: @"C:\Videos\Game\Game 2026-10-15 21-30-00.png", shotKeyFree: false));
        Assert.Equal(@"C:\Videos\Game\Game 2026-10-15 21-30-00.png", vm.LastShotPath, "the newest screenshot");
        Assert.True(raised.Contains(nameof(Kairos.Ui.SettingsViewModel.LastShotPath)), "the Clips page hears of it");
    });

    [Test]
    public static void Smart_save_shows_each_phrase_on_its_own() => OnSta(() =>
    {
        Text.Language = "en";
        var vm = new Kairos.Ui.SettingsViewModel();
        vm.SeedStatus(Status(new Kairos.Engine.Voice.VoiceStatus(true, true, "idle", 0, null, null, false, 0.06, true, HasShotPhrase: false)));
        Assert.Equal(("ready", "none"), (vm.VoiceView, vm.ShotView), "a clip phrase, no screenshot phrase yet");
        vm.SeedStatus(Status(new Kairos.Engine.Voice.VoiceStatus(true, true, "enrolling", 1, null, null, false, 0.06, true, false, "shot")));
        Assert.Equal(("ready", "enrolling"), (vm.VoiceView, vm.ShotView), "recording the screenshot phrase shows in its own card");
        vm.SeedStatus(Status(new Kairos.Engine.Voice.VoiceStatus(true, false, "enrolling", 2, null, null, false, 0.06, true, true, "clip")));
        Assert.Equal(("enrolling", "ready"), (vm.VoiceView, vm.ShotView), "and the clip phrase in its own");
    });

    [Test]
    public static void A_broken_picture_is_read_as_nothing_instead_of_failing() => OnSta(() =>
    {
        string dir = TempDir();
        try
        {
            string broken = Path.Combine(dir, "Game 2026-10-15 21-30-00.png");
            File.WriteAllBytes(broken, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]); // a PNG cut short
            Assert.Equal(null, Kairos.Ui.PictureClipboard.TryLoad(broken), "nothing to show, no exception");
            Assert.Equal(null, Kairos.Ui.PictureClipboard.TryLoad(Path.Combine(dir, "gone.png")), "a picture that is gone");
            var data = Kairos.Ui.PictureClipboard.Data(broken);
            Assert.True(!data.GetDataPresent(System.Windows.DataFormats.Bitmap) && data.GetFileDropList().Count == 1, "copy still offers the file");
            Assert.Equal(null, new Kairos.Ui.WpfShareHost(_ => { }, () => dir).ReadPicture(broken), "the share panel says it cannot read it");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Test]
    public static void A_test_started_from_the_screenshot_card_shows_there_and_names_the_phrase() => OnSta(() =>
    {
        Text.Language = "en";
        var vm = new Kairos.Ui.SettingsViewModel();
        vm.TestCard = "shot";
        vm.SeedStatus(Status(new Kairos.Engine.Voice.VoiceStatus(true, true, "testing", 0, null, 0.03, true, 0.06, true, true, "clip", "shot")));
        Assert.Equal(("ready", "testing"), (vm.VoiceView, vm.ShotView), "the test shows in the screenshot card");
        Assert.Equal("Matched: screenshot phrase", vm.VoiceResult, "and says which phrase it heard");
        vm.TestCard = "clip";
        vm.SeedStatus(Status(new Kairos.Engine.Voice.VoiceStatus(true, true, "testing", 0, null, 0.03, true, 0.06, true, true, "clip", "clip")));
        Assert.Equal(("testing", "ready"), (vm.VoiceView, vm.ShotView), "from the clip card: there");
        Assert.Equal("Matched: clip phrase", vm.VoiceResult, "the clip phrase");
    });

    [Test]
    public static void The_size_limit_counts_kairos_screenshots_but_never_other_pictures()
    {
        string dir = TempDir();
        try
        {
            var index = new ClipIndex(Path.Combine(dir, "clips.txt"));
            var day = new DateTime(2026, 1, 1);
            string photo = MakeFile(Path.Combine(dir, "Captures"), "Holiday.png", 300, day);
            string oldShot = MakeFile(Path.Combine(dir, "Game"), "Game 2026-01-02 10-00-00.png", 300, day.AddDays(1));
            string newClip = MakeFile(Path.Combine(dir, "Game"), "Game 2026-01-03 10-00-00.mp4", 300, day.AddDays(2));
            index.Add(oldShot);
            index.Add(newClip);

            var recycled = new List<string>();
            int removed = StorageJanitor.Enforce(dir, limitBytes: 400 * 1024, index, recycled.Add);

            Assert.Equal(1, removed, "one file over the limit");
            Assert.Equal(oldShot, recycled.Single(), "the oldest thing Kairos saved, a screenshot");
            Assert.True(File.Exists(photo), "a picture Kairos did not save is never touched");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
