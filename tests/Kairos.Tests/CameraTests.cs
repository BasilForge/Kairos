using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>The camera over the game: a corner box, or the top of a 9:16 picture, and its own file.</summary>
public static class CameraTests
{
    [Test]
    public static void The_camera_is_off_until_turned_on_and_its_place_stays_inside()
    {
        var s = new AppSettings();
        Assert.True(!s.Camera.Enabled && s.Camera.Layout == CameraLayout.Corner && !s.Camera.SeparateFile, "off, corner, one file");
        s.Camera.X = 4;
        s.Camera.Y = -1;
        s.Camera.Size = 3;
        s.Normalize();
        Assert.True(s.Camera.X is >= 0 and <= 1 && s.Camera.Y is >= 0 and <= 1, "a place on the picture");
        Assert.Equal(CameraSettings.MaxSize, s.Camera.Size, "not bigger than half the picture");
        var copy = SettingsStore.Deserialize(SettingsStore.Serialize(s));
        Assert.Equal(s.Camera.Size, copy.Camera.Size, "saved");
    }

    [Test]
    public static void The_corner_box_keeps_the_cameras_shape_inside_the_picture()
    {
        var c = new CameraSettings { X = 0.9, Y = 0.9, Size = 0.25 };
        var (x, y, w, h) = CameraMath.Corner(c, 1920, 1080, 16 / 9.0);
        Assert.Equal((480.0, 270.0), (w, h), "a quarter of the width, the camera's shape");
        Assert.Equal((1440.0, 810.0), (x, y), "pushed back inside at the corner");
        var round = CameraMath.Corner(c with { Shape = CameraShape.Round }, 1920, 1080, 16 / 9.0);
        Assert.Equal(round.W, round.H, "a circle's box is square");
    }

    [Test]
    public static void The_vertical_picture_is_9_16_with_the_camera_on_top()
    {
        Assert.Equal((1080, 1920), CameraMath.VerticalSize(OutputResolution.P1080, 1440), "1080p");
        Assert.Equal((720, 1280), CameraMath.VerticalSize(OutputResolution.P720, 1440), "720p");
        Assert.Equal((1440, 2560), CameraMath.VerticalSize(OutputResolution.P1440, 1440), "1440p");
        Assert.Equal((1080, 1920), CameraMath.VerticalSize(OutputResolution.Native, 1080), "the screen's own height as the width");
        Assert.Equal((1440, 2560), CameraMath.VerticalSize(OutputResolution.Native, 2160), "a 4K screen: no bigger than 1440x2560");
        var (camera, game) = CameraMath.Bands(1080, 1920);
        Assert.Equal((0, 0, 1080, 672), camera, "the top 35 %, even");
        Assert.Equal((0, 672, 1080, 1248), game, "the game below");
    }

    [Test]
    public static void Only_the_cameras_place_and_shape_are_changed_without_a_rebuild()
    {
        var before = new AppSettings();
        before.Camera.Enabled = true;
        var moved = SettingsStore.Copy(before);
        moved.Camera.X = 0.1;
        moved.Camera.Size = 0.3;
        moved.Camera.Shape = CameraShape.Round;
        Assert.True(!SettingsDiff.NeedsRebuild(before, moved), "place, size, shape: live");
        var vertical = SettingsStore.Copy(before);
        vertical.Camera.Layout = CameraLayout.Vertical;
        Assert.True(SettingsDiff.NeedsRebuild(before, vertical), "the layout changes the canvas");
        var off = SettingsStore.Copy(before);
        off.Camera.Enabled = false;
        Assert.True(SettingsDiff.NeedsRebuild(before, off), "on and off");
    }

    [Test]
    public static void The_camera_file_runs_at_60_fps_or_less()
    {
        Assert.Equal(1u, CameraMath.FileFpsDivisor(30), "30 fps as it is");
        Assert.Equal(1u, CameraMath.FileFpsDivisor(60), "60 fps as it is");
        Assert.Equal(3u, CameraMath.FileFpsDivisor(144), "144 fps: every third frame (48)");
        Assert.Equal(4u, CameraMath.FileFpsDivisor(240), "240 fps: every fourth (60)");
        Assert.Equal(2u, CameraMath.FileFpsDivisor(120), "120 fps: every second (60)");
    }

    /// <summary>
    /// A camera opened at its "default" size can be 640x480 (many webcams), blurry over a 9:16 picture: the biggest size
    /// it gives up to 1080p is asked for (2026-10-22).
    /// </summary>
    [Test]
    public static void The_camera_is_asked_for_its_biggest_size_up_to_1080p()
    {
        Assert.Equal("1920x1080", CameraMath.BestSize(["640x480", "1920x1080", "1280x720", "3840x2160"]), "a 4K webcam: 1080p");
        Assert.Equal("1280x720", CameraMath.BestSize(["320x240", "640x480", "1280x720"]), "a 720p webcam: all of it");
        Assert.Equal("1080x1920", CameraMath.BestSize(["720x1280", "1080x1920"]), "a phone held upright: 1080 wide");
        Assert.Equal("1440x1080", CameraMath.BestSize(["1440x1080", "1280x720"]), "4:3 at 1080 lines");
        Assert.Equal(null, CameraMath.BestSize(["3840x2160"]), "only bigger than 1080p: its own default");
        Assert.Equal(null, CameraMath.BestSize([]), "no list: its own default");
        Assert.Equal("1280x720", CameraMath.BestSize(["junk", "1280x720"]), "a line that is not a size is left out");
    }
}
