using System.Text;

namespace Kairos.App;

/// <summary>
/// Clips the user pinned in the Library, one full path per line. Pinned clips stay on top of the list
/// and show under Favorites; the list is only Kairos's own notes, not the files on disk.
/// </summary>
public sealed class ClipFavorites(string path)
{
    /// <summary>Folder-list value for "Favorites only" (not a real folder name: * is illegal on disk).</summary>
    public const string FilterKey = "*Favorites*";

    static readonly Encoding Utf8 = new UTF8Encoding(false);
    readonly object _gate = new();

    public static string DefaultPath => Path.Combine(Settings.SettingsStore.AppDataDir, "favorites.txt");

    /// <summary>Full paths, compared case-insensitively like Windows does.</summary>
    public HashSet<string> Load()
    {
        lock (_gate)
            return File.Exists(path)
                ? new HashSet<string>(File.ReadAllLines(path, Utf8).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public bool Contains(string clip)
    {
        lock (_gate)
            return LoadUnlocked().Contains(Path.GetFullPath(clip));
    }

    /// <summary>Pins or unpins <paramref name="clip"/>. Returns whether it is pinned afterwards.</summary>
    public bool Toggle(string clip)
    {
        lock (_gate)
        {
            var set = LoadUnlocked();
            string full = Path.GetFullPath(clip);
            if (!set.Add(full))
                set.Remove(full);
            SaveUnlocked(set);
            return set.Contains(full);
        }
    }

    /// <summary>Forgets paths that were deleted, moved or recycled.</summary>
    public void Remove(IEnumerable<string> clips)
    {
        lock (_gate)
        {
            var set = LoadUnlocked();
            bool changed = false;
            foreach (string clip in clips)
                changed |= set.Remove(Path.GetFullPath(clip));
            if (changed)
                SaveUnlocked(set);
        }
    }

    /// <summary>Drops paths that no longer exist on disk.</summary>
    public void Prune()
    {
        lock (_gate)
        {
            if (!File.Exists(path))
                return;
            var keep = File.ReadAllLines(path, Utf8).Where(l => l.Length > 0 && File.Exists(l))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            SaveUnlocked(keep);
        }
    }

    HashSet<string> LoadUnlocked() =>
        File.Exists(path)
            ? new HashSet<string>(File.ReadAllLines(path, Utf8).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void SaveUnlocked(IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines, Utf8);
        File.Move(tmp, path, overwrite: true);
    }
}
