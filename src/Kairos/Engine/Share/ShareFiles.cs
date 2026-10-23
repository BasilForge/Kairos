namespace Kairos.Engine.Share;

/// <summary>Where prepared copies go: %LOCALAPPDATA%\Kairos\Share\&lt;platform&gt;\, kept for a few days.</summary>
public static class ShareFiles
{
    static readonly TimeSpan KeepFor = TimeSpan.FromDays(3);

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name, "Share");

    /// <summary>The clip's own name, so the upload shows the game and the date, one folder per platform.</summary>
    public static string Target(string folder, SharePlatform platform, string clipPath, string extension = ".mp4") =>
        Path.Combine(folder, platform.ToString(), Path.GetFileNameWithoutExtension(clipPath) + extension);

    /// <summary>The clips folder's "Shared": copies made with Copy (a cut, or a size) are kept there, in the Library.</summary>
    public static string SharedIn(string clipsFolder) => Path.Combine(clipsFolder, "Shared");

    /// <summary>
    /// A copy named after its clip, its part and size (<paramref name="mb"/>, 0 for a plain cut):
    /// "Game 2026-10-12 21-04-55 (4.30-5.00, 10 MB).mp4".
    /// </summary>
    public static string CopyTarget(string sharedFolder, string clipPath, double start, double end, int mb = 0) =>
        Path.Combine(sharedFolder, $"{Path.GetFileNameWithoutExtension(clipPath)} ({Stamp(start)}-{Stamp(end)}{(mb > 0 ? $", {mb} MB" : "")}).mp4");

    static string Stamp(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}.{t.Minutes:00}.{t.Seconds:00}" : $"{t.Minutes}.{t.Seconds:00}";
    }

    /// <summary>Removes prepared copies older than three days. Only touches this app's own share folder.</summary>
    public static void Clean(string folder, DateTime now)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (string file in Directory.EnumerateFiles(folder, "*.mp4*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(folder, "*.jpg", SearchOption.AllDirectories)))
        {
            try
            {
                if (now - File.GetLastWriteTime(file) > KeepFor)
                    File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still being uploaded or open somewhere: next time.
            }
        }
    }
}
