namespace Kairos.App;

/// <summary>Reads the xcomposite window strings libobs uses: "id", "title" and "class" on separate lines.</summary>
public static class WindowTarget
{
    static string[] Parts(string? window) => string.IsNullOrEmpty(window) ? [] : window.Split("\r\n");

    public static string? Title(string? window) => Parts(window) is { Length: 3 } p ? p[1] : null;

    /// <summary>The window's class, which names its program (Steam, Brave, a Proton game's .exe).</summary>
    public static string? ExeName(string? window) =>
        Parts(window) is { Length: 3 } p && p[2].Length > 0 ? Path.GetFileNameWithoutExtension(p[2]) : null;
}
