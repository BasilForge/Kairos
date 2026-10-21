namespace Kairos.App;

/// <summary>Start-with-the-desktop entry: ~/.config/autostart/kairos.desktop (the XDG autostart specification).</summary>
public static class Autostart
{
    static string EntryPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "kairos.desktop");

    public static string CommandFor(string exePath) => $"\"{exePath}\" --background";

    public static void Sync(bool enabled, string exePath)
    {
        if (!enabled)
        {
            if (File.Exists(EntryPath))
                File.Delete(EntryPath);
            return;
        }
        string entry = "[Desktop Entry]\nType=Application\nName=Kairos\nComment=Instant replay\n" +
            $"Exec={CommandFor(exePath)}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
        if (File.Exists(EntryPath) && File.ReadAllText(EntryPath) == entry)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, entry);
    }
}
