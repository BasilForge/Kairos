namespace Kairos.Engine.Updates;

/// <summary>Where an update stands, as the settings window's bar shows it.</summary>
public enum UpdateState { None, Available, Downloading, Ready, WaitingForRecording, Failed, WaitingForWindow, WaitingForGame }

/// <param name="NotesUrl">The release's page ("What's new"); null when the updates come from a local folder.</param>
/// <param name="Important">Marked important in its notes (<see cref="UpdateNotes"/>): a card on the Status page, told daily.</param>
/// <param name="Summary">The card's lines, in the user's language when the notes have them.</param>
public sealed record UpdateStatus(string Version, UpdateState State, int Percent, string? NotesUrl, bool Important = false,
    IReadOnlyList<string>? Summary = null)
{
    /// <summary>A newer Kairos is offered (found, downloading, waiting or failed).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Offered => State != UpdateState.None;
}

/// <summary>How the latest look for an update ended.</summary>
public enum LookResult { None, UpToDate, Found, Failed }

/// <summary>
/// The looks for an update, for General's "Check now": whether this install can look at all (an installed Kairos), one
/// running, how many ended so far (the window shows the one after its press) and how the latest ended.
/// </summary>
public sealed record UpdateLook(bool Works, bool Running, int Done, LookResult Last);

/// <summary>The decisions about updates, apart from the downloading itself.</summary>
public static class UpdateRules
{
    public const string Repository = "https://github.com/BasilForge/Kairos";

    /// <summary>The first look waits this long after the replay buffer is up, so a start-up is never slowed by it.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>Time to look for an update: turned on, a minute after the replay buffer first ran, then once a day.</summary>
    public static bool ShouldCheck(bool enabled, DateTime? bufferActiveSince, DateTime? lastCheck, DateTime now) =>
        enabled && bufferActiveSince is { } since && now - since >= FirstDelay && (lastCheck is not { } last || now - last >= Interval);

    /// <summary>Each new version gets one notice, ever.</summary>
    public static bool ShouldNotify(string version, IReadOnlySet<string> seen) => !seen.Contains(version);

    /// <summary>An important update is told again each day until it is in (the engine waits for no game in front).</summary>
    public static bool RemindDue(DateTime? lastReminded, DateTime now) => lastReminded is not { } last || now - last >= Interval;

    /// <summary>
    /// A newer Kairos than the one that ran last: said once ("Updated to …"). A first install is not an update; 1.0.x kept
    /// no last version, so its settings file says Kairos ran before.
    /// </summary>
    public static bool JustUpdated(string? lastRun, string current, bool hadSettings = false) =>
        lastRun != current && (lastRun != null || hadSettings);

    /// <summary>
    /// A downloaded update goes in only when nothing is lost: it waits for a recording (or a clip being written) to end,
    /// for a game in front to be left (a restart there would drop the replay and the hotkeys mid-game), and for the
    /// settings window to be free (unsaved changes, the editor, sharing).
    /// </summary>
    public static UpdateState WhenDownloaded(bool recording, bool windowBusy, bool gaming = false) =>
        recording ? UpdateState.WaitingForRecording : gaming ? UpdateState.WaitingForGame
        : windowBusy ? UpdateState.WaitingForWindow : UpdateState.Ready;

    /// <summary>The window says whether it is in use with each status question (twice a second); a closed one stops asking.</summary>
    public static readonly TimeSpan WindowSilence = TimeSpan.FromSeconds(2);

    public static bool WindowBusy(bool saidBusy, DateTime askedAt, DateTime now) => saidBusy && now - askedAt < WindowSilence;

    public static string? NotesUrl(string version, bool localSource) => localSource ? null : $"{Repository}/releases/tag/v{version}";

    /// <summary>The local update folder named in Kairos's settings folder (testing before publishing), if it exists.</summary>
    public static string? LocalFeed(string appDataDir) => LocalSource(Path.Combine(appDataDir, "update-source.txt"));

    /// <summary>The local folder named in <paramref name="overrideFile"/> (testing before publishing), if it exists.</summary>
    public static string? LocalSource(string overrideFile)
    {
        try
        {
            return File.Exists(overrideFile) && File.ReadAllText(overrideFile).Trim() is { Length: > 0 } folder && IsLocalFolder(folder)
                   && Directory.Exists(folder)
                ? folder
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A full path on one of this PC's fixed drives: never a network folder (\\server\share or a mapped drive), which
    /// would hand updates to another machine, and which Windows would connect to (and sign in to) just to look.
    /// </summary>
    public static bool IsLocalFolder(string folder) => Path.IsPathFullyQualified(folder) && Replay.DiskReplayPlace.IsFixed(folder);
}
