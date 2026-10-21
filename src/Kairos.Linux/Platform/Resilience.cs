using Kairos.Engine;

namespace Kairos.App;

/// <summary>Keeps the engine alive through errors.</summary>
public static class Resilience
{
    /// <summary>Logs unexpected exceptions instead of letting a stray task end the process.</summary>
    public static void InstallHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ObsLog.Error("fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ObsLog.Warn("task: " + e.Exception);
            e.SetObserved();
        };
    }
}
