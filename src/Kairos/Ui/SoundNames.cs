using Kairos.App;
using Kairos.Engine.Audio;

namespace Kairos.Ui;

/// <summary>How a sound source or track is shown: its icon (Segoe Fluent Icons) and name.</summary>
static class SoundNames
{
    /// <summary>Game, chat, microphone, an app's window, or a speaker (other sounds, shared, the mix).</summary>
    public static string Glyph(TrackRole role) => role switch
    {
        TrackRole.Game or TrackRole.System => "",
        TrackRole.Discord => "",
        TrackRole.Microphone => "",
        TrackRole.App => "",
        _ => "",
    };

    /// <summary>The known tracks in the user's language (a 1.0.1 clip's system sound is its game); an app by its own name.</summary>
    public static string Name(TrackRole role, string label) => role switch
    {
        TrackRole.Game or TrackRole.System => Text.Get("audio.game"),
        TrackRole.Discord => "Discord",
        TrackRole.Microphone => Text.Get("audio.mic"),
        TrackRole.Other => Text.Get("audio.otherSounds"),
        TrackRole.Shared => Text.Get("audio.shared"),
        _ => label,
    };
}
