using System.Windows;
using Kairos.Engine.Edit;

namespace Kairos.Tests;

/// <summary>Zoom in the editor: a part of the video zooms into an area, easing in and out at its edges.</summary>
public static class VideoZoomTests
{
    static readonly Rect Full = new(0, 0, 1, 1);
    static readonly Rect Quarter = new(0, 0, 0.5, 0.5);

    /// <summary>A 30 s clip cut at 10 s and 20 s, its middle part zoomed.</summary>
    static EditProject Middle(Rect? zoom = null, double speed = 1)
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(10).SplitAt(20);
        return p.WithParts([1], k => k with { Zoom = zoom ?? Quarter, Speed = speed });
    }

    static void Near(Rect want, Rect got, string what)
    {
        bool close = Math.Abs(want.X - got.X) < 1e-6 && Math.Abs(want.Y - got.Y) < 1e-6
                     && Math.Abs(want.Width - got.Width) < 1e-6 && Math.Abs(want.Height - got.Height) < 1e-6;
        Assert.True(close, $"{what}: expected {want} but was {got}");
    }

    [Test]
    public static void A_zoomed_part_eases_in_and_out_at_its_edges()
    {
        var p = Middle();
        Near(Full, p.ZoomAt(5), "before the part");
        Near(Full, p.ZoomAt(10), "the part's first frame: still the whole picture");
        Near(new Rect(0, 0, 0.75, 0.75), p.ZoomAt(10.15), "half way in (smoothstep at 0.5)");
        Near(Quarter, p.ZoomAt(10.3), "in");
        Near(Quarter, p.ZoomAt(15), "the middle");
        Near(new Rect(0, 0, 0.75, 0.75), p.ZoomAt(19.85), "half way out");
        Near(Full, p.ZoomAt(25), "after the part");
        Assert.True(p.HasZoom && !p.IsUnchanged, "a zoom is an edit");
    }

    [Test]
    public static void Neighbours_with_the_same_zoom_do_not_ease_between_them()
    {
        var p = Middle().SplitAt(15);
        Near(Quarter, p.ZoomAt(15), "at the split: still zoomed");
        Near(Quarter, p.ZoomAt(14.9), "just before it");
        Assert.Equal(p.Kept[1].Zoom, p.Kept[2].Zoom, "both halves keep the zoom");
    }

    [Test]
    public static void A_short_part_eases_in_and_out_without_a_jump()
    {
        var p = EditProject.For("clip.mp4", 30).SplitAt(10).SplitAt(10.4).WithParts([1], k => k with { Zoom = Quarter });
        var mid = p.ZoomAt(10.2);
        Assert.True(mid.Width > 0.5 && mid.Width < 1, $"part way: {mid.Width}");
        Near(Full, p.ZoomAt(10.4), "out at its end");
    }

    [Test]
    public static void A_faster_part_eases_over_what_is_seen()
    {
        var p = Middle(speed: 2);
        Near(new Rect(0, 0, 0.75, 0.75), p.ZoomAt(10.3), "0.3 s of source is 0.15 s seen at 2×: half way");
        Near(Quarter, p.ZoomAt(10.6), "in after 0.3 s seen");
    }

    [Test]
    public static void Zooms_change_a_part_and_go_with_undo()
    {
        var plain = EditProject.For("clip.mp4", 30).SplitAt(10);
        var zoomed = plain.WithParts([0], k => k with { Zoom = Quarter });
        Assert.True(zoomed.Kept[0] != plain.Kept[0] && !zoomed.Kept[0].IsPlain, "a zoomed part differs");
        Assert.Equal(plain.Kept[0], zoomed.WithParts([0], k => k with { Zoom = null }).Kept[0], "no zoom: the same part");
        Assert.True(!plain.HasZoom, "nothing zoomed");
    }

    [Test]
    public static void A_drawn_box_takes_the_videos_shape()
    {
        var box = VideoZoom.Fit(new Rect(0.2, 0.2, 0.4, 0.1));
        Assert.Near(box.Width, box.Height, 1e-9, "same share of each side keeps the video's shape");
        Assert.True(box.Width >= 0.4 - 1e-9, "never smaller than drawn across");
        var corner = VideoZoom.Fit(new Rect(0.9, 0.9, 0.3, 0.3));
        Assert.True(corner.Right <= 1 + 1e-9 && corner.Bottom <= 1 + 1e-9, "inside the picture");
        Assert.True(VideoZoom.Fit(new Rect(0.5, 0.5, 0.01, 0.01)).Width >= VideoZoom.MinSize - 1e-9, "not smaller than 10 %");
    }

    static Kairos.Ui.Edit.EditorViewModel Editor()
    {
        Kairos.App.Text.Language = "en";
        var vm = new Kairos.Ui.Edit.EditorViewModel(EditProject.For(@"C:\Clips.mp4", 30), isPicture: false, Kairos.Ui.Edit.EditorMode.Library, 16 / 9.0, 3);
        vm.Apply(vm.Project.SplitAt(10).SplitAt(20));
        return vm;
    }

    [Test]
    public static void The_zoom_tool_zooms_the_chosen_parts()
    {
        var vm = Editor();
        vm.SelectPart(1);
        vm.ZoomCommand.Execute(null);
        Assert.True(vm.IsZoomMode && vm.SelectedParts.SequenceEqual([1]), "the chosen part stays chosen");
        Assert.Equal("Draw the area to zoom to on the picture", vm.PageHint, "what to do");
        vm.ZoomTo(new Rect(0.5, 0.5, 0.4, 0.2));
        Assert.Equal(VideoZoom.Fit(new Rect(0.5, 0.5, 0.4, 0.2)), vm.Project.Kept[1].Zoom, "the part zooms, the video's shape");
        Assert.True(vm.Project.Kept[0].Zoom == null && vm.Project.Kept[2].Zoom == null, "only that part");
        Assert.Equal(vm.Project.Kept[1].Zoom, vm.ZoomShown, "the box shows over the picture");
        Assert.True(vm.PartZoomed, "Remove zoom shows");
        vm.RemoveZoomCommand.Execute(null);
        Assert.True(!vm.Project.HasZoom, "removed");
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Project.Kept[1].Zoom != null, "undo brings it back");
    }

    [Test]
    public static void The_zoom_tool_takes_the_playing_part_when_none_is_chosen()
    {
        var vm = Editor();
        vm.PlayheadOutput = 25;
        vm.ZoomCommand.Execute(null);
        Assert.True(vm.SelectedParts.SequenceEqual([2]), "the part under the playhead");
        vm.SelectPart(0);
        Assert.True(vm.IsZoomMode, "choosing another part keeps the tool");
        vm.ModeDoneCommand.Execute(null);
        Assert.True(!vm.IsZoomMode && vm.ZoomShown == null, "Done ends it");
    }

    [Test]
    public static void A_screenshot_has_no_zoom_tool()
    {
        var vm = new Kairos.Ui.Edit.EditorViewModel(EditProject.For(@"C:\Clips.png", 0), isPicture: true, Kairos.Ui.Edit.EditorMode.Library, 16 / 9.0, 0);
        Assert.True(!vm.CanZoom && !vm.ZoomCommand.CanExecute(null), "pictures crop instead");
    }
}
