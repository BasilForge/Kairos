using System.Diagnostics;
using Kairos.Engine;
using Kairos.Engine.Share;

namespace Kairos.Tests;

/// <summary>
/// A file just written is held a moment by another program (an antivirus reading it: ~30 ms on a test PC, longer for
/// a video). Share copies, edits and disk replay clips are moved into place then: the move waits for it instead of
/// running through 48 other names in a few milliseconds ("(31).mp4", or "Couldn't prepare the clip", 2026-10-21).
/// </summary>
public static class FilePlaceTests
{
    static string Folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Holds <paramref name="path"/> open with no sharing for <paramref name="ms"/>, as a scanner does.</summary>
    static Task Hold(string path, int ms)
    {
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return Task.Run(async () =>
        {
            await Task.Delay(ms);
            held.Dispose();
        });
    }

    [Test]
    public static void A_copy_held_a_moment_after_writing_keeps_its_name()
    {
        string dir = Folder();
        string temp = Path.Combine(dir, "clip.mp4.0123456789abcdef0123456789abcdef.part");
        File.WriteAllBytes(temp, new byte[4096]);
        var released = Hold(temp, 300);
        string target = Path.Combine(dir, "clip.mp4");
        Assert.Equal(target, ClipTranscoder.Place(temp, target), "the name it was given, once the scan let go");
        Assert.True(File.Exists(target) && !File.Exists(temp), "moved");
        released.Wait();
    }

    [Test]
    public static void A_part_file_held_a_moment_is_still_deleted()
    {
        string temp = Path.Combine(Folder(), "clip.mp4.0123456789abcdef0123456789abcdef.part");
        File.WriteAllBytes(temp, new byte[4096]);
        var released = Hold(temp, 300);
        ClipTranscoder.TryDelete(temp);
        Assert.True(!File.Exists(temp), "no half file left behind");
        released.Wait();
    }

    /// <summary>The older copy still open (an upload of it in progress): the new one takes the next free name.</summary>
    [Test]
    public static void A_copy_whose_older_one_stays_open_takes_the_next_name()
    {
        string dir = Folder();
        string target = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(target, new byte[16]);
        using var upload = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        string temp = Path.Combine(dir, "clip.mp4.0123456789abcdef0123456789abcdef.part");
        File.WriteAllBytes(temp, new byte[4096]);
        var clock = Stopwatch.StartNew();
        Assert.Equal(Path.Combine(dir, "clip (2).mp4"), ClipTranscoder.Place(temp, target), "beside the one in use");
        Assert.True(clock.ElapsedMilliseconds < 1000, $"at once, no wait for the older copy ({clock.ElapsedMilliseconds} ms)");
    }

    [Test]
    public static void A_held_file_is_tried_again_soon_then_less_often_for_3_s()
    {
        var waits = HeldFile.Waits().ToList();
        Assert.Equal("10,20,40,80,160,200", string.Join(",", waits.Take(6)), "a scan of ~30 ms costs ~30 ms, not 200");
        Assert.True(waits.Sum() is >= 3000 and < 3200, $"about 3 s in all ({waits.Sum()} ms)");
    }
}
