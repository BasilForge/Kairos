using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kairos.Ui.Edit;

namespace Kairos.Tests;

/// <summary>
/// The Library player's playing and seeking (2026-10-21: a click on the bar did nothing while the clip played,
/// and the arrows then stepped from where it really was, not from where the bar showed).
/// </summary>
public static class ViewerPlaybackTests
{
    sealed class Shown
    {
        public readonly List<double> Stills = [];
        public ImageSource? Picture;
    }

    static (ViewerPlayback Playback, EditorUiTests.FakeSession Session, Shown Shown) Make(double duration = 30)
    {
        var session = new EditorUiTests.FakeSession();
        var shown = new Shown();
        var playback = new ViewerPlayback(session, @"C:\Clips\a.mp4", duration, () => (1280, 720), at =>
        {
            shown.Stills.Add(at);
            return new WriteableBitmap(2, 2, 96, 96, PixelFormats.Bgra32, null);
        });
        playback.Picture += picture => shown.Picture = picture;
        return (playback, session, shown);
    }

    [Test]
    public static void A_click_on_the_bar_while_playing_plays_on_from_there() => EditorUiTests.OnSta(() =>
    {
        var (playback, session, shown) = Make();
        playback.Toggle();
        Assert.True(playback.Playing && session.Starts is [0], "playing from the start");
        Assert.True(shown.Picture == null, "its picture waits for its first frame (it was shown blank: black)");
        session.Positions[0](2);
        Assert.Near(2, playback.Position, 1e-9, "the time follows the player");
        Assert.True(shown.Picture == session.Pictures[0], "shown with its first frame");

        playback.SeekTo(10);
        Assert.True(playback.Playing && session.Playing, "still playing after the click");
        Assert.Near(10, session.Starts[^1], 1e-9, "from where the bar was clicked");
        Assert.True(shown.Picture == session.Pictures[0] && shown.Stills.Count == 0,
            "the last picture stays until the new run's first frame: no black, and not a still that stops moving");
        session.Positions[0](2.1);
        Assert.Near(10, playback.Position, 1e-9, "the run before the click no longer moves the time");
        session.Positions[^1](10.2);
        Assert.Near(10.2, playback.Position, 1e-9, "the new run does");
        Assert.True(shown.Picture == session.Pictures[^1], "and shows its picture");
    });

    [Test]
    public static void The_arrows_step_from_where_the_bar_shows() => EditorUiTests.OnSta(() =>
    {
        var (playback, session, _) = Make();
        playback.Toggle();
        playback.SeekTo(10);
        playback.SeekBy(5);
        Assert.Near(15, session.Starts[^1], 1e-9, "five seconds on from the click");
        playback.SeekBy(-5);
        playback.SeekBy(-5);
        Assert.Near(5, session.Starts[^1], 1e-9, "ten seconds back");
        playback.SeekBy(-20);
        Assert.Near(0, session.Starts[^1], 1e-9, "never before the start");
    });

    [Test]
    public static void A_drag_shows_where_it_is_and_plays_on_when_let_go() => EditorUiTests.OnSta(() =>
    {
        var (playback, session, shown) = Make();
        playback.Toggle();
        playback.Hold();
        Assert.True(!playback.Playing && !session.Playing && playback.Held, "waits while dragging");
        playback.SeekTo(15);
        playback.SeekTo(20);
        Assert.True(!session.Playing && shown.Stills.SequenceEqual([15.0, 20.0]), "the picture follows the drag");
        Assert.Near(20, playback.Position, 1e-9, "and the time");
        playback.Release();
        Assert.True(playback.Playing, "plays again when let go");
        Assert.Near(20, session.Starts[^1], 1e-9, "from where the drag ended");
    });

    [Test]
    public static void Paused_it_moves_without_playing_and_plays_from_there() => EditorUiTests.OnSta(() =>
    {
        var (playback, session, shown) = Make();
        playback.SeekTo(12);
        playback.SeekBy(5);
        Assert.True(!playback.Playing && session.Starts.Count == 0, "seeking while paused does not play");
        Assert.True(shown.Stills.SequenceEqual([12.0, 17.0]), "but shows the picture there");
        playback.Hold();
        playback.SeekTo(3);
        playback.Release();
        Assert.True(!playback.Playing, "a drag while paused stays paused");
        Assert.True(shown.Stills.SequenceEqual([12.0, 17.0, 3.0, 3.0]), "and shows the picture there again at full size when let go");
        playback.Toggle();
        Assert.Near(3, session.Starts[^1], 1e-9, "play starts where it was moved to");
    });

    [Test]
    public static void A_held_arrow_moves_the_picture_and_plays_on_once_when_let_go() => EditorUiTests.OnSta(() =>
    {
        var (playback, session, shown) = Make();
        playback.Toggle();
        playback.SeekBy(5); // the first press
        int starts = session.Starts.Count;
        for (int i = 0; i < 3; i++)
        {
            playback.Hold(); // its repeats
            playback.SeekBy(5);
        }
        Assert.True(!session.Playing && session.Starts.Count == starts, "the player is not started again at every repeat");
        Assert.True(shown.Stills.SequenceEqual([10.0, 15.0, 20.0]), "the picture follows");
        playback.Release();
        Assert.True(playback.Playing && session.Starts.Count == starts + 1, "let go: plays on, once");
        Assert.Near(20, session.Starts[^1], 1e-9, "from where it was let go");
    });

    [Test]
    public static void Stepping_past_the_end_stops_there_instead_of_starting_over() => EditorUiTests.OnSta(() =>
    {
        var (playback, session, _) = Make(duration: 30);
        playback.Toggle();
        playback.SeekTo(28);
        playback.SeekBy(5);
        Assert.True(!playback.Playing && !session.Playing, "stopped at the end");
        Assert.Near(30, playback.Position, 1e-9, "at the end, not back at the start");
        playback.Toggle();
        Assert.Near(0, session.Starts[^1], 1e-9, "play at the end plays it again from the start");
    });

    [Test]
    public static void The_picture_is_decoded_at_the_size_it_is_shown_never_above_the_clip()
    {
        Assert.Equal((2400, 1350), ViewerPlayback.PictureSize(2560, 1440, 2400, 1500), "a large window: its width, not 1280 enlarged");
        Assert.Equal((1920, 1080), ViewerPlayback.PictureSize(2560, 1440, 2500, 1080), "a wide, low area: its height decides");
        Assert.Equal((2560, 1440), ViewerPlayback.PictureSize(2560, 1440, 3800, 2100), "never more than the clip has");
        Assert.Equal((640, 360), ViewerPlayback.PictureSize(2560, 1440, 300, 200), "a tiny window still gets a clear picture");
        Assert.Equal((640, 360), ViewerPlayback.PictureSize(640, 360, 2400, 1350), "a small clip at its own size");
        Assert.Equal((1080, 1920), ViewerPlayback.PictureSize(1080, 1920, 2400, 1920), "a tall clip");
        Assert.Equal((2560, 1440), ViewerPlayback.PictureSize(3840, 2160, 3700, 2000), "at most 1440p wide: a 4K screen stays light");
    }

    [Test]
    public static void Each_play_asks_the_size_again() => EditorUiTests.OnSta(() =>
    {
        var session = new EditorUiTests.FakeSession();
        var size = (W: 1280, H: 720);
        var playback = new ViewerPlayback(session, @"C:\Clips\a.mp4", 30, () => size, _ => null);
        playback.Toggle();
        size = (2400, 1350);
        playback.SeekTo(5);
        Assert.True(session.Widths.SequenceEqual([1280, 2400]), "the window grew: the next run is sharper");
    });
}
