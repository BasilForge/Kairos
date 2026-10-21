using Avalonia.Threading;

namespace Kairos.Platform;

/// <summary>A timer that ticks on the UI thread (what WinForms' Timer is on Windows).</summary>
public sealed class UiTimer : IDisposable
{
    readonly DispatcherTimer _timer = new();

    public UiTimer(int intervalMs) => _timer.Interval = TimeSpan.FromMilliseconds(intervalMs);

    public event Action? Tick
    {
        add => _timer.Tick += (_, _) => value?.Invoke();
        remove { }
    }

    public bool Enabled => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>Runs <paramref name="action"/> once, <paramref name="ms"/> from now.</summary>
    public static void After(int ms, Action action)
    {
        var once = new UiTimer(ms);
        once.Tick += () => { once.Dispose(); action(); };
        once.Start();
    }

    public void Dispose() => _timer.Stop();
}
