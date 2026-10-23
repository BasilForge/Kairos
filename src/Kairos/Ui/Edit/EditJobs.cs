using System.Text.RegularExpressions;
using Kairos.App;
using Kairos.Engine;
using Kairos.Engine.Edit;

namespace Kairos.Ui.Edit;

/// <summary>Writes an edit to <paramref name="partPath"/> and returns where it ended up.</summary>
public delegate string EditRender(EditProject p, string partPath, IProgress<double> progress, CancellationToken ct);

/// <summary>One save of an edit, running in the background.</summary>
public sealed class EditJob(string source, bool replace)
{
    readonly CancellationTokenSource _cts = new();
    double _progress;

    public string Source => source;
    public bool Replace => replace;
    /// <summary>0..1.</summary>
    public double Progress => Volatile.Read(ref _progress);
    /// <summary>Why it failed (empty when cancelled), or null.</summary>
    public string? Error { get; internal set; }
    /// <summary>The saved file, or null when it failed or was cancelled.</summary>
    public Task<string?> Task { get; internal set; } = System.Threading.Tasks.Task.FromResult<string?>(null);

    public event Action<double>? ProgressChanged;
    public event Action<string>? Completed;
    /// <summary>Why (empty when cancelled).</summary>
    public event Action<string>? Failed;

    internal CancellationToken Token => _cts.Token;

    public void Cancel() => _cts.Cancel();

    internal void Report(double value)
    {
        Volatile.Write(ref _progress, value);
        ProgressChanged?.Invoke(value);
    }

    internal void Complete(string path) => Completed?.Invoke(path);

    internal void Fail(string why)
    {
        Error = why;
        Failed?.Invoke(why);
    }
}

/// <summary>
/// Saves edits in the background: as a new copy beside the original ("… (edited)"), or replacing it — then only after
/// the edit is fully written: the players let go of the original, it goes to the Recycle Bin, and the edit takes its
/// name. A failure or a cancel leaves the original as it was and no half-written file.
/// </summary>
public sealed class EditJobs(EditRender render, Action<string> recycle)
{
    readonly List<EditJob> _running = [];

    /// <summary>A save's file is in place (on the save's thread): a cloud folder may keep it online only.</summary>
    public Action<string>? Saved { get; init; }

    /// <summary>A save is still running (asked twice a second: no copy of the list).</summary>
    public bool Busy
    {
        get
        {
            lock (_running)
                return _running.Count > 0;
        }
    }

    public IReadOnlyList<EditJob> Running
    {
        get
        {
            lock (_running)
                return _running.ToList();
        }
    }

    /// <param name="releaseSource">Run before the original is recycled (on the thread that started the job).</param>
    public EditJob Start(EditProject p, bool replaceOriginal, Action? releaseSource = null)
    {
        var job = new EditJob(p.Source, replaceOriginal);
        var context = SynchronizationContext.Current;
        void OnStarter(Action action)
        {
            if (context == null)
                action();
            else
                context.Send(_ => action(), null);
        }

        string dir = Path.GetDirectoryName(p.Source)!;
        string ext = Path.GetExtension(p.Source);
        string part = Path.Combine(dir, $".{Path.GetFileNameWithoutExtension(p.Source)}.{Guid.NewGuid():N}.edit{ext}");
        lock (_running)
            _running.Add(job);
        job.Task = System.Threading.Tasks.Task.Run(() =>
        {
            string? written = null;
            try
            {
                written = render(p, part, new Reporter(job, OnStarter), job.Token);
                job.Token.ThrowIfCancellationRequested();
                if (replaceOriginal)
                {
                    OnStarter(() => releaseSource?.Invoke());
                    recycle(p.Source);
                }
                string target = ClipOrganizer.Move(written, replaceOriginal ? p.Source : NewCopyPath(p.Source));
                written = null;
                Saved?.Invoke(target);
                OnStarter(() => job.Complete(target));
                return target;
            }
            catch (OperationCanceledException)
            {
                OnStarter(() => job.Fail(""));
                return null;
            }
            catch (Exception e) when (e is Engine.Share.ShareException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                OnStarter(() => job.Fail(e.Message));
                return null;
            }
            finally
            {
                foreach (string? leftover in new[] { written, part })
                    if (leftover != null)
                        TryDelete(leftover);
                lock (_running)
                    _running.Remove(job);
            }
        });
        return job;
    }

    public void CancelAll()
    {
        foreach (var job in Running)
            job.Cancel();
    }

    /// <summary>
    /// The process is ending: stops every save and waits (up to <paramref name="timeout"/>) for each to remove its part
    /// file, so nothing half written stays in the clip folder. Call it after the window's dispatcher stopped.
    /// </summary>
    /// <returns>Whether they all stopped in time.</returns>
    public bool CancelAndWait(TimeSpan timeout)
    {
        var running = Running;
        foreach (var job in running)
            job.Cancel();
        try
        {
            return Task.WaitAll([.. running.Select(j => (Task)j.Task)], timeout);
        }
        catch (AggregateException)
        {
            return true; // they ended (a failure is still an end)
        }
    }

    /// <summary>A save's part file: ".&lt;name&gt;.&lt;32 hex&gt;.edit.&lt;ext&gt;", maybe with the exporter's own ".&lt;32 hex&gt;.part".</summary>
    static readonly Regex Leftover = new(@"^\..+\.[0-9a-f]{32}\.edit\.[A-Za-z0-9]+(\.[0-9a-f]{32}\.part)?$", RegexOptions.CultureInvariant);

    /// <summary>Removes part files a save left when Kairos was ended mid-save (run once when the window opens).</summary>
    public static void SweepLeftovers(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (string file in Directory.EnumerateFiles(folder, ".*", options))
            if (Leftover.IsMatch(Path.GetFileName(file)))
                TryDelete(file);
    }

    /// <summary>
    /// "Game 2026-10-16 18-00-00 (edited).mp4" beside the original ("(معدّل)" in Arabic). An edited copy edited again keeps
    /// one mark: its number ("(edited) (2)") and its mark in any language come off first (the save numbers a taken name).
    /// </summary>
    public static string NewCopyPath(string original)
    {
        string name = Copies.Replace(Path.GetFileNameWithoutExtension(original), "");
        foreach (var language in Text.Languages)
            if (Text.Load(language.Code).TryGetValue("editor.edited", out string? word) && name.EndsWith($" ({word})", StringComparison.Ordinal))
            {
                name = name[..^(word.Length + 3)];
                break;
            }
        return Path.Combine(Path.GetDirectoryName(original)!, $"{name} ({Text.Get("editor.edited")}){Path.GetExtension(original)}");
    }

    static readonly System.Text.RegularExpressions.Regex Copies = new(@" \(\d+\)$");

    static void TryDelete(string path) => Engine.Share.ClipTranscoder.TryDelete(path);

    sealed class Reporter(EditJob job, Action<Action> onStarter) : IProgress<double>
    {
        int _percent = -1;

        public void Report(double value)
        {
            int percent = (int)(value * 100);
            if (percent == _percent)
                return;
            _percent = percent;
            onStarter(() => job.Report(value));
        }
    }
}
