using System.Text.RegularExpressions;

namespace Kairos.Engine;

/// <summary>What a file on the Clips page is.</summary>
public enum ClipKind { Video, Picture }

/// <summary>A video or picture on the Clips page: where it is, the group it is listed under, when it was written and its size.</summary>
/// <param name="Camera">The clip's own camera file ("….camera.mp4" beside it), or null.</param>
public sealed record LibraryClip(string Path, string Group, DateTime Time, long Size, ClipKind Kind = ClipKind.Video, string? Camera = null);

/// <summary>
/// Every video and picture in the clips folder, whatever made it (Kairos, NVIDIA, Game Bar, OBS...) and however deep:
/// the clips folder may be Windows' own Videos, with a folder per game.
/// </summary>
public static partial class ClipLibrary
{
    /// <summary>Folders deeper than this are not looked in (also a guard against folder links that loop).</summary>
    const int MaxDepth = 8;

    static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        MatchType = MatchType.Simple,
    };

    /// <summary>A video (.mp4) or a picture (.png, .jpg, .jpeg), whatever the case; null for anything else.</summary>
    public static ClipKind? KindOf(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" => ClipKind.Video,
        ".png" or ".jpg" or ".jpeg" => ClipKind.Picture,
        _ => null,
    };

    /// <summary>Every video and picture under <paramref name="root"/>, newest first; hidden folders and caches (".name") are left out.</summary>
    public static List<LibraryClip> Scan(string root)
    {
        var found = new List<FileInfo>();
        if (Directory.Exists(root))
            Walk(new DirectoryInfo(root), found, 0);
        // A clip's camera file is part of that clip: no card of its own.
        var cameras = found.Where(f => IsCameraFile(f.Name)).Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return found
            .Where(f => !IsCameraFile(f.Name))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new LibraryClip(f.FullName, GroupOf(root, f.FullName), f.LastWriteTime, f.Length, KindOf(f.Name)!.Value,
                cameras.Count > 0 && CameraOf(f.FullName) is var camera && cameras.Contains(camera) ? camera : null))
            .ToList();
    }

    /// <summary>"….camera.mp4": a clip's own camera file.</summary>
    public static bool IsCameraFile(string name) => name.EndsWith(".camera.mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a clip's camera file is (whether it is there or not).</summary>
    public static string CameraOf(string clip) => System.IO.Path.ChangeExtension(clip, null) + ".camera.mp4";

    /// <summary>A clip's files: the clip, then its camera file when there is one (they are deleted or freed together).</summary>
    public static IEnumerable<string> FilesOf(string clip)
    {
        yield return clip;
        string camera = CameraOf(clip);
        if (File.Exists(camera))
            yield return camera;
    }

    /// <summary>The newest picture under <paramref name="root"/>, or null.</summary>
    public static string? NewestPicture(string root) => Scan(root).FirstOrDefault(c => c.Kind == ClipKind.Picture)?.Path;

    /// <summary>The newest video under <paramref name="root"/> (not a picture), or null.</summary>
    public static string? NewestVideo(string root) => Scan(root).FirstOrDefault(c => c.Kind == ClipKind.Video)?.Path;

    static void Walk(DirectoryInfo dir, List<FileInfo> into, int depth)
    {
        try
        {
            foreach (var file in dir.EnumerateFiles("*", Options))
                if (!file.Name.StartsWith('.') && KindOf(file.Name) != null)
                    into.Add(file);
            if (depth < MaxDepth)
                foreach (var sub in dir.EnumerateDirectories("*", Options))
                    if (!sub.Name.StartsWith('.'))
                        Walk(sub, into, depth + 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder that went away or cannot be read: the rest is still listed.
        }
    }

    /// <summary>
    /// The first folder under the root (a game; folders inside it count for it). Directly in the root: the app in
    /// Kairos's own names ("Desktop 2026-10-14 23-17-00"), else the root folder's own name ("Videos").
    /// </summary>
    public static string GroupOf(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        int separator = relative.IndexOfAny(['\\', '/']);
        if (separator > 0)
            return relative[..separator];
        var named = KairosName().Match(Path.GetFileNameWithoutExtension(path));
        return named.Success ? named.Groups[1].Value : new DirectoryInfo(root).Name;
    }

    [GeneratedRegex(@"^(.+?) \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}(?: \(\d+\))?$")]
    private static partial Regex KairosName();

    /// <summary>The groups and how many videos and pictures each has, alphabetical.</summary>
    public static List<(string Group, int Count)> Groups(IEnumerable<LibraryClip> clips) =>
        clips.GroupBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Group, g.Count()))
            .OrderBy(g => g.Item1, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
