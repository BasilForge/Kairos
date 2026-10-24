using Kairos.Settings;

namespace Kairos.Engine.Audio;

/// <summary>A running process: its id, its parent's, and its name (no ".exe").</summary>
public readonly record struct ProcInfo(uint Pid, uint Parent, string Name);

/// <summary>
/// What the partition needs: the game's process (0: none yet), the rows, Kairos's own process name, and the processes
/// playing on a playback device added as a row, with that device's id (heard from that device, so from no process row).
/// </summary>
public sealed record PartitionInput(uint GamePid, IReadOnlyList<AudioSource> Rows, string SelfName, IReadOnlyDictionary<uint, string>? OnOutputs = null);

/// <summary>
/// Which processes each Separate-sources row captures. A capture takes a process and its children, so each row gets the
/// top process of its app (the game, Discord, an added app); "other" gets every other process with sound, except one
/// inside a captured tree (it is already heard), one above a named row's process (a launcher such as Steam holds its
/// game: capturing it would record the game twice), a sound mixer app (SteelSeries Sonar plays every app's sound again:
/// each is heard from itself) and Kairos itself. No sound is captured twice: an app named outright
/// (Discord, an added app) claims its tree before the Game row, so a game that is really Discord's stream viewer, or
/// an added app gone full screen, stays in its own row; an app's second root process is heard as Other sounds.
/// </summary>
public static class AudioPartition
{
    static readonly Dictionary<uint, string> NoOutputs = [];

    /// <param name="startedAt">When a process started (any monotonic unit), or null when unknown: a parent that started
    /// after its child is a reused pid, not its launcher.</param>
    /// <param name="heardOn">Filled with the named rows whose app is heard on a device row instead: row id, device id (the
    /// Audio page says where their sound went).</param>
    public static IReadOnlyDictionary<string, uint[]> Assign(IReadOnlyCollection<ProcInfo> processes, IReadOnlySet<uint> withSound,
        PartitionInput input, Func<uint, long?>? startedAt = null, IDictionary<string, string>? heardOn = null)
    {
        var byPid = new Dictionary<uint, ProcInfo>();
        foreach (var p in processes)
            byPid.TryAdd(p.Pid, p);
        // A start time opens the process: asked only along the edges the rows walk, and once per process per call (never
        // every process on the PC every 2 s, the game's anti-cheat among them).
        var started = new Dictionary<uint, long?>();
        long? StartedAt(uint pid) => started.TryGetValue(pid, out var at) ? at : started[pid] = startedAt!(pid);
        bool Launched(ProcInfo p) => startedAt == null || StartedAt(p.Parent) is not { } parent || StartedAt(p.Pid) is not { } child || parent <= child;
        var children = processes.Where(p => p.Parent != p.Pid).ToLookup(p => p.Parent, p => p);

        // The top process of a run of same-named processes (an app's own helpers are its children).
        uint Top(uint pid)
        {
            for (int guard = 0; guard < 64 && byPid.TryGetValue(pid, out var p) && byPid.TryGetValue(p.Parent, out var parent)
                                && parent.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase) && p.Parent != pid && Launched(p); guard++)
                pid = p.Parent;
            return pid;
        }
        // A process's parents, the nearest first.
        IEnumerable<uint> Ancestors(uint pid)
        {
            for (int guard = 0; guard < 64 && byPid.TryGetValue(pid, out var p) && p.Parent != pid && Launched(p); guard++)
                yield return pid = p.Parent;
        }
        uint[] TopsNamed(Func<string, bool> name) =>
            processes.Where(p => name(p.Name)).Select(p => Top(p.Pid)).Distinct().ToArray();
        // What plays on a playback device added as a row is heard from that device: a tree holding such a process (the
        // process or one of its parents) is no row's.
        var onOutputs = input.OnOutputs ?? NoOutputs;
        var deviceOf = new Dictionary<uint, string>(); // a tree that plays on a device row: that device
        foreach (var (pid, device) in onOutputs)
            foreach (uint node in Ancestors(pid).Prepend(pid))
                deviceOf.TryAdd(node, device);

        // Each named row claims one tree, the apps named outright first; a root inside or above a claimed tree, or a
        // row's second root, is left for Other sounds.
        var result = new Dictionary<string, uint[]>();
        var named = new HashSet<uint>();
        var inside = new HashSet<uint>();
        var above = new HashSet<uint>();
        var spare = new List<uint>();
        void Claim(uint top)
        {
            named.Add(top);
            var stack = new Stack<uint>([top]);
            while (stack.TryPop(out uint pid))
                if (inside.Add(pid))
                    foreach (var child in children[pid])
                        if (Launched(child))
                            stack.Push(child.Pid);
            above.UnionWith(Ancestors(top));
        }
        foreach (var row in input.Rows.OrderBy(r => r.Kind == AudioSourceKind.Game ? 1 : 0))
        {
            uint[] tops = row.Kind switch
            {
                AudioSourceKind.Game => input.GamePid != 0 && byPid.ContainsKey(input.GamePid) ? [Top(input.GamePid)] : [],
                AudioSourceKind.Discord => TopsNamed(n => n is "Discord" or "DiscordPTB" or "DiscordCanary"),
                AudioSourceKind.App => TopsNamed(n => n.Equals(row.Target, StringComparison.OrdinalIgnoreCase)),
                _ => [],
            };
            var free = tops.Where(t => !inside.Contains(t) && !above.Contains(t) && !deviceOf.ContainsKey(t)).ToList();
            if (free.Count == 0)
            {
                if (heardOn != null && tops.Where(deviceOf.ContainsKey).Select(t => deviceOf[t]).FirstOrDefault() is { } device)
                    heardOn[row.Id] = device;
                continue;
            }
            result[row.Id] = [free[0]];
            Claim(free[0]);
            spare.AddRange(free.Skip(1));
        }
        foreach (uint pid in spare.Where(pid => !inside.Contains(pid) && !above.Contains(pid)))
            Claim(pid); // its sound goes to Other sounds, as one tree
        var extraRoots = spare.Where(named.Contains).ToHashSet();

        bool Candidate(uint pid) => pid != 0 && !onOutputs.ContainsKey(pid) && !above.Contains(pid) && (!inside.Contains(pid) || extraRoots.Contains(pid))
                                   && !(byPid.TryGetValue(pid, out var p)
                                        && (p.Name.Equals(input.SelfName, StringComparison.OrdinalIgnoreCase) || VirtualAudio.IsMixerApp(p.Name)));
        var others = withSound.Select(pid => extraRoots.Contains(Top(pid)) ? Top(pid) : pid).Where(Candidate).ToHashSet();
        // One with an ancestor among the others is heard in that ancestor's tree.
        var other = others.Where(pid => !Ancestors(pid).Any(others.Contains)).Order().ToArray();
        if (input.Rows.FirstOrDefault(r => r.Kind == AudioSourceKind.OtherSounds) is { } otherRow && other.Length > 0)
            result[otherRow.Id] = other;
        return result;
    }
}
