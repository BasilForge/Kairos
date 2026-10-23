using System.Text.Json;

namespace Kairos.App;

public enum CloudKind { OneDrive, Dropbox, GoogleDrive }

/// <summary>A cloud provider's folder on this PC.</summary>
public sealed record CloudFolder(CloudKind Kind, string Path)
{
    /// <summary>Google Drive for desktop streams by itself (files stay online until opened): no local copies to free.</summary>
    public bool Streams => Kind == CloudKind.GoogleDrive;
}

/// <summary>What the providers leave on a PC (the real one: <see cref="WindowsCloudSources"/>; tests: a fake).</summary>
public interface ICloudSources
{
    string? Env(string name);
    /// <summary>OneDrive's accounts' folders (HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder).</summary>
    IEnumerable<string> OneDriveUserFolders();
    string? ReadFile(string path);
    /// <summary>Ready drives: their root and volume label.</summary>
    IEnumerable<(string Root, string Label)> Drives();
    bool DirectoryExists(string path);
    string UserProfile { get; }
    string LocalAppData { get; }
    string AppData { get; }
}

/// <summary>OneDrive, Dropbox and Google Drive for desktop folders on this PC (read only: nothing is opened or changed).</summary>
public static class CloudFolders
{
    public static List<CloudFolder> Find(ICloudSources s)
    {
        var found = new List<CloudFolder>();
        void Add(CloudKind kind, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            path = System.IO.Path.TrimEndingDirectorySeparator(path.Trim());
            if (s.DirectoryExists(path) && !found.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                found.Add(new CloudFolder(kind, path));
        }

        foreach (string name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            Add(CloudKind.OneDrive, s.Env(name));
        foreach (string folder in s.OneDriveUserFolders())
            Add(CloudKind.OneDrive, folder);

        foreach (string dir in new[] { s.LocalAppData, s.AppData })
        {
            if (s.ReadFile(System.IO.Path.Combine(dir, "Dropbox", "info.json")) is not { } json)
                continue;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var account in doc.RootElement.EnumerateObject())
                    if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                        Add(CloudKind.Dropbox, p.GetString());
            }
            catch (JsonException)
            {
                // Not Dropbox's file as Kairos knows it: no Dropbox offered.
            }
        }

        foreach (var (root, label) in s.Drives())
            if (label.Equals("Google Drive", StringComparison.OrdinalIgnoreCase))
                Add(CloudKind.GoogleDrive, System.IO.Path.Combine(root, "My Drive"));
        Add(CloudKind.GoogleDrive, System.IO.Path.Combine(s.UserProfile, "My Drive")); // mirror mode
        return found;
    }

    /// <summary>Whether the provider's app runs (it uploads only then); <paramref name="running"/> is the tests' process check.</summary>
    public static bool ClientRunning(CloudKind kind, Func<string, bool>? running = null)
    {
        string process = kind switch
        {
            CloudKind.OneDrive => "OneDrive",
            CloudKind.Dropbox => "Dropbox",
            _ => "GoogleDriveFS", // Google Drive for desktop
        };
        return running?.Invoke(process) ?? ProcessList.Any((_, name) => name.Equals(process, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The provider whose folder holds <paramref name="folder"/> (the folder itself too), or null.</summary>
    public static CloudFolder? Under(string folder, IReadOnlyList<CloudFolder> found)
    {
        string path = System.IO.Path.TrimEndingDirectorySeparator(folder);
        return found.FirstOrDefault(f => path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)
                                         || path.StartsWith(f.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    static readonly Lazy<IReadOnlyList<CloudFolder>> Providers = new(() => Find(new WindowsCloudSources()));

    /// <summary>The providers on this PC (looked for once a process).</summary>
    public static IReadOnlyList<CloudFolder> OnThisPc() => Providers.Value;

    /// <summary><paramref name="folder"/> is in OneDrive or Dropbox: its files' local copies can be freed.</summary>
    public static bool FreesLocal(string folder) => Under(folder, OnThisPc()) is { Streams: false };

    /// <summary>
    /// A clip, screenshot, recording or edit was saved at <paramref name="path"/>: in a OneDrive or Dropbox folder, with
    /// <paramref name="freeLocalCopies"/> on, its thumbnail is made when it is <paramref name="shown"/> (so the Library
    /// never downloads it again to show it) and then it is kept online only. Nothing elsewhere. Done in the background.
    /// </summary>
    public static void Saved(string path, bool freeLocalCopies, bool shown = true)
    {
        if (!freeLocalCopies)
            return;
        Task.Run(() =>
        {
            if (Path.GetDirectoryName(path) is not { } folder || !FreesLocal(folder))
                return;
            if (shown)
                ShellImage.Warm(path, () => CloudFiles.FreeLocalCopy(path));
            else
                CloudFiles.FreeLocalCopy(path);
        });
    }
}

public sealed class WindowsCloudSources : ICloudSources
{
    public string? Env(string name) => Environment.GetEnvironmentVariable(name);

    public IEnumerable<string> OneDriveUserFolders()
    {
        using var accounts = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");
        foreach (string name in accounts?.GetSubKeyNames() ?? [])
        {
            using var account = accounts!.OpenSubKey(name);
            if (account?.GetValue("UserFolder") is string folder)
                yield return folder;
        }
    }

    public string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<(string Root, string Label)> Drives()
    {
        // Fixed drives only (Google Drive's is one): a network or disc drive can keep the window waiting.
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            string label;
            try
            {
                if (!drive.IsReady)
                    continue;
                label = drive.VolumeLabel;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            yield return (drive.RootDirectory.FullName, label);
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
}

/// <summary>
/// Windows's cloud-files attributes: whether a file's bytes are on this PC, and asking the provider to keep it online
/// only (it frees the local copy once uploaded; opening the file downloads it again, as in File Explorer).
/// </summary>
public static class CloudFiles
{
    const FileAttributes Pinned = (FileAttributes)0x80000, Unpinned = (FileAttributes)0x100000;
    const FileAttributes RecallOnOpen = (FileAttributes)0x40000, RecallOnDataAccess = (FileAttributes)0x400000;

    /// <summary>Its bytes are here: reading it downloads nothing.</summary>
    public static bool IsLocal(FileAttributes a) => (a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) == 0;

    public static FileAttributes FreedLocal(FileAttributes a) => (a | Unpinned) & ~Pinned;

    /// <summary>Asks the provider to keep <paramref name="path"/> online only; nothing when it is gone or locked.</summary>
    public static void FreeLocalCopy(string path)
    {
        try
        {
            File.SetAttributes(path, FreedLocal(File.GetAttributes(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }
}
