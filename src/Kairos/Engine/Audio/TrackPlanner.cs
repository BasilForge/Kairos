using Kairos.Settings;

namespace Kairos.Engine.Audio;

/// <summary>One track of a clip: its libobs mix (0 = track 1), what it holds and the name written into the file.</summary>
public sealed record PlannedTrack(int Mixer, TrackRole Role, string Name)
{
    public PlannedTrack(int mixer, TrackRole role) : this(mixer, role, TrackLayout.FileName(role)) { }
}

/// <summary>The tracks a pipeline records, and which of them each sound source feeds.</summary>
public sealed class TrackPlan(IReadOnlyList<PlannedTrack> tracks, IReadOnlyDictionary<string, uint> mixers)
{
    public IReadOnlyList<PlannedTrack> Tracks { get; } = tracks;
    public IReadOnlyList<string> Names { get; } = tracks.Select(t => t.Name).ToList();

    /// <summary>The libobs mixer bits for a source (a row's id; "system" and "mic" in the together layout); 0 for none.</summary>
    public uint MixersFor(string sourceId) => mixers.TryGetValue(sourceId, out uint m) ? m : 0;

    /// <summary>Whether a row has a track of its own (not the shared one).</summary>
    public bool OwnsTrack(string sourceId)
    {
        uint own = MixersFor(sourceId) & ~1u;
        return own != 0 && Tracks.Any(t => 1u << t.Mixer == own && t.Role != TrackRole.Shared);
    }
}

/// <summary>
/// Turns the audio settings into tracks. Track 1 is everything (what any player plays). After it, each row with its own
/// track gets one, in row order; the rows without one share a last track, so tracks 2 and on always add up to track 1
/// and an edit can set each apart. libobs has six mixes: rows past them share too.
/// </summary>
public static class TrackPlanner
{
    public const int MaxTracks = 6;

    public static TrackPlan Plan(AudioSettings a) => a.Layout == AudioLayout.Separate ? Separate(a) : Together(a);

    /// <summary>Kairos 1.0's tracks: the mix, then (separate tracks) the system sound and the microphone.</summary>
    static TrackPlan Together(AudioSettings a)
    {
        var tracks = new List<PlannedTrack> { new(0, TrackRole.Mix) };
        uint systemBit = 0, micBit = 0;
        if (a.SeparateTracks)
        {
            if (a.System || a.SourceAudioOnly)
            {
                tracks.Add(new(1, TrackRole.System));
                systemBit = 2;
            }
            if (a.Mic)
            {
                tracks.Add(new(2, TrackRole.Microphone));
                micBit = 4;
            }
        }
        return new TrackPlan(tracks, new Dictionary<string, uint>
        {
            ["system"] = 1u | systemBit,
            // Smart Save hears the microphone even when it is not recorded: then it feeds no track.
            ["mic"] = a.Mic ? 1u | micBit : 0,
        });
    }

    static TrackPlan Separate(AudioSettings a)
    {
        var rows = a.Sources;
        int own = rows.Count(r => r.OwnTrack), room = MaxTracks - 1;
        bool shared = rows.Any(r => !r.OwnTrack) || own > room;
        int ownAllowed = shared ? room - 1 : room;
        var tracks = new List<PlannedTrack> { new(0, TrackRole.Mix) };
        var mixers = new Dictionary<string, uint>();
        var sharing = new List<string>();
        foreach (var row in rows)
        {
            if (row.OwnTrack && tracks.Count - 1 < ownAllowed)
            {
                int mixer = tracks.Count;
                tracks.Add(new(mixer, Role(row.Kind), Name(row)));
                mixers[row.Id] = 1u | (1u << mixer);
            }
            else
                sharing.Add(row.Id);
        }
        if (sharing.Count > 0)
        {
            int mixer = tracks.Count;
            tracks.Add(new(mixer, TrackRole.Shared));
            foreach (string id in sharing)
                mixers[id] = 1u | (1u << mixer);
        }
        return new TrackPlan(tracks, mixers);
    }

    /// <summary>What a row's track holds: the built-in rows' own; an added app or device is known by its name.</summary>
    public static TrackRole Role(AudioSourceKind kind) => kind switch
    {
        AudioSourceKind.Game => TrackRole.Game,
        AudioSourceKind.Discord => TrackRole.Discord,
        AudioSourceKind.Microphone => TrackRole.Microphone,
        AudioSourceKind.OtherSounds => TrackRole.Other,
        _ => TrackRole.App,
    };

    /// <summary>The track's name in the file: the built-in rows' own, an added app's or device's label.</summary>
    public static string Name(AudioSource row) => Role(row.Kind) is var role and not TrackRole.App ? TrackLayout.FileName(role)
        : row.Label.Length > 0 ? row.Label : row.Target;
}
