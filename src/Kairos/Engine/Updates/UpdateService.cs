using Velopack;
using Velopack.Sources;

namespace Kairos.Engine.Updates;

/// <summary>A newer version and its release notes (carried in the update feed).</summary>
public sealed record UpdateFound(string Version, string? Notes);

/// <summary>What the update service needs from Velopack (a stand-in in tests).</summary>
public interface IUpdater
{
    /// <summary>The newer version available, or null.</summary>
    Task<UpdateFound?> Check();
    Task Download(Action<int> progress, CancellationToken ct);
    /// <summary>Starts Velopack's updater, which waits for Kairos to exit, applies the update and starts Kairos again.</summary>
    void ApplyAndExit(string[] restartArgs);
}

/// <summary>
/// Kairos's updates, in the engine: looks for a newer version, says so once, downloads it when the user presses Update
/// and restarts into it (never during a recording). An important one (<see cref="UpdateNotes"/>) also downloads by itself
/// and goes in at the next start (<see cref="PendingImportant"/>). Failures are logged, never shown, and tried again the
/// next day.
/// </summary>
public sealed class UpdateService(IUpdater? updater, string seenFile, bool localSource)
{
    volatile UpdateStatus? _status;
    volatile bool _downloaded;
    /// <summary>
    /// An important update's download, started by the check itself (true once it is in Velopack's folder). Update now
    /// waits for it instead of starting a second one; a failed one is tried again with the next day's check.
    /// </summary>
    volatile Task<bool>? _fetch;
    DateTime? _lastReminded;
    /// <summary>A usual update found and not told yet: told once, when no game is in front (then kept in update-seen.txt).</summary>
    volatile bool _notifyPending;
    /// <summary>One check at a time (the daily one and the window's "check now" could meet on Velopack's lock).</summary>
    int _checking;
    int _looksDone;
    volatile LookResult _lastLook;

    /// <summary>The looks so far (<see cref="UpdateLook"/>): how the latest ended, for General's "Check now".</summary>
    public UpdateLook Look => new(updater != null, Volatile.Read(ref _checking) == 1, Volatile.Read(ref _looksDone), _lastLook);

    /// <summary>The longest a quiet download may take before it is given up (tried again with the next day's check).</summary>
    static readonly TimeSpan FetchLimit = TimeSpan.FromMinutes(30);

    /// <summary>The version an important update downloaded by itself is, beside update-seen.txt (read at the next start).</summary>
    static string ImportantFile(string dir) => Path.Combine(dir, "update-important.txt");
    static string TriedFile(string dir) => ImportantFile(dir) + ".tried";

    /// <summary>Kairos is the newer version now: the notes about the important update go.</summary>
    public static void ForgetImportant(string appDataDir)
    {
        File.Delete(ImportantFile(appDataDir));
        File.Delete(TriedFile(appDataDir));
    }

    /// <summary>
    /// An update to tell about now (the engine adds: no game in front): an important one each day until it is in, a usual
    /// one once.
    /// </summary>
    public bool RemindDue(DateTime now) => _status is { State: UpdateState.Available } s
        && (s.Important ? UpdateRules.RemindDue(_lastReminded, now) : _notifyPending);

    /// <summary>The notice showed: a usual update is never told again, an important one tomorrow.</summary>
    public void Reminded(DateTime now)
    {
        _lastReminded = now;
        if (!_notifyPending || _status is not { } status)
            return;
        _notifyPending = false;
        try
        {
            File.AppendAllLines(seenFile, [status.Version]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ObsLog.Warn("update seen: " + e.Message);
        }
    }

    public UpdateStatus? Status => _status;

    public DateTime? LastCheck { get; private set; }

    /// <summary>Off (a development build, not installed by the Setup).</summary>
    public bool Available => updater != null;

    /// <summary>The service for this Kairos: Velopack's when installed, from GitHub or a local test folder.</summary>
    public static UpdateService ForThisInstall(string appDataDir)
    {
        string seen = Path.Combine(appDataDir, "update-seen.txt");
        if (App.InstallPaths.Root == null)
            return new UpdateService(null, seen, localSource: false);
        string? folder = UpdateRules.LocalFeed(appDataDir);
        return new UpdateService(new VelopackUpdater(Manager(folder)), seen, folder != null);
    }

    /// <summary>Velopack's manager: GitHub's releases, or a local test folder named in the settings folder.</summary>
    static UpdateManager Manager(string? localFolder) => localFolder != null
        ? new UpdateManager(localFolder)
        : new UpdateManager(new GithubSource(UpdateRules.Repository, null, prerelease: AppInfo.Version.Contains('-')));

    /// <param name="language">The user's language, for the card's lines.</param>
    public async Task Check(DateTime now, string language = "en")
    {
        if (updater == null || Interlocked.Exchange(ref _checking, 1) == 1)
            return;
        LastCheck = now;
        _lastLook = LookResult.None; // this look's own ending, never the last one's
        try
        {
            if (await updater.Check().ConfigureAwait(false) is not { } found)
            {
                _lastLook = LookResult.UpToDate;
                ObsLog.Info("update check: up to date");
                return;
            }
            _lastLook = LookResult.Found;
            string version = found.Version;
            ObsLog.Info($"update check: {version} available");
            if (_status?.Version != version)
            {
                _downloaded = false;
                _fetch = null;
                _status = new UpdateStatus(version, UpdateState.Available, 0, UpdateRules.NotesUrl(version, localSource),
                    UpdateNotes.IsImportant(found.Notes), UpdateNotes.Summary(found.Notes, language));
            }
            if (_status.Important)
            {
                // Downloaded quietly (the offer stays as it is) for the next start; told each day instead (RemindDue).
                if (_fetch is not { } fetch || fetch.IsCompleted && !fetch.Result)
                    _fetch = Fetch(version);
                await _fetch.ConfigureAwait(false);
                return;
            }
            if (UpdateRules.ShouldNotify(version, ReadSeen()))
                _notifyPending = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _lastLook = _lastLook == LookResult.Found ? LookResult.Found : LookResult.Failed; // a found one that failed to fetch stays found
            ObsLog.Warn("update check: " + e.Message);
        }
        finally
        {
            Interlocked.Increment(ref _looksDone);
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>
    /// An important update's quiet download, noted so the next start puts it in before anything records (it never goes in
    /// mid-session by itself). False when it failed.
    /// </summary>
    async Task<bool> Fetch(string version)
    {
        string dir = Path.GetDirectoryName(seenFile)!;
        try
        {
            using var limit = new CancellationTokenSource(FetchLimit);
            // Its progress shows only if the user pressed Update meanwhile (the offer stays as it is otherwise).
            await updater!.Download(p =>
            {
                if (_status is { State: UpdateState.Downloading } s)
                    _status = s with { Percent = p };
            }, limit.Token).ConfigureAwait(false);
            // Put in at a start once only: one that already failed there waits for the user's Update (no restart at every login).
            if (!TriedAlready(dir, version))
                File.WriteAllText(ImportantFile(dir), version);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ObsLog.Warn("update download (background): " + e.Message);
            return false;
        }
    }

    static bool TriedAlready(string dir, string version)
    {
        try
        {
            return File.Exists(TriedFile(dir)) && File.ReadAllText(TriedFile(dir)).Trim() == version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// At a start, before anything records: the important update downloaded earlier, when Velopack still has it waiting
    /// (<paramref name="pendingVersion"/>); null otherwise.
    /// </summary>
    public static string? PendingImportant(string appDataDir, string? pendingVersion)
    {
        try
        {
            string file = ImportantFile(appDataDir);
            return pendingVersion != null && File.Exists(file) && File.ReadAllText(file).Trim() == pendingVersion ? pendingVersion : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// At a start (Program, before the engine): an important update downloaded earlier goes in now, and Kairos starts again
    /// into it with <paramref name="restartArgs"/>. True when Velopack took over (this process must end). Tried once: the
    /// note is moved aside first, so a failing update never loops.
    /// </summary>
    public static bool ApplyPendingImportant(string appDataDir, string[] restartArgs)
    {
        // A usual start: one look for the note, Velopack untouched.
        if (App.InstallPaths.Root == null || !File.Exists(ImportantFile(appDataDir)))
            return false;
        try
        {
            var manager = Manager(UpdateRules.LocalFeed(appDataDir));
            if (manager.UpdatePendingRestart is not { } pending || PendingImportant(appDataDir, pending.Version.ToString()) == null)
                return false;
            File.Move(ImportantFile(appDataDir), TriedFile(appDataDir), overwrite: true);
            // Silent (no Velopack window at login): Update.exe waits for this process to end, puts it in and starts Kairos.
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, restartArgs);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false; // the old version starts; the offer stays
        }
    }

    /// <summary>Downloads the update found; when it goes in is the engine's call afterwards (<see cref="Settle"/>).</summary>
    public async Task Download()
    {
        if (updater == null || _status is not { State: UpdateState.Available or UpdateState.Failed } status)
            return;
        _status = status with { State = UpdateState.Downloading, Percent = 0 };
        try
        {
            // The quiet download of an important update, when there is one (waited for if still under way).
            if (_fetch is not { } fetch || !await fetch.ConfigureAwait(false))
                await updater.Download(p => _status = _status! with { Percent = p }, CancellationToken.None).ConfigureAwait(false);
            _status = _status! with { Percent = 100 };
            _downloaded = true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ObsLog.Warn("update download: " + e.Message);
            _status = _status! with { State = UpdateState.Failed };
        }
    }

    /// <summary>
    /// Hands over to Velopack's updater, which waits for Kairos to exit; true when it took over (Kairos then leaves). False
    /// when it could not start: Kairos keeps running and the bar says the update failed. <paramref name="openWindow"/>:
    /// Kairos comes back with its window (the user pressed Update there); otherwise only in the tray.
    /// </summary>
    public bool Apply(bool openWindow)
    {
        try
        {
            if (updater == null)
                return false;
            updater.ApplyAndExit(openWindow ? [] : ["--background"]);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ObsLog.Warn("update apply: " + e.Message);
            _downloaded = false;
            if (_status is { } status)
                _status = status with { State = UpdateState.Failed };
            return false;
        }
    }

    /// <summary>
    /// On the engine's thread, where recordings start and stop: true when the downloaded update goes in now; otherwise it
    /// waits for the recording or the window (asked again on the tray's tick and when a recording ends).
    /// </summary>
    public bool Settle(bool recording, bool windowBusy, bool gaming = false)
    {
        if (!_downloaded || _status is not { } status)
            return false;
        _status = status with { State = UpdateRules.WhenDownloaded(recording, windowBusy, gaming) };
        return _status.State == UpdateState.Ready;
    }

    HashSet<string> ReadSeen()
    {
        try
        {
            return File.Exists(seenFile) ? [.. File.ReadAllLines(seenFile).Select(l => l.Trim()).Where(l => l.Length > 0)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    sealed class VelopackUpdater(UpdateManager manager) : IUpdater
    {
        UpdateInfo? _found;

        public async Task<UpdateFound?> Check()
        {
            _found = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            return _found is { } f ? new UpdateFound(f.TargetFullRelease.Version.ToString(), f.TargetFullRelease.NotesMarkdown) : null;
        }

        public Task Download(Action<int> progress, CancellationToken ct) =>
            _found == null ? Task.CompletedTask : manager.DownloadUpdatesAsync(_found, progress, ct);

        public void ApplyAndExit(string[] restartArgs) =>
            manager.WaitExitThenApplyUpdates(_found?.TargetFullRelease, silent: true, restart: true, restartArgs);
    }
}
