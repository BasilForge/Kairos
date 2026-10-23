using Kairos.Engine.Edit;

namespace Kairos.Tests;

/// <summary>The FFmpeg filter text an edit becomes: colour looks, cuts, speed, blur, items and sound.</summary>
public static class EditGraphTests
{
    [Test]
    public static void Each_look_is_a_filter_and_none_is_nothing()
    {
        Assert.Equal(null, ColourLooks.Filter(ColourLook.None, 1), "none");
        Assert.Equal(null, ColourLooks.Filter(ColourLook.Vivid, 0), "no strength");
        Assert.Equal("eq=saturation=1.35:contrast=1.08", ColourLooks.Filter(ColourLook.Vivid, 1), "vivid");
        Assert.Equal("eq=saturation=1.175:contrast=1.04", ColourLooks.Filter(ColourLook.Vivid, 0.5), "half vivid");
        Assert.Equal("eq=contrast=1.12:saturation=0.85,colorbalance=rs=-0.05:bs=0.07:rh=0.06:bh=-0.04",
            ColourLooks.Filter(ColourLook.Cinematic, 1), "cinematic");
        Assert.Equal("colorbalance=rs=0.08:gs=0.02:bs=-0.08:rm=0.05:bm=-0.05", ColourLooks.Filter(ColourLook.Warm, 1), "warm");
        Assert.Equal("colorbalance=rs=-0.07:bs=0.08:bm=0.05", ColourLooks.Filter(ColourLook.Cool, 1), "cool");
        Assert.Equal("unsharp=5:5:0.9:5:5:0", ColourLooks.Filter(ColourLook.Sharp, 1), "sharp");
        Assert.Equal("hue=s=0", ColourLooks.Filter(ColourLook.Mono, 1), "mono");
    }

    [Test]
    public static void Mono_at_half_strength_keeps_half_the_colour()
    {
        Assert.Equal("hue=s=0.5", ColourLooks.Filter(ColourLook.Mono, 0.5), "half");
        Assert.Equal("unsharp=5:5:0.45:5:5:0", ColourLooks.Filter(ColourLook.Sharp, 0.5), "half sharp");
    }

    static readonly IReadOnlyDictionary<Guid, Overlay> NoPngs = new Dictionary<Guid, Overlay>();

    static EditGraph Build(EditProject p, int audioStreams = 3, IReadOnlyDictionary<Guid, Overlay>? pngs = null) =>
        EditGraph.Build(p, 1920, 1080, 60, audioStreams, pngs ?? NoPngs);

    [Test]
    public static void An_unedited_clip_passes_through()
    {
        var g = Build(EditProject.For("c.mp4", 30));
        Assert.True(g.Video.StartsWith("[vin]", StringComparison.Ordinal) && g.Video.EndsWith("[vout]", StringComparison.Ordinal), g.Video);
        Assert.True(g.Video.Contains("trim=start=0:end=30", StringComparison.Ordinal), g.Video);
        Assert.True(!g.Video.Contains("concat", StringComparison.Ordinal), "one piece: no concat");
        Assert.True(g.Audio.Contains("[aout]", StringComparison.Ordinal), g.Audio);
        Assert.Equal("aout,at0,at1", string.Join(",", g.AudioOutputs), "the mix, the game and the mic, like the clip");
        Assert.True(!g.Audio.Contains("amix", StringComparison.Ordinal) && g.Audio.Contains("[a0]", StringComparison.Ordinal),
            "nothing changed in the sound: track 1 is copied as recorded, not mixed again: " + g.Audio);
        Assert.Equal("0,1,2", string.Join(",", g.AudioStreams), "every track read");
    }

    [Test]
    public static void An_unchanged_sound_copies_every_track_cut_and_retimed()
    {
        var layout = Kairos.Engine.Audio.TrackLayout.Of(["Mix", "Game", "Discord", "Microphone", "Other sounds"], 5);
        var p = EditProject.For("c.mp4", 30).SplitAt(10).WithParts([0], k => k with { Speed = 2 });
        var g = EditGraph.Build(p, 1920, 1080, 60, 5, NoPngs, layout);
        Assert.True(!g.Audio.Contains("amix", StringComparison.Ordinal) && !g.Audio.Contains("volume=", StringComparison.Ordinal), "no mixing, no levels: " + g.Audio);
        Assert.True(g.Audio.Contains("[a0]asplit=2", StringComparison.Ordinal) && g.Audio.Contains("atempo=2", StringComparison.Ordinal), "cut and retimed: " + g.Audio);
        Assert.Equal(5, g.AudioOutputs.Count, "the mix and the four tracks");
        Assert.True(g.Audio.Contains("concat=n=2:v=0:a=1[aout]", StringComparison.Ordinal) && g.Audio.Contains("concat=n=2:v=0:a=1[at3]", StringComparison.Ordinal), g.Audio);
    }

    [Test]
    public static void A_cut_concats_two_pieces()
    {
        var g = Build(EditProject.For("c.mp4", 30).SplitAt(5).SplitAt(20).DeleteSegmentAt(10));
        Assert.True(g.Video.Contains("concat=n=2:v=1:a=0", StringComparison.Ordinal), g.Video);
        Assert.True(g.Video.Contains("trim=start=20:end=30", StringComparison.Ordinal), g.Video);
        Assert.True(g.Audio.Contains("atrim=start=20:end=30", StringComparison.Ordinal), g.Audio);
        Assert.True(g.Audio.Contains("concat=n=2:v=0:a=1", StringComparison.Ordinal), g.Audio);
    }

    [Test]
    public static void Speed_two_uses_setpts_and_atempo()
    {
        var g = Build(EditProject.For("c.mp4", 30).SetSpeed(0, 10, 2));
        Assert.True(g.Video.Contains("setpts=(PTS-STARTPTS)/2", StringComparison.Ordinal), g.Video);
        Assert.True(g.Audio.Contains("atempo=2", StringComparison.Ordinal), g.Audio);
        Assert.True(g.Video.Contains("fps=60", StringComparison.Ordinal), "the clip's frame rate throughout");
    }

    [Test]
    public static void Quarter_speed_chains_atempo()
    {
        var g = Build(EditProject.For("c.mp4", 30).SetSpeed(0, 10, 0.25));
        Assert.True(g.Audio.Contains("atempo=0.5,atempo=0.5", StringComparison.Ordinal), g.Audio);
    }

    [Test]
    public static void A_blur_is_cropped_blurred_and_laid_over_in_its_time()
    {
        var blur = new BlurItem(Guid.NewGuid(), 3, 5, new System.Windows.Rect(0.5, 0.5, 0.25, 0.25));
        var g = Build(EditProject.For("c.mp4", 30).With(blur));
        Assert.True(g.Video.Contains("crop=480:270:960:540", StringComparison.Ordinal), g.Video);
        Assert.True(g.Video.Contains("boxblur", StringComparison.Ordinal), g.Video);
        Assert.True(g.Video.Contains("enable='between(t,3,5)'", StringComparison.Ordinal), g.Video);
    }

    [Test]
    public static void Game_and_mic_mix_with_their_levels()
    {
        var g = Build(EditProject.For("c.mp4", 30) with { Sound = Sounds.Clip(game: 0.5, micMuted: true) });
        Assert.Equal("1,2", string.Join(",", g.AudioStreams), "game and mic tracks");
        Assert.True(g.Audio.Contains("volume=0.5,asplit=2[", StringComparison.Ordinal), g.Audio);
        Assert.True(g.Audio.Contains("volume=0,asplit=2[", StringComparison.Ordinal), g.Audio);
        Assert.True(g.Audio.Contains("[t0o0]anull[at0]", StringComparison.Ordinal) && g.Audio.Contains("[t1o0]anull[at1]", StringComparison.Ordinal),
            "game and mic kept apart at their levels: " + g.Audio);
        Assert.True(g.Audio.Contains("amix=inputs=2", StringComparison.Ordinal), g.Audio);
    }

    [Test]
    public static void A_clip_with_its_own_tracks_sets_each_one()
    {
        var layout = Kairos.Engine.Audio.TrackLayout.Of(["Mix", "Game", "Discord", "Microphone", "Other sounds"], 5);
        var p = EditProject.For("c.mp4", 30).SplitAt(10).WithParts([1], k => k with { Levels = k.Levels.With(0, 1.5) })
            with { Sound = new SoundLevels { Tracks = default(TrackLevels).WithMuted(1, true).With(2, 0.5) } };
        var g = EditGraph.Build(p, 1920, 1080, 60, 5, NoPngs, layout);
        Assert.Equal("1,2,3,4", string.Join(",", g.AudioStreams), "the four tracks that make the mix");
        Assert.Equal("Mix,Game,Discord,Microphone,Other sounds", string.Join(",", g.AudioNames), "named like the clip");
        Assert.Equal(5, g.AudioOutputs.Count, "the mix and each track");
        Assert.True(g.Audio.Contains("amix=inputs=4:normalize=0", StringComparison.Ordinal), g.Audio);
        Assert.True(g.Audio.Contains("atrim=start=0:end=10,asetpts=PTS-STARTPTS,volume=0,", StringComparison.Ordinal), "Discord muted: " + g.Audio);
        Assert.True(g.Audio.Contains("atrim=start=10:end=30,asetpts=PTS-STARTPTS,volume=1.5,", StringComparison.Ordinal), "the part's louder game: " + g.Audio);
        Assert.True(g.Audio.Contains("atrim=start=0:end=10,asetpts=PTS-STARTPTS,volume=0.5,", StringComparison.Ordinal), "the mic at half: " + g.Audio);
        Assert.Equal("Mix,System,Microphone", string.Join(",", Build(EditProject.For("c.mp4", 30)).AudioNames), "a 1.0.1 clip's tracks get their names");
        Assert.Equal("Mix", string.Join(",", Build(EditProject.For("c.mp4", 30), audioStreams: 1).AudioNames), "one track: the mix");
    }

    [Test]
    public static void One_stream_uses_the_all_level()
    {
        var g = Build(EditProject.For("c.mp4", 30) with { Sound = new SoundLevels(All: 0.7) }, audioStreams: 1);
        Assert.Equal("0", string.Join(",", g.AudioStreams), "the mix");
        Assert.True(g.Audio.Contains("volume=0.7[", StringComparison.Ordinal), g.Audio);
        Assert.True(!g.Audio.Contains("amix", StringComparison.Ordinal), g.Audio);
        var silent = Build(EditProject.For("c.mp4", 30), audioStreams: 0);
        Assert.True(silent.Audio == "" && silent.AudioStreams.Count == 0, "no sound: no audio graph");
    }

    [Test]
    public static void A_louder_part_has_its_own_volume()
    {
        var p = EditProject.For("c.mp4", 30).SplitAt(10).WithParts([1], k => k with { Levels = Sounds.Part(game: 1.5, mic: 0.5) })
            with { Sound = Sounds.Clip(game: 0.8) };
        var g = Build(p);
        Assert.True(g.Audio.Contains("atrim=start=0:end=10,asetpts=PTS-STARTPTS,volume=0.8,asplit=2[", StringComparison.Ordinal), "first part's game: " + g.Audio);
        Assert.True(g.Audio.Contains("atrim=start=10:end=30,asetpts=PTS-STARTPTS,volume=1.2,asplit=2[", StringComparison.Ordinal), "second part's game 0.8 x 1.5: " + g.Audio);
        Assert.True(g.Audio.Contains("atrim=start=10:end=30,asetpts=PTS-STARTPTS,volume=0.5,asplit=2[", StringComparison.Ordinal), "second part's mic: " + g.Audio);
        Assert.True(g.Audio.Contains("concat=n=2:v=0:a=1[aout]", StringComparison.Ordinal), g.Audio);
        Assert.True(g.Audio.Contains("[t0q0][t0q1]concat=n=2:v=0:a=1[at0]", StringComparison.Ordinal)
                    && g.Audio.Contains("[t1q0][t1q1]concat=n=2:v=0:a=1[at1]", StringComparison.Ordinal), "game and mic joined apart: " + g.Audio);
        var one = Build(EditProject.For("c.mp4", 30).SplitAt(10).WithParts([0], k => k with { Levels = Sounds.Part(game: 2), Speed = 2 }), audioStreams: 1);
        Assert.True(one.Audio.Contains("atrim=start=0:end=10,asetpts=PTS-STARTPTS,volume=2,atempo=2[", StringComparison.Ordinal), "a one-track clip: " + one.Audio);
    }

    [Test]
    public static void A_part_look_is_on_only_while_that_part_plays()
    {
        var p = EditProject.For("c.mp4", 30).SplitAt(10).WithParts([1], k => k with { Look = ColourLook.Mono });
        var g = Build(p);
        Assert.True(g.Video.Contains("hue=s=0:enable='between(t,10,30)'", StringComparison.Ordinal), g.Video);
        Assert.True(!g.Video.Contains("between(t,0,10)", StringComparison.Ordinal), "the first part has none: " + g.Video);
        var both = Build(p with { Look = ColourLook.Warm });
        Assert.True(both.Video.Contains("colorbalance=rs=0.08:gs=0.02:bs=-0.08:rm=0.05:bm=-0.05:enable='between(t,0,10)'", StringComparison.Ordinal),
            "the clip's look on the other part: " + both.Video);
        Assert.True(both.Video.IndexOf("hue=s=0", StringComparison.Ordinal) < both.Video.IndexOf("overlay", StringComparison.Ordinal)
                    || !both.Video.Contains("overlay", StringComparison.Ordinal), "looks come before texts, so texts keep their colour");
    }

    [Test]
    public static void Overlay_paths_are_escaped()
    {
        var item = new TextItem(Guid.NewGuid(), 1, 2, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        var g = Build(EditProject.For("c.mp4", 30).With(item), pngs: new Dictionary<Guid, Overlay> { [item.Id] = new(@"C:\T\a.png", 128, 72) });
        Assert.True(g.Video.Contains(@"movie='C\:/T/a.png'", StringComparison.Ordinal), g.Video);
        Assert.True(g.Video.Contains("overlay=128:72:enable='between(t,1,2)'", StringComparison.Ordinal), "laid where the item is: " + g.Video);
    }

    [Test]
    public static void A_zoom_crops_after_the_looks_and_blurs_and_before_the_drawings()
    {
        var plain = EditProject.For("clip.mp4", 30).SplitAt(10);
        Assert.True(!Build(plain).Video.Contains("zoom"), "no zoom: no crop");
        Assert.Equal(null, Build(plain).ZoomAt, "nothing to drive");
        var blur = new BlurItem(Guid.NewGuid(), 0, 30, new System.Windows.Rect(0.1, 0.1, 0.2, 0.2));
        var text = new TextItem(Guid.NewGuid(), 0, 30, "hi", new System.Windows.Rect(0.4, 0.4, 0.2, 0.1), "#FFFFFF", 0.05, TextEffect.Plain);
        var zoomed = plain.With(text).With(blur).WithParts([1], k => k with { Zoom = new System.Windows.Rect(0, 0, 0.5, 0.5) });
        var g = Build(zoomed, pngs: new Dictionary<Guid, Overlay> { [text.Id] = new Overlay(@"C:		ext.png", 10, 10) });
        int crop = g.Video.IndexOf("crop@zoom=w=iw:h=ih:x=0:y=0,scale=1920:1080", StringComparison.Ordinal);
        Assert.True(crop > 0, g.Video);
        Assert.True(g.Video.IndexOf("boxblur", StringComparison.Ordinal) < crop, "the blur is zoomed with the picture");
        Assert.True(g.Video.IndexOf("movie=", StringComparison.Ordinal) > crop, "texts and drawings stay put");
        Assert.True(g.ZoomAt != null, "the export drives it");
    }

    [Test]
    public static void A_zoom_in_pixels_is_even_and_inside_the_picture()
    {
        Assert.Equal((960, 540, 0, 0), VideoZoom.Crop(new System.Windows.Rect(0, 0, 0.5, 0.5), 1920, 1080), "the top-left quarter");
        var (w, h, x, y) = VideoZoom.Crop(new System.Windows.Rect(0.333, 0.777, 0.3, 0.3), 1920, 1080);
        Assert.True(w % 2 == 0 && h % 2 == 0 && x % 2 == 0 && y % 2 == 0, "even (4:2:0)");
        Assert.True(x + w <= 1920 && y + h <= 1080, "inside");
        Assert.Equal((1920, 1080, 0, 0), VideoZoom.Crop(VideoZoom.Full, 1920, 1080), "the whole picture");
    }
}
