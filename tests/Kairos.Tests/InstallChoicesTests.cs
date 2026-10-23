using Kairos.App;
using Kairos.Settings;

namespace Kairos.Tests;

/// <summary>The install wizard's choices (language, start with Windows) reach Kairos once, on its next start.</summary>
public static class InstallChoicesTests
{
    [Test]
    public static void Unknown_or_broken_lines_change_nothing()
    {
        var settings = new AppSettings();
        settings.General.Language = "ar";
        InstallChoices.Apply("language=xx\nstartWithWindows=maybe\n# a note\nsomething=1\n=\n", settings);
        Assert.Equal("ar", settings.General.Language, "a language Kairos does not have is ignored");
        Assert.Equal(true, settings.General.StartWithWindows, "not a yes or no: left as it was");
    }

    [Test]
    public static void The_choices_are_applied_once_and_their_file_goes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-choices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, InstallChoices.FileName), "language=pt-BR\r\nstartWithWindows=false\r\n");
            var settings = new AppSettings();
            settings.General.Language = "en";
            Assert.True(InstallChoices.ConsumeInto(settings, dir), "there was a wizard file");
            Assert.Equal("pt-BR", settings.General.Language, "its language");
            Assert.Equal(false, settings.General.StartWithWindows, "start with Windows was unticked");
            Assert.True(!File.Exists(Path.Combine(dir, InstallChoices.FileName)), "the file is gone after use");
            settings.General.Language = "es";
            Assert.True(!InstallChoices.ConsumeInto(settings, dir), "the next start finds nothing");
            Assert.Equal("es", settings.General.Language, "and changes nothing");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public static void The_wizard_names_match_the_installers_languages()
    {
        Assert.Equal("zhHans", WizardLanguage.InnoName("zh-Hans"), "chinese");
        Assert.Equal("ptBR", WizardLanguage.InnoName("pt-BR"), "portuguese");
        Assert.Equal("ar", WizardLanguage.InnoName("ar"), "arabic");
        Assert.Equal("fr", WizardLanguage.InnoName("fr"), "french");
    }

    [Test]
    public static void The_french_wizard_choice_reaches_the_app()
    {
        var settings = new AppSettings();
        settings.General.Language = "en";
        InstallChoices.Apply("language=fr\r\nstartWithWindows=false\r\n", settings);
        Assert.Equal("fr", settings.General.Language, "French selected in the wizard");
        Assert.Equal(false, settings.General.StartWithWindows, "the other choice is applied too");
    }

    [Test]
    public static void Every_installer_language_has_all_messages_and_the_same_placeholders()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "Kairos.sln")))
            root = root.Parent;
        string script = File.ReadAllText(Path.Combine(root!.FullName, "installer", "Kairos.iss"));
        var lines = script.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Dictionary<string, string> Messages(string language) => lines.Where(l => l.StartsWith(language + ".", StringComparison.Ordinal))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0][(language.Length + 1)..], p => p[1]);
        string Placeholders(string value) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(value, @"%[1-9n]")
            .Select(m => m.Value).Order());
        var english = Messages("en");
        foreach (var language in Text.Languages)
        {
            string name = WizardLanguage.InnoName(language.Code);
            Assert.True(lines.Any(l => l.StartsWith($"Name: \"{name}\";", StringComparison.Ordinal)), language.Code + ": offered by the wizard");
            var messages = Messages(name);
            Assert.Equal(language.Code, messages.GetValueOrDefault("LangCode"), language.Code + ": reaches the app");
            foreach (var (key, value) in english.Where(m => m.Key != "LangCode"))
            {
                Assert.True(messages.TryGetValue(key, out string? translated) && !string.IsNullOrWhiteSpace(translated), name + ": " + key);
                Assert.Equal(Placeholders(value), Placeholders(translated!), name + ": " + key + " placeholders");
            }
        }
    }

    [Test]
    public static void The_app_remembers_its_language_for_the_next_wizard()
    {
        string key = @"Software\Kairos-tests\" + Guid.NewGuid().ToString("N");
        try
        {
            WizardLanguage.Remember("pt-BR", key);
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("ptBR", k?.GetValue(WizardLanguage.ValueName) as string, "written");
            WizardLanguage.Forget(key);
            Assert.True(Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key) == null, "gone on uninstall");
            WizardLanguage.Forget(key); // twice: no throw
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Kairos-tests", throwOnMissingSubKey: false);
        }
    }
}
