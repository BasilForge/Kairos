using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kairos.App;

/// <summary>
/// What WinForms' SystemEvents gave the Windows app. Resume from sleep shows as a jump of the wall clock against the
/// monotonic clock (which does not run while suspended); a logout or shutdown arrives as SIGTERM or SIGHUP.
/// </summary>
public static class PowerEvents
{
    static readonly TimeSpan Slip = TimeSpan.FromSeconds(20);

    public static event Action? Resumed;
    public static event Action? SessionEnding;

    static Timer? _watch;
    static PosixSignalRegistration? _term, _hup;

    public static void Start()
    {
        var wall = DateTime.UtcNow;
        long mono = Stopwatch.GetTimestamp();
        _watch = new Timer(_ =>
        {
            var now = DateTime.UtcNow;
            long monoNow = Stopwatch.GetTimestamp();
            var slipped = (now - wall) - Stopwatch.GetElapsedTime(mono, monoNow);
            wall = now;
            mono = monoNow;
            if (slipped > Slip)
                Resumed?.Invoke();
        }, null, 5000, 5000);

        void End(PosixSignalContext c)
        {
            c.Cancel = true; // the engine leaves through its own shutdown
            SessionEnding?.Invoke();
        }
        _term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, End);
        _hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, End);
    }
}
