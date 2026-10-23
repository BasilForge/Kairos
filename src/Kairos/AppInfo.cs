using System.Reflection;
using System.Text.RegularExpressions;

namespace Kairos;

/// <summary>Every name the app registers with Windows, in one place so a rename is a single edit.</summary>
public static class AppInfo
{
    public const string Name = "Kairos";
    public const string EngineMutex = @"Local\Kairos.Engine";

    /// <summary>
    /// The engine's one-at-a-time lock: a test run with a pipe of its own (KAIROS_PIPE) has a lock of its own too, so it
    /// runs beside the installed Kairos instead of handing over to it.
    /// </summary>
    public static string EngineLock(string? pipe) => string.IsNullOrEmpty(pipe) ? EngineMutex : $"{EngineMutex}.{pipe}";
    public const string SettingsMutex = @"Local\Kairos.Settings";
    public const string SettingsShowEvent = @"Local\Kairos.Settings.Show";
    /// <summary>
    /// The engine's pipe in this Windows session, like its lock (Local\): pipe names are shared by the whole PC, so with
    /// one name a second person signed in at the same time had a Kairos whose window could not reach its engine.
    /// </summary>
    public static string PipeName { get; } = PipeFor(System.Diagnostics.Process.GetCurrentProcess().SessionId);

    public static string PipeFor(int session) => $"Kairos.{session}";
    public const string RunValueName = "Kairos";
    /// <summary>Kairos's Discord server: help, problems and ideas.</summary>
    public const string DiscordUrl = "https://discord.gg/YWgaS6JRzF";

    /// <summary>The full version, a prerelease part included ("1.0.0-beta.1"), without the build details after "+".</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>A version as people read it: "1.0.0-beta.3" is "1.0.0 beta 3" in their language; any other stays as it is.</summary>
    public static string Display(string version) => Regex.Match(version, @"^(\d+\.\d+\.\d+)-beta\.(\d+)$") is { Success: true } m
        ? App.Text.Format("version.beta", m.Groups[1].Value, m.Groups[2].Value)
        : version;
}
