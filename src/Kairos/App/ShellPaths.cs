namespace Kairos.App;

public static class ShellPaths
{
    /// <summary>Explorer from the Windows folder, never an explorer.exe found earlier on the search path (e.g. next to Kairos.exe).</summary>
    /// <summary>Opens <paramref name="folder"/> in the file manager.</summary>
    public static System.Diagnostics.ProcessStartInfo OpenFolder(string folder) => new(Explorer, $"\"{folder}\"") { UseShellExecute = true };

    /// <summary>Opens the file manager with <paramref name="path"/> selected.</summary>
    public static System.Diagnostics.ProcessStartInfo ShowInFolder(string path) => new(Explorer, $"/select,\"{path}\"") { UseShellExecute = true };

    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
}
