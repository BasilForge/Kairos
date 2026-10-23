namespace Kairos.App;

/// <summary>Keeps Kairos's own clips under a size limit by sending the oldest to the Recycle Bin.</summary>
public static class StorageJanitor
{
    /// <summary>Oldest-first paths to delete so the total drops under <paramref name="limitBytes"/>. Never the newest clip.</summary>
    public static List<string> SelectForDeletion(IEnumerable<(string Path, long Size, DateTime Time)> files, long limitBytes)
    {
        var victims = new List<string>();
        if (limitBytes <= 0)
            return victims;

        var ordered = files.OrderBy(f => f.Time).ToList();
        long total = ordered.Sum(f => f.Size);
        for (int i = 0; i < ordered.Count - 1 && total > limitBytes; i++)
        {
            victims.Add(ordered[i].Path);
            total -= ordered[i].Size;
        }
        return victims;
    }

    /// <summary>
    /// The files to remove so the folder drops under the limit. In a cloud folder (<paramref name="cloud"/>) only the copies
    /// on this PC count: the oldest are kept online only, never one that already is.
    /// </summary>
    public static List<string> Choose(IEnumerable<(string Path, long Size, DateTime Time, FileAttributes Attributes)> files, long limitBytes, bool cloud) =>
        SelectForDeletion(files.Where(f => !cloud || CloudFiles.IsLocal(f.Attributes)).Select(f => (f.Path, f.Size, f.Time)), limitBytes);

    /// <summary>
    /// Only clips and screenshots listed in <paramref name="index"/> (saved by Kairos) count and can be removed; any
    /// other video or picture in the folder is left alone. In a cloud folder (<paramref name="cloud"/>) the oldest local
    /// copies are freed (kept online only) instead: removing them there would remove them from the cloud. Returns how
    /// many files were removed or freed.
    /// </summary>
    public static int Enforce(string folder, long limitBytes, ClipIndex index, Action<string> recycle, CloudFolder? provider = null)
    {
        // Google Drive keeps its own local copies, and removing a file there removes it from the cloud too.
        if (limitBytes <= 0 || !Directory.Exists(folder) || provider is { Streams: true })
            return 0;
        bool cloud = provider != null;

        var mine = index.Load();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var root = new DirectoryInfo(folder);
        var files = root.EnumerateFiles("*.mp4", options).Concat(root.EnumerateFiles("*.png", options))
            .Where(f => mine.Contains(f.FullName))
            .Select(f => (f.FullName, f.Length, f.LastWriteTime, f.Attributes));
        int deleted = 0;
        Action<string> remove = cloud ? CloudFiles.FreeLocalCopy : recycle;
        foreach (string path in Choose(files, limitBytes, cloud))
        {
            try
            {
                foreach (string file in Engine.ClipLibrary.FilesOf(path)) // its camera file goes with it
                    remove(file);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
            }
        }
        if (!cloud)
            index.Prune();
        return deleted;
    }

    public static int Enforce(string folder, int maxFolderGb, ClipIndex index, CloudFolder? provider = null) =>
        Enforce(folder, (long)maxFolderGb << 30, index, path => RecycleBin.Send(path, RecycleBin.BackgroundFlags), provider);
}
