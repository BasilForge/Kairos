namespace Kairos.Engine.Replay;

/// <summary>
/// Where the replay's ring file goes and how big it is. Never inside the clips folder: that may be a OneDrive or Google
/// Drive folder, which would upload gigabytes again and again.
/// </summary>
public static class DiskReplayPlace
{
    public const string FileName = "replay.ring";
    public const string CameraFile = "camera.ring";

    /// <summary>
    /// The ring's folder on <paramref name="drive"/> ("D:"; null: the clips folder's drive): the Windows drive keeps it
    /// under the user's own folder (<paramref name="localAppData"/>), any other drive at its root, hidden.
    /// </summary>
    public static string Folder(string? drive, string clipsFolder, string systemDrive, string localAppData)
    {
        string d = (drive ?? Path.GetPathRoot(clipsFolder) ?? systemDrive).TrimEnd('\\').ToUpperInvariant();
        return d.Equals(systemDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(localAppData, "Kairos", "Replay")
            : d + @"\.kairos-replay";
    }

    /// <summary>The ring's size: the RAM budget's worst case and room (<see cref="BufferBudget"/>), without its RAM cap.</summary>
    public static long RingBytes(int maxKbps, int seconds) =>
        (EncoderPresets.EstimateBufferMb(maxKbps, seconds) * 5L / 4 + 64) * 1024 * 1024;

    /// <summary>
    /// The ring's folder on this PC for <paramref name="drive"/> (null: the clips folder's drive, or Windows's own when the
    /// clips are on a USB stick or the network).
    /// </summary>
    public static string FolderFor(string? drive, string clipsFolder)
    {
        // Self-tests and measures run with a data folder of their own: their rings stay in it, never the real one's.
        if (Environment.GetEnvironmentVariable("KAIROS_DATA") is { Length: > 0 } data)
            return Path.Combine(data, "Replay");
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:";
        return Folder(drive ?? (IsFixed(clipsFolder) ? null : systemDrive), clipsFolder, systemDrive,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>Whether <paramref name="path"/> is on a fixed drive (not a USB stick, a card or the network).</summary>
    public static bool IsFixed(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false; // a network path (\\server\share)
        }
    }

    /// <summary>The free space a ring needs: itself and 1 GB more, so the drive never fills up because of it.</summary>
    public static long NeededBytes(long ringBytes) => ringBytes + 1024L * 1024 * 1024;

    /// <summary>The ring files a folder holds (the main replay's and the camera file's).</summary>
    public static readonly string[] Files = [FileName, CameraFile];

    /// <summary>
    /// Removes Kairos's ring files from <paramref name="folder"/>, and the folder when nothing else is in it. They are
    /// Kairos's own working files (no clip is ever in them), so they go for good: the space is what they are for.
    /// </summary>
    public static void Remove(string folder)
    {
        try
        {
            foreach (string file in Files.Select(f => Path.Combine(folder, f)))
                File.Delete(file); // none there: nothing happens
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn($"replay on the disk: {folder} not removed: {e.Message}");
        }
    }

    /// <summary>Removes the camera file's ring only (its file turned off, the replay still on the disk).</summary>
    public static void RemoveCamera(string folder)
    {
        try
        {
            File.Delete(Path.Combine(folder, CameraFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn($"replay on the disk: the camera's ring not removed: {e.Message}");
        }
    }

    /// <summary>Every folder a ring may be in on this PC (the uninstall's clean-up).</summary>
    public static IEnumerable<string> AllFolders(string localAppData) =>
        new[] { Path.Combine(localAppData, "Kairos", "Replay") }
            .Concat(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => Path.Combine(d.Name, ".kairos-replay")));
}
