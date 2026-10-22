using Kairos.Engine.Audio;

namespace Kairos.App;

/// <summary>
/// The apps open now, for the Audio page's "Add source": any with a window someone can see, or with sound, read when the
/// menu opens (an app opened a moment ago is there, playing or not). Kairos, Discord (its own row), sound mixer apps
/// (their sound is every other app's again) and Windows's own shell are left out.
/// </summary>
public static class RunningApps
{
    static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "SearchApp", "LockApp", "ShellHost", "Widgets", "SystemSettings",
    };

    public static List<NameValue> Now()
    {
        try
        {
            return Pick(ProcessList.All(), WindowTarget.ProcessesWithWindows(), CoreAudio.ProcessesWithSound(), ForegroundApp.ForProcess);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            return [];
        }
    }

    /// <summary>One line per app (its name, and its exe to follow), A to Z.</summary>
    /// <param name="name">The name people know the app by, from a process and its exe name.</param>
    public static List<NameValue> Pick(IEnumerable<ProcInfo> processes, ISet<uint> withWindow, ISet<uint> withSound, Func<uint, string, string> name) =>
        [.. processes.Where(p => (withWindow.Contains(p.Pid) || withSound.Contains(p.Pid)) && !Shell.Contains(p.Name)
                                 && !p.Name.Equals(ProcessList.SelfName, StringComparison.OrdinalIgnoreCase)
                                 && !p.Name.Equals(AppInfo.Name, StringComparison.OrdinalIgnoreCase)
                                 && !p.Name.StartsWith("Discord", StringComparison.OrdinalIgnoreCase) && !VirtualAudio.IsMixerApp(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(app => new NameValue(name(app.First().Pid, app.Key), app.Key))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
}
