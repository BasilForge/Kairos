using Kairos.Engine.Edit;

namespace Kairos.Tests;

/// <summary>The levels of a clip's own sound tracks in an edit: values, so undo and "unchanged" compare them.</summary>
public static class TrackLevelsTests
{
    [Test]
    public static void Track_levels_are_values()
    {
        var a = default(TrackLevels).With(2, 0.5).WithMuted(1, true);
        var b = default(TrackLevels).WithMuted(1, true).With(2, 0.5);
        Assert.True(a == b, "the same levels are the same value");
        Assert.True(default(TrackLevels).With(3, 0.4).With(3, 1) == default, "a level back at 1 is as recorded");
        Assert.True(a[2] == 0.5 && a[0] == 1 && a[4] == 1, "the level set, the others as recorded");
        Assert.True(a.Muted(1) && !a.Muted(2) && a.Heard(1) == 0 && a.Heard(2) == 0.5, "muted is heard at 0");
        Assert.True(default(TrackLevels).IsPlain && !a.IsPlain, "plain until something changes");
    }

    [Test]
    public static void Any_tracks_level_is_a_change()
    {
        Assert.True(!(new ClipSpan(0, 1) with { Levels = default(TrackLevels).With(4, 0.5) }).IsPlain, "any track's level makes a part not plain");
        Assert.True(new SoundLevels() == new SoundLevels { Tracks = default }, "the default sound");
        Assert.True(!(EditProject.For("c.mp4", 10) with { Sound = new SoundLevels { Tracks = default(TrackLevels).WithMuted(3, true) } }).IsUnchanged,
            "a muted track is a change to save");
    }

    [Test]
    public static void Levels_stay_in_range()
    {
        var p = EditProject.For("c.mp4", 10).WithParts([0], k => k with { Levels = k.Levels.With(3, 5).With(4, -1) });
        Assert.True(p.Kept[0].Levels[3] == ClipSpan.MaxLevel && p.Kept[0].Levels[4] == 0, "kept within 0..2");
    }
}
