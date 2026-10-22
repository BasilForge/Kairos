using Kairos.Engine.Edit;

namespace Kairos.Tests;

/// <summary>A 1.0.1 clip's game and mic (its own tracks 1 and 2) as an edit holds them.</summary>
static class Sounds
{
    /// <summary>The whole clip's levels.</summary>
    public static SoundLevels Clip(double game = 1, double mic = 1, bool gameMuted = false, bool micMuted = false) =>
        new() { Tracks = Part(game, mic).WithMuted(0, gameMuted).WithMuted(1, micMuted) };

    /// <summary>A part's own levels.</summary>
    public static TrackLevels Part(double game = 1, double mic = 1) => default(TrackLevels).With(0, game).With(1, mic);
}
