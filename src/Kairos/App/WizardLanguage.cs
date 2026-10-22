using Microsoft.Win32;

namespace Kairos.App;

/// <summary>
/// Kairos's language where the install wizard looks for its previous one (Inno Setup's UsePreviousLanguage reads the
/// key of its AppId, "Kairos.Wizard"): installing over Kairos then opens in Kairos's language, and the wizard sends a
/// language only when the user picks another. The key has no DisplayName, so Apps & features does not list it.
/// </summary>
public static class WizardLanguage
{
    /// <summary>The key of this install's wizard: its package id (Kairos, or KairosTest for a test build) + ".Wizard_is1".</summary>
    public static string DefaultKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + PackageId() + ".Wizard_is1";
    public const string ValueName = "Inno Setup: Language";

    /// <summary>The wizard's name for a Kairos language (its [Languages] names carry no dash).</summary>
    public static string InnoName(string code) => code.Replace("-", "", StringComparison.Ordinal);

    public static void Remember(string code, string? keyPath = null)
    {
        string name = InnoName(code);
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath ?? DefaultKey);
        if (key.GetValue(ValueName) as string != name)
            key.SetValue(ValueName, name);
    }

    /// <summary>On uninstall: the key goes (none is fine).</summary>
    public static void Forget(string? keyPath = null) =>
        Registry.CurrentUser.DeleteSubKeyTree(keyPath ?? DefaultKey, throwOnMissingSubKey: false);

    /// <summary>Velopack's id for this install (a test install has its own), or Kairos's name when it has none (a dev run).</summary>
    static string PackageId()
    {
        try
        {
            return Velopack.Locators.VelopackLocator.Current?.AppId is { Length: > 0 } id ? id : AppInfo.Name;
        }
        catch (Exception)
        {
            return AppInfo.Name; // not installed by Velopack
        }
    }
}
