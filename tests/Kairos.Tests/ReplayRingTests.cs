using Kairos.Engine.Replay;

namespace Kairos.Tests;

/// <summary>The replay's ring file: written in a loop at a fixed size, read back from a keyframe, never half-overwritten.</summary>
public static class ReplayRingTests
{
    static string NewPath() => Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), DiskReplayPlace.FileName);

    /// <summary>One second of 60 fps video (a keyframe each second) and 47 audio packets, each packet filled with its number.</summary>
    static void FeedSecond(ReplayRing ring, int second, int videoBytes = 400, int audioBytes = 50)
    {
        for (int f = 0; f < 60; f++)
        {
            long usec = second * 1_000_000L + f * 1_000_000L / 60;
            long frame = second * 60L + f;
            ring.Append(new RingPacket(frame, frame, usec, 1, 60, 0, f == 0), Fill(videoBytes, (byte)(frame % 251)));
            if (f * 47 / 60 != (f + 1) * 47 / 60) // 47 a second
                ring.Append(new RingPacket(usec * 48 / 1000, usec * 48 / 1000, usec, 1, 48000, 1, true), Fill(audioBytes, 7));
        }
    }

    static byte[] Fill(int n, byte v) => Enumerable.Repeat(v, n).ToArray();

    [Test]
    public static void The_file_keeps_its_size_and_the_newest_seconds_while_it_loops()
    {
        string path = NewPath();
        using (var ring = new ReplayRing(path, capacity: 256 * 1024, keepSeconds: 60, blockBytes: 16 * 1024))
        {
            for (int s = 0; s < 40; s++)
                FeedSecond(ring, s); // ~26 KB a second: the 256 KB file loops about four times
            var slice = ring.Take(5)!;
            Assert.True(slice.Packets[0].Track == 0 && slice.Packets[0].Key, "starts on a video keyframe");
            Assert.Equal(35 * 1_000_000L, slice.Packets[0].DtsUsec, "the keyframe 5 s before the newest frame");
            Assert.True(slice.Packets.Where(p => p.Track == 1).All(p => p.DtsUsec >= slice.Packets[0].DtsUsec), "sound from that moment on");
            var read = slice.ReadAll().ToList();
            Assert.Equal(slice.Packets.Count, read.Count, "every packet read back");
            Assert.True(read.Where(r => r.Packet.Track == 0).All(r => r.Data.Length == 400 && r.Data.ToArray().All(b => b == (byte)(r.Packet.Pts % 251))),
                "each frame's own bytes");
            Assert.True(ring.BytesWritten > 3 * 256 * 1024, "it really looped");
        }
        Assert.Equal(256L * 1024, new FileInfo(path).Length, "the file stays at its size");
    }

    [Test]
    public static void Older_seconds_than_the_file_holds_are_not_offered()
    {
        using var ring = new ReplayRing(NewPath(), capacity: 128 * 1024, keepSeconds: 60, blockBytes: 16 * 1024);
        for (int s = 0; s < 30; s++)
            FeedSecond(ring, s);
        var slice = ring.Take(60)!;
        Assert.True(slice.Packets[0].DtsUsec > 20 * 1_000_000L, $"only what is still in the file: from {slice.Packets[0].DtsUsec / 1e6:F1} s");
        Assert.Equal(slice.Packets.Count, slice.ReadAll().Count(), "and all of it reads back");
    }

    [Test]
    public static void Nothing_is_offered_before_the_first_keyframe_and_clear_starts_empty()
    {
        using var ring = new ReplayRing(NewPath(), capacity: 128 * 1024, keepSeconds: 60, blockBytes: 16 * 1024);
        Assert.True(ring.Take(5) == null, "empty");
        ring.Append(new RingPacket(0, 0, 0, 1, 60, 0, false), Fill(100, 1));
        Assert.True(ring.Take(5) == null, "no keyframe yet");
        FeedSecond(ring, 1);
        Assert.True(ring.Take(5) != null, "a keyframe: a clip");
        long written;
        do // what was queued before the clear reaches the drive first
        {
            written = ring.BytesWritten;
            Thread.Sleep(200);
        } while (ring.BytesWritten != written);
        ring.Clear();
        Thread.Sleep(200);
        Assert.True(ring.Take(5) == null, "start fresh: nothing left");
        Assert.Equal(written, ring.BytesWritten, "clearing writes nothing");
    }

    [Test]
    public static void A_save_overtaken_by_the_writer_fails_cleanly()
    {
        using var ring = new ReplayRing(NewPath(), capacity: 128 * 1024, keepSeconds: 60, blockBytes: 16 * 1024);
        for (int s = 0; s < 6; s++)
            FeedSecond(ring, s);
        var slice = ring.Take(10)!;
        for (int s = 6; s < 20; s++)
            FeedSecond(ring, s); // the file loops over what the slice holds
        bool failed = false;
        try
        {
            _ = slice.ReadAll().ToList();
        }
        catch (RingOvertakenException)
        {
            failed = true;
        }
        Assert.True(failed, "never a clip with someone else's bytes");
    }

    [Test]
    public static void The_file_is_reused_at_the_same_size()
    {
        string path = NewPath();
        using (var ring = new ReplayRing(path, capacity: 128 * 1024, keepSeconds: 60, blockBytes: 16 * 1024))
            FeedSecond(ring, 0);
        var created = File.GetCreationTimeUtc(path);
        using (var ring = new ReplayRing(path, capacity: 128 * 1024, keepSeconds: 60, blockBytes: 16 * 1024))
            FeedSecond(ring, 0);
        Assert.Equal(created, File.GetCreationTimeUtc(path), "the same file, not a new one");
        using (new ReplayRing(path, capacity: 192 * 1024, keepSeconds: 60, blockBytes: 16 * 1024)) { }
        Assert.Equal(192L * 1024, new FileInfo(path).Length, "a new size: resized");
    }

    [Test]
    public static void A_write_error_is_reported_once_and_appends_then_do_nothing()
    {
        int failures = 0;
        using var ring = new ReplayRing(NewPath(), capacity: 128 * 1024, keepSeconds: 60, blockBytes: 16 * 1024,
            write: (_, _) => throw new IOException("disk full"));
        ring.Failed += _ => Interlocked.Increment(ref failures);
        for (int s = 0; s < 4; s++)
            FeedSecond(ring, s);
        for (int i = 0; i < 100 && failures == 0; i++)
            Thread.Sleep(50); // after its tries (about 2.5 s)
        Assert.Equal(1, failures, "one report, once the tries ran out");
        Assert.True(ring.HasFailed, "failed for good");
    }

    [Test]
    public static void A_quiet_replay_keeps_only_its_own_length_in_the_index()
    {
        // A large file filled slowly (an idle desktop) would otherwise index hours of tiny packets.
        using var ring = new ReplayRing(NewPath(), capacity: 64L << 20, keepSeconds: 5, blockBytes: 1 << 20);
        for (int s = 0; s < 40; s++)
            FeedSecond(ring, s);
        var slice = ring.Take(600)!;
        Assert.True(slice.Packets[0].DtsUsec >= 24 * 1_000_000L, $"no older than its length and a margin: from {slice.Packets[0].DtsUsec / 1e6:F0} s");
        Assert.Equal(slice.Packets.Count, slice.ReadAll().Count(), "read back in runs");
    }
}

/// <summary>
/// The replay must never go back to RAM for a trifle. A write that fails for a moment, a slow
/// drive, a file another replay is still closing: the disk carries on.
/// </summary>
public static class ReplayRingHardshipTests
{
    static string NewPath() => Path.Combine(Path.GetTempPath(), "kairos-tests", Guid.NewGuid().ToString("N"), DiskReplayPlace.FileName);

    static void Feed(ReplayRing ring, int seconds, int frameBytes = 400)
    {
        for (int s = 0; s < seconds; s++)
            for (int f = 0; f < 60; f++)
            {
                long usec = s * 1_000_000L + f * 1_000_000L / 60;
                ring.Append(new RingPacket(s * 60L + f, s * 60L + f, usec, 1, 60, 0, f == 0), new byte[frameBytes]);
            }
    }

    [Test]
    public static void A_write_that_fails_a_few_times_is_tried_again_and_the_ring_goes_on()
    {
        int failures = 0, calls = 0;
        string path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.OpenHandle(path + ".data", FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.None, 1 << 20);
        using var ring = new ReplayRing(path, capacity: 1 << 20, keepSeconds: 60, blockBytes: 16 * 1024, write: (offset, data) =>
        {
            if (Interlocked.Increment(ref calls) is 2 or 3 or 4)
                throw new IOException("the device is busy"); // three failures in a row, then fine
            RandomAccess.Write(file, data.Span, offset);
        });
        ring.Failed += _ => Interlocked.Increment(ref failures);
        Feed(ring, 10);
        var slice = ring.Take(5)!;
        Assert.Equal(slice.Packets.Count, slice.ReadAll().Count(), "a clip after the hiccup");
        Assert.True(!ring.HasFailed && failures == 0, "still on the disk");
    }

    [Test]
    public static void A_slow_drive_still_saves_a_clip()
    {
        // A drive that takes 300 ms a block (an old hard disk busy loading a game): the clip waits for it.
        using var ring = new ReplayRing(NewPath(), capacity: 4 << 20, keepSeconds: 60, blockBytes: 64 * 1024, write: (_, _) => Thread.Sleep(300));
        Feed(ring, 6);
        var slice = ring.Take(3)!;
        Assert.True(slice.Packets.Count > 0 && !ring.HasFailed, "no failure for being slow");
    }

    [Test]
    public static void A_ring_still_being_closed_by_the_last_replay_is_waited_for()
    {
        // A rebuild: the old replay's writer still has the file open as the new one opens it.
        string path = NewPath();
        using (new ReplayRing(path, capacity: 1 << 20, keepSeconds: 60, blockBytes: 16 * 1024)) { }
        var held = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(800);
            held.Dispose();
        });
        using var ring = new ReplayRing(path, capacity: 1 << 20, keepSeconds: 60, blockBytes: 16 * 1024);
        release.Wait();
        Feed(ring, 3);
        Assert.True(ring.Take(2) != null && !ring.HasFailed, "opened once the old one let go");
    }

    [Test]
    public static void Only_a_drive_that_cannot_keep_up_for_long_counts_as_failed()
    {
        int failures = 0;
        var gate = new ManualResetEventSlim(false);
        using var ring = new ReplayRing(NewPath(), capacity: 64L << 20, keepSeconds: 600, blockBytes: 1 << 20, write: (_, _) => gate.Wait(),
            backlogLimit: 4L << 20);
        ring.Failed += _ => Interlocked.Increment(ref failures);
        Feed(ring, 30, frameBytes: 4000); // ~7 MB queued behind a drive that never answers: past the (test's) 4 MB limit
        Assert.True(ring.HasFailed && failures == 1, "a drive stuck for good: back to RAM, said once");
        gate.Set();
    }
}
