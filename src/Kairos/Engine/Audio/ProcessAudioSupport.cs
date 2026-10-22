namespace Kairos.Engine.Audio;

/// <summary>
/// Whether Windows can give one app's own sound (process loopback): Windows 10 version 2004 (build 19041, May 2020)
/// and later. Separate sources, an app's or a game's sound alone, and the "Other sounds" row all need it; on an older
/// Windows all of the PC's sound is recorded together.
/// </summary>
public static class ProcessAudioSupport
{
    public const int FirstBuild = 19041;

    /// <summary>This Windows (the tests set it to try an older one).</summary>
    public static bool Available { get; internal set; } = Environment.OSVersion.Version.Build >= FirstBuild;
}
