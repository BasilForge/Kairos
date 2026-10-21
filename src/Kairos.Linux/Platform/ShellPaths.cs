using System.Diagnostics;

namespace Kairos.App;

public static class ShellPaths
{
    public static ProcessStartInfo OpenFolder(string folder) => new("xdg-open", [folder]) { UseShellExecute = false };

    /// <summary>
    /// The file manager with <paramref name="path"/> selected (the FileManager1 interface Nautilus, Nemo, Dolphin and
    /// Thunar implement); a plain "open the folder" when none answers.
    /// </summary>
    public static ProcessStartInfo ShowInFolder(string path) => new("sh",
        ["-c", "dbus-send --session --print-reply --dest=org.freedesktop.FileManager1 --type=method_call /org/freedesktop/FileManager1 " +
               "org.freedesktop.FileManager1.ShowItems array:string:\"file://$1\" string: >/dev/null 2>&1 || xdg-open \"$(dirname \"$1\")\"",
         "sh", Path.GetFullPath(path)]) { UseShellExecute = false };
}
