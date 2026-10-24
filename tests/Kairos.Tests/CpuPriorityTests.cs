using System.Diagnostics;
using System.Runtime.InteropServices;
using Kairos.Engine;

namespace Kairos.Tests;

/// <summary>
/// A user's 8 threads full in ARC Raiders: libobs's video threads waited behind the game while its audio thread (MMCSS)
/// went on, and clips froze with sound. The recording now goes ahead of the game on the CPU.
/// </summary>
public static class CpuPriorityTests
{
    [DllImport("kernel32.dll")]
    static extern int GetThreadPriority(IntPtr thread);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();

    /// <summary>A thread named <paramref name="name"/> (Thread.Name is its description); its priority once raised.</summary>
    static int PriorityAfterRaise(string name)
    {
        int priority = int.MinValue;
        using var named = new ManualResetEventSlim();
        using var raised = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            named.Set();
            raised.Wait();
            priority = GetThreadPriority(GetCurrentThread());
        }) { IsBackground = true, Name = name };
        thread.Start();
        named.Wait();
        CpuPriority.RaiseVideoThreads();
        raised.Set();
        thread.Join();
        return priority;
    }

    [Test]
    public static void Libobs_video_threads_go_time_critical_and_no_other()
    {
        Assert.Equal(15, PriorityAfterRaise("libobs: graphics thread"), "the thread that draws and converts the picture");
        Assert.Equal(15, PriorityAfterRaise("obs gpu encode thread"), "the one that hands it to the encoder");
        Assert.Equal(15, PriorityAfterRaise("video-io: video thread"), "the one that times the frames");
        Assert.Equal(0, PriorityAfterRaise("Smart Save"), "any other: as it was");
    }

    /// <summary>
    /// libobs's GPU encode thread names itself once it first runs, after the output's start has returned: on a CPU full
    /// of a game's threads it was not named yet when Kairos looked (a user's log, 2 builds of 3; 8 of 89 starts here), and
    /// stayed at normal priority while the encoder skipped a quarter of the frames.
    /// </summary>
    [Test]
    public static void A_thread_that_names_itself_after_the_start_is_raised_soon()
    {
        int priority = int.MinValue;
        using var looked = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            looked.Wait();
            Thread.CurrentThread.Name = "obs gpu encode thread"; // named after Kairos first looked
            for (int i = 0; i < 100 && (priority = GetThreadPriority(GetCurrentThread())) != 15; i++)
                Thread.Sleep(50);
        }) { IsBackground = true };
        thread.Start();
        CpuPriority.RaiseVideoThreadsSoon();
        looked.Set();
        thread.Join();
        Assert.Equal(15, priority, "raised by a later look, within a few seconds");
    }

    [Test]
    public static void The_process_goes_above_normal_only_from_normal()
    {
        Assert.Equal(ProcessPriorityClass.AboveNormal, CpuPriority.Raised(ProcessPriorityClass.Normal), "the usual start");
        Assert.Equal(null, CpuPriority.Raised(ProcessPriorityClass.BelowNormal), "a test runner started it lower: left so");
        Assert.Equal(null, CpuPriority.Raised(ProcessPriorityClass.High), "the user set it higher: theirs");
    }
}
