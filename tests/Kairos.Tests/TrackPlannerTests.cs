using Kairos.Engine.Audio;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Which libobs mixes (tracks) each sound source feeds: track 1 is everything, then the tracks that add up to it.</summary>
public static class TrackPlannerTests
{
    static AudioSettings Separate(params AudioSource[] added)
    {
        var a = new AudioSettings { Layout = AudioLayout.Separate, Sources = [.. AudioSettings.BuiltInSources(), .. added] };
        return a;
    }

    [Test]
    public static void The_built_in_rows_get_a_track_each_after_the_mix()
    {
        var plan = TrackPlanner.Plan(Separate());
        Assert.Equal("Mix,Game,Discord,Microphone,Other sounds", string.Join(",", plan.Names), "tracks in row order");
        Assert.Equal(1u | 2u, plan.MixersFor("game"), "the game: the mix and its own track");
        Assert.Equal(1u | 16u, plan.MixersFor("other"), "other sounds: the mix and track 5");
    }

    [Test]
    public static void Rows_without_their_own_track_share_one()
    {
        var a = Separate(new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", Label = "Spotify", OwnTrack = false });
        a.Sources.Single(r => r.Kind == AudioSourceKind.Discord).OwnTrack = false;
        var plan = TrackPlanner.Plan(a);
        Assert.Equal("Mix,Game,Microphone,Other sounds,Shared", string.Join(",", plan.Names), "one shared track at the end");
        Assert.Equal(plan.MixersFor("discord"), plan.MixersFor("app:spotify"), "both in the shared track");
        Assert.Equal(1u | 16u, plan.MixersFor("discord"), "the mix and track 5");
    }

    [Test]
    public static void Never_more_than_six_tracks()
    {
        var a = Separate(
            new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", Label = "Spotify" },
            new AudioSource { Id = "app:brave", Kind = AudioSourceKind.App, Target = "brave", Label = "Brave" });
        var plan = TrackPlanner.Plan(a);
        Assert.Equal(6, plan.Names.Count, "libobs has six mixes");
        Assert.Equal("Mix,Game,Discord,Microphone,Other sounds,Shared", string.Join(",", plan.Names), "the rows past the limit share the last");
        Assert.Equal(plan.MixersFor("app:spotify"), plan.MixersFor("app:brave"), "both shared");
        Assert.True(a.Sources.All(r => (plan.MixersFor(r.Id) & 1) == 1), "every source is in the mix");
    }

    [Test]
    public static void An_app_track_is_named_after_the_app()
    {
        var plan = TrackPlanner.Plan(new AudioSettings
        {
            Layout = AudioLayout.Separate,
            Sources = [.. AudioSettings.BuiltInSources().Where(r => r.Kind != AudioSourceKind.OtherSounds),
                new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", Label = "Spotify" }],
        });
        Assert.Equal("Spotify", plan.Names[^1], "its label");
    }

    [Test]
    public static void Together_is_kairos_1_0()
    {
        var a = new AudioSettings(); // System, Mic, separate tracks
        var plan = TrackPlanner.Plan(a);
        Assert.Equal("Mix,System,Microphone", string.Join(",", plan.Names), "three tracks, named now");
        Assert.Equal(1u | 2u, plan.MixersFor("system"), "as SystemMixers()");
        Assert.Equal(1u | 4u, plan.MixersFor("mic"), "the mic: track 3");
        Assert.Equal("Mix", string.Join(",", TrackPlanner.Plan(new AudioSettings { SeparateTracks = false }).Names), "one track");
        Assert.Equal(0u, TrackPlanner.Plan(new AudioSettings { Mic = false }).MixersFor("mic"), "Smart Save only: the mic in no track");
        Assert.Equal("Mix,System", string.Join(",", TrackPlanner.Plan(new AudioSettings { Mic = false }).Names), "no mic track");
    }

    [Test]
    public static void A_row_owns_a_track_only_when_one_is_left_for_it()
    {
        var a = Separate(
            new AudioSource { Id = "app:spotify", Kind = AudioSourceKind.App, Target = "Spotify", Label = "Spotify" },
            new AudioSource { Id = "app:brave", Kind = AudioSourceKind.App, Target = "brave", Label = "Brave" });
        var plan = TrackPlanner.Plan(a);
        Assert.True(plan.OwnsTrack("game") && plan.OwnsTrack("other"), "the first rows have theirs");
        Assert.True(!plan.OwnsTrack("app:spotify") && !plan.OwnsTrack("app:brave"), "past the limit: shared");
        Assert.True(!TrackPlanner.Plan(Separate()).OwnsTrack("nobody"), "no such row");
    }
}
