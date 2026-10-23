using Kairos.Engine.Audio;

namespace Kairos.Tests;

/// <summary>Kairos names its audio tracks inside the MP4 (the hdlr box), so the editor and captions know which is which.</summary>
public static class TrackLayoutTests
{
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Share", name);

    static string Copy(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.Copy(Fixture(name), path);
        return path;
    }

    [Test]
    public static void Names_go_into_the_file_in_place_and_come_back()
    {
        string clip = Copy("clip-av1-2tracks.mp4");
        long length = new FileInfo(clip).Length;
        Assert.True(Mp4TrackNames.Write(clip, ["Mix", "Microphone"]), "written");
        Assert.Equal("Mix|Microphone", string.Join("|", Mp4TrackNames.Read(clip)), "read back");
        Assert.Equal(length, new FileInfo(clip).Length, "in place: the same size");
        TestFfmpeg.Load();
        Assert.Equal("Mix|Microphone", string.Join("|", Kairos.Engine.Share.ClipTranscoder.Probe(clip).AudioNames!), "FFmpeg reads them too");
    }

    [Test]
    public static void A_clip_saved_by_obs_reads_its_default_names()
    {
        Assert.Equal("SoundHandler|SoundHandler", string.Join("|", Mp4TrackNames.Read(Fixture("clip-av1-2tracks.mp4"))), "ffmpeg's default");
    }

    [Test]
    public static void A_name_longer_than_the_slot_is_cut()
    {
        string clip = Copy("clip-h264.mp4");
        Mp4TrackNames.Write(clip, ["Other sounds and more"]);
        Assert.Equal("Other sounds", Mp4TrackNames.Read(clip)[0], "12 characters, the slot ffmpeg leaves");
    }

    [Test]
    public static void Old_clips_read_as_before()
    {
        var three = TrackLayout.Of([], 3);
        Assert.Equal("Mix,System,Microphone", string.Join(",", three.Roles), "1.0.1's three tracks");
        Assert.Equal("2", string.Join(",", three.VoiceTracks), "the mic for captions");
        Assert.Equal("Mix", string.Join(",", TrackLayout.Of([], 1).Roles), "one track");
        Assert.Equal("Mix", string.Join(",", TrackLayout.Of([], 2).Roles), "two tracks without names: only the mix is known");
        Assert.Equal("Mix", string.Join(",", TrackLayout.Of([], 5).Roles), "a separate-sources clip that lost its names: the mix only, not a wrong guess");
        Assert.Equal("Mix,System,Microphone", string.Join(",", TrackLayout.Of(["SoundHandler", "SoundHandler", "SoundHandler"], 3).Roles), "unnamed");
    }

    [Test]
    public static void Named_tracks_give_their_roles()
    {
        var layout = TrackLayout.Of(["Mix", "Game", "Discord", "Microphone", "Other sounds"], 5);
        Assert.Equal("Mix,Game,Discord,Microphone,Other", string.Join(",", layout.Roles), "roles");
        Assert.Equal("2,3", string.Join(",", layout.VoiceTracks), "Discord and the mic carry voices");
        Assert.Equal("1,2,3,4", string.Join(",", layout.PartitionTracks), "they add up to the mix");
        var app = TrackLayout.Of(["Mix", "Spotify", "Shared"], 3);
        Assert.Equal("Mix,App,Shared", string.Join(",", app.Roles), "an app's own track, and the shared one");
        Assert.Equal("Spotify", app.Labels[1], "its name");
    }

    [Test]
    public static void A_recording_gets_its_names_once_its_file_is_let_go()
    {
        // A recording is written in fragments, and its muxer still holds the file a moment after it stops.
        string recording = Copy("recording-fragmented.mp4");
        var later = new List<Action>();
        using (new FileStream(recording, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            TrackNaming.Name(recording, ["Mix", "Microphone"], (_, retry) => later.Add(retry));
            Assert.Equal(1, later.Count, "held: tried again a little later");
        }
        var pending = later[0];
        later.Clear();
        pending();
        Assert.Equal(0, later.Count, "let go: written at once");
        Assert.Equal("Mix|Microphone", string.Join("|", Mp4TrackNames.Read(recording)), "a recording's tracks are named too");
        TestFfmpeg.Load();
        Assert.Near(2, Kairos.Engine.Share.ClipTranscoder.Probe(recording).Duration, 0.2, "and it still plays");
    }
}
