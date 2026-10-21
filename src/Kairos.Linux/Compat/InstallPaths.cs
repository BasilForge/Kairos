namespace Kairos.App;

/// <summary>
/// Where this Kairos runs from. Linux has no per-user installer folder: an AppImage is one file, a package puts the
/// program under /usr, and a development build runs from its build folder. None of them is an "install root" Kairos owns.
/// </summary>
public static class InstallPaths
{
    /// <summary>Always null: the Windows install folder has no Linux counterpart.</summary>
    public static string? Root => null;

    /// <summary>What autostart and restarts launch: the AppImage when run from one, else this executable.</summary>
    public static string LaunchExe => Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image ? image : Environment.ProcessPath!;

    /// <summary><paramref name="path"/> is <paramref name="root"/> or a folder in it (trailing slashes ignored).</summary>
    public static bool IsInside(string root, string path)
    {
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return p == r || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
