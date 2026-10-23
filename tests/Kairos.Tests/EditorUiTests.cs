using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Audio;
using Kairos.Engine.Edit;
using Kairos.Ui;
using Kairos.Ui.Edit;

namespace Kairos.Tests;

/// <summary>The editor's windows and their view models (no window is shown).</summary>
public static class EditorUiTests
{
    static ClipItem Item(string path, ClipKind kind) => new()
    {
        Path = path,
        Name = Path.GetFileNameWithoutExtension(path),
        App = "Game",
        Time = new DateTime(2026, 10, 16, 12, 0, 0),
        Size = 1024,
        Kind = kind,
    };

    [Test]
    public static void Playing_a_video_opens_kairos_player()
    {
        Text.Language = "en";
        var vm = new ClipsViewModel(() => Path.GetTempPath(), () => "Alt+F10", () => "Alt+F12");
        ClipItem? video = null, picture = null;
        vm.VideoRequested += c => video = c;
        vm.PictureRequested += c => picture = c;
        vm.PlayCommand.Execute(Item(@"C:\Clips\Game\a.mp4", ClipKind.Video));
        vm.PlayCommand.Execute(Item(@"C:\Clips\Game\b.png", ClipKind.Picture));
        Assert.Equal("a", video?.Name, "a video opens in Kairos, not the Windows player");
        Assert.Equal("b", picture?.Name, "a picture in the picture viewer");
    }

    static readonly ClipSpan Plain = new(0, 30);

    static EditorViewModel Video(EditorMode mode = EditorMode.Library, int audioStreams = 3) =>
        new(EditProject.For(@"C:\Clips\a.mp4", 30), isPicture: false, mode, aspect: 16 / 9.0, audioStreams);

    static EditorViewModel Picture() =>
        new(EditProject.For(@"C:\Clips\b.png", 0), isPicture: true, EditorMode.Library, aspect: 2, audioStreams: 0);

    [Test]
    public static void Reset_brings_the_clip_back_as_it_was_and_one_undo_returns_the_edits()
    {
        var vm = Video();
        Assert.True(!vm.ResetCommand.CanExecute(null), "nothing to reset");
        var text = new TextItem(Guid.NewGuid(), 1, 3, "GG", new System.Windows.Rect(0.1, 0.1, 0.3, 0.08), "#FFFFFF", 0.06, TextEffect.Box);
        vm.Apply(vm.Project.SplitAt(10).With(text));
        Assert.True(vm.ResetCommand.CanExecute(null), "edits: it can reset");
        vm.ResetCommand.Execute(null);
        Assert.True(vm.AskingReset && !vm.Project.IsUnchanged, "asks first, nothing changed yet");
        vm.CancelResetCommand.Execute(null);
        Assert.True(!vm.AskingReset && vm.Project.Items.Count == 1, "kept");
        vm.ResetCommand.Execute(null);
        vm.ConfirmResetCommand.Execute(null);
        Assert.True(!vm.AskingReset && vm.Project.IsUnchanged, "the clip as it was");
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Project.Kept.Count == 2 && vm.Project.Items.Count == 1, "one undo returns every edit");
    }

    [Test]
    public static void The_rows_grow_with_a_taller_editor_and_shrink_only_past_the_limit()
    {
        var vm = Video();
        double width = TimelineLayout.HeadWidth + 900 + TimelineLayout.EndPad;
        Assert.Near(TimelineLayout.BarHeight, new TimelineLayout(vm.Project, vm.Rows, width).Bar, 1e-9, "no room given: comfortable bars");
        var taller = new TimelineLayout(vm.Project, vm.Rows, width, fillHeight: 200);
        Assert.True(taller.Bar > TimelineLayout.BarHeight && taller.Height <= 200 + 1e-6, $"a taller editor: taller bars filling it ({taller.Bar:0.#} px, {taller.Height:0} px)");
        Assert.Near(TimelineLayout.TallBarHeight, new TimelineLayout(vm.Project, vm.Rows, width, fillHeight: 5000).Bar, 1e-9, "never taller than the tallest");
        Assert.Near(TimelineLayout.BarHeight, new TimelineLayout(vm.Project, vm.Rows, width, fillHeight: 60).Bar, 1e-9, "a short editor: comfortable bars, not thinner");
        var capped = new TimelineLayout(vm.Project, vm.Rows, width, maxHeight: 90, fillHeight: 400);
        Assert.True(capped.Bar < TimelineLayout.BarHeight, "past the limit the bars still get thinner");
    }

    [Test]
    public static void Every_row_shows_from_the_start_at_one_height_and_parts_stand_apart()
    {
        var vm = Video();
        Assert.Equal(4, vm.Rows.Count, "video, text, draw and shapes from the start");
        vm.Apply(vm.Project.SplitAt(10).SplitAt(20));
        var layout = new TimelineLayout(vm.Project, vm.Rows, TimelineLayout.HeadWidth + 900 + TimelineLayout.EndPad);
        Assert.Near(layout.RowHeightOf(1), layout.RowHeightOf(0), 1e-9, "the video row is as tall as an item row");
        Assert.Near(layout.RowHeightOf(1), layout.RowHeightOf(3), 1e-9, "every empty row the same");
        Assert.True(layout.PartBox(1).Left - layout.PartBox(0).Right >= 8, $"a gap you can see between parts ({layout.PartBox(1).Left - layout.PartBox(0).Right:0.#} px)");
        Assert.Near(layout.PartBox(0).Height, layout.Bar, 1e-9, "a part bar is as tall as an item bar");
    }

    [Test]
    public static void The_whole_clips_speed_sets_every_part_and_says_when_they_differ()
    {
        var vm = Video();
        vm.Apply(vm.Project.SplitAt(10).SplitAt(20));
        Assert.Equal(3, vm.Project.Kept.Count, "three parts");
        vm.ClipSpeed = 2;
        Assert.True(vm.Project.Kept.All(k => Math.Abs(k.Speed - 2) < 1e-9), "every part at 2x");
        Assert.Equal("2.0×", vm.ClipSpeedLabel, "the label");
        vm.SelectPart(0);
        vm.PartSpeed = 1;
        Assert.True(double.IsNaN(vm.ClipSpeed), "the parts differ now");
        Assert.Equal(Text.Get("editor.speed.varies"), vm.ClipSpeedLabel, "and the label says so");
        Assert.True(vm.ClipSpeeds.All(s => !s.IsSelected), "no speed tile chosen");
        vm.ClipSpeeds.Single(s => Math.Abs(s.Value - 0.5) < 1e-9).IsSelected = true;
        Assert.True(vm.Project.Kept.All(k => Math.Abs(k.Speed - 0.5) < 1e-9), "a tile sets them all");
        Assert.True(vm.ClipSpeeds.Single(s => Math.Abs(s.Value - 0.5) < 1e-9).IsSelected, "and shows as chosen");
    }

    /// <summary>From the share panel too, Save asks how to save (a new copy or in place of the original).</summary>
    [Test]
    public static void The_share_editor_saves_like_the_library()
    {
        Text.Language = "en";
        Assert.Equal("Save", Video(EditorMode.Share).PrimaryLabel, "share");
        Assert.Equal("Save", Video().PrimaryLabel, "library");
        var vm = Video(EditorMode.Share);
        bool asked = false;
        vm.SaveAsked += () => asked = true;
        Assert.True(!vm.PrimaryCommand.CanExecute(null), "nothing to save yet");
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        vm.PrimaryCommand.Execute(null);
        Assert.True(asked, "asks how to save");
    }

    /// <summary>
    /// Saving keeps the editor up with its progress until the edit is fully written (nothing can be changed, closed or
    /// pressed meanwhile); then it is done. A cancel or a failure goes back to editing.
    /// </summary>
    [Test]
    public static void Saving_shows_its_progress_and_holds_the_editor()
    {
        Text.Language = "en";
        var vm = Video();
        bool closed = false, cancelled = false;
        vm.Closed += () => closed = true;
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        vm.BeginSaving(replace: false, cancel: () => cancelled = true);
        Assert.True(vm.IsSaving && vm.SavePercent == "0%" && vm.SaveNote == "As a new copy", "saving, from 0%");
        vm.ReportSaving(0.456);
        Assert.Equal("45%", vm.SavePercent, "its progress");
        Assert.True(!vm.KeyCommand("Delete") && !vm.KeyCommand("S") && vm.Project.Kept.Count == 2, "no edits meanwhile");
        vm.CloseCommand.Execute(null);
        Assert.True(!closed && !vm.AskingDiscard, "and no leaving");
        vm.CancelSaveCommand.Execute(null);
        Assert.True(cancelled, "Cancel stops the save");
        vm.EndSaving();
        Assert.True(!vm.IsSaving, "back to editing");
        vm.BeginSaving(replace: true, cancel: () => { });
        Assert.Equal("Replacing the original", vm.SaveNote, "in place of the original");
        vm.FinishSaving();
        Assert.True(vm.IsSaving && vm.SaveDone && vm.SavePercent == "100%", "done: full, a check");
        vm.EndSaving();
        vm.BeginSaving(replace: false, cancel: () => { });
        vm.FailSaving("disk full");
        Assert.True(vm.HasSaveError && vm.SaveError.Contains("disk full", StringComparison.Ordinal), "why it failed");
        vm.BackToEditingCommand.Execute(null);
        Assert.True(!vm.IsSaving, "back to editing");
    }

    /// <summary>Back or Esc with changes asks before throwing them away (40 captions are not lost to a double Esc).</summary>
    [Test]
    public static void Leaving_with_changes_asks_first()
    {
        var vm = Video();
        bool closed = false;
        vm.Closed += () => closed = true;
        vm.CloseCommand.Execute(null);
        Assert.True(closed && !vm.AskingDiscard, "nothing changed: it just closes");

        vm = Video();
        closed = false;
        vm.Closed += () => closed = true;
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        vm.CloseCommand.Execute(null);
        Assert.True(!closed && vm.AskingDiscard, "asks first");
        vm.KeepEditingCommand.Execute(null);
        Assert.True(!closed && !vm.AskingDiscard && vm.Project.Kept.Count == 2, "keeps editing, the edit intact");
        vm.CloseCommand.Execute(null);
        vm.DiscardCommand.Execute(null);
        Assert.True(closed && !vm.AskingDiscard, "thrown away only when asked to");
    }

    internal static void OnSta(Action test)
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
                error = e.Message;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.True(error == null, error ?? "");
    }

    /// <summary>Stands in for the preview player: no picture decoding, no sound.</summary>
    internal sealed class FakeSession : IPlayerSession
    {
        public readonly List<double> Starts = [];
        public readonly List<int> Widths = [];
        public readonly List<Action<double>> Positions = [];
        public readonly List<Action<byte[], double>?> Filters = [];
        /// <summary>Where each part read ahead starts, and each started or read-ahead part's picture.</summary>
        public readonly List<double> Queued = [];
        public readonly List<System.Windows.Media.Imaging.WriteableBitmap> Pictures = [];
        public bool Playing;
        public Kairos.Engine.Share.TrackMix? Mix;
        Action<string?>? _ended;
        (Action<double> Position, Action<string?> Ended, Action<byte[], double>? Filter, Kairos.Engine.Share.TrackMix? Mix, double Volume)? _next;

        public bool Start(string clip, double from, double to, int width, int height,
            Action<System.Windows.Media.Imaging.WriteableBitmap> show, Action<double> position, Action<string?> ended, double rate = 1,
            Action<byte[], double>? filter = null, Kairos.Engine.Share.TrackMix? mix = null)
        {
            Starts.Add(from);
            Widths.Add(width);
            Mix = mix;
            Positions.Add(ShownFirst(show, position));
            Filters.Add(filter);
            Playing = true;
            _ended = ended;
            _next = null;
            return true;
        }

        public bool Queue(string clip, double from, double to, int width, int height,
            Action<System.Windows.Media.Imaging.WriteableBitmap> show, Action<double> position, Action<string?> ended, double rate,
            Action<byte[], double>? filter, Kairos.Engine.Share.TrackMix? mix, double volume)
        {
            Queued.Add(from);
            _next = (ShownFirst(show, position), ended, filter, mix, volume);
            return true;
        }

        /// <summary>A new picture, shown with the first position, as the player does once its first frame is in it.</summary>
        Action<double> ShownFirst(Action<System.Windows.Media.Imaging.WriteableBitmap> show, Action<double> position)
        {
            var picture = new System.Windows.Media.Imaging.WriteableBitmap(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            Pictures.Add(picture);
            bool shown = false;
            return t =>
            {
                if (!shown)
                {
                    shown = true;
                    show(picture);
                }
                position(t);
            };
        }

        /// <summary>The playing part plays to its end: the part read ahead goes on, as the player does.</summary>
        public void End()
        {
            var ended = _ended!;
            if (_next is { } next)
            {
                Positions.Add(next.Position);
                Filters.Add(next.Filter);
                (Mix, Volume, _ended, _next) = (next.Mix, next.Volume, next.Ended, null);
            }
            else
                Playing = false;
            ended(null);
        }

        public void Stop()
        {
            Playing = false;
            _next = null;
        }

        public double Volume { get; set; }
    }

    /// <summary>
    /// A split clip plays straight through: the next part is read ahead while this one plays and goes on the moment it
    /// ends (it was started only then, its picture blank while the clip was opened and read up to the cut: black between
    /// the parts, 2026-10-22); and a part's picture shows only once its first frame is in it.
    /// </summary>
    [Test]
    public static void A_split_clip_plays_straight_through_without_a_blank_picture() => OnSta(() =>
    {
        var vm = Video();
        vm.Apply(vm.Project.SplitAt(10)); // two parts: 0..10 and 10..30
        var session = new FakeSession();
        var shown = new List<System.Windows.Media.Imaging.BitmapSource?>();
        using var playback = new EditorPlayback(vm, shown.Add, _ => { }, session);
        vm.KeyCommand("Space");
        Assert.True(session.Playing && shown.Count == 0, "playing, its picture not shown blank: the one before stays");
        session.Positions[0](0);
        Assert.True(shown.Count == 1 && shown[0] == session.Pictures[0], "shown with its first frame");

        session.Positions[0](7.9);
        Assert.Equal(0, session.Queued.Count, "more than 2 s left: the next part is not read yet");
        session.Positions[0](8.1);
        Assert.Equal("10", string.Join(",", session.Queued), "2 s before the cut, the next part is read ahead");
        session.Positions[0](9);
        Assert.Equal(1, session.Queued.Count, "read ahead once");

        vm.ClipTracks[0].Level = 0.5;
        Assert.True(session.Queued.Count == 1 && session.Starts.Count == 1, "a sound level changed: both play on, nothing read again");

        session.End();
        Assert.True(session.Playing && session.Starts.Count == 1, "the next part goes on, not started again");
        Assert.Equal(1, shown.Count, "its picture waits for its first frame");
        session.Positions[^1](10);
        Assert.True(shown.Count == 2 && shown[1] == session.Pictures[^1], "then shows");
        Assert.Near(10, vm.PlayheadOutput, 1e-9, "the playhead goes on from the cut");
        Assert.Near(0.5, session.Mix!.Gains[0], 1e-9, "at its sound level as it is now");

        session.Positions[^1](29);
        Assert.Equal(1, session.Queued.Count, "the last part: nothing to read ahead");
        session.End();
        Assert.True(!session.Playing, "stops at the end");
    });

    /// <summary>
    /// Moving through the clip while it plays: a click plays on from there, a drag waits until the mouse is let go,
    /// and the arrow keys step a second back or on. Paused, it stays paused.
    /// </summary>
    [Test]
    public static void Seeking_while_playing_plays_on_from_there() => OnSta(() =>
    {
        var vm = Video();
        var session = new FakeSession();
        using var playback = new EditorPlayback(vm, _ => { }, _ => { }, session);
        vm.KeyCommand("Space");
        Assert.True(session.Playing && session.Starts.Count == 1, "playing");
        session.Positions[0](2);
        Assert.Near(2, vm.PlayheadOutput, 1e-9, "the playhead follows");

        vm.BeginSeek();
        vm.PlayheadOutput = 10;
        vm.EndSeek();
        Assert.True(session.Playing, "still playing after a click");
        Assert.Near(10, session.Starts[^1], 1e-9, "from where the timeline was clicked");
        session.Positions[0](2.1);
        Assert.Near(10, vm.PlayheadOutput, 1e-9, "the old run no longer moves the playhead");

        vm.BeginSeek();
        Assert.True(!session.Playing, "waits while dragging");
        vm.PlayheadOutput = 15;
        vm.PlayheadOutput = 20;
        Assert.True(!session.Playing, "still waiting");
        vm.EndSeek();
        Assert.True(session.Playing, "plays again when let go");
        Assert.Near(20, session.Starts[^1], 1e-9, "from where the drag ended");

        Assert.True(vm.KeyCommand("Right"), "the right arrow is used");
        Assert.Near(21, session.Starts[^1], 1e-9, "a second on");
        vm.KeyCommand("Left");
        vm.KeyCommand("Left");
        Assert.Near(19, session.Starts[^1], 1e-9, "two seconds back");

        vm.KeyCommand("Space");
        Assert.True(!session.Playing, "paused");
        int starts = session.Starts.Count;
        vm.KeyCommand("Right");
        Assert.True(!session.Playing && session.Starts.Count == starts, "seeking while paused does not play");
        Assert.Near(20, vm.PlayheadOutput, 1e-9, "but moves");
    });

    static System.Windows.Media.Imaging.RenderTargetBitmap Shot(System.Windows.FrameworkElement element, int width, int height)
    {
        element.Measure(new System.Windows.Size(width, height));
        element.Arrange(new System.Windows.Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    static byte[] Pixels(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes;
    }

    /// <summary>
    /// While the clip plays, the playhead moves 60 times a second: only the playhead moves, the timeline is not drawn
    /// again (it was, every frame), and the picture is exactly what drawing it all again gives.
    /// </summary>
    [Test]
    public static void Moving_the_playhead_moves_only_the_playhead() => OnSta(() =>
    {
        var vm = Video();
        vm.Apply(vm.Project.SplitAt(10).With(new TextItem(Guid.NewGuid(), 2, 20, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box)));
        var timeline = new EditTimeline(vm);
        Shot(timeline, 900, 220);
        int renders = timeline.Renders;
        for (int i = 1; i <= 30; i++)
            vm.PlayheadOutput = i * 0.5;
        var moved = Shot(timeline, 900, 220);
        Assert.Equal(renders, timeline.Renders, "the timeline is not drawn again while the playhead moves");

        var fresh = Video();
        fresh.Apply(vm.Project);
        fresh.PlayheadOutput = 15;
        var drawn = Shot(new EditTimeline(fresh), 900, 220);
        Assert.True(Pixels(moved).SequenceEqual(Pixels(drawn)), "the same picture as drawing it all again");

        vm.TimelineZoom = 10;
        Shot(timeline, 900, 220);
        int zoomed = timeline.Renders;
        vm.PlayheadOutput = 29;
        Shot(timeline, 900, 220);
        Assert.True(timeline.Renders > zoomed, "zoomed in, a playhead leaving the view scrolls it (drawn again)");
    });

    /// <summary>The picture's overlay is drawn again only when what shows changes (an item comes or goes), not every frame.</summary>
    [Test]
    public static void The_overlay_redraws_only_when_what_shows_changes() => OnSta(() =>
    {
        var vm = Video();
        vm.Apply(vm.Project.With(new TextItem(Guid.NewGuid(), 2, 20, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box)));
        var canvas = new EditCanvas(vm);
        vm.PlayheadOutput = 3;
        Shot(canvas, 640, 360);
        int renders = canvas.Renders;
        for (int i = 0; i < 20; i++)
            vm.PlayheadOutput = 3 + i * 0.5;
        Shot(canvas, 640, 360);
        Assert.Equal(renders, canvas.Renders, "the text shows all along: not drawn again");
        vm.PlayheadOutput = 25;
        Shot(canvas, 640, 360);
        Assert.Equal(renders + 1, canvas.Renders, "the text is gone: drawn again once");
    });

    /// <summary>A tile or button reached with Tab keeps its arrows (to walk through them); a clicked one leaves them to the playhead.</summary>
    [Test]
    public static void A_tile_reached_with_tab_keeps_its_arrows() => OnSta(() =>
    {
        Assert.True(EditorView.KeepsArrows(new System.Windows.Controls.Slider(), fromKeyboard: false), "a slider always");
        Assert.True(EditorView.KeepsArrows(new System.Windows.Controls.RadioButton(), fromKeyboard: true), "a tile reached with Tab");
        Assert.True(!EditorView.KeepsArrows(new System.Windows.Controls.RadioButton(), fromKeyboard: false), "a clicked tile: the playhead");
        Assert.True(!EditorView.KeepsArrows(new System.Windows.Controls.Border(), fromKeyboard: true), "anything else: the playhead");
        Assert.True(!EditorView.KeepsArrows(null, fromKeyboard: true), "nothing focused: the playhead");
    });

    /// <summary>
    /// Holding an arrow key while the clip plays moves the playhead without starting the player on every repeat
    /// (it restarted about 30 times a second): it waits while the key is held and plays on once when let go.
    /// </summary>
    [Test]
    public static void Holding_an_arrow_plays_on_once_when_let_go() => OnSta(() =>
    {
        var vm = Video();
        var session = new FakeSession();
        using var playback = new EditorPlayback(vm, _ => { }, _ => { }, session);
        vm.KeyCommand("Space");
        int starts = session.Starts.Count;
        vm.KeyCommand("Shift+Right");
        Assert.Equal(starts + 1, session.Starts.Count, "the first press plays on from there");
        for (int i = 0; i < 30; i++)
            vm.KeyCommand("Shift+Right", repeat: true);
        Assert.Equal(starts + 1, session.Starts.Count, "no start on each repeat");
        Assert.True(!session.Playing, "waits while the key is held");
        Assert.Near(3.1, vm.PlayheadOutput, 1e-6, "the playhead moved all the way");
        vm.KeyReleased();
        Assert.True(session.Playing && session.Starts.Count == starts + 2, "plays on once when let go");
        Assert.Near(3.1, session.Starts[^1], 1e-6, "from where the key was let go");
        vm.KeyReleased();
        Assert.Equal(starts + 2, session.Starts.Count, "a second release does nothing");

        vm.KeyCommand("Space");
        vm.Apply(vm.Project.SplitAt(10).SplitAt(20));
        vm.PlayheadOutput = 0;
        vm.KeyCommand("Space");
        int again = session.Starts.Count;
        vm.KeyCommand("Down");
        Assert.Equal(again + 1, session.Starts.Count, "a jump plays on from the cut");
        vm.KeyCommand("Down", repeat: true);
        Assert.True(!session.Playing && session.Starts.Count == again + 1, "a held jump waits");
        vm.KeyReleased();
        Assert.True(session.Playing && session.Starts.Count == again + 2, "and plays on once when let go");
        Assert.Near(20, session.Starts[^1], 1e-6, "from the second cut");
    });

    /// <summary>
    /// Changing things while the clip plays keeps it playing: a text's colour, size or font (drawn over the picture),
    /// a blur box (in the playing pictures at once) and the sound level go on without a stop; a cut plays on with the
    /// new parts.
    /// </summary>
    [Test]
    public static void Editing_while_playing_keeps_playing() => OnSta(() =>
    {
        var vm = Video();
        var session = new FakeSession();
        using var playback = new EditorPlayback(vm, _ => { }, _ => { }, session);
        var text = new TextItem(Guid.NewGuid(), 0, 30, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        vm.Apply(vm.Project.With(text));
        vm.SelectedItem = text;
        vm.KeyCommand("Space");
        int starts = session.Starts.Count;

        vm.TextColor = "#FFD54A";
        vm.TextSize = 0.09;
        vm.Fonts.Single(f => f.Key == "lalezar").IsSelected = true;
        Assert.True(session.Playing && session.Starts.Count == starts, "a text restyled: still playing, not started again");

        vm.Apply(vm.Project.With(new BlurItem(Guid.NewGuid(), 0, 30, new System.Windows.Rect(0, 0, 0.5, 1))));
        Assert.True(session.Playing && session.Starts.Count == starts, "a blur added: still playing");
        const int w = 1280, h = 720; // the preview's size for a 16:9 clip
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4)
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(i / 4 % 2 == 0 ? 0 : 255); // stripes
        var before = (byte[])pixels.Clone();
        session.Filters[^1]!(pixels, 1.0);
        int row = h / 2 * w * 4;
        Assert.True(!pixels.AsSpan(row, w * 2).SequenceEqual(before.AsSpan(row, w * 2)), "the left half of the playing picture is blurred at once");
        Assert.True(pixels.AsSpan(row + w * 3, w).SequenceEqual(before.AsSpan(row + w * 3, w)), "the right half is not");

        vm.ClipTracks[0].Level = 0.3;
        vm.ClipTracks[1].Level = 0.3;
        Assert.True(session.Playing && session.Starts.Count == starts, "sound levels: still playing");
        Assert.Equal("0.3,0.3", string.Join(",", session.Mix!.Gains), "game and mic at their new levels");
        Assert.Near(1, session.Volume, 1e-9, "mixed at their own levels");

        vm.Apply(vm.Project.SplitAt(20).DeleteSegmentAt(25));
        Assert.True(session.Playing && session.Starts.Count == starts + 1, "a cut: plays on with the new parts");
    });

    /// <summary>Typing a text looks like the text: its own plain box (not a settings field with a clear button).</summary>
    [Test]
    public static void Typing_a_text_looks_like_the_text() => OnSta(() =>
    {
        var outlined = new TextItem(Guid.NewGuid(), 0, 5, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFD54A", 0.06, TextEffect.Outline, "lalezar");
        var editor = EditCanvas.TextEditor(outlined, 40);
        Assert.True(editor.OverridesDefaultStyle && editor.Template?.VisualTree != null, "its own plain look");
        Assert.Equal(System.Windows.Media.Colors.Transparent, ((System.Windows.Media.SolidColorBrush)editor.Background).Color, "no box behind an outlined text");
        Assert.Equal(TextFonts.For("lalezar").Family, editor.FontFamily, "in its font");
        var boxed = EditCanvas.TextEditor(outlined with { Effect = TextEffect.Box }, 40);
        Assert.True(((System.Windows.Media.SolidColorBrush)boxed.Background).Color.A > 0, "a boxed text keeps its box");
    });

    /// <summary>
    /// Space pressed in the middle of a drag plays at once; letting go then does not start it a second time (two players
    /// at once could free a colour filter still in use). A drag to the very end while playing stays there.
    /// </summary>
    [Test]
    public static void Space_during_a_drag_plays_once() => OnSta(() =>
    {
        var vm = Video();
        var session = new FakeSession();
        using var playback = new EditorPlayback(vm, _ => { }, _ => { }, session);
        vm.KeyCommand("Space");
        vm.BeginSeek();
        vm.PlayheadOutput = 12;
        vm.KeyCommand("Space");
        int starts = session.Starts.Count;
        Assert.True(session.Playing, "Space plays");
        vm.EndSeek();
        Assert.Equal(starts, session.Starts.Count, "letting go does not start it again");

        vm.BeginSeek();
        vm.PlayheadOutput = vm.Project.OutputDuration;
        vm.EndSeek();
        Assert.True(!session.Playing, "dragged to the end: it stays there, paused");
        Assert.Near(vm.Project.OutputDuration, vm.PlayheadOutput, 1e-9, "at the end");
    });

    /// <summary>
    /// After a slider was used, the arrow keys still move through the clip once the timeline is clicked (it takes the
    /// keyboard), and a slider that does get an arrow moves a small step, not a tenth of everything.
    /// </summary>
    [Test]
    public static void The_timeline_takes_the_keys_and_sliders_step_finely() => OnSta(() =>
    {
        Assert.True(new EditTimeline(Video()).Focusable, "the timeline can take the keyboard");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, "src", "Kairos")))
            repo = repo.Parent;
        Assert.True(repo != null, "the repository");
        string xaml = File.ReadAllText(Path.Combine(repo!.FullName, "src", "Kairos", "Ui", "Edit", "EditorView.xaml"));
        var sliders = System.Text.RegularExpressions.Regex.Matches(xaml, "<Slider[^>]*>");
        Assert.True(sliders.Count >= 6, "the editor's sliders");
        // Its own step, or the sound tracks' style, which sets one.
        var trackStyle = System.Text.RegularExpressions.Regex.Match(xaml, "<Style x:Key=\"TrackSlider\".*?</Style>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(trackStyle.Value.Contains("Property=\"SmallChange\"", StringComparison.Ordinal), "the sound tracks' sliders step finely");
        foreach (System.Text.RegularExpressions.Match slider in sliders)
            Assert.True(slider.Value.Contains("SmallChange=", StringComparison.Ordinal) || slider.Value.Contains("{StaticResource TrackSlider}", StringComparison.Ordinal),
                "a fine arrow step: " + slider.Value[..Math.Min(90, slider.Value.Length)]);
    });

    /// <summary>
    /// The timeline's pointer says what a click does: a line on the ruler and empty room (moves the playhead), a hand on a
    /// row's name or a part (the arrow: it chooses), a two-way arrow on an item's end (drag its time), four arrows on an item (drag it).
    /// </summary>
    [Test]
    public static void The_timeline_pointer_says_what_a_click_does()
    {
        var text = new TextItem(Guid.NewGuid(), 10, 20, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        var p = EditProject.For(@"C:\Clips\a.mp4", 30).With(text);
        var layout = new TimelineLayout(p, [TimelineRow.Video, TimelineRow.Text], TimelineLayout.HeadWidth + 900 + TimelineLayout.EndPad);
        double textY = layout.RowTop(1) + layout.RowHeightOf(1) / 2;
        Assert.Near(TimelineLayout.HeadWidth + 300, layout.XOf(10), 1e-9, "10 s of 30 on a 900 px lane");
        Assert.Equal(System.Windows.Input.Cursors.IBeam, EditTimeline.CursorAt(layout, new System.Windows.Point(300, 8)), "the ruler: a line");
        Assert.Equal(System.Windows.Input.Cursors.Arrow, EditTimeline.CursorAt(layout, new System.Windows.Point(300, layout.RowTop(0) + 20)), "a part");
        Assert.Equal(System.Windows.Input.Cursors.Arrow, EditTimeline.CursorAt(layout, new System.Windows.Point(40, textY)), "a row's name");
        Assert.Equal(System.Windows.Input.Cursors.IBeam, EditTimeline.CursorAt(layout, new System.Windows.Point(layout.XOf(4), textY)), "beside the bar");
        Assert.Equal(System.Windows.Input.Cursors.SizeWE, EditTimeline.CursorAt(layout, new System.Windows.Point(layout.XOf(10) + 1, textY)), "its start");
        Assert.Equal(System.Windows.Input.Cursors.SizeWE, EditTimeline.CursorAt(layout, new System.Windows.Point(layout.XOf(20) - 2, textY)), "its end");
        Assert.Equal(System.Windows.Input.Cursors.SizeAll, EditTimeline.CursorAt(layout, new System.Windows.Point(layout.XOf(15), textY)), "on the bar: drag it");
        Assert.Equal(1, layout.RowAt(textY), "the text row");
        Assert.Equal(-1, layout.RowAt(8), "the ruler");
    }

    /// <summary>Zoomed in, the clip is wider than the lane and scrolls sideways; x and time stay each other's inverse.</summary>
    [Test]
    public static void Zoom_and_scroll_map_time_to_x()
    {
        var p = EditProject.For("c.mp4", 30);
        var layout = new TimelineLayout(p, [TimelineRow.Video], TimelineLayout.HeadWidth + 900 + TimelineLayout.EndPad, zoom: 2, scroll: 450);
        Assert.Near(1800, layout.ContentWidth, 1e-9, "twice as wide");
        Assert.Near(900, layout.MaxScroll, 1e-9, "scrolls as far as the extra width");
        Assert.Near(TimelineLayout.HeadWidth + 450, layout.XOf(15), 1e-9, "the middle of the clip after scrolling half a lane");
        Assert.Near(15, layout.OutputAt(TimelineLayout.HeadWidth + 450), 1e-9, "and back");
        Assert.Near(450, TimelineLayout.ScrollToShow(layout, 15), 1e-9, "already on screen: no scroll");
        Assert.Near(1800 * 28 / 30.0 - 900 * 0.9, TimelineLayout.ScrollToShow(layout, 28), 1e-9, "off to the right: scrolled so it shows near the end");
    }

    /// <summary>A new text left empty goes away when something else is chosen, and that other thing stays chosen.</summary>
    [Test]
    public static void Choosing_another_item_after_an_empty_new_text_keeps_it_chosen() => OnSta(() =>
    {
        var vm = Video();
        var canvas = new EditCanvas(vm);
        var other = TextAt(0.6, 0.6, 0.2, 0.1);
        vm.Apply(vm.Project.With(other));
        vm.AddTextCommand.Execute(null);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background); // typing opens
        vm.Select(other);
        Assert.Equal(1, vm.Project.Items.Count, "the empty text is gone");
        Assert.Equal(other.Id, vm.SelectedItem?.Id, "the other text is chosen");
    });

    /// <summary>The inspector never scrolls: when its page does not fit, the optional lines (labels, hints) go, the last first.</summary>
    [Test]
    public static void The_inspector_leaves_out_optional_lines_instead_of_scrolling() => OnSta(() =>
    {
        System.Windows.Controls.Border Line(double height, bool optional)
        {
            var line = new System.Windows.Controls.Border { Height = height };
            FitPanel.SetOptional(line, optional);
            return line;
        }
        var panel = new FitPanel();
        var label = Line(20, optional: true);
        var tiles = Line(40, optional: false);
        var hint = Line(30, optional: true);
        var slider = Line(30, optional: false);
        foreach (var child in new[] { label, tiles, hint, slider })
            panel.Children.Add(child);
        panel.Measure(new System.Windows.Size(200, 1000));
        Assert.True(panel.LeftOut.Count == 0 && panel.DesiredSize.Height == 120, "room for all");
        panel.Measure(new System.Windows.Size(200, 95));
        Assert.True(panel.LeftOut.Count == 1 && panel.LeftOut.Contains(hint), "the hint goes first (the last optional line)");
        panel.Measure(new System.Windows.Size(200, 75));
        Assert.True(panel.LeftOut.Count == 2 && panel.DesiredSize.Height == 70, "then the label; the settings always stay");
    });

    /// <summary>A big block with a smaller stand-in (the look tiles): when room runs short the stand-in shows instead.</summary>
    [Test]
    public static void A_block_gives_way_to_its_smaller_stand_in() => OnSta(() =>
    {
        System.Windows.Controls.Border Line(double height) => new() { Height = height };
        var panel = new FitPanel();
        var speed = Line(100);
        var bigLooks = Line(100);
        var smallLooks = Line(40);
        var sound = Line(50);
        FitPanel.SetOptional(bigLooks, true);
        FitPanel.SetStandIn(smallLooks, true);
        foreach (var child in new[] { speed, bigLooks, smallLooks, sound })
            panel.Children.Add(child);
        panel.Measure(new System.Windows.Size(200, 1000));
        Assert.True(panel.LeftOut.Count == 1 && panel.LeftOut.Contains(smallLooks) && panel.DesiredSize.Height == 250, "room: the big tiles, not the stand-in");
        panel.Measure(new System.Windows.Size(200, 200));
        Assert.True(panel.LeftOut.Count == 1 && panel.LeftOut.Contains(bigLooks) && panel.DesiredSize.Height == 190, "short: the small tiles instead");
    });

    /// <summary>
    /// What goes first when room runs short: the lowest priority (a hint, a label), then the next (the sound rows give way
    /// to two columns), and among equals the last first.
    /// </summary>
    [Test]
    public static void Lines_are_left_out_in_their_priority() => OnSta(() =>
    {
        System.Windows.Controls.Border Line(double height, bool optional = false, int priority = 0)
        {
            var line = new System.Windows.Controls.Border { Height = height };
            FitPanel.SetOptional(line, optional);
            FitPanel.SetPriority(line, priority);
            return line;
        }
        var panel = new FitPanel();
        var speedTiles = Line(20, optional: true, priority: 2);
        var slider = Line(40);
        var sound = Line(60, optional: true, priority: 1);
        var twoColumns = Line(30);
        FitPanel.SetStandIn(twoColumns, true);
        var hint = Line(20, optional: true);
        foreach (var child in new[] { speedTiles, slider, sound, twoColumns, hint })
            panel.Children.Add(child);
        panel.Measure(new System.Windows.Size(200, 120));
        Assert.True(panel.LeftOut.Count == 2 && panel.LeftOut.Contains(hint), "the hint first");
        panel.Measure(new System.Windows.Size(200, 100));
        Assert.True(panel.LeftOut.Contains(sound) && !panel.LeftOut.Contains(twoColumns) && !panel.LeftOut.Contains(speedTiles),
            "then the sound rows give way to two columns, before the speed tiles go");
        panel.Measure(new System.Windows.Size(200, 75));
        Assert.True(panel.LeftOut.Contains(speedTiles) && panel.DesiredSize.Height == 70, "the speed tiles last");
    });

    /// <summary>Items at the same time in one row share it in thinner layers: each bar shows and can be pressed.</summary>
    [Test]
    public static void Overlapping_bars_share_their_row_in_layers()
    {
        TextItem Text(double start, double end) =>
            new(Guid.NewGuid(), start, end, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        var whole = Text(0, 30);
        var early = Text(2, 8);
        var late = Text(20, 28);
        var p = EditProject.For("c.mp4", 30).With(whole).With(early).With(late);
        var layout = new TimelineLayout(p, [TimelineRow.Video, TimelineRow.Text], TimelineLayout.HeadWidth + 900 + TimelineLayout.EndPad);
        var bars = layout.Bars(1).ToDictionary(b => b.Bar.Item.Id, b => b.Box);
        Assert.True(bars[whole.Id].Bottom <= bars[early.Id].Top + 1e-9 || bars[early.Id].Bottom <= bars[whole.Id].Top + 1e-9, "the two at 2..8 do not cover each other");
        Assert.Near(bars[early.Id].Top, bars[late.Id].Top, 1e-9, "early and late do not meet: the same layer");
        Assert.True(bars.Values.All(b => b.Top >= layout.RowTop(1) && b.Bottom <= layout.RowTop(1) + layout.RowHeightOf(1)), "all inside the row");
        double y = (bars[early.Id].Top + bars[early.Id].Bottom) / 2;
        Assert.Equal(early.Id, layout.BarAt(new System.Windows.Point(layout.XOf(5), y))?.Bar.Item.Id, "the early one can be pressed");
        double y2 = (bars[whole.Id].Top + bars[whole.Id].Bottom) / 2;
        Assert.Equal(whole.Id, layout.BarAt(new System.Windows.Point(layout.XOf(5), y2))?.Bar.Item.Id, "and the long one beside it");
    }

    /// <summary>
    /// A row grows with its layers, each bar comfortably tall, until the timeline would pass its room; then the bars get
    /// thinner (never below a few pixels) so nothing scrolls.
    /// </summary>
    [Test]
    public static void Rows_grow_with_their_layers_within_the_room()
    {
        TextItem Text(double start, double end) =>
            new(Guid.NewGuid(), start, end, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        var p = EditProject.For("c.mp4", 30).With(Text(0, 10)).With(Text(2, 12)).With(Text(4, 14));
        TimelineRow[] rows = [TimelineRow.Video, TimelineRow.Text];
        var roomy = new TimelineLayout(p, rows, 1000);
        Assert.Near(2 * TimelineLayout.RowPad + 3 * TimelineLayout.BarHeight + 2 * TimelineLayout.LayerGap, roomy.RowHeightOf(1), 1e-9,
            "three layers, each a full bar");
        Assert.Near(TimelineLayout.BarHeight, roomy.Bar, 1e-9, "comfortable");
        var tight = new TimelineLayout(p, rows, 1000, maxHeight: 110);
        Assert.True(tight.Height <= 110 + 1e-9, $"fits its room: {tight.Height}");
        Assert.True(tight.Bar >= TimelineLayout.MinBarHeight && tight.Bar < TimelineLayout.BarHeight, $"thinner bars: {tight.Bar}");
    }

    /// <summary>A bar shows its item's own colour (a text made green shows green) and an icon of its kind (blur is striped).</summary>
    [Test]
    public static void Bars_show_their_items_colour_and_kind()
    {
        var green = new TextItem(Guid.NewGuid(), 0, 5, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#3DDC84", 0.06, TextEffect.Box);
        var caption = green with { Id = Guid.NewGuid(), Group = Guid.NewGuid() };
        var pink = new StrokeItem(Guid.NewGuid(), 0, 5, [[new System.Windows.Point(0, 0), new System.Windows.Point(0.1, 0.1)]], "#F48BA4", 0.01);
        var arrow = new ShapeItem(Guid.NewGuid(), 0, 5, ShapeKind.Arrow, new System.Windows.Point(0, 0), new System.Windows.Point(0.1, 0.1), "#4FC3F7", 0.01);
        var blur = new BlurItem(Guid.NewGuid(), 0, 5, new System.Windows.Rect(0, 0, 0.2, 0.2));
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x3D, 0xDC, 0x84), EditTimeline.BarColor(green), "the text's green");
        Assert.Equal(System.Windows.Media.Color.FromRgb(0xF4, 0x8B, 0xA4), EditTimeline.BarColor(pink), "the line's pink");
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x4F, 0xC3, 0xF7), EditTimeline.BarColor(arrow), "the arrow's blue");
        var glyphs = new EditItem[] { green, caption, pink, arrow, blur }.Select(EditTimeline.BarGlyph).ToList();
        Assert.Equal(5, glyphs.Distinct().Count(), "a text, a caption, a line, an arrow and a blur each have their own icon");
        Assert.True(EditTimeline.IsStriped(blur) && !EditTimeline.IsStriped(arrow), "blur is striped, a shape is not");
    }

    static TextItem TextBetween(double start, double end, Guid? group = null) =>
        new(Guid.NewGuid(), start, end, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box, "segoe", group);

    static EditorViewModel WithTexts(out List<TextItem> texts, int count = 5)
    {
        var vm = Video();
        texts = Enumerable.Range(0, count).Select(i => TextBetween(i * 3, i * 3 + 2)).ToList();
        var p = vm.Project;
        foreach (var t in texts)
            p = p.With(t);
        vm.Apply(p);
        return vm;
    }

    static string Chosen(EditorViewModel vm, List<TextItem> texts) =>
        string.Join(",", texts.Select((t, i) => vm.IsChosen(t) ? i.ToString() : null).OfType<string>());

    /// <summary>Shift chooses all in between (in one row), Ctrl adds or takes out one, Ctrl on a row's name adds the row.</summary>
    [Test]
    public static void Shift_chooses_all_in_between_and_ctrl_adds_one()
    {
        var vm = WithTexts(out var texts);
        vm.Select(texts[0]);
        vm.SelectRange(texts[3]);
        Assert.Equal("0,1,2,3", Chosen(vm, texts), "the first to the fourth");
        vm.Select(texts[4], add: true);
        vm.Select(texts[1], add: true);
        Assert.Equal("0,2,3,4", Chosen(vm, texts), "Ctrl: one in, one out");
        var line = new StrokeItem(Guid.NewGuid(), 0, 5, [[new System.Windows.Point(0, 0), new System.Windows.Point(0.1, 0.1)]], "#F48BA4", 0.01);
        vm.Apply(vm.Project.With(line));
        vm.SelectRow(TimelineRow.Text);
        vm.SelectRow(TimelineRow.Draw, add: true);
        Assert.True(vm.SelectedItems.Count == 6, "Ctrl on a row's name adds that row");
        vm.SelectPart(0);
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        vm.PlayheadOutput = 20;
        vm.KeyCommand("S");
        vm.SelectPart(0);
        vm.SelectPartRange(2);
        Assert.Equal("0,1,2", string.Join(",", vm.SelectedParts.OrderBy(i => i)), "parts too");
    }

    [Test]
    public static void Ctrl_A_chooses_everything()
    {
        var vm = Video();
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        vm.KeyCommand("Ctrl+A");
        Assert.Equal(2, vm.SelectedParts.Count, "no items: every part");
        var both = WithTexts(out var texts, 3);
        both.KeyCommand("Ctrl+A");
        Assert.Equal("0,1,2", Chosen(both, texts), "every item");
    }

    /// <summary>A box dragged over the timeline chooses the bars it touches.</summary>
    [Test]
    public static void A_box_chooses_the_bars_it_touches()
    {
        var vm = WithTexts(out var texts);
        var layout = new TimelineLayout(vm.Project, vm.Rows, TimelineLayout.HeadWidth + 900 + TimelineLayout.EndPad);
        double y = layout.RowTop(1) + layout.RowHeightOf(1) / 2;
        var box = new System.Windows.Rect(new System.Windows.Point(layout.XOf(4), y - 2), new System.Windows.Point(layout.XOf(10), y + 2));
        var hit = layout.ItemsIn(box).Select(i => texts.FindIndex(t => t.Id == i.Id)).OrderBy(i => i);
        Assert.Equal("1,2,3", string.Join(",", hit), "3..5, 6..8 and 9..11 cross 4..10");
        vm.SetChoice(layout.ItemsIn(box));
        Assert.Equal("1,2,3", Chosen(vm, texts), "chosen");
    }

    /// <summary>Q and W bring the chosen items' starts or ends to the playhead; with nothing chosen they cut the part there.</summary>
    [Test]
    public static void Q_and_W_trim_to_the_playhead()
    {
        var vm = Video();
        var a = TextBetween(2, 8);
        var b = TextBetween(4, 10);
        vm.Apply(vm.Project.With(a).With(b));
        vm.Select(a);
        vm.Select(b, add: true);
        vm.PlayheadOutput = 5;
        vm.KeyCommand("Q");
        Assert.True(vm.Project.Items.All(i => Math.Abs(i.Start - 5) < 1e-9), "both start at the playhead");
        vm.PlayheadOutput = 7;
        vm.KeyCommand("W");
        Assert.True(vm.Project.Items.All(i => Math.Abs(i.End - 7) < 1e-9), "both end there");
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Project.Items.Any(i => Math.Abs(i.End - 10) < 1e-9), "one undo per key");
        vm.ClearChoice();
        vm.PlayheadOutput = 12;
        vm.KeyCommand("W");
        Assert.Near(12, vm.Project.OutputDuration, 1e-9, "nothing chosen: W cuts the clip after the playhead");
    }

    [Test]
    public static void Alt_arrows_move_the_chosen_in_time()
    {
        var vm = WithTexts(out var texts, 2);
        vm.Select(texts[1]);
        vm.KeyCommand("Alt+Right");
        vm.KeyCommand("Alt+Shift+Right");
        Assert.Near(3 + 1.1, vm.Project.Items.Single(i => i.Id == texts[1].Id).Start, 1e-6, "a tenth, then a second later");
        vm.KeyCommand("Alt+Left");
        Assert.Near(3 + 1.0, vm.Project.Items.Single(i => i.Id == texts[1].Id).Start, 1e-6, "and back a tenth");
    }

    /// <summary>Ctrl+C / Ctrl+V paste at the playhead; Ctrl+D puts copies right after; the copies are chosen.</summary>
    [Test]
    public static void Copy_paste_and_duplicate()
    {
        var vm = Video();
        var caption = TextBetween(2, 3, Guid.NewGuid());
        var title = TextBetween(4, 6);
        vm.Apply(vm.Project.With(caption).With(title));
        vm.Select(caption);
        vm.Select(title, add: true);
        vm.KeyCommand("Ctrl+C");
        vm.PlayheadOutput = 10;
        vm.KeyCommand("Ctrl+V");
        var pasted = vm.SelectedItems.OrderBy(i => i.Start).ToList();
        Assert.True(pasted.Count == 2 && vm.Project.Items.Count == 4, "two copies, chosen");
        Assert.True(Math.Abs(pasted[0].Start - 10) < 1e-9 && Math.Abs(pasted[1].Start - 12) < 1e-9, "at the playhead, as far apart as before");
        Assert.True(pasted.All(i => i is TextItem { Group: null }), "a pasted caption is a text of its own");
        vm.KeyCommand("Ctrl+D");
        var copies = vm.SelectedItems.OrderBy(i => i.Start).ToList();
        Assert.True(vm.Project.Items.Count == 6 && Math.Abs(copies[0].Start - 14) < 1e-9, "right after the chosen ones");
    }

    [Test]
    public static void Enter_types_in_the_chosen_text()
    {
        var vm = WithTexts(out var texts, 2);
        TextItem? typing = null;
        vm.TypeRequested += t => typing = t;
        vm.Select(texts[1]);
        vm.PlayheadOutput = 0.5;
        Assert.True(vm.KeyCommand("Enter"), "used");
        Assert.True(typing?.Id == texts[1].Id, "opened for typing");
        Assert.Near(3, vm.PlayheadOutput, 1e-9, "and on screen");
    }

    /// <summary>Ctrl+B splits like S; up and down jump between cuts and items' ends; Home and End to the ends.</summary>
    [Test]
    public static void Ctrl_B_splits_and_up_down_jump_between_edits()
    {
        var vm = Video();
        vm.Apply(vm.Project.With(TextBetween(4, 8)));
        vm.PlayheadOutput = 10;
        vm.KeyCommand("Ctrl+B");
        Assert.Equal(2, vm.Project.Kept.Count, "split");
        vm.PlayheadOutput = 5;
        vm.KeyCommand("Down");
        Assert.Near(8, vm.PlayheadOutput, 1e-9, "the text's end");
        vm.KeyCommand("Down");
        Assert.Near(10, vm.PlayheadOutput, 1e-9, "the cut");
        vm.KeyCommand("Up");
        vm.KeyCommand("Up");
        Assert.Near(4, vm.PlayheadOutput, 1e-9, "back to the text's start");
        vm.KeyCommand("End");
        Assert.Near(30, vm.PlayheadOutput, 1e-9, "the end");
        vm.KeyCommand("Home");
        Assert.Near(0, vm.PlayheadOutput, 1e-9, "the start");
    }

    [Test]
    public static void Zoom_keys_and_the_shortcuts_sheet()
    {
        var vm = Video();
        vm.KeyCommand("Ctrl+Plus");
        Assert.Near(1.25, vm.TimelineZoom, 1e-9, "closer");
        vm.KeyCommand("Ctrl+Plus");
        vm.KeyCommand("Ctrl+Minus");
        Assert.Near(1.25, vm.TimelineZoom, 1e-9, "and back");
        vm.KeyCommand("Backslash");
        Assert.Near(1, vm.TimelineZoom, 1e-9, "the whole clip");
        vm.KeyCommand("?");
        Assert.True(vm.ShowingShortcuts, "the shortcuts sheet");
        vm.KeyCommand("Escape");
        Assert.True(!vm.ShowingShortcuts && !vm.AskingDiscard, "Esc closes it first");
    }

    /// <summary>Chosen parts take a look of their own in the inspector; the whole clip's look then sets every part again.</summary>
    [Test]
    public static void Chosen_parts_get_their_own_look()
    {
        var vm = Video();
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        vm.SelectPart(1);
        vm.PartLooks.Single(l => l.Look == ColourLook.Mono).IsSelected = true;
        Assert.True(vm.Project.Kept[1].Look == ColourLook.Mono && vm.Project.Kept[0].Look == null, "that part alone");
        Assert.True(vm.PartLooks.Single(l => l.Look == ColourLook.Mono).IsSelected, "shown chosen");
        vm.PartLookStrength = 0.5;
        Assert.Near(0.5, vm.Project.Kept[1].LookStrength, 1e-9, "its strength");
        vm.ClearChoice();
        vm.Looks.Single(l => l.Look == ColourLook.Warm).IsSelected = true;
        Assert.True(vm.Project.Look == ColourLook.Warm && vm.Project.Kept.All(k => k.Look == null), "the whole clip: every part");
    }

    /// <summary>Over the picture: four arrows on an item (drag it), slanted arrows on the chosen one's corners, each tool its own.</summary>
    [Test]
    public static void The_picture_pointer_says_what_a_drag_does()
    {
        var vm = Video();
        var text = TextAt(0.1, 0.1, 0.3, 0.2);
        vm.Apply(vm.Project.With(text));
        var picture = new System.Windows.Rect(0, 0, 1000, 500);
        System.Windows.Point At(double x, double y) => new(x * 1000, y * 500);
        Assert.Equal(System.Windows.Input.Cursors.SizeAll, EditCanvas.HoverAt(vm, picture, At(0.2, 0.2)), "on a text: move it");
        Assert.Equal(System.Windows.Input.Cursors.Arrow, EditCanvas.HoverAt(vm, picture, At(0.8, 0.8)), "elsewhere: the arrow");
        vm.Select(text);
        Assert.Equal(System.Windows.Input.Cursors.SizeNWSE, EditCanvas.HoverAt(vm, picture, At(0.4, 0.3)), "its bottom right corner");
        Assert.Equal(System.Windows.Input.Cursors.SizeNESW, EditCanvas.HoverAt(vm, picture, At(0.4, 0.1)), "its top right corner");
        vm.DrawCommand.Execute(null);
        Assert.True(EditCanvas.HoverAt(vm, picture, At(0.2, 0.2)) == ThemeCursors.Get(ThemeCursors.Shape.Pen), "drawing: the pen");
        vm.BlurCommand.Execute(null);
        Assert.True(EditCanvas.HoverAt(vm, picture, At(0.2, 0.2)) == ThemeCursors.Get(ThemeCursors.Shape.BlurTool), "blur: its own");
    }

    /// <summary>Every shortcut on the sheet does something, and every line of it reads in both languages.</summary>
    [Test]
    public static void Every_shortcut_on_the_sheet_works_and_reads_in_both_languages()
    {
        var lines = EditorShortcuts.Left.Concat(EditorShortcuts.Right).SelectMany(g => g.Lines).ToList();
        foreach (var line in lines.Where(l => l.Command != null))
        {
            var vm = WithTexts(out var texts, 3);
            vm.PlayheadOutput = 4;
            vm.Select(texts[1]); // a text chosen, the playhead on it
            Assert.True(vm.KeyCommand(line.Command!), "does nothing: " + line.Command);
        }
        foreach (string language in new[] { "en", "ar" })
        {
            Text.Language = language;
            var keys = EditorShortcuts.Left.Concat(EditorShortcuts.Right).Select(g => g.Title)
                .Concat(lines.Select(l => l.Label)).Concat(lines.SelectMany(l => l.Keys).Where(k => k.StartsWith("t:", StringComparison.Ordinal)).Select(k => k[2..]));
            foreach (string key in keys)
                Assert.True(Text.Get(key) != key, $"no {language} words for {key}");
        }
        Text.Language = "en";
    }

    /// <summary>Dragging the end of one chosen bar moves the same end of every chosen bar by as much.</summary>
    [Test]
    public static void Dragging_one_end_changes_every_chosen_bar()
    {
        var a = TextBetween(2, 6);
        var b = TextBetween(10, 12);
        var p = EditProject.For("c.mp4", 30).With(a).With(b);
        var moved = TimelineMath.WithBarEdges(p, [a, b], a, start: false, output: 7);
        Assert.True(Math.Abs(moved.Single(i => i.Id == a.Id).End - 7) < 1e-9 && Math.Abs(moved.Single(i => i.Id == b.Id).End - 13) < 1e-9,
            "both a second longer");
    }

    /// <summary>Dragging a bar moves its item in time: the same length, never out of the clip.</summary>
    [Test]
    public static void Moving_a_bar_keeps_its_length_inside_the_clip()
    {
        var item = new TextItem(Guid.NewGuid(), 10, 14, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        var p = EditProject.For("c.mp4", 30).With(item);
        var later = TimelineMath.MoveBar(p, item, 5);
        Assert.True(Math.Abs(later.Start - 15) < 1e-9 && Math.Abs(later.End - 19) < 1e-9, $"5 s later: {later.Start}..{later.End}");
        var end = TimelineMath.MoveBar(p, item, 100);
        Assert.True(Math.Abs(end.End - 30) < 1e-9 && Math.Abs(end.Start - 26) < 1e-9, $"stops at the end: {end.Start}..{end.End}");
        var start = TimelineMath.MoveBar(p, item, -100);
        Assert.True(Math.Abs(start.Start) < 1e-9 && Math.Abs(start.End - 4) < 1e-9, $"and at the start: {start.Start}..{start.End}");
    }

    /// <summary>The text tool's font and style show the chosen text's own, change it, and are used for new texts.</summary>
    [Test]
    public static void Text_fonts_and_styles_apply_to_the_chosen_text()
    {
        Kairos.App.Text.Language = "en";
        var vm = Video();
        Assert.True(vm.Fonts.Count == TextFonts.All.Count && vm.Fonts.Count(f => f.IsSelected) == 1, "every font, one chosen");
        var text = new TextItem(Guid.NewGuid(), 1, 3, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box, "segoe");
        vm.Apply(vm.Project.With(text));
        vm.SelectedItem = text;
        Assert.True(vm.TextFont == "segoe" && vm.TextEffect == TextEffect.Box, "shows the chosen text's own");
        vm.Fonts.Single(f => f.Key == "ruqaa").IsSelected = true;
        vm.TextEffect = TextEffect.Outline;
        var now = vm.Project.Items.OfType<TextItem>().Single();
        Assert.True(now.Font == "ruqaa" && now.Effect == TextEffect.Outline, $"restyled: {now.Font} {now.Effect}");
        Assert.Equal("ruqaa", vm.Fonts.Single(f => f.IsSelected).Key, "the tile follows");
    }

    [Test]
    public static void Undo_is_disabled_until_a_change()
    {
        var vm = Video();
        Assert.True(!vm.UndoCommand.CanExecute(null) && !vm.PrimaryCommand.CanExecute(null), "nothing to undo or save yet");
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        Assert.True(vm.UndoCommand.CanExecute(null) && vm.PrimaryCommand.CanExecute(null), "a change");
        vm.UndoCommand.Execute(null);
        Assert.Equal(1, vm.Project.Kept.Count, "undone");
        Assert.True(vm.RedoCommand.CanExecute(null), "and can be redone");
    }

    [Test]
    public static void Choosing_a_look_sets_it_with_its_strength()
    {
        var vm = Video();
        vm.LookCommand.Execute(ColourLook.Warm);
        vm.LookStrength = 0.4;
        vm.LookStrength = 0.6; // a slider drag: one step to undo
        Assert.Equal(ColourLook.Warm, vm.Project.Look, "look");
        Assert.Near(0.6, vm.Project.LookStrength, 1e-9, "strength");
        vm.UndoCommand.Execute(null);
        Assert.Equal(ColourLook.Warm, vm.Project.Look, "undo takes back the strength drag, not the look");
        Assert.Near(1, vm.Project.LookStrength, 1e-9, "full strength again");
    }

    [Test]
    public static void Muting_the_mic_sets_the_sound_levels()
    {
        Text.Language = "en";
        var vm = Video();
        Assert.True(vm.HasSeparateTracks, "game and mic for a Kairos clip");
        Assert.Equal("Game,Microphone", string.Join(",", vm.ClipTracks.Select(t => t.Name)), "a 1.0.1 clip: game and mic, as before");
        vm.ClipTracks[1].On = false;
        vm.ClipTracks[0].Level = 0.5;
        Assert.True(vm.Project.Sound.Tracks.Muted(1), "mic muted");
        Assert.Near(0.5, vm.Project.Sound.Tracks[0], 1e-9, "game level");
        Assert.Equal("0%", vm.ClipTracks[1].Label, "a muted track reads 0%");
        var one = Video(audioStreams: 1);
        Assert.True(!one.HasSeparateTracks && one.ClipTracks.Count == 0, "one track: one All slider");
        Assert.Equal("Sound", one.PartTracks.Single().Name, "and a part's whole sound");
    }

    /// <summary>A separate-sources clip: each of its own tracks by name, for the clip (level and switch) and for a part.</summary>
    [Test]
    public static void A_clip_with_its_own_tracks_lists_them_by_name()
    {
        Text.Language = "en";
        var layout = TrackLayout.Of(["Mix", "Game", "Discord", "Microphone", "Spotify"], 5);
        var vm = new EditorViewModel(EditProject.For(@"C:\Clips\a.mp4", 30), isPicture: false, EditorMode.Library, 16 / 9.0, 5, layout: layout);
        Assert.Equal("Game,Discord,Microphone,Spotify", string.Join(",", vm.ClipTracks.Select(t => t.Name)), "the clip's tracks");
        Assert.Equal("Game,Discord,Microphone,Spotify", string.Join(",", vm.PartTracks.Select(t => t.Name)), "the same for a part");
        vm.ClipTracks[1].On = false;
        Assert.True(vm.Project.Sound.Tracks.Muted(1), "Discord muted");
        vm.SelectPart(0);
        vm.PartTracks[3].Level = 0.5;
        Assert.Equal(0.5, vm.Project.Kept[0].Levels[3], "Spotify quieter in the part");
        Assert.Equal("50%", vm.PartTracks[3].Label, "shown as a percent");
    }

    [Test]
    public static void A_picture_can_be_cropped_and_a_clip_has_a_timeline()
    {
        Text.Language = "en";
        Assert.True(Picture().CanCrop && !Picture().HasTimeline, "a picture: crop, no timeline");
        Assert.True(!Video().CanCrop && Video().HasTimeline, "a clip: a timeline, no crop");
        var clip = Video();
        clip.CropCommand.Execute(null);
        Assert.Equal(EditMode.None, clip.EditMode, "no crop mode for a clip");
    }

    /// <summary>Each pen line is its own item: the brush is for the next line, and a line chosen later changes alone.</summary>
    [Test]
    public static void Each_line_is_its_own_item_with_its_own_colour()
    {
        var vm = Video();
        vm.DrawCommand.Execute(null);
        Assert.True(vm.IsDrawMode && vm.IsLinePage, "drawing: the brush shows");
        vm.PlayheadOutput = 2;
        vm.PenColor = "#FF5A5A";
        var red = vm.AddStroke([new System.Windows.Point(0.1, 0.1), new System.Windows.Point(0.2, 0.2)]);
        vm.PenColor = "#4FC3F7";
        var blue = vm.AddStroke([new System.Windows.Point(0.3, 0.1), new System.Windows.Point(0.4, 0.2)]);
        StrokeItem Now(StrokeItem s) => vm.Project.Items.OfType<StrokeItem>().Single(i => i.Id == s.Id);
        Assert.Equal(2, vm.Project.Items.OfType<StrokeItem>().Count(), "two lines");
        Assert.True(Now(red).ColorHex == "#FF5A5A" && Now(blue).ColorHex == "#4FC3F7", "the brush changed only the next line");
        vm.ModeDoneCommand.Execute(null);
        vm.Select(Now(red));
        Assert.Equal("#FF5A5A", vm.PenColor, "the chosen line's own colour shows");
        vm.PenColor = "#3DDC84";
        Assert.True(Now(red).ColorHex == "#3DDC84" && Now(blue).ColorHex == "#4FC3F7", "the chosen line alone changes");
        vm.UndoCommand.Execute(null);
        Assert.Equal("#FF5A5A", Now(red).ColorHex, "undo takes the colour back");
    }

    /// <summary>The inspector shows what is chosen: the whole clip, a part, texts, lines, several kinds, or a mode's brush.</summary>
    [Test]
    public static void The_inspector_shows_what_is_chosen()
    {
        Text.Language = "en";
        var vm = Video();
        Assert.True(vm.IsClipPage && vm.PageTitle == "Whole clip", "nothing chosen: the whole clip");
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        Assert.True(vm.IsPartPage && vm.SelectedParts.SequenceEqual([1]), "after a split the part from the playhead is chosen");
        Assert.Equal("Part 2", vm.PageTitle, "named");
        Assert.Equal("0:10 – 0:30", vm.PageNote, "with its time");
        var text = TextAt(0.1, 0.1, 0.2, 0.1);
        var shape = new ShapeItem(Guid.NewGuid(), 0, 30, ShapeKind.Arrow, new System.Windows.Point(0.5, 0.5), new System.Windows.Point(0.6, 0.6), "#FFFFFF", 0.01);
        vm.Apply(vm.Project.With(text).With(shape));
        vm.Select(text);
        Assert.True(vm.IsTextPage && vm.HasWords && vm.Words == "GG", "a text: its words and style");
        vm.Select(shape, add: true);
        Assert.True(vm.IsMixedPage && vm.PageNote == "2 selected", "two kinds: one colour for both");
        vm.MixedColor = "#FFD54A";
        Assert.True(vm.Project.Items.All(i => i is TextItem { ColorHex: "#FFD54A" } or ShapeItem { ColorHex: "#FFD54A" }), "both took it");
        vm.ShapesCommand.Execute(null);
        Assert.True(vm.IsShapePage && vm.SelectedItems.Count == 0, "a mode starts with nothing chosen");
        vm.KeyCommand("Escape");
        Assert.True(vm.IsClipPage, "Esc leaves the mode");
    }

    /// <summary>A chosen part plays at its own speed with its own sound; the slider runs 0.25x..4x with 1x in the middle.</summary>
    [Test]
    public static void A_chosen_part_has_its_own_speed_and_sound()
    {
        var vm = Video();
        vm.PlayheadOutput = 10;
        vm.KeyCommand("S");
        vm.SelectPart(0);
        vm.Speeds.Single(s => s.Value == 2).IsSelected = true;
        Assert.Near(25, vm.Project.OutputDuration, 1e-9, "the first 10 s now take 5");
        Assert.True(vm.Speeds.Single(s => s.Value == 2).IsSelected, "the 2x button shows it");
        vm.PartTracks[0].Level = 1.5;
        vm.PartTracks[0].Level = 1.4; // a slider drag: one step
        Assert.True(vm.Project.Kept[0].Levels[0] == 1.4 && vm.Project.Kept[1].Levels[0] == 1, "only the chosen part is louder");
        Assert.Equal("140%", vm.PartTracks[0].Label, "shown as a percent");
        vm.UndoCommand.Execute(null);
        Assert.Equal(1.0, vm.Project.Kept[0].Levels[0], "one undo for the drag");
        vm.PartSpeedSlider = 0.5;
        Assert.Near(1, vm.PartSpeed, 1e-9, "the middle is normal speed");
        vm.PartSpeedSlider = 1;
        Assert.Near(4, vm.PartSpeed, 1e-9, "the end is 4x");
        vm.SelectRow(TimelineRow.Video);
        vm.Speeds.Single(s => s.Value == 0.5).IsSelected = true;
        Assert.True(vm.Project.Kept.All(k => k.Speed == 0.5), "the Video row's name chooses every part");
    }

    [Test]
    public static void Delete_removes_the_chosen_parts_or_items_and_keeps_one_part()
    {
        var vm = Video();
        vm.PlayheadOutput = 12;
        vm.KeyCommand("S");
        Assert.Equal("0..12, 12..30", string.Join(", ", vm.Project.Kept.Select(k => $"{k.Start:0.##}..{k.End:0.##}")), "split");
        vm.SelectPart(0);
        vm.KeyCommand("Delete");
        Assert.Equal("12..30", string.Join(", ", vm.Project.Kept.Select(k => $"{k.Start:0.##}..{k.End:0.##}")), "the chosen part is gone");
        vm.SelectPart(0);
        vm.KeyCommand("Delete");
        Assert.Equal(1, vm.Project.Kept.Count, "the last part stays");
        var a = TextAt(0.1, 0.1, 0.2, 0.1);
        var b = TextAt(0.5, 0.5, 0.2, 0.1);
        vm.Apply(vm.Project.With(a).With(b));
        vm.Select(a);
        vm.Select(b, add: true);
        vm.Select(a, add: true); // Shift-click again: out of the choice
        Assert.True(vm.SelectedItems.Count == 1 && vm.SelectedItem?.Id == b.Id, "b alone");
        vm.KeyCommand("Delete");
        Assert.True(vm.Project.Items.Count == 1 && vm.Project.Items[0].Id == a.Id, "only the chosen one is deleted");
        Assert.True(vm.IsClipPage, "nothing chosen after");
    }

    /// <summary>Text adds a text in the middle at the playhead, chosen and open for typing (left empty, it goes with one undo).</summary>
    [Test]
    public static void Add_text_puts_a_text_in_the_middle_ready_to_type()
    {
        Text.Language = "en";
        var vm = Video();
        vm.PlayheadOutput = 4;
        TextItem? typing = null;
        vm.TypeRequested += t => typing = t;
        vm.AddTextCommand.Execute(null);
        var text = vm.Project.Items.OfType<TextItem>().Single();
        Assert.True(typing?.Id == text.Id && vm.SelectedItem?.Id == text.Id, "chosen and open for typing");
        Assert.Near(0.5, text.Box.X + text.Box.Width / 2, 1e-9, "in the middle across");
        Assert.Near(0.5, text.Box.Y + text.Box.Height / 2, 1e-9, "and up and down");
        Assert.Near(4, text.Start, 1e-9, "at the playhead");
        vm.Words = "GG WP";
        Assert.Equal("GG WP", vm.Project.Items.OfType<TextItem>().Single().Text, "words typed in the inspector");
        vm.UndoCommand.Execute(null);
        Assert.True(vm.Project.IsUnchanged, "adding and typing are one step");
    }

    [Test]
    public static void Place_moves_every_chosen_text()
    {
        var vm = Video();
        var a = TextAt(0.1, 0.1, 0.2, 0.1);
        var b = TextAt(0.6, 0.4, 0.3, 0.1);
        vm.Apply(vm.Project.With(a).With(b));
        vm.SelectRow(TimelineRow.Text);
        vm.PlaceCommand.Execute("bottom");
        var texts = vm.Project.Items.OfType<TextItem>().ToList();
        Assert.True(texts.All(t => Math.Abs(t.Box.Bottom - (1 - Kairos.Engine.Captions.Captions.BottomMargin)) < 1e-9), "standing on the bottom line");
        Assert.True(texts.All(t => Math.Abs(t.Box.X + t.Box.Width / 2 - 0.5) < 1e-9), "centred");
        Assert.Equal("bottom", vm.TextPlace, "shown");
        vm.PlaceCommand.Execute("top");
        Assert.Equal("top", vm.TextPlace, "and up again");
    }

    /// <summary>Esc leaves a mode first, then the choice, then the editor (asked when there are changes).</summary>
    [Test]
    public static void Escape_steps_back_one_thing_at_a_time()
    {
        var vm = Video();
        bool closed = false;
        vm.Closed += () => closed = true;
        var text = TextAt(0.1, 0.1, 0.2, 0.1);
        vm.Apply(vm.Project.With(text));
        vm.Select(text);
        vm.KeyCommand("Escape");
        Assert.True(vm.SelectedItems.Count == 0 && !closed && !vm.AskingDiscard, "first nothing chosen");
        vm.DrawCommand.Execute(null);
        vm.KeyCommand("Escape");
        Assert.True(vm.EditMode == EditMode.None && !vm.AskingDiscard, "out of the mode");
        vm.KeyCommand("Escape");
        Assert.True(vm.AskingDiscard, "then leaving asks");
    }

    /// <summary>A shape (or blur box) dragged on the picture ends its mode and is chosen, its settings showing.</summary>
    [Test]
    public static void A_shape_made_is_chosen_and_its_mode_ends()
    {
        var vm = Video();
        vm.ShapesCommand.Execute(null);
        vm.ShapeColor = "#3DDC84"; // the next shape's colour
        var made = new ShapeItem(Guid.NewGuid(), 0, 5, vm.ShapeKind, new System.Windows.Point(0.1, 0.1), new System.Windows.Point(0.3, 0.3), vm.ShapeColor, vm.ShapeThickness);
        vm.AddMade(made);
        Assert.True(vm.EditMode == EditMode.None && vm.SelectedItem?.Id == made.Id && vm.IsShapePage, "chosen, the shapes mode over");
        vm.ShapeKind = ShapeKind.Ellipse;
        Assert.Equal(ShapeKind.Ellipse, ((ShapeItem)vm.Project.Items.Single()).Kind, "its settings change it");
    }

    /// <summary>The timeline's rows: the clip, then a row per kind of item, every one there from the start.</summary>
    [Test]
    public static void Rows_are_all_there_from_the_start_and_items_land_in_their_kinds_row()
    {
        var vm = Video();
        Assert.Equal("Video,Text,Draw,Shapes", string.Join(",", vm.Rows), "every row from the start, empty or not");
        var caption = TextAt(0.1, 0.8, 0.3, 0.1) with { Group = Guid.NewGuid() };
        var stroke = new StrokeItem(Guid.NewGuid(), 0, 5, [[new System.Windows.Point(0, 0), new System.Windows.Point(0.1, 0.1)]], "#FFFFFF", 0.01);
        vm.Apply(vm.Project.With(caption).With(stroke));
        Assert.Equal("Video,Text,Draw,Shapes", string.Join(",", vm.Rows), "the rows do not come and go");
        Assert.True(EditorViewModel.RowOf(caption) == TimelineRow.Text && EditorViewModel.RowOf(stroke) == TimelineRow.Draw, "captions in the text row, lines in draw");
        vm.SelectRow(TimelineRow.Text);
        Assert.True(vm.IsRowChosen(TimelineRow.Text) && !vm.IsRowChosen(TimelineRow.Draw), "the text row is chosen");
    }

    static TextItem TextAt(double x, double y, double w, double h) =>
        new(Guid.NewGuid(), 0, 30, "GG", new System.Windows.Rect(x, y, w, h), "#FFFFFF", 0.06, TextEffect.Box);

    [Test]
    public static void Points_are_stored_independent_of_the_view_size()
    {
        // A 16:9 picture in a wide view (bars left and right) and in a tall one (bars top and bottom).
        var wide = EditGeometry.PictureRect(new System.Windows.Size(1000, 400), 16 / 9.0);
        var tall = EditGeometry.PictureRect(new System.Windows.Size(400, 1000), 16 / 9.0);
        Assert.Near(400 * 16 / 9.0, wide.Width, 1e-6, "fills the height");
        Assert.Near(400 * 9 / 16.0, tall.Height, 1e-6, "fills the width");
        var a = EditGeometry.Normalize(new System.Windows.Point(wide.X + wide.Width * 0.25, wide.Y + wide.Height * 0.5), wide);
        var b = EditGeometry.Normalize(new System.Windows.Point(tall.X + tall.Width * 0.25, tall.Y + tall.Height * 0.5), tall);
        Assert.True(Math.Abs(a.X - 0.25) < 1e-9 && Math.Abs(a.Y - 0.5) < 1e-9 && Math.Abs(b.X - a.X) < 1e-9 && Math.Abs(b.Y - a.Y) < 1e-9,
            $"same place on the picture: {a} {b}");
        var outside = EditGeometry.Normalize(new System.Windows.Point(-50, -50), wide);
        Assert.True(outside.X == 0 && outside.Y == 0, "kept on the picture");
    }

    [Test]
    public static void A_corner_drag_resizes_and_keeps_the_box_inside()
    {
        var box = new System.Windows.Rect(0.6, 0.6, 0.3, 0.3);
        var bigger = EditGeometry.Resize(box, Corner.BottomRight, new System.Windows.Point(1.3, 1.2));
        Assert.True(Math.Abs(bigger.Right - 1) < 1e-9 && Math.Abs(bigger.Bottom - 1) < 1e-9, $"stops at the edge: {bigger}");
        Assert.Near(0.6, bigger.X, 1e-9, "the opposite corner stays");
        var tiny = EditGeometry.Resize(box, Corner.TopLeft, new System.Windows.Point(0.95, 0.95));
        Assert.True(tiny.Width >= EditGeometry.MinSize - 1e-9 && tiny.Height >= EditGeometry.MinSize - 1e-9, $"never smaller than the minimum: {tiny}");
    }

    [Test]
    public static void Hit_test_picks_the_topmost_item()
    {
        var below = TextAt(0.1, 0.1, 0.4, 0.2);
        var above = TextAt(0.2, 0.15, 0.4, 0.2);
        Assert.Equal(above.Id, EditGeometry.HitTest([below, above], new System.Windows.Point(0.3, 0.2), 0.01)?.Id, "the one drawn last");
        Assert.Equal(below.Id, EditGeometry.HitTest([below, above], new System.Windows.Point(0.12, 0.12), 0.01)?.Id, "only the first there");
        Assert.Equal(null, EditGeometry.HitTest([below, above], new System.Windows.Point(0.9, 0.9), 0.01), "nothing");
    }

    [Test]
    public static void A_new_item_lasts_five_seconds_from_the_playhead()
    {
        var p = EditProject.For("c.mp4", 30).SplitAt(10).SplitAt(20).DeleteSegmentAt(15); // keeps 0..10 and 20..30
        var (start, end) = EditGeometry.NewItemTime(p, 2);
        Assert.True(start == 2 && end == 7, $"2..7: {start}..{end}");
        (start, end) = EditGeometry.NewItemTime(p, 18); // edit time 18 = source 28
        Assert.True(Math.Abs(start - 28) < 1e-9 && Math.Abs(end - 30) < 1e-9, $"stops at the clip's end: {start}..{end}");
        (start, end) = EditGeometry.NewItemTime(EditProject.For("b.png", 0), 0);
        Assert.True(start == 0 && end == 0, "a screenshot has no time");
    }

    [Test]
    public static void Item_bars_follow_the_edit()
    {
        var item = new TextItem(Guid.NewGuid(), 22, 24, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        var p = EditProject.For("c.mp4", 30).SplitAt(5).SplitAt(20).DeleteSegmentAt(10).With(item); // keeps 0..5 and 20..30
        var bar = TimelineMath.ItemBars(p).Single();
        Assert.True(Math.Abs(bar.Start - 7) < 1e-9 && Math.Abs(bar.End - 9) < 1e-9, $"at 7..9 of the edit: {bar.Start}..{bar.End}");
        var gone = new TextItem(Guid.NewGuid(), 8, 12, "x", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        Assert.Equal(1, TimelineMath.ItemBars(p.With(gone)).Count, "an item that is all cut out has no bar");
    }

    [Test]
    public static void Dragging_a_bar_end_writes_source_time()
    {
        var item = new TextItem(Guid.NewGuid(), 22, 24, "GG", new System.Windows.Rect(0.1, 0.1, 0.2, 0.1), "#FFFFFF", 0.06, TextEffect.Box);
        var p = EditProject.For("c.mp4", 30).SplitAt(5).SplitAt(20).DeleteSegmentAt(10).With(item);
        var longer = TimelineMath.WithBarEdge(p, item, start: false, output: 11);
        Assert.Near(26, longer.End, 1e-9, "edit time 11 is source 26");
        var earlier = TimelineMath.WithBarEdge(p, item, start: true, output: 3);
        Assert.Near(3, earlier.Start, 1e-9, "edit time 3 is source 3");
        var crossed = TimelineMath.WithBarEdge(p, item, start: true, output: 14);
        Assert.True(crossed.Start < crossed.End, "an end dragged past the other keeps the item at least a moment long");
    }

    [Test]
    public static void Playback_skips_the_cut()
    {
        var p = EditProject.For("c.mp4", 30).SplitAt(5).SplitAt(20).DeleteSegmentAt(10); // 0..5 and 20..30
        string Show(IEnumerable<PlayRange> r) => string.Join(", ", r.Select(x => $"{x.SourceStart:0.##}..{x.SourceEnd:0.##}@{x.OutputStart:0.##}x{x.Factor:0.##}"));
        Assert.Equal("0..5@0x1, 20..30@5x1", Show(EditorPlayback.Ranges(p, 0)), "from the start: both parts, the cut skipped");
        Assert.Equal("22..30@7x1", Show(EditorPlayback.Ranges(p, 7)), "from the playhead");
        var fast = EditProject.For("c.mp4", 30).SetSpeed(0, 10, 2);
        Assert.Equal("0..10@0x2, 10..30@5x1", Show(EditorPlayback.Ranges(fast, 0)), "a faster part plays at its speed");
    }

    [Test]
    public static void A_look_changes_the_preview_picture()
    {
        TestFfmpeg.Load();
        var p = EditProject.For("c.mp4", 30) with { Look = ColourLook.Mono };
        var px = new byte[32 * 18 * 4];
        for (int i = 0; i < px.Length; i += 4)
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = ((byte)30, (byte)40, (byte)220, (byte)255);
        using (var colour = new FrameFilter(32, 18, ColourLooks.Filter(p.Look, p.LookStrength)))
            EditorPlayback.Filter(p, colour, px, 32, 18, source: 1);
        Assert.True(Math.Abs(px[2] - px[1]) < 12 && Math.Abs(px[1] - px[0]) < 12, $"grey: {px[2]},{px[1]},{px[0]}");
    }

    /// <summary>What the preview plays, as "tracks gains" (0 is the mix).</summary>
    static string Heard(SoundLevels s, ClipSpan part, TrackLayout layout)
    {
        var plan = EditorPlayback.PreviewSound(s, part, layout);
        return $"{string.Join("+", plan.Tracks)} {string.Join("+", plan.Gains.Select(g => g.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))}";
    }

    static readonly TrackLayout TwoTracks = TrackLayout.Of([], 3), OneTrack = TrackLayout.Of([], 1),
        FiveTracks = TrackLayout.Of(["Mix", "Game", "Discord", "Microphone", "Other sounds"], 5);

    [Test]
    public static void The_preview_plays_the_sound_left_on()
    {
        Assert.Equal("1+2 1+1", Heard(new SoundLevels(), Plain, TwoTracks), "game and mic, as recorded");
        Assert.Equal("1+2 0.5+0", Heard(Sounds.Clip(game: 0.5, micMuted: true), Plain, TwoTracks), "the game alone");
        Assert.Equal("1+2 0+0.8", Heard(Sounds.Clip(mic: 0.8, gameMuted: true), Plain, TwoTracks), "the mic alone");
        Assert.Equal("1+2 0+0", Heard(Sounds.Clip(gameMuted: true, micMuted: true), Plain, TwoTracks), "nothing");
        Assert.Equal("0 0.3", Heard(new SoundLevels(All: 0.3), Plain, OneTrack), "a one-track clip: its sound");
        // A part's own levels on top of the clip's.
        Assert.Equal("1+2 1.5+1", Heard(new SoundLevels(), Plain with { Levels = Sounds.Part(game: 1.5) }, TwoTracks), "a louder part");
        Assert.Equal("1+2 0.75+0", Heard(Sounds.Clip(game: 0.5), Plain with { Levels = Sounds.Part(game: 1.5, mic: 0) }, TwoTracks), "the part's mic off");
        Assert.Equal("0 0.6", Heard(new SoundLevels(All: 0.3), Plain with { Levels = Sounds.Part(game: 2) }, OneTrack), "a one-track part");
    }

    [Test]
    public static void The_preview_mixes_the_clips_own_tracks()
    {
        var discordOff = new SoundLevels { Tracks = default(TrackLevels).WithMuted(1, true).With(2, 0.5) };
        Assert.Equal("1+2+3+4 1+0+0.5+1", Heard(discordOff, Plain, FiveTracks), "Discord muted, the mic at half, the others as recorded");
        Assert.Equal("1+2+3+4 1+0+1.5+1", Heard(discordOff, Plain with { Levels = default(TrackLevels).With(2, 3) }, FiveTracks),
            "a part's own level on top");
    }

    static string JobFolder(out string original)
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        original = Path.Combine(dir, "Game 2026-10-16 18-00-00.mp4");
        File.WriteAllText(original, "original");
        return dir;
    }

    static string Write(string path, string text)
    {
        File.WriteAllText(path, text);
        return path;
    }

    [Test]
    public static void A_new_copy_sits_beside_the_original()
    {
        Text.Language = "en";
        string dir = JobFolder(out string original);
        try
        {
            var jobs = new EditJobs((_, part, _, _) => Write(part, "edited"), _ => throw new Exception("nothing is recycled"));
            var job = jobs.Start(EditProject.For(original, 30), replaceOriginal: false);
            string? saved = job.Task.GetAwaiter().GetResult();
            Assert.Equal(Path.Combine(dir, "Game 2026-10-16 18-00-00 (edited).mp4"), saved, "beside it, named as edited");
            Assert.Equal("original", File.ReadAllText(original), "the original is kept");
            Assert.Equal(2, Directory.GetFiles(dir).Length, "no part file left");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void Replace_recycles_the_original_only_after_success()
    {
        string dir = JobFolder(out string original);
        try
        {
            bool recycled = false;
            var jobs = new EditJobs((_, part, _, _) =>
            {
                File.WriteAllText(part, "half");
                throw new Kairos.Engine.Share.ShareException("the encoder failed");
            }, _ => recycled = true);
            string? failed = null;
            var job = jobs.Start(EditProject.For(original, 30), replaceOriginal: true);
            job.Failed += why => failed = why;
            Assert.Equal(null, job.Task.GetAwaiter().GetResult(), "nothing saved");
            Assert.True(!recycled && File.ReadAllText(original) == "original", "the original stays");
            Assert.Equal(1, Directory.GetFiles(dir).Length, "the half-written file is gone");
            Assert.True(failed?.Contains("encoder") == true || job.Error?.Contains("encoder") == true, "and it says why");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void Closing_cancels_and_cleans()
    {
        string dir = JobFolder(out string original);
        try
        {
            using var started = new ManualResetEventSlim();
            var jobs = new EditJobs((_, part, _, ct) =>
            {
                File.WriteAllText(part, "half");
                started.Set();
                ct.WaitHandle.WaitOne(5000);
                ct.ThrowIfCancellationRequested();
                return part;
            }, _ => { });
            var job = jobs.Start(EditProject.For(original, 30), replaceOriginal: false);
            started.Wait(5000);
            jobs.CancelAll();
            Assert.Equal(null, job.Task.GetAwaiter().GetResult(), "cancelled");
            Assert.Equal(1, Directory.GetFiles(dir).Length, "only the original is left");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The window closing mid-save: the process waits (briefly) for the save to stop and remove its part file, instead of
    /// ending with it half written in the clip folder.
    /// </summary>
    [Test]
    public static void Closing_the_window_mid_save_waits_for_the_part_file_to_go()
    {
        string dir = JobFolder(out string original);
        try
        {
            using var started = new ManualResetEventSlim();
            var jobs = new EditJobs((_, part, _, ct) =>
            {
                File.WriteAllText(part, "half");
                started.Set();
                ct.WaitHandle.WaitOne(5000);
                Thread.Sleep(300); // an export takes a moment to stop
                ct.ThrowIfCancellationRequested();
                return part;
            }, _ => { });
            jobs.Start(EditProject.For(original, 30), replaceOriginal: false);
            started.Wait(5000);
            Assert.True(jobs.CancelAndWait(TimeSpan.FromSeconds(5)), "stopped in time");
            Assert.Equal(1, Directory.GetFiles(dir).Length, "only the original is left, before the process ends");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void Leftovers_of_an_interrupted_save_are_swept()
    {
        string dir = JobFolder(out string original);
        try
        {
            string game = Directory.CreateDirectory(Path.Combine(dir, "Game")).FullName;
            string id = Guid.NewGuid().ToString("N"), id2 = Guid.NewGuid().ToString("N");
            string[] leftovers =
            [
                Path.Combine(dir, $".Game 2026-10-16 18-00-00.{id}.edit.mp4"),
                Path.Combine(game, $".Clip.{id}.edit.mp4.{id2}.part"),
                Path.Combine(game, $".Shot.{id}.edit.png"),
            ];
            string[] kept = [Path.Combine(dir, ".hidden.txt"), Path.Combine(game, "Clip (edited).mp4"), Path.Combine(game, ".Clip.edit.mp4")];
            foreach (string f in leftovers.Concat(kept))
                File.WriteAllText(f, "x");
            EditJobs.SweepLeftovers(dir);
            Assert.True(leftovers.All(f => !File.Exists(f)), "half-written saves removed");
            Assert.True(kept.All(File.Exists) && File.Exists(original), "everything else stays");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void Replace_releases_the_file_first()
    {
        string dir = JobFolder(out string original);
        try
        {
            var order = new List<string>();
            var jobs = new EditJobs((_, part, _, _) => { order.Add("render"); return Write(part, "edited"); },
                path => { order.Add("recycle"); File.Delete(path); });
            var job = jobs.Start(EditProject.For(original, 30), replaceOriginal: true, releaseSource: () => order.Add("release"));
            string? saved = job.Task.GetAwaiter().GetResult();
            Assert.Equal("render, release, recycle", string.Join(", ", order), "players let go of it before it is recycled");
            Assert.Equal(original, saved, "the edit takes the original's name");
            Assert.Equal("edited", File.ReadAllText(original), "and is the edit");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A new text, then a press on the inspector's words field: the text stays and the field keeps the keyboard (the
    /// editor took it back and dropped the empty text, so what was typed went to its shortcuts). Left empty there, it goes.
    /// </summary>
    [Test]
    public static void Typing_a_new_texts_words_in_the_inspector_keeps_the_text() => OnSta(() =>
    {
        var vm = Video();
        var canvas = new EditCanvas(vm);
        canvas.Measure(new System.Windows.Size(640, 360));
        canvas.Arrange(new System.Windows.Rect(0, 0, 640, 360));
        void Pump() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        vm.AddTextCommand.Execute(null);
        Pump();
        Assert.True(canvas.Typing, "typing opens on the picture");
        canvas.CloseEditor(movedOn: true); // the keyboard went to another field: the inspector's words
        Assert.Equal(1, vm.Project.Items.Count, "the new text stays while its words are typed in the inspector");
        vm.DropEmptyTexts();
        Assert.Equal(0, vm.Project.Items.Count, "left without words there, it goes");
        vm.AddTextCommand.Execute(null);
        Pump();
        canvas.CloseEditor();
        Assert.Equal(0, vm.Project.Items.Count, "typing ended anywhere else: an empty text goes at once, as before");
    });
}
