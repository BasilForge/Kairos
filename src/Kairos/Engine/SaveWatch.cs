namespace Kairos.Engine;

/// <summary>
/// Makes a clip save that did not happen visible (it used to fail in silence): a press while instant replay is off,
/// paused, waiting for a game or just starting; a save OBS never finished writing; a file that came out empty or on a
/// full disk. Also says, now and then, that the clips' disk is nearly full. The engine shows what it reports (text keys).
/// </summary>
public sealed class SaveWatch
{
    /// <summary>What a save request found.</summary>
    public enum Request { Started, Off, Starting, Busy }

    /// <summary>Under this, a save is followed by a "low disk space" note.</summary>
    public const long LowDiskBytes = 10L << 30;

    /// <summary>Under this after a save, the disk ran out while the clip was written.</summary>
    public const long FullDiskBytes = 64L << 20;

    static readonly TimeSpan LowDiskEvery = TimeSpan.FromHours(6);

    DateTime? _since;
    TimeSpan _limit;
    DateTime _lowDiskSaid = DateTime.MinValue;

    /// <summary>How long writing a save may take before it counts as failed: 30 s plus the buffer at 40 MB/s (a slow disk).</summary>
    public static TimeSpan WriteLimit(long bufferBytes) => TimeSpan.FromSeconds(30 + Math.Max(0, bufferBytes) / (40.0 * 1024 * 1024));

    /// <summary>
    /// Why instant replay could not save: turned off, paused by the user, waiting for a game, or <paramref name="failed"/>
    /// to start (the encoder would not open, for one).
    /// </summary>
    public static string OffReason(bool enabled, bool paused, bool waiting, bool failed = false) =>
        !enabled ? "toast.notSaved.off" : paused ? "toast.notSaved.paused" : waiting ? "toast.notSaved.waiting"
        : failed ? "toast.notSaved.failed" : "toast.notSaved.off";

    /// <summary>A save was asked for: the reason to show (a text key), or null.</summary>
    public string? Requested(Request request, DateTime now, long bufferBytes, string? offReason)
    {
        switch (request)
        {
            case Request.Started:
                _since = now;
                _limit = WriteLimit(bufferBytes);
                return null;
            case Request.Off:
                return offReason ?? "toast.notSaved.off";
            case Request.Starting:
                return "toast.notSaved.starting";
            default:
                return null; // pressed again while one is being written: that one reports
        }
    }

    /// <summary>OBS says the clip is written: null when it looks right, else why it is not a clip.</summary>
    /// <param name="freeBytes">Free space left on the clip's disk, or -1 when unknown.</param>
    public string? Saved(long fileBytes, long freeBytes)
    {
        _since = null;
        if (fileBytes <= 0)
            return "toast.notSaved.write";
        if (freeBytes >= 0 && freeBytes < FullDiskBytes)
            return "toast.notSaved.disk";
        return null;
    }

    /// <summary>
    /// On the engine's tick: the reason, once, when a save was never finished: recording <paramref name="behind"/> a game
    /// (libobs writes a clip only once its encoder reaches the press, minutes late then), else a write that never ended.
    /// </summary>
    public string? Tick(DateTime now, bool behind = false)
    {
        if (_since is not { } since || now - since <= _limit)
            return null;
        _since = null;
        return behind ? "toast.notSaved.behind" : "toast.notSaved.write";
    }

    /// <summary>A save is being written (asked for, not reported yet).</summary>
    public bool Pending => _since != null;

    /// <summary>The pipeline was built again: the save it was writing is gone, and is not reported later as not written.</summary>
    public void Cancel() => _since = null;

    /// <summary>After a save: true when the disk is nearly full and that was not said in the last hours.</summary>
    public bool ShouldWarnLowDisk(long freeBytes, DateTime now)
    {
        if (freeBytes < 0 || freeBytes >= LowDiskBytes || now - _lowDiskSaid < LowDiskEvery)
            return false;
        _lowDiskSaid = now;
        return true;
    }

    /// <summary>Free space on the disk holding <paramref name="path"/>, or -1 when it cannot be read.</summary>
    public static long FreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }
}
