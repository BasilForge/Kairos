using System.Diagnostics;
using System.Runtime.InteropServices;
using Kairos.App;

namespace Kairos.Engine;

public static partial class SelfTest
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowW(string? className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessageW(IntPtr window, uint message, IntPtr w, IntPtr l);

    const uint WmClose = 0x0010;

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    /// <summary>
    /// The test game's process, as the engine's look gives it (a game Kairos cannot open is never hooked); this process
    /// for a window that does not exist, which opens like any game of the user's own.
    /// </summary>
    static uint GamePid(string? window)
    {
        IntPtr found = window != null ? FindWindowW(WindowTarget.ClassName(window), WindowTarget.Title(window)) : IntPtr.Zero;
        return found != IntPtr.Zero && GetWindowThreadProcessId(found, out uint pid) != 0 ? pid : (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Games with "fall back to the screen" and the test "game" (ffplay parked off-screen by tools/test-game-layer.ps1):
    /// with no game the screen is copied; the game hooked, it is not; the game closed, it is again (<see cref="HookedGame"/>).
    /// An off-screen window is never fullscreen, so the game capture is pointed at it; the rest is the engine's own
    /// once-a-second look and libobs's signal.
    /// </summary>
    static int GamesGameScene(CapturePipeline pipeline, string folder, TextWriter output, string? window)
    {
        if (window == null)
        {
            output.WriteLine("scene: FAIL a game window is needed");
            return 2;
        }
        Thread.Sleep(1000);
        double? screenLit = ScreenLit(pipeline);
        bool copiedAlone = pipeline.ScreenCopied;
        pipeline.TargetGameForTest(window);
        bool hooked = false;
        for (int i = 0; i < 20 && !hooked; i++)
        {
            Thread.Sleep(1000);
            pipeline.WatchGame(); // the engine's once-a-second look
            hooked = pipeline.GameHooked;
        }
        int colours = CanvasColours(pipeline, folder);
        output.WriteLine($"scene games-game: the screen copied with no game {copiedAlone}, the game hooked {hooked}, {colours} colours");
        string? why = !copiedAlone ? "no game: the screen it falls back to is not copied"
            : !hooked ? "the game was never hooked"
            : colours <= 1 ? "the canvas is black"
            : HookedGame(pipeline, folder, output, window, screenLit);
        pipeline.WatchGame();
        why ??= pipeline.ScreenCopied ? null : "the game gone, the screen is not copied";
        output.WriteLine(why == null ? "scene ok" : $"scene: FAIL {why}");
        return why == null ? 0 : 1;
    }

    /// <summary>
    /// One app with the test game as the chosen app (tools/test-game-layer.ps1 -Scene apps-game): the window capture shows
    /// it; with the game fullscreen in front (as the engine's look would see it) the game layer hooks it and the window
    /// is not copied under it; leaving the game (an alt-tab, recorded) shows no black frame, and a look later the window
    /// is copied again with the layer gone. Back in the game, its hook lost while it stays open (a new resolution): the
    /// "unhooked" signal alone brings the window back, black only while Windows starts its capture.
    /// </summary>
    static int AppsGameScene(CapturePipeline pipeline, string folder, TextWriter output, string? window, int compare)
    {
        if (WindowTarget.ExeName(window) is not { } exe)
        {
            output.WriteLine("scene: FAIL a game window is needed");
            return 2;
        }
        var game = new FullscreenApp(window!, GamePid(window), exe, pipeline.CaptureDevice ?? "");
        // The engine's once-a-second looks, the game fullscreen in front or another app, until the condition holds.
        bool Look(FullscreenApp? front, Func<bool> until, int looks)
        {
            for (int i = 0; i < looks; i++)
            {
                pipeline.WatchWindow(front, front?.Exe ?? "explorer");
                Thread.Sleep(1000);
                if (until())
                    return true;
            }
            return false;
        }
        Look(null, () => false, 3);
        bool copiedAlone = pipeline.WindowCopied, noticeAlone = pipeline.CaptureNotice == null;
        bool hooked = Look(game, () => pipeline.GameLayerShown, 20);
        if (hooked && compare > 0)
            CompareCopy(pipeline, () => pipeline.WatchWindow(game, exe), () => pipeline.WindowCopied, compare, output);
        Thread.Sleep(1500); // the window capture lets go on its next frame
        bool copiedUnder = pipeline.WindowCopied;
        int colours = CanvasColours(pipeline, folder);
        output.WriteLine($"scene apps-game: the window copied alone {copiedAlone} (picture {noticeAlone}), the game layer shown {hooked}, " +
                         $"the window copied under it {copiedUnder}, {colours} colours");
        string? why = !copiedAlone || !noticeAlone ? "the chosen window alone is not recorded"
            : !hooked ? "the game layer never hooked the game"
            : copiedUnder ? "the window is still copied under the game's picture"
            : colours <= 1 ? "the canvas is black"
            : null;
        if (why == null)
        {
            pipeline.SetBufferActive(true);
            var (lit, back) = RecordAround(pipeline, folder, "alt-tab", () => pipeline.WatchWindow(null, "explorer"), () => pipeline.WindowCopied);
            why = Judge("alt-tab", lit, back, maxBlack: 0, screenDark: false, output);
            bool layerGone = Look(null, () => !pipeline.GameLayerShown, 3);
            int after = CanvasColours(pipeline, folder);
            output.WriteLine($"left the game: the layer gone {layerGone}, the window copied {pipeline.WindowCopied}, {after} colours, notice {pipeline.CaptureNotice ?? "none"}");
            why ??= !layerGone ? "the game layer stayed after the game was left"
                : !pipeline.WindowCopied || after <= 1 || pipeline.CaptureNotice != null ? "the window was not recorded again" : null;
        }
        if (why == null)
        {
            bool backIn = Look(game, () => pipeline.GameLayerShown, 20);
            Thread.Sleep(1500);
            if (!backIn || pipeline.WindowCopied)
                why = "back in the game: the window is still copied under it";
            else
            {
                var (lit, back) = RecordAround(pipeline, folder, "hook-lost", pipeline.DropGameLayerHookForTest, () => pipeline.WindowCopied);
                why = Judge("hook lost", lit, back, maxBlack: WindowStartFrames, screenDark: false, output);
            }
        }
        output.WriteLine(why == null ? "scene ok" : $"scene: FAIL {why}");
        return why == null ? 0 : 1;
    }

    /// <summary>
    /// Black frames allowed while Windows starts capturing the window again (its first picture comes on its own thread:
    /// one frame later, measured three times at 30 fps; a few when the PC is busy).
    /// </summary>
    const int WindowStartFrames = 4;

    /// <summary>
    /// --compare-copy &lt;seconds&gt;: the game's picture showing, four phases of that length in turn, the capture under
    /// it (the screen, One app's window) on as before 1.1.1 or not; <paramref name="look"/> is the engine's look that
    /// applies it. Each says its times (for a GPU sampler outside the process), the frames lagged and skipped in it, and
    /// Kairos's CPU time.
    /// </summary>
    static void CompareCopy(CapturePipeline pipeline, Action look, Func<bool> copiedNow, int seconds, TextWriter output)
    {
        pipeline.SetBufferActive(true);
        using var self = Process.GetCurrentProcess();
        foreach (bool copied in new[] { false, true, false, true })
        {
            pipeline.KeepCaptureUnder = copied;
            look();
            Thread.Sleep(1000); // the copy starts or lets go
            var before = pipeline.FrameCounts();
            self.Refresh();
            var cpu = self.TotalProcessorTime;
            var from = DateTime.Now;
            Thread.Sleep(seconds * 1000);
            var after = pipeline.FrameCounts();
            self.Refresh();
            double cpuMs = (self.TotalProcessorTime - cpu).TotalMilliseconds;
            output.WriteLine(FormattableString.Invariant(
                $"phase {(copied ? "copied" : "not-copied")} {from:HH:mm:ss.fff} {DateTime.Now:HH:mm:ss.fff} the capture under the game on {copiedNow()}, lagged {after.Lagged - before.Lagged} of {after.Rendered - before.Rendered}, skipped {after.Skipped - before.Skipped} of {after.Encoded - before.Encoded}, cpu {cpuMs:F0} ms"));
        }
        pipeline.KeepCaptureUnder = false;
        look();
        pipeline.SetBufferActive(false);
    }

    /// <summary>The screen's lit share before any game (<see cref="ScreenCheck.LitShare"/>): a dark one cannot show a black frame.</summary>
    static double? ScreenLit(CapturePipeline pipeline)
    {
        var (w, h) = pipeline.Base;
        return ScreenCheck.LookLit(w, h).GetAwaiter().GetResult();
    }

    /// <summary>
    /// A hooked test game whose picture shows: the screen under it must not be copied (libobs lets its duplication go).
    /// Then, each while recording: with <paramref name="altTab"/> (Screen) the user leaves the game, which goes on running:
    /// the screen must come back with no black frame (the layer leaves once the screen gives a picture), and is back in
    /// the game after; then the game closes: libobs's "unhooked" signal alone (no once-a-second look runs) must bring the
    /// screen back, black only while the screen copy starts: one frame, a few when starting it holds libobs's graphics
    /// thread (the game's picture is gone at once; OBS's game capture stays black). Null when all hold, else why not.
    /// </summary>
    static string? HookedGame(CapturePipeline pipeline, string folder, TextWriter output, string window, double? screenLit,
        (Action Leave, Func<bool> BackIn)? altTab = null)
    {
        Thread.Sleep(1500); // the screen capture lets its duplication go on its next frame
        bool copiedUnder = pipeline.ScreenCopied;
        output.WriteLine($"hooked game: the screen copied under its picture {copiedUnder}");
        if (copiedUnder)
            return "the screen is still copied under the game's picture";
        if (!pipeline.SetBufferActive(true))
            return $"could not start the replay ({pipeline.LastError})";
        bool screenDark = screenLit is not { } seen || seen < 0.05;
        if (screenDark)
            output.WriteLine($"the screen alone is dark ({screenLit:P1}): a black frame cannot be told from it");
        if (altTab is var (leave, backIn))
        {
            var (lit, back) = RecordAround(pipeline, folder, "alt-tab", leave, () => pipeline.ScreenCopied);
            if (Judge("alt-tab", lit, back, maxBlack: 0, screenDark, output) is { } why)
                return why;
            if (!backIn())
                return "the game layer did not show the game again";
            Thread.Sleep(1500);
            if (pipeline.ScreenCopied)
                return "back in the game: the screen is still copied";
        }
        IntPtr game = FindWindowW(WindowTarget.ClassName(window), WindowTarget.Title(window));
        if (game == IntPtr.Zero)
            return "the game window is not found";
        var (closed, closedBack) = RecordAround(pipeline, folder, "close", () => PostMessageW(game, WmClose, IntPtr.Zero, IntPtr.Zero),
            () => pipeline.ScreenCopied);
        return Judge("game closed", closed, closedBack, maxBlack: 4, screenDark, output);
    }

    /// <summary>
    /// Records 1.5 s, does <paramref name="act"/>, waits (5 s at most) for the capture under the game to be on again
    /// (<paramref name="copied"/>), records 1.5 s more: each frame's lit share, and how long it took (-1: never).
    /// </summary>
    static (List<double>? Lit, long BackMs) RecordAround(CapturePipeline pipeline, string folder, string name, Action act, Func<bool> copied)
    {
        string? recorded = null;
        using var stopped = new SemaphoreSlim(0);
        Action<string, double, IReadOnlyList<string>> onStopped = (path, _, _) =>
        {
            recorded = path;
            stopped.Release();
        };
        pipeline.RecordingStopped += onStopped;
        try
        {
            if (!pipeline.ToggleRecording(Path.Combine(folder, name + ".mp4")))
                return (null, -1);
            Thread.Sleep(1500);
            var clock = Stopwatch.StartNew();
            act();
            long back = -1;
            while (back < 0 && clock.ElapsedMilliseconds < 5000)
            {
                if (copied())
                    back = clock.ElapsedMilliseconds;
                else
                    Thread.Sleep(2);
            }
            Thread.Sleep(1500);
            pipeline.ToggleRecording();
            stopped.Wait(TimeSpan.FromSeconds(15));
            return (recorded != null ? FrameLitShares(recorded) : null, back);
        }
        finally
        {
            pipeline.RecordingStopped -= onStopped;
        }
    }

    /// <summary>The screen came back, and at most <paramref name="maxBlack"/> frames were black, in a row; null when so.</summary>
    static string? Judge(string what, List<double>? lit, long back, int maxBlack, bool screenDark, TextWriter output)
    {
        if (lit is not { Count: > 30 })
            return $"{what}: the recording could not be read";
        var black = Enumerable.Range(0, lit.Count).Where(i => ScreenCheck.IsBlack(lit[i])).ToList();
        output.WriteLine($"{what}: the capture under the game on again after {back} ms; {lit.Count} frames, black {black.Count}" +
                         (black.Count > 0 ? " (" + string.Join(", ", black) + ")" : "") + FormattableString.Invariant($", darkest {lit.Min():P1}"));
        bool inARow = black.Count == 0 || black[^1] - black[0] == black.Count - 1;
        return back < 0 ? $"{what}: the screen never came back"
            : !screenDark && (black.Count > maxBlack || !inARow) ? $"{what}: {black.Count} black frames (at most {maxBlack}, in a row)"
            : null;
    }

    /// <summary>Each frame's lit share (<see cref="ScreenCheck.LitShare"/>) in a small copy of the clip, decoded as the editor plays it.</summary>
    static List<double> FrameLitShares(string path)
    {
        const int W = 64, H = 36;
        using var clip = new Share.ClipPlayback(path, W, H);
        var bgra = new byte[W * H * 4];
        var shares = new List<double>();
        while (clip.NextPicture())
        {
            clip.CopyPicture(bgra);
            shares.Add(ScreenCheck.LitShare(new ScreenshotFrame(bgra, W, H)));
        }
        return shares;
    }
}
