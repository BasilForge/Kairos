using System.Collections.Concurrent;
using System.Text.Json;

namespace Kairos.App;

/// <summary>
/// The words Kairos shows (the tray, toasts and the settings window): one file per language in App/Lang, built into the
/// program. Only the language in use is read, and English fills any gap. Another language is one more file and one more
/// line in <see cref="Languages"/>.
/// </summary>
public static class Text
{
    /// <summary>
    /// A language: its code (its file's name), its name in itself, whether it is written right to left, and which form
    /// its words take after a number (see <see cref="Count"/>).
    /// </summary>
    public sealed record Lang(string Code, string Name, bool RightToLeft = false, Func<int, string>? Plural = null)
    {
        /// <summary>The form after <paramref name="n"/>; without a rule of its own, one (1) and other, as in English.</summary>
        public string FormFor(int n) => Plural?.Invoke(n) ?? (n == 1 ? "one" : "other");
    }

    /// <summary>Every language, in the order they are offered.</summary>
    public static IReadOnlyList<Lang> Languages { get; } =
    [
        // Formal Arabic: one (1), two (2, the dual on its own), few (3–10, and 103–110…: the plural), many (11–99, and
        // 111–199…: the singular with tanween) and other (100, 200…: the plain singular).
        new("ar", "العربية", RightToLeft: true, Plural: n => n switch
        {
            1 => "one", 2 => "two", _ when n % 100 is >= 3 and <= 10 => "few", _ when n % 100 >= 11 => "many", _ => "other",
        }),
        new("en", "English"),
        new("zh-Hans", "简体中文", Plural: _ => "other"),
        // Russian: one (1, 21, 31…), few (2–4, 22–24…) and many (5–20, 25–30…, 11–14).
        new("ru", "Русский", Plural: n => (n % 10, n % 100) switch { (1, not 11) => "one", (>= 2 and <= 4, < 12 or > 14) => "few", _ => "many" }),
        new("es", "Español"),
        new("pt-BR", "Português (Brasil)", Plural: n => n is 0 or 1 ? "one" : "other"),
        new("de", "Deutsch"),
    ];

    /// <summary>The language an unknown or missing one becomes.</summary>
    public const string Fallback = "en";

    static Lang? Find(string? code) => Languages.FirstOrDefault(l => l.Code == code);

    public static bool IsSupported(string? code) => Find(code) != null;

    static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> Loaded = new();
    static Lang _current = Find("ar")!;

    /// <summary>The language in use (an unknown code is <see cref="Fallback"/>); its file is read when a word is first asked for.</summary>
    public static string Language
    {
        get => _current.Code;
        set => _current = Find(value) ?? Find(Fallback)!;
    }

    /// <summary>Arabic itself (its own wordmark), not just any right-to-left language.</summary>
    public static bool IsArabic => _current.Code == "ar";

    /// <summary>Written right to left: the window, the toasts and the tray menu are mirrored.</summary>
    public static bool IsRightToLeft => _current.RightToLeft;

    static IReadOnlyDictionary<string, string> Words => Load(_current.Code);

    static IReadOnlyDictionary<string, string> English => Load(Fallback);

    /// <summary>A language's words, read once from the program (Lang.&lt;code&gt;.json).</summary>
    internal static IReadOnlyDictionary<string, string> Load(string code) => Loaded.GetOrAdd(code, c =>
    {
        using var stream = typeof(Text).Assembly.GetManifestResourceStream($"Lang.{c}.json")
            ?? throw new InvalidOperationException($"The {c} words are missing from the program.");
        var words = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        // A platform may reword what names its own system (Lang.<code>.linux.json: "Start with your desktop", "Trash").
        if (OperatingSystem.IsLinux() && typeof(Text).Assembly.GetManifestResourceStream($"Lang.{c}.linux.json") is { } overrides)
            using (overrides)
                foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(overrides) ?? [])
                    words[key] = value;
        return words;
    });

    public static string Get(string key) =>
        Words.TryGetValue(key, out var s) ? s : English.TryGetValue(key, out var en) ? en : key;

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>
    /// A number with its word in the right form, from "{key}.one/two/few/many/other" (the language's
    /// <see cref="Lang.Plural"/>; a form its file does not have is its .other).
    /// </summary>
    public static string Count(string key, int n)
    {
        string full = $"{key}.{_current.FormFor(n)}";
        return string.Format(Get(Words.ContainsKey(full) ? full : $"{key}.other"), n);
    }

    /// <summary>The form a language's words take after <paramref name="n"/>.</summary>
    internal static string PluralForm(string language, int n) => (Find(language) ?? Find(Fallback)!).FormFor(n);

    public static IEnumerable<string> AllKeys() => English.Keys;

    /// <summary>Keeps a left-to-right run (like "2560×1440") in order inside Arabic text.</summary>
    // WPF ignores embedding marks here, so a left-to-right mark on each side makes the numbers resolve left-to-right.
    public static string Ltr(string s) => IsRightToLeft ? "‎" + s + "‎" : s;

    /// <summary>"5:00" style duration ("1:02:05" from an hour on).</summary>
    public static string Duration(int seconds) => seconds >= 3600
        ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
        : $"{seconds / 60}:{seconds % 60:00}";
}
