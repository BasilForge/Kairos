using System.Text;

namespace Kairos.Engine;

public static class ClipOrganizer
{
    const int MaxNameLength = 60;
    const string Desktop = "Desktop";

    static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp",
        "lockapp", "textinputhost", "kairos",
    };

    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    static readonly HashSet<char> InvalidChars = [.. Path.GetInvalidFileNameChars()];

    /// <summary>
    /// The folder a saved clip goes to: the app in front, unless that is the desktop or Kairos's own window (a save from a
    /// macro, the tray or Kairos's Save button) while a game was in front within the clip's length: that game's (a user's
    /// ARC Raiders clips all went to "Desktop"). Kairos's window and the game's name are looked up only when needed.
    /// </summary>
    public static string SaveFolderName(string front, Func<bool> frontIsKairos, Func<string>? lastGame, TimeSpan sinceLastGame, double clipSeconds) =>
        lastGame != null && sinceLastGame.TotalSeconds <= clipSeconds && (front == Desktop || frontIsKairos()) ? lastGame() : front;

    /// <summary>Folder-safe name for the app in the foreground when a clip was saved.</summary>
    public static string AppFolderName(string? processName, string? productName, string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(processName) || ShellProcesses.Contains(processName))
            return Desktop;

        bool genericProduct = productName != null &&
            productName.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
            productName.Contains("Operating System", StringComparison.OrdinalIgnoreCase);

        foreach (string? candidate in new[] { genericProduct ? null : productName, processName, windowTitle })
        {
            string name = Sanitize(candidate);
            if (name.Length > 0)
                return ReservedNames.Contains(name) ? name + "_" : name;
        }
        return Desktop;
    }

    static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            bool bad = InvalidChars.Contains(c) || char.IsControl(c);
            char next = bad ? ' ' : c;
            if (next == ' ' && (sb.Length == 0 || sb[^1] == ' '))
                continue;
            sb.Append(next);
        }
        string result = sb.ToString().Trim().TrimEnd('.').Trim();
        return result.Length > MaxNameLength ? result[..MaxNameLength].TrimEnd() : result;
    }

    /// <summary>First usable folder of: preferred, the default (Videos\Kairos), then D:\Clips.</summary>
    public static string ResolveFolder(string preferred, Func<string, bool> canUse, out bool fellBack)
    {
        string videos = Settings.OutputSettings.DefaultFolder;
        foreach (string candidate in new[] { preferred, videos })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && canUse(candidate))
            {
                fellBack = !string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase);
                return candidate;
            }
        }
        fellBack = true;
        return videos;
    }

    /// <summary>
    /// Clips can go in <paramref name="folder"/>: writable, and not inside the install (an uninstall removes the whole
    /// install folder, and clips must never go with it).
    /// </summary>
    public static bool Usable(string folder, string? installRoot) =>
        (installRoot == null || !App.InstallPaths.IsInside(installRoot, folder)) && CanWrite(folder);

    /// <summary>True when the folder exists or can be created and a file can be written in it.</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".kairos-{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static string TargetPath(string root, string app, DateTime time, bool perAppFolders, string extension = ".mp4")
    {
        // Invariant: a region with another calendar would otherwise give other years (1448-05-01) in file names.
        string file = $"{app} {time.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture)}{extension}";
        return perAppFolders ? Path.Combine(root, app, file) : Path.Combine(root, file);
    }

    /// <summary>A picture (a screenshot) rather than a video, by its extension.</summary>
    public static bool IsPicture(string path) => ClipLibrary.KindOf(path) == ClipKind.Picture;

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="target"/> (in its folder, its name, or that name with " (n)"
    /// when it was taken): a clip written where it belongs.
    /// </summary>
    public static bool IsAt(string path, string? target) =>
        target != null
        && string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileNameWithoutExtension(path).StartsWith(Path.GetFileNameWithoutExtension(target), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="target"/>, or with " (n)" added when a file of that name is already there.</summary>
    public static string FreePath(string target)
    {
        string stem = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target));
        string ext = Path.GetExtension(target);
        string final = target;
        for (int n = 2; File.Exists(final); n++)
            final = $"{stem} ({n}){ext}";
        return final;
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="target"/>, adding " (n)" if taken. Returns the final path. A file
    /// another program holds open for a moment (an antivirus or a thumbnail reading a clip just written) is tried again
    /// for up to 3 s before the move gives up.
    /// </summary>
    public static string Move(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        return HeldFile.Retry(() =>
        {
            string final = FreePath(target);
            File.Move(source, final);
            return final;
        });
    }
}
