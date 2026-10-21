using System.Text;

namespace Kairos.App;

/// <summary>
/// "Move to the Recycle Bin" as the freedesktop.org Trash specification has it: the file goes to the home trash with a
/// .trashinfo note of where it came from, so the file manager can restore it.
/// </summary>
public static class RecycleBin
{
    /// <summary>Kept for the callers shared with Windows; the trash has no dialogs to control.</summary>
    [Flags]
    public enum Op : ushort
    {
        Silent = 0x0004,
        NoConfirmation = 0x0010,
        AllowUndo = 0x0040,
        NoErrorUi = 0x0400,
        WantNukeWarning = 0x4000,
    }

    public static Op UserFlags => Op.AllowUndo | Op.NoConfirmation | Op.Silent;

    public static Op BackgroundFlags => Op.AllowUndo | Op.NoConfirmation | Op.Silent | Op.NoErrorUi;

    /// <exception cref="IOException">The file could not be moved to the trash.</exception>
    public static void Send(string path, Op flags) => Send(path, TrashRoot(), DateTime.Now);

    internal static string TrashRoot()
    {
        string data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "Trash");
    }

    internal static string Send(string path, string trash, DateTime now)
    {
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new FileNotFoundException("nothing to move to the trash", full);
        string files = Path.Combine(trash, "files"), info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        // The note is created first and exclusively: that claims the name, so two deletions never share one.
        string name = Path.GetFileName(full), stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string chosen = name;
        for (int n = 2; ; n++)
        {
            try
            {
                using var note = new FileStream(Path.Combine(info, chosen + ".trashinfo"), FileMode.CreateNew, FileAccess.Write);
                var text = Encoding.UTF8.GetBytes($"[Trash Info]\nPath={Escape(full)}\nDeletionDate={now:yyyy-MM-ddTHH:mm:ss}\n");
                note.Write(text);
                break;
            }
            catch (IOException) when (File.Exists(Path.Combine(info, chosen + ".trashinfo")))
            {
                chosen = $"{stem} {n}{ext}";
            }
        }

        string target = Path.Combine(files, chosen);
        try
        {
            if (Directory.Exists(full))
                Directory.Move(full, target);
            else
                File.Move(full, target);
        }
        catch
        {
            File.Delete(Path.Combine(info, chosen + ".trashinfo"));
            throw;
        }
        return target;
    }

    /// <summary>A path as the specification writes it: percent-encoded, slashes kept.</summary>
    static string Escape(string path) => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
}
