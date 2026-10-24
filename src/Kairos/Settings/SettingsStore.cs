using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairos.Settings;

public static class SettingsStore
{
    /// <summary>%AppData%\Kairos; KAIROS_DATA points a development run (renders of the project page) at another folder.</summary>
    public static string AppDataDir { get; } = Environment.GetEnvironmentVariable("KAIROS_DATA") is { Length: > 0 } dev
        ? dev
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.Name);

    public static string DefaultPath { get; } = Path.Combine(AppDataDir, "settings.json");

    /// <summary>
    /// A run with a data folder of its own (KAIROS_DATA: a test or development run): it never touches what belongs to the
    /// installed Kairos outside that folder (Windows's start-up entry, the installer's language).
    /// </summary>
    public static bool OwnDataFolder { get; } = !WritesSystemSettings(Environment.GetEnvironmentVariable("KAIROS_DATA"));

    /// <summary>Whether a run may write Windows's start-up entry and the installer's language (<see cref="OwnDataFolder"/>: no).</summary>
    public static bool WritesSystemSettings(string? dataFolderVariable) => string.IsNullOrEmpty(dataFolderVariable);

    static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new TolerantEnumConverterFactory());
        return o;
    }

    public static AppSettings Load(string path)
    {
        AppSettings settings;
        if (!File.Exists(path))
        {
            settings = new AppSettings();
            settings.General.Language = AppSettings.DefaultLanguage(System.Globalization.CultureInfo.CurrentUICulture);
            settings.Audio.Layout = AudioLayout.Separate; // a new install: every source on its own row and track
            settings.Video.Auto = true; // and what the hardware records best with
            settings.Output.FreeLocalCopies = true; // in a cloud folder, the disk keeps only what is not uploaded yet
        }
        else
        {
            string json = ReadShared(path);
            try
            {
                settings = Deserialize(json); // with what an older Kairos's file needs (its one app)
                // 1.0.1 wrote no version: its file is version 1 (the property starts at the current one for settings made
                // here, so a 1.0.1 file read as current was never brought up to date).
                if (!Holds(json, "Version"))
                    settings.Version = 1;
                settings.Upgrade(autoInFile: Holds(json, "Video", "Auto"));
            }
            catch (JsonException)
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "settings.bad.json"), json);
                settings = new AppSettings();
            }
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// Whether a settings file says <paramref name="path"/> at all, any case ("Version"; "Video", "Auto"): 1.0.1's says
    /// neither.
    /// </summary>
    static bool Holds(string json, params string[] path)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var at = doc.RootElement;
        foreach (string name in path)
        {
            if (at.ValueKind != JsonValueKind.Object)
                return false;
            var found = at.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (found.Value.ValueKind == JsonValueKind.Undefined)
                return false;
            at = found.Value;
        }
        return true;
    }

    /// <summary>
    /// Reads without blocking a concurrent save (which replaces the file) and retries briefly while
    /// something else (an antivirus scan, the other Kairos process) holds it. Falling back to defaults here
    /// would let the next Apply overwrite the user's real settings, so a lasting lock still throws.
    /// </summary>
    static string ReadShared(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 20 && File.Exists(path))
            {
                Thread.Sleep(50);
            }
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// <paramref name="change"/> made to the settings as saved, and saved: an engine's own change (a game told about
    /// Borderless, a mixer set up), never its whole copy, which can be older than the file (a Save whose "apply" never
    /// reached it: writing that copy undid the user's change).
    /// </summary>
    public static void Update(string path, Action<AppSettings> change)
    {
        var settings = Load(path);
        change(settings);
        settings.Normalize();
        Save(path, settings);
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, Options);

    /// <summary>A deep copy, as saved and read back.</summary>
    public static AppSettings Copy(AppSettings settings) => Deserialize(Serialize(settings));

    public static AppSettings Deserialize(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        // A file from before 1.1: its one app becomes the first of the apps.
        if (s.Capture is { Apps: null or [], Window.Length: > 0 } c)
            c.Apps = [c.Window];
        s.Normalize();
        return s;
    }

    /// <summary>Reads enums as strings and maps unknown names to the enum's first member.</summary>
    sealed class TolerantEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type t) => t.IsEnum;

        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) =>
            (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(t))!;
    }

    sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse(reader.GetString(), ignoreCase: true, out T value) &&
                Enum.IsDefined(value))
                return value;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int n) &&
                Enum.IsDefined(typeof(T), n))
                return (T)Enum.ToObject(typeof(T), n);
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
