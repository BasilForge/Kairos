namespace Kairos.Engine.Audio;

/// <summary>What an audio track holds.</summary>
public enum TrackRole { Mix, Game, Discord, Microphone, Other, Shared, App, System }

/// <summary>
/// A clip's audio tracks: track 1 the full mix, then the tracks that add up to it, from the names Kairos writes into
/// the file (<see cref="Mp4TrackNames"/>). A clip without them (Kairos 1.0, another recorder) reads as before: exactly
/// three tracks are the mix, the system sound and the microphone; otherwise only the first, the mix, is known (a
/// separate-sources clip that lost its names is not guessed at).
/// </summary>
public sealed record TrackLayout(IReadOnlyList<TrackRole> Roles, IReadOnlyList<string> Labels)
{
    /// <summary>The tracks that carry voices (captions listen to these): the microphone, and Discord.</summary>
    public IReadOnlyList<int> VoiceTracks => Indexes(r => r is TrackRole.Microphone or TrackRole.Discord);

    /// <summary>
    /// The tracks after the mix: together they are the mix, so an edit can set each on its own (none when there is only
    /// one: it is the mix again).
    /// </summary>
    public IReadOnlyList<int> PartitionTracks => Indexes(r => r != TrackRole.Mix) is { Count: >= 2 } own ? own : [];

    IReadOnlyList<int> Indexes(Func<TrackRole, bool> which) =>
        Roles.Select((role, i) => (role, i)).Where(t => which(t.role)).Select(t => t.i).ToList();

    /// <summary>The names Kairos writes for its own tracks (an app's or a device's track carries its label instead).</summary>
    static readonly Dictionary<TrackRole, string> FileNames = new()
    {
        [TrackRole.Mix] = "Mix",
        [TrackRole.Game] = "Game",
        [TrackRole.Discord] = "Discord",
        [TrackRole.Microphone] = "Microphone",
        [TrackRole.Other] = "Other sounds",
        [TrackRole.Shared] = "Shared",
        [TrackRole.System] = "System",
    };

    static readonly Dictionary<string, TrackRole> Known = FileNames.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static string FileName(TrackRole role) => FileNames[role];

    /// <summary>A clip's tracks from what FFmpeg read of it.</summary>
    public static TrackLayout Of(Share.MediaInfo info) => Of(info.AudioNames ?? [], info.AudioStreams);

    public static TrackLayout Of(IReadOnlyList<string> names, int audioStreams)
    {
        if (names.Count == audioStreams && audioStreams > 0 && Known.TryGetValue(names[0], out var first) && first == TrackRole.Mix)
            return new TrackLayout(names.Select(n => Known.TryGetValue(n, out var role) ? role : TrackRole.App).ToList(), names);
        TrackRole[] roles = audioStreams == 3 ? [TrackRole.Mix, TrackRole.System, TrackRole.Microphone] : [TrackRole.Mix];
        return new TrackLayout(roles, [.. roles.Select(FileName)]);
    }
}
