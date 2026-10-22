namespace Kairos.Settings;

/// <summary>
/// The audio settings set on the running sources, without a rebuild: the volumes and the rows' mutes. The settings
/// window sends them while a slider moves (the "live" pipe command); a change to only these keeps the replay going
/// (<see cref="SettingsDiff"/>).
/// </summary>
public sealed record LiveAudio(double SystemVolume, double MicVolume, List<LiveAudio.Row> Rows)
{
    public sealed record Row(string Id, double Volume, bool Muted);

    public static LiveAudio Of(AudioSettings a) => new(a.SystemVolume, a.MicVolume, [.. a.Sources.Select(r => new Row(r.Id, r.Volume, r.Muted))]);

    /// <summary>Every volume at 1 and nothing muted, for the same rows.</summary>
    public static LiveAudio Default(AudioSettings a) => new(1, 1, [.. a.Sources.Select(r => new Row(r.Id, 1, false))]);

    /// <summary>A row's volume and mute (a row not named here: as recorded).</summary>
    public (double Volume, bool Muted) For(string rowId) =>
        Rows?.FirstOrDefault(r => r.Id == rowId) is { } row ? (Limit(row.Volume), row.Muted) : (1, false);

    /// <summary>A volume as the sources take it (0..2).</summary>
    public static double Limit(double volume) => Math.Clamp(volume, 0, 2);

    /// <summary>Sets these on <paramref name="a"/> (a row it does not name keeps its own).</summary>
    public void SetOn(AudioSettings a)
    {
        a.SystemVolume = Limit(SystemVolume);
        a.MicVolume = Limit(MicVolume);
        foreach (var row in Rows ?? [])
            if (a.Sources.FirstOrDefault(r => r.Id == row.Id) is { } same)
            {
                same.Volume = Limit(row.Volume);
                same.Muted = row.Muted;
            }
    }
}
