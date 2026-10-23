using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Kairos.App;
using Kairos.Settings;

namespace Kairos.Ui;

/// <summary>
/// Kairos.exe --perf ...: the real settings window, its pages, the player, the editor and the share panel, run off screen
/// (nothing shows, no focus, no sound) so their CPU and memory can be measured from outside. After each step it prints
/// the managed heap (after a full collection, for measuring only) and the process's memory and handles.
/// <list type="bullet">
/// <item>window &lt;seconds&gt; [page]: the window open and idle on a page, with its live status polls.</item>
/// <item>cycle &lt;rounds&gt; &lt;clip&gt;: each round, every page, the clip in the player, the editor (3 s of play) and the share panel.</item>
/// <item>editor &lt;clip&gt; &lt;play s&gt; &lt;idle s&gt;: the editor playing the clip, then paused.</item>
/// <item>save &lt;clip&gt; [play s]: the editor cuts it in three, plays while typing, saves a new copy beside it and must
/// go back to the Library by itself (give it a copy in a scratch folder).</item>
/// </list>
/// </summary>
public static partial class SettingsApp
{
    public static int Perf(string[] args)
    {
        if (args.Length >= 4 && args[1] == "walk")
            return PerfWalk(args[2], args[3], args.Length > 4 ? args[4] : "ar");
        if (args.Length >= 4 && args[1] == "export")
            return PerfExport(args[2], args[3]);
        if (args.Length >= 3 && args[1] == "startup")
            return PerfStartup(args[2]);
        if (args.Length >= 4 && args[1] == "grabs")
            return PerfGrabs(args[2], int.Parse(args[3]));
        if (args.Length >= 3 && args[1] == "library")
            return PerfLibrary(args[2]);
        if (args.Length >= 2 && args[1] == "parts")
            return PerfParts();
        if (args.Length >= 4 && args[1] == "grabbers")
        {
            // grabbers <clip> <count>: a frame reader opened, read and closed again and again (handles must not grow).
            Engine.Share.ClipTranscoder.RequireLoaded();
            for (int i = 1; i <= int.Parse(args[3]); i++)
            {
                using (var grabber = new Engine.Share.FrameGrabber(args[2]))
                    grabber.Grab(1, 320, exact: true);
                if (i % 5 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    using var self = Process.GetCurrentProcess();
                    Console.WriteLine(FormattableString.Invariant($"PERF grabbers {i} handles={self.HandleCount} privMB={self.PrivateMemorySize64 / 1048576.0:F1}"));
                }
            }
            return 0;
        }
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --perf window <s> [page] | cycle <rounds> <clip> [idle s] | editor <clip> <play s> <idle s> | save <clip> [play s]");
            return 2;
        }
        Text.Language = SettingsStore.Load(SettingsStore.DefaultPath).General.Language;
        var app = CreateApplication();
        Edit.PlayerSession.Muted = true; // measured, never heard
        Engine.Share.ClipTranscoder.RequireLoaded(); // a measuring build has no FFmpeg of its own (KAIROS_FFMPEG)
        var vm = new SettingsViewModel();
        var window = WindowPlacement.OffScreen(new MainWindow(vm));
        window.Show();
        string? pollMode = Environment.GetEnvironmentVariable("KAIROS_PERF_POLL");
        if (pollMode is "send" or "sync")
        {
            // Only the questions, twice a second, the answers dropped (to tell the parts of a poll apart).
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += async (_, _) =>
            {
                if (pollMode == "send")
                    await PipeClient.SendAsync("status", 800);
                else
                    PipeClient.Send("status", 800);
            };
            t.Start();
        }
        else if (pollMode != "none")
            vm.Start(); // (KAIROS_PERF_POLL=none: without the status polls, to tell their cost apart)
        Wait(1);
        Report("open");
        switch (args[1])
        {
            case "window":
                window.ShowPage(args.Length > 3 ? args[3] : "status");
                Wait(1);
                Report("page");
                Mark("idle-start"); // the sampler measures from here
                Wait(double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
                Report("idle");
                break;
            case "cycle":
                for (int round = 1; round <= int.Parse(args[2]); round++)
                {
                    Round(window, args[3]);
                    Report($"round{round}");
                }
                Mark("idle-start");
                Wait(args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 15);
                Report("after");
                break;
            case "save":
                // save <clip>: the editor cuts the clip in three, plays through the cuts, saves a new copy (beside the
                // clip, so give it one in a scratch folder) and must go back to the Library by itself.
                app.DispatcherUnhandledException += (_, e) => Console.WriteLine("PERF ui-error " + e.Exception);
                TaskScheduler.UnobservedTaskException += (_, e) => Console.WriteLine("PERF task-error " + e.Exception);
                window.ShowPage("clips");
                Wait(1);
                if (window.OpenEditor(args[2], isPicture: false, Edit.EditorMode.Library) is not { } saving)
                {
                    Console.WriteLine("PERF editor did not open");
                    break;
                }
                Wait(2);
                var edit = saving.Model;
                double length = edit.Project.OutputDuration;
                edit.Apply(edit.Project.SplitAt(length * 0.27).SplitAt(length * 0.55));
                Wait(0.5);
                edit.AddTextCommand.Execute(null);
                edit.KeyCommand("Home");
                edit.KeyCommand("Space");
                // Typing while it plays, through every cut; what is heard is reported each second.
                double playFor = double.Parse(args.Length > 3 ? args[3] : "4", System.Globalization.CultureInfo.InvariantCulture);
                for (int tick = 1; tick <= playFor * 4; tick++)
                {
                    Wait(0.25);
                    edit.Words += tick % 2 == 0 ? "d" : "f";
                    if (tick % 4 == 0)
                        Console.WriteLine(FormattableString.Invariant($"PERF playing at={edit.PlayheadOutput:F1} {saving.Playback.PlayReport}"));
                }
                edit.KeyCommand("Space");
                Wait(0.5);
                edit.PrimaryCommand.Execute(null);
                Wait(0.3);
                // As a click comes: from the window's message loop (a save's ending is sent back through its context).
                window.Dispatcher.BeginInvoke(() =>
                {
                    saving.NewCopyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                });
                var asked = DateTime.Now;
                while (window.EditorLayer.Visibility == Visibility.Visible && (DateTime.Now - asked).TotalSeconds < 120)
                    Wait(0.5);
                Console.WriteLine(FormattableString.Invariant(
                    $"PERF saved editorOpen={window.EditorLayer.Visibility == Visibility.Visible} saving={edit.IsSaving} done={edit.SaveDone} error='{edit.SaveError}' after={(DateTime.Now - asked).TotalSeconds:F1}s"));
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Wait(0.5);
                break;
            case "editor":
                window.ShowPage("clips");
                Wait(1);
                if (window.OpenEditor(args[2], isPicture: false, Edit.EditorMode.Library) is not { } editor)
                {
                    Console.WriteLine("PERF editor did not open");
                    break;
                }
                Wait(2);
                Report("editor-open");
                Mark("play-start");
                editor.Model.KeyCommand("Space");
                Wait(double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
                editor.Model.KeyCommand("Space");
                Report("played");
                Mark("idle-start");
                Wait(double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture));
                Report("editor-idle");
                window.CloseEditorNow();
                Wait(2);
                Report("editor-closed");
                break;
        }
        vm.Stop();
        window.Close();
        Report("closed");
        return 0;
    }

    /// <summary>
    /// export &lt;clip&gt; &lt;out.mp4&gt;: the whole clip saved with the Vivid look, as the editor saves it (nothing shown);
    /// prints how long it took. For measuring heavy processing.
    /// </summary>
    static int PerfExport(string clip, string target)
    {
        Engine.Share.ClipTranscoder.RequireLoaded();
        var info = Engine.Share.ClipTranscoder.Probe(clip);
        var p = Engine.Edit.EditProject.For(clip, info.Duration) with { Look = Engine.Edit.ColourLook.Vivid, LookStrength = 1 };
        var graph = Engine.Edit.EditGraph.Build(p, info.Width, info.Height, info.Fps, info.AudioStreams, new Dictionary<Guid, Engine.Edit.Overlay>(),
            Engine.Audio.TrackLayout.Of(info));
        var plan = Engine.EncoderPresets.Plan(VideoCodec.Av1, QualityPreset.High, info.Width, info.Height, (int)Math.Round(info.Fps), 22, 40,
            nvencAvailable: true);
        // KAIROS_PERF_OWNDEVICE=1: the encoder on a device of its own (as before the shared one), to compare.
        if (Environment.GetEnvironmentVariable("KAIROS_PERF_OWNDEVICE") == "1")
            Engine.Edit.EditExporter.UseDecoderDevice = Engine.Share.ClipTranscoder.UseDecoderDevice = false;
        Mark("export-start");
        Engine.Edit.EditExporter.Profile = true;
        var watch = Stopwatch.StartNew();
        var result = Engine.Edit.EditExporter.Export(clip, target, graph, plan, null, CancellationToken.None);
        Console.WriteLine(FormattableString.Invariant($"PERF export-done up={Up():F1} seconds={watch.Elapsed.TotalSeconds:F1} MB={result.Bytes / 1048576.0:F0} frames={Engine.Edit.EditExporter.LastDecodedFrames}"));
        var stages = Engine.Edit.EditExporter.StageNames.Zip(Engine.Edit.EditExporter.StageTicks,
            (name, ticks) => FormattableString.Invariant($"{name}={ticks * 1000.0 / Stopwatch.Frequency:F0}"));
        Console.WriteLine("PERF export-stages ms " + string.Join(" ", stages));
        return 0;
    }

    static readonly string[] PerfPages = ["status", "clips", "capture", "quality", "audio", "hotkeys", "voice", "connections", "general"];

    static void Round(MainWindow window, string clip)
    {
        string skip = Environment.GetEnvironmentVariable("KAIROS_PERF_SKIP") ?? "";
        bool Skips(string step) => skip.Split(',').Contains(step);
        foreach (string page in PerfPages.Where(p => !Skips(p)))
        {
            window.ShowPage(page);
            Wait(0.6);
        }
        window.ShowPage("clips");
        Wait(0.5);
        var item = window.Library.Items.FirstOrDefault(c => string.Equals(c.Path, clip, StringComparison.OrdinalIgnoreCase));
        if (item != null && !Skips("player"))
        {
            window.ShowVideo(item);
            Wait(2);
            window.VideoLayer.Hide();
            Wait(0.5);
        }
        if (!Skips("editor") && window.OpenEditor(clip, isPicture: false, Edit.EditorMode.Library) is { } editor)
        {
            Wait(1.5);
            editor.Model.KeyCommand("Space");
            Wait(3);
            editor.Model.KeyCommand("Space");
            Wait(0.5);
            window.CloseEditorNow();
            Wait(0.5);
        }
        if (!Skips("share"))
        {
            var share = window.ShowShare(clip);
            Wait(3);
            share.Close();
            Wait(0.5);
        }
        window.ShowPage("status");
        Wait(0.5);
    }

    /// <summary>Runs the window (timers, layout, rendering) for a while, as if left alone.</summary>
    static void Wait(double seconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static double Up()
    {
        using var self = Process.GetCurrentProcess();
        return (DateTime.Now - self.StartTime).TotalSeconds;
    }

    static void Mark(string what) => Console.WriteLine(FormattableString.Invariant($"PERF {what} up={Up():F1}"));

    static void Report(string step)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        Console.WriteLine(FormattableString.Invariant(
            $"PERF {step} up={Up():F1} heapMB={GC.GetTotalMemory(false) / 1048576.0:F1} committedMB={info.TotalCommittedBytes / 1048576.0:F1} privMB={self.PrivateMemorySize64 / 1048576.0:F1} wsMB={self.WorkingSet64 / 1048576.0:F1} handles={self.HandleCount} threads={self.Threads.Count} gdi={GetGuiResources(self.Handle, 0)} user={GetGuiResources(self.Handle, 1)} gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} jit={System.Runtime.JitInfo.GetCompiledMethodCount()} jitMs={System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds:F0} pool={ThreadPool.CompletedWorkItemCount} alloc={GC.GetTotalAllocatedBytes() / 1048576.0:F1}"));
    }

    [DllImport("user32.dll")]
    static extern int GetGuiResources(IntPtr process, int flags);
}
