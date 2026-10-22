using System.Diagnostics;

namespace Kairos.App;

public static class ShellPaths
{
    /// <summary>Explorer from the Windows folder, never an explorer.exe found earlier on the search path (e.g. next to Kairos.exe).</summary>
    public static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>winget from the folder Windows installs it in, never a winget.exe found earlier on the search path.</summary>
    public static string Winget =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");

    /// <summary>A file or web page in its usual app (nothing when Windows has none for it).</summary>
    public static void Open(string target)
    {
        if (SideEffects.Held($"open {target}"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>File Explorer with <paramref name="path"/> selected (nothing when the file is gone).</summary>
    public static void ShowInExplorer(string? path)
    {
        if (path != null && File.Exists(path) && !SideEffects.Held($"show in File Explorer {path}"))
            Process.Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>A folder in File Explorer, made first when it is not there yet.</summary>
    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        if (!SideEffects.Held($"open folder {folder}"))
            Process.Start(new ProcessStartInfo(Explorer, $"\"{folder}\"") { UseShellExecute = true });
    }
}
