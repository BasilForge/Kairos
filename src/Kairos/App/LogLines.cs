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
    /// How it ran since the last health line: frames skipped by the encoder and lagged by the picture out of the frames
    /// since then (a rebuild's fresh counters count from zero), and Kairos's own CPU, memory and the clips drive's room.
    /// </summary>
    public static string Health(EngineStatus? now, EngineStatus? before, double cpuPercent, long ramMb, string drive, long freeGb)
    {
        string rest = $"cpu {cpuPercent:F0}% · ram {ramMb} MB · {drive} {freeGb} GB free";
        if (now == null)
            return $"health: replay not built · {rest}";
        bool fresh = before == null || now.TotalFrames < before.TotalFrames;
        double total = Math.Max(1, now.TotalFrames - (fresh ? 0 : before!.TotalFrames));
        double skipped = now.SkippedFrames - (fresh ? 0 : Math.Min(before!.SkippedFrames, now.SkippedFrames));
        double lagged = now.LaggedFrames - (fresh ? 0 : Math.Min(before!.LaggedFrames, now.LaggedFrames));
        return $"health: replay {OnOff(now.BufferActive)} · {now.Fps:F1} fps · skipped {skipped / total:P2} · lagged {lagged / total:P2} · {rest}"
            .Replace(" %", "%");
    }

    /// <summary>The session at its end: how long, what was saved or not, and how many hotkey presses reached Kairos.</summary>
    public static string Session(TimeSpan up, int saved, int notSaved, int shots, int presses) =>
        $"session: {(int)up.TotalHours}h{up.Minutes:00}m · clips saved {saved} · not saved {notSaved} · screenshots {shots} · hotkey presses {presses}";
}
