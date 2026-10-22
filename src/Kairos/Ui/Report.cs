using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kairos.Engine;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>
/// "Save a report": the newest logs, the settings, what the engine learnt about the PC and what it says now (its encoder,
/// size, dropped frames, what it could not capture), in one zip on the Desktop. It is sent to us, so nothing in it names
/// the user: their folder (C:\Users\name) and window titles are taken out, older logs included.
/// </summary>
public static class Report
{
    /// <summary>Writes the zip and returns its path.</summary>
    /// <param name="status">The engine's last status and what it found on the PC, as JSON (status.json), or null.</param>
    /// <param name="profile">The user's folder (tests name one).</param>
    public static string Save(string dataDir, string desktop, DateTime now, string? status = null, string? profile = null)
    {
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(desktop, $"Kairos report {now:yyyy-MM-dd HH-mm-ss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        string logs = Path.Combine(dataDir, "logs");
        // The six newest that say something. A running engine's log is listed as empty until it is closed, so its size is
        // read from the file itself.
        IEnumerable<FileInfo> newest = Directory.Exists(logs) ? new DirectoryInfo(logs).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc) : [];
        int added = 0;
        foreach (var log in newest)
            if (added < 6 && Add(zip, Read(log.FullName), "logs/" + log.Name, text => ObsLog.Redact(text, profile)))
                added++;
        foreach (string name in new[] { Path.GetFileName(SettingsStore.DefaultPath), App.HardwareState.FileName })
            Add(zip, Read(Path.Combine(dataDir, name)), name, text => CleanJson(text, profile));
        Add(zip, status, "status.json", text => CleanJson(text, profile));
        return path;
    }

    /// <summary>The file's text, or null when it is missing. Shared reads: the engine may be writing its log right now.</summary>
    static string? Read(string file)
    {
        if (!File.Exists(file))
            return null;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadToEnd();
    }

    /// <summary>Adds the text, cleaned, unless there is none; whether it did.</summary>
    static bool Add(ZipArchive zip, string? text, string entry, Func<string, string> clean)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        using var writer = new StreamWriter(zip.CreateEntry(entry, CompressionLevel.Optimal).Open());
        writer.Write(clean(text));
        return true;
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A JSON file without the user's folder or window titles (one that is not JSON loses the folder only).</summary>
    static string CleanJson(string json, string profile)
    {
        try
        {
            if (JsonNode.Parse(json) is { } node)
            {
                DropTitles(node);
                json = node.ToJsonString(Indented);
            }
        }
        catch (JsonException)
        {
        }
        return ObsLog.Redact(json, profile);
    }

    /// <summary>Every libobs window string ("title:class:exe") in <paramref name="node"/> keeps its class and exe only.</summary>
    static void DropTitles(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, child) in o.ToList())
                    if (Untitled(child) is { } s)
                        o[key] = s;
                    else
                        DropTitles(child);
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                    if (Untitled(a[i]) is { } s)
                        a[i] = s;
                    else
                        DropTitles(a[i]);
                break;
        }
    }

    static string? Untitled(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) && s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && App.WindowTarget.Title(s) != null
            ? s[s.IndexOf(':')..]
            : null;
}
