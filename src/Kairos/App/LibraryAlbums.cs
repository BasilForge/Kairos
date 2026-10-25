using System.Text.Json;

namespace Kairos.App;

/// <summary>A user-made Library album (clip paths). Defaults like Videos are not stored here.</summary>
public sealed class UserAlbum
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Hidden { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public List<string> Paths { get; set; } = [];
}

/// <summary>User albums in %AppData%\Kairos\library-albums.json. Default folders are never written here.</summary>
public sealed class LibraryAlbums(string path)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly object _gate = new();

    public static string DefaultPath => Path.Combine(Settings.SettingsStore.AppDataDir, "library-albums.json");

    /// <summary>Browse id for a user album in the Library.</summary>
    public static string BrowseId(string albumId) => "u:" + albumId;

    public static bool IsUserBrowse(string? browse) =>
        browse != null && browse.StartsWith("u:", StringComparison.Ordinal);

    public static string? AlbumIdOf(string? browse) =>
        IsUserBrowse(browse) ? browse![2..] : null;

    public List<UserAlbum> Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<List<UserAlbum>>(File.ReadAllText(path), Json) is { } list)
                    return list;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Engine.ObsLog.Warn($"library albums: {e.Message}");
            }
            return [];
        }
    }

    public void Save(List<UserAlbum> albums)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(albums, Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Engine.ObsLog.Warn($"library albums: {e.Message}");
            }
        }
    }

    public UserAlbum Add(string name)
    {
        var list = Load();
        var album = new UserAlbum
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Created = DateTime.Now,
        };
        list.Add(album);
        Save(list);
        return album;
    }

    public UserAlbum? Find(string id) => Load().FirstOrDefault(a => a.Id == id);

    public void Update(UserAlbum album)
    {
        var list = Load();
        int at = list.FindIndex(a => a.Id == album.Id);
        if (at < 0)
            return;
        list[at] = album;
        Save(list);
    }

    public void Remove(string id)
    {
        var list = Load();
        list.RemoveAll(a => a.Id == id);
        Save(list);
    }

    /// <summary>Drops clip paths that no longer exist; removes those paths from every album.</summary>
    public void PruneMissing(IEnumerable<string> existingPaths)
    {
        var keep = existingPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = Load();
        bool changed = false;
        foreach (var album in list)
        {
            int before = album.Paths.Count;
            album.Paths = album.Paths.Where(p => keep.Contains(p)).ToList();
            changed |= album.Paths.Count != before;
        }
        if (changed)
            Save(list);
    }

    public void RemovePaths(IEnumerable<string> clips)
    {
        var drop = clips.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = Load();
        bool changed = false;
        foreach (var album in list)
        {
            int before = album.Paths.Count;
            album.Paths = album.Paths.Where(p => !drop.Contains(Path.GetFullPath(p))).ToList();
            changed |= album.Paths.Count != before;
        }
        if (changed)
            Save(list);
    }
}
