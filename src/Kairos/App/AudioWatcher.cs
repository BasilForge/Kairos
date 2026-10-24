using Kairos.Engine.Audio;
using Kairos.Settings;

namespace Kairos.App;

/// <summary>The game the Game row follows: the app in front while it fills its screen, kept while it runs.</summary>
public static class GameFollow
{
    public static uint Next(uint following, bool fullscreen, uint front, IReadOnlySet<uint> running, uint self)
    {
        if (fullscreen && front != 0 && front != self)
            return front;
        return following != 0 && running.Contains(following) ? following : 0;
    }
}

/// <summary>
/// Separate sources: every 2 s, which processes each row captures (<see cref="AudioPartition"/>), worked out off the
/// window thread and handed to <paramref name="apply"/> on it. Only made for that layout: nothing runs otherwise.
/// </summary>
/// <param name="captured">Apps: the chosen app's window recorded now.</param>
/// <param name="apply">Each row's processes, and the named rows heard on a device row instead (row id, device id).</param>
public sealed class AudioWatcher(Func<AppSettings> settings, Action<IReadOnlyDictionary<string, uint[]>, IReadOnlyDictionary<string, string>> apply,
    Func<string?> captured)
    : IDisposable
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    uint _game;

    /// <summary>The game the Game row follows now (0: none).</summary>
    public uint GamePid => _game;
    bool _busy;

    public void Start()
    {
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    void Tick()
    {
        if (_busy)
            return;
        _busy = true;
        var s = settings();
        var (fullscreen, front) = GameWatcher.Foreground(); // cheap, and only right on this thread
        string? window = captured();
        uint self = (uint)Environment.ProcessId, following = _game;
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            var processes = ProcessList.All();
            var running = processes.Select(p => p.Pid).ToHashSet();
            uint game = Chosen(s, processes, window) ?? GameFollow.Next(following, fullscreen, front, running, self);
            var (withSound, onOutputs) = CoreAudio.Sound(s.Audio.Sources);
            var heardOn = new Dictionary<string, string>();
            var assignment = AudioPartition.Assign(processes, withSound, new PartitionInput(game, s.Audio.Sources, ProcessList.SelfName, onOutputs),
                ProcessList.StartedAt, heardOn);
            return (game, assignment, heardOn);
        }).ContinueWith(t =>
        {
            ui?.Post(_ =>
            {
                _busy = false;
                if (t.IsCompletedSuccessfully)
                {
                    _game = t.Result.game;
                    apply(t.Result.assignment, t.Result.heardOn);
                }
                else if (t.Exception is { } e)
                    Engine.ObsLog.Warn($"audio watcher: {e.GetBaseException().Message}");
            }, null);
        });
    }

    /// <summary>The game named outright: the app recorded in Apps mode, or an app pinned on the Game row.</summary>
    static uint? Chosen(AppSettings s, IReadOnlyList<ProcInfo> processes, string? captured)
    {
        string? exe = s.Audio.Sources.FirstOrDefault(r => r.Kind == AudioSourceKind.Game)?.Target is { Length: > 0 } pinned ? pinned
            : s.Capture.Mode == CaptureMode.Window ? WindowTarget.ExeName(captured) : null;
        if (exe == null)
            return null;
        return processes.FirstOrDefault(p => p.Name.Equals(exe, StringComparison.OrdinalIgnoreCase)).Pid;
    }

    public void Dispose() => _timer.Dispose();
}
