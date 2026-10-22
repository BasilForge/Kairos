using Kairos.Engine.Edit;

namespace Kairos.Tests;

/// <summary>The edit description: cuts, speed, items and the undo history (times in source seconds).</summary>
public static class EditProjectTests
{
    static string Spans(IEnumerable<ClipSpan> spans) => string.Join(", ", spans.Select(s => $"{s.Start:0.##}..{s.End:0.##}"));

    static TextItem Text(double start, double end) =>
        new(Guid.NewGuid(), start, end, "GG", new System.Windows.Rect(0.1, 0.1, 0.3, 0.1), "#FFFFFF", 0.06, TextEffect.Box);

    [Test]
    public static void Cutting_out_the_middle_joins_the_rest()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(5).SplitAt(20).DeleteSegmentAt(10);
        Assert.Equal("0..5, 20..30", Spans(p.Kept), "kept");
        Assert.Near(15, p.OutputDuration, 1e-9, "output length");
        Assert.Near(7, p.ToOutput(22), 1e-9, "source 22 plays at 7");
        Assert.Near(21, p.ToSource(6) ?? -1, 1e-9, "output 6 is source 21");
        Assert.True(!p.IsUnchanged, "changed");
        Assert.True(EditProject.For("clip.mp4", 30).IsUnchanged, "a new edit changes nothing");
    }

    [Test]
    public static void The_last_segment_cannot_be_deleted()
    {
        var p = EditProject.For("clip.mp4", 30);
        Assert.Equal("0..30", Spans(p.DeleteSegmentAt(10).Kept), "still there");
    }

    [Test]
    public static void A_split_closer_than_a_frame_is_ignored()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(5);
        Assert.Equal("0..5, 5..30", Spans(p.SplitAt(5 + 1 / 120.0).Kept), "too close to the cut");
        Assert.Equal("0..5, 5..30", Spans(p.SplitAt(0).Kept), "at the very start");
    }

    [Test]
    public static void An_item_shows_only_in_kept_parts()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(5).SplitAt(20).DeleteSegmentAt(10);
        Assert.Equal("3..5", Spans(p.Visible(Text(3, 8))), "crosses the cut");
        Assert.Equal("", Spans(p.Visible(Text(10, 12))), "inside the cut");
        Assert.Equal("4..5, 20..21", Spans(p.Visible(Text(4, 21))), "spans the cut");
    }

    [Test]
    public static void Speed_changes_output_time()
    {
        var p = EditProject.For("clip.mp4", 30).SetSpeed(0, 10, 2);
        Assert.Near(25, p.OutputDuration, 1e-9, "the first 10 s take 5");
        Assert.Near(5, p.ToOutput(10), 1e-9, "source 10 plays at 5");
        Assert.Near(10, p.ToSource(5) ?? -1, 1e-9, "and back");
        Assert.Near(30, p.SetSpeed(0, 25, 1).OutputDuration, 1e-9, "normal speed again");
    }

    [Test]
    public static void Each_part_has_its_own_speed_and_sound()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(10).WithParts([0], k => k with { Speed = 2, Levels = Sounds.Part(game: 1.5, mic: 0.5) });
        Assert.Near(25, p.OutputDuration, 1e-9, "the first part twice as fast");
        Assert.Equal(1, p.PartAt(6), "output 6 is in the second part");
        Assert.Equal(0, p.PartAt(4.9), "and 4.9 in the first");
        Assert.True(!p.IsUnchanged, "a part's sound counts as a change");
        Assert.True(!EditProject.For("clip.mp4", 30).WithParts([0], k => k with { Levels = Sounds.Part(mic: 0.2) }).IsUnchanged, "even on one part");
        // A split part's halves keep its speed and sound.
        var split = p.SplitAt(2);
        Assert.Equal("0..4 x2 1.5/0.5, 4..10 x2 1.5/0.5, 10..30 x1 1/1",
            string.Join(", ", split.Kept.Select(k => $"{k.Start:0.##}..{k.End:0.##} x{k.Speed:0.##} {k.Levels[0]:0.##}/{k.Levels[1]:0.##}")), "halves");
    }

    [Test]
    public static void Chosen_parts_are_removed_but_never_all()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(10).SplitAt(20);
        Assert.Equal("10..20", Spans(p.WithoutParts([0, 2]).Kept), "two of three gone");
        Assert.True(ReferenceEquals(p, p.WithoutParts([0, 1, 2])), "all of them: nothing happens");
        Assert.True(ReferenceEquals(p, p.WithoutParts([5])), "a part that is not there");
    }

    [Test]
    public static void Set_speed_splits_at_both_ends()
    {
        var p = EditProject.For("clip.mp4", 30).SetSpeed(5, 10, 0.5);
        Assert.Equal("0..5, 5..10, 10..30", Spans(p.Kept), "the stretch is its own part");
        Assert.Near(0.5, p.Kept[1].Speed, 1e-9, "slow");
        Assert.Near(35, p.OutputDuration, 1e-9, "5 s take 10");
    }

    [Test]
    public static void A_part_can_have_its_own_colour_look()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(10) with { Look = ColourLook.Warm };
        p = p.WithParts([1], k => k with { Look = ColourLook.Mono, LookStrength = 0.5 });
        Assert.Equal((ColourLook.Warm, 1.0), p.LookAt(5), "the first part: the clip's look");
        Assert.Equal((ColourLook.Mono, 0.5), p.LookAt(20), "the second its own");
        Assert.True(!EditProject.For("clip.mp4", 30).WithParts([0], k => k with { Look = ColourLook.Vivid }).IsUnchanged, "a part's look is a change");
        var all = p.WithLook(ColourLook.Cool);
        Assert.True(all.Look == ColourLook.Cool && all.LookStrength == 1 && all.Kept.All(k => k.Look == null), "the whole clip's look: every part takes it");
        Assert.Equal(ColourLook.Mono, p.SplitAt(25).Kept[2].Look, "a split half keeps it");
    }

    [Test]
    public static void Q_and_W_cut_a_part_to_the_playhead()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(10);
        Assert.Equal("0..10, 15..30", Spans(p.TrimPartAt(15, start: true).Kept), "Q: from the part's start to here is gone");
        Assert.Equal("0..10, 10..15", Spans(p.TrimPartAt(15, start: false).Kept), "W: from here to the part's end");
        Assert.True(ReferenceEquals(p, p.TrimPartAt(10, start: true)), "at the part's start: nothing to cut");
    }

    [Test]
    public static void Items_are_added_replaced_and_removed()
    {
        var item = Text(1, 2);
        var p = EditProject.For("clip.mp4", 30).With(item);
        Assert.Equal(1, p.Items.Count, "added");
        p = p.With(item with { End = 4 });
        Assert.Equal(1, p.Items.Count, "replaced by id");
        Assert.Near(4, p.Items[0].End, 1e-9, "the new one");
        Assert.Equal(0, p.Without(item.Id).Items.Count, "removed");
    }

    [Test]
    public static void Undo_and_redo_walk_the_history()
    {
        var start = EditProject.For("clip.mp4", 30);
        var history = new EditHistory(start);
        Assert.True(!history.CanUndo && !history.CanRedo, "nothing yet");
        var first = start.SplitAt(5);
        var second = first.SplitAt(10);
        history.Push(first);
        history.Push(second);
        Assert.True(history.Undo() && history.Undo(), "two steps back");
        Assert.True(ReferenceEquals(start, history.Current), "the start");
        Assert.True(history.Redo() && ReferenceEquals(first, history.Current), "one forward");
        history.Push(first.SplitAt(20));
        Assert.True(!history.CanRedo, "a new change drops what was undone");
    }
}
