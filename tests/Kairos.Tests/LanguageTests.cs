using System.Globalization;
using System.Text.RegularExpressions;
using Kairos.App;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>Kairos's languages: each one complete with its blanks in place, its plurals right, and Windows' own picked first.</summary>
public static class LanguageTests
{
    static readonly Regex Blank = new(@"\{\d+(:[^}]*)?\}", RegexOptions.CultureInvariant);

    static string Blanks(string words) => string.Join(",", Blank.Matches(words).Select(m => m.Value).Order());

    static string Prefix(string key) => key.LastIndexOf('.') is var dot and >= 0 ? key[..dot] : key;

    /// <summary>Runs <paramref name="test"/> in another language, then puts the language back.</summary>
    internal static void WithLanguage(string code, Action test)
    {
        string saved = Text.Language;
        try
        {
            Text.Language = code;
            test();
        }
        finally
        {
            Text.Language = saved;
        }
    }

    [Test]
    public static void Every_language_has_every_text_with_the_same_blanks()
    {
        var english = Text.Load("en");
        var counted = english.Keys.Where(k => k.EndsWith(".other", StringComparison.Ordinal)).Select(Prefix).ToHashSet();
        var problems = new List<string>();
        foreach (var language in Text.Languages)
        {
            var words = Text.Load(language.Code);
            foreach (var (key, en) in english.Where(e => !counted.Contains(Prefix(e.Key))))
            {
                if (!words.TryGetValue(key, out var w) || string.IsNullOrWhiteSpace(w))
                    problems.Add($"{language.Code}: {key} missing");
                else if (Blanks(w) != Blanks(en))
                    problems.Add($"{language.Code}: {key} blanks differ");
            }
            // Numbered words: every form the language's rule gives; the number shows in a form used for more than one
            // number (Russian "one" is 21 and 31 too), while a form for one number alone may say it in words (Arabic).
            var numbers = Enumerable.Range(0, 250).GroupBy(language.FormFor).ToDictionary(g => g.Key, g => g.Count());
            foreach (string bare in counted)
                foreach (var (form, used) in numbers)
                {
                    string key = $"{bare}.{form}";
                    if (!words.TryGetValue(key, out var w) && !words.TryGetValue($"{bare}.other", out w))
                        problems.Add($"{language.Code}: {key} missing");
                    else if (used > 1 && !w.Contains("{0}", StringComparison.Ordinal))
                        problems.Add($"{language.Code}: {key} has no number");
                }
            foreach (string key in words.Keys.Where(k => !english.ContainsKey(k) && !counted.Contains(Prefix(k))))
                problems.Add($"{language.Code}: {key} is not one of Kairos's texts");
        }
        Assert.True(problems.Count == 0, string.Join("; ", problems.Take(30)) + (problems.Count > 30 ? $" … {problems.Count} in all" : ""));
    }

    /// <summary>A language that is not English says most things in its own words (brand names and formats stay as they are).</summary>
    [Test]
    public static void Every_language_is_translated_not_copied()
    {
        var english = Text.Load("en");
        foreach (var language in Text.Languages.Where(l => l.Code != "en"))
        {
            var words = Text.Load(language.Code);
            int same = english.Count(e => words.TryGetValue(e.Key, out var w) && w == e.Value && e.Value.Any(char.IsLetter) && e.Value.Length > 12);
            Assert.True(same <= english.Count / 25, $"{language.Code}: {same} texts are the English ones");
        }
    }

    [Test]
    public static void Numbers_take_each_languages_own_forms()
    {
        string FormsOf(string code, params int[] ns) => string.Join(",", ns.Select(n => Text.PluralForm(code, n)));
        Assert.Equal("one,few,many,many,one,few,many,many", FormsOf("ru", 1, 2, 5, 11, 21, 22, 112, 14), "Russian");
        Assert.Equal("one,one,other", FormsOf("pt-BR", 0, 1, 2), "Portuguese");
        Assert.Equal("other,other", FormsOf("zh-Hans", 1, 2), "Chinese");
        Assert.Equal("one,other,other", FormsOf("de", 1, 2, 0), "German");
        Assert.Equal("one,two,few,many,other", FormsOf("ar", 1, 2, 3, 11, 100), "Arabic");
        WithLanguage("ru", () =>
        {
            Assert.True(Text.Count("count.clips", 3) != Text.Count("count.clips", 5), "Russian: 3 and 5 read differently");
            Assert.True(Text.Count("count.clips", 3).Contains('3', StringComparison.Ordinal), Text.Count("count.clips", 3));
        });
    }

    [Test]
    public static void Windows_language_is_picked_when_kairos_speaks_it()
    {
        string Pick(string culture) => AppSettings.DefaultLanguage(new CultureInfo(culture));
        Assert.Equal("ar", Pick("ar-SA"), "Arabic");
        Assert.Equal("ru", Pick("ru-RU"), "Russian");
        Assert.Equal("es", Pick("es-MX"), "Spanish, Mexico");
        Assert.Equal("pt-BR", Pick("pt-PT"), "Portuguese");
        Assert.Equal("de", Pick("de-AT"), "German, Austria");
        Assert.Equal("zh-Hans", Pick("zh-CN"), "Simplified Chinese");
        Assert.Equal("en", Pick("zh-TW"), "Traditional Chinese: not yet, English");
        Assert.Equal("en", Pick("fr-FR"), "not one of Kairos's: English");
        Assert.Equal("en", Pick("en-GB"), "English, Britain");
    }

    [Test]
    public static void An_unknown_language_is_english_and_each_is_offered_in_its_own_name()
    {
        WithLanguage("xx", () => Assert.Equal(Text.Fallback, Text.Language, "unknown: English"));
        var settings = SettingsStore.Deserialize("""{"general":{"language":"xx"}}""");
        settings.Normalize();
        Assert.Equal(Text.Fallback, settings.General.Language, "a settings file with an unknown language");
        Assert.True(Text.Languages.All(l => l.Name.Length > 0) && Text.Languages.Select(l => l.Name).Distinct().Count() == Text.Languages.Count,
            "every language has its own name");
        Assert.True(Text.Languages.Count(l => l.RightToLeft) == 1 && Text.Languages.Single(l => l.RightToLeft).Code == "ar", "Arabic is written right to left");
    }

    [Test]
    public static void No_text_key_is_written_twice()
    {
        // A key written twice loads as its last value: the first meaning is lost without a word.
        foreach (var language in Text.Languages)
        {
            using var stream = typeof(Text).Assembly.GetManifestResourceStream($"Lang.{language.Code}.json")!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            var reader = new System.Text.Json.Utf8JsonReader(copy.ToArray(), new System.Text.Json.JsonReaderOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            var seen = new HashSet<string>();
            var twice = new List<string>();
            while (reader.Read())
                if (reader.TokenType == System.Text.Json.JsonTokenType.PropertyName && reader.CurrentDepth == 1 && !seen.Add(reader.GetString()!))
                    twice.Add(reader.GetString()!);
            Assert.Equal("", string.Join(", ", twice), $"{language.Code}: keys written twice");
        }
    }
}
