using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.App;

/// <summary>
/// The log's own lines, so a log someone sends shows the root of a problem: the settings and what changed, the hotkeys
/// and whether Kairos holds them, health every few minutes, and the session's story. Nothing personal: apps by their
/// exe name only (never a window's title), folders by their kind (OneDrive, a drive letter), never a path.
/// </summary>
public static class LogLines
{
    static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>
    /// What shapes Kairos, as key=value in a fixed order (the hotkeys as key.*); <paramref name="folderKind"/> names the
    /// clips folder's place (OneDrive, Dropbox, Google Drive, or its drive).
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Pairs(AppSettings s, Func<string, string> folderKind)
    {
        var c = s.Capture;
        var v = s.Video;
        var r = s.Replay;
        var list = new List<(string, string)>
        {
            ("mode", c.Mode.ToString()),
            ("apps", c.Mode == CaptureMode.Window ? string.Join(",", c.Apps.Select(a => WindowTarget.ExeName(a) ?? "?")) : "-"),
            ("cursor", OnOff(c.CaptureCursor)),
            ("only-gaming", OnOff(s.General.OnlyWhenGaming)),
            ("riot-screen", OnOff(c.ScreenForRiotGames)),
            ("game-fallback", OnOff(c.GameFallbackToScreen)),
            ("quality", v.Auto ? "auto"
                : $"{EncoderCatalog.CodecName(v.Codec)} {v.Resolution} {v.Fps}fps {v.Quality}"
                  + (v.Quality == QualityPreset.Custom ? $" cq{v.CustomCq} {v.CustomMbps}Mbps" : "")),
            ("replay", OnOff(r.Enabled)),
            ("length", $"{r.Seconds}s"),
            ("replay-store", r.OnDisk ? $"disk {r.DiskDrive ?? "auto"}" : "ram"),
            ("quick", r.QuickEnabled ? $"{r.QuickSeconds}s" : "off"),
            ("fresh", OnOff(r.FreshAfterSave)),
            ("audio", s.Audio.Layout.ToString().ToLowerInvariant()),
            // Each row (* on its own track): an app by its exe, a device row by its kind only (its name can be a person's).
            ("sources", s.Audio.Layout == AudioLayout.Separate
                ? string.Join(",", s.Audio.Sources.Select(r => (r.Kind switch
                {
                    AudioSourceKind.App => r.Target,
                    AudioSourceKind.Game => r.Target.Length > 0 ? "game:" + r.Target : "game",
                    AudioSourceKind.Microphone => "mic",
                    AudioSourceKind.OtherSounds => "other",
                    _ => r.Kind.ToString().ToLowerInvariant(),
                }) + (r.OwnTrack ? "*" : "")))
                : "-"),
            ("system", OnOff(s.Audio.System)),
            ("mic", OnOff(s.Audio.Mic)),
            ("app-sound-only", OnOff(s.Audio.SourceAudioOnly)),
            ("camera", s.Camera.Enabled ? $"{s.Camera.Layout}{(s.Camera.SeparateFile ? "+file" : "")}".ToLowerInvariant() : "off"),
            ("folder", folderKind(s.Output.Folder)),
            ("per-game", OnOff(s.Output.PerAppFolders)),
            ("limit", s.Output.MaxFolderGb > 0 ? $"{s.Output.MaxFolderGb}GB" : "none"),
            ("free-local", OnOff(s.Output.FreeLocalCopies)),
            ("lang", s.General.Language),
            ("startup", OnOff(s.General.StartWithWindows)),
            ("updates", OnOff(s.General.CheckUpdates)),
            ("smart-save", s.Voice.Enabled ? s.Voice.Sensitivity.ToString().ToLowerInvariant() : "off"),
        };
        var k = s.Hotkeys;
        foreach (var (id, key) in new[] { ("save", k.Save), ("quick", k.Quick), ("toggle", k.ToggleBuffer), ("record", k.Record),
                     ("pauseRecord", k.PauseRecord ?? ""), ("screenshot", k.Screenshot) })
            list.Add(("key." + id, string.IsNullOrWhiteSpace(key) ? "none" : key));
        return list;
    }

    /// <summary>The settings line at a start (the hotkeys have their own line, with whether Kairos holds each).</summary>
    public static string Settings(AppSettings s, Func<string, string> folderKind) =>
        "settings: " + string.Join(" · ", Pairs(s, folderKind).Where(p => !p.Key.StartsWith("key.", StringComparison.Ordinal))
            .Select(p => $"{p.Key}={p.Value}"));

    /// <summary>What a save from the settings window changed ("only-gaming off→on, key.save Alt+F10→Ctrl+F8"); null: nothing.</summary>
    public static string? Changes(AppSettings before, AppSettings after, Func<string, string> folderKind)
    {
        var was = Pairs(before, folderKind).ToDictionary(p => p.Key, p => p.Value);
        var changed = Pairs(after, folderKind).Where(p => was[p.Key] != p.Value).Select(p => $"{p.Key} {was[p.Key]}→{p.Value}").ToList();
        return changed.Count == 0 ? null : string.Join(", ", changed);
    }

    /// <summary>Each hotkey and whether Kairos holds it ("taken": another app registered it first).</summary>
    public static string Hotkeys(IEnumerable<(string Id, string Key, bool Held)> keys) =>
        "hotkeys: " + string.Join(" · ", keys.Select(k => string.IsNullOrWhiteSpace(k.Key) ? $"{k.Id}=none" : $"{k.Id}={k.Key} {(k.Held ? "ok" : "taken")}"));

    /// <summary>
    /// How it ran since the last health line: frames skipped by the encoder out of those it was given (a rebuild's fresh
    /// counters count from zero), pictures lagged out of those rendered (libobs counts them for the whole session: a
    /// rebuild once made it "lagged 885 %"), and Kairos's own CPU, memory and the clips drive's room.
    /// </summary>
    public static string Health(EngineStatus? now, EngineStatus? before, double cpuPercent, long ramMb, string drive, long freeGb)
    {
        string rest = $"cpu {cpuPercent:F0}% · ram {ramMb} MB · {drive} {freeGb} GB free";
        if (now == null)
            return $"health: replay not built · {rest}";
        var was = before?.Frames ?? default;
        return $"health: replay {OnOff(now.BufferActive)} · {now.Fps:F1} fps · skipped {now.Frames.SkippedSince(was):P2} · lagged {now.Frames.LaggedSince(was):P2} · {rest}"
            .Replace(" %", "%");
    }

    /// <summary>
    /// A clip from the replay on the disk: how full its picture was, and how far the picture was behind the sound. An
    /// encoder a game starved left a few pictures from long before in a clip of the "last minutes" (a user's report).
    /// </summary>
    public static string RingClip(int pictures, double pictureShare, double pictureBehind) =>
        FormattableString.Invariant($"clip from the disk: {pictures} pictures, {pictureShare * 100:F0}% of the frame rate")
        + (pictureBehind >= 1 ? FormattableString.Invariant($" · the newest picture {pictureBehind:F0} s behind the sound: the encoder fell behind") : "");

    /// <summary>
    /// Kairos's share of the graphics card's memory (<see cref="Engine.GpuMemory"/>): over its budget Windows pages it out
    /// and frames wait (a game filling the card); what is in system memory was paged out already.
    /// </summary>
    public static string GpuMemory(ulong used, ulong budget, ulong inSystem)
    {
        const double Mb = 1 << 20;
        return FormattableString.Invariant($"gpu memory: Kairos uses {used / Mb:F0} MB of its {budget / Mb:F0} MB budget on the card · {inSystem / Mb:F0} MB in system memory")
               + (used > budget ? " · over budget: Windows pages it out" : "");
    }

    /// <summary>The session at its end: how long, what was saved or not, and how many hotkey presses reached Kairos.</summary>
    public static string Session(TimeSpan up, int saved, int notSaved, int shots, int presses) =>
        $"session: {(int)up.TotalHours}h{up.Minutes:00}m · clips saved {saved} · not saved {notSaved} · screenshots {shots} · hotkey presses {presses}";
}
