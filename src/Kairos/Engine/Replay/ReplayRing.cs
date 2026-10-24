using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace Kairos.Engine.Replay;

/// <summary>
/// One encoded packet's place in time, as libobs stamps it: its track (0 video, n the n-th sound track), pts and dts in
/// ticks of 1/<see cref="TbDen"/> s, and <see cref="TbNum"/> ticks per video frame (a frame-rate divisor makes it 2 or
/// more; sound counts samples, 1 a tick).
/// </summary>
public readonly record struct RingPacket(long Pts, long Dts, long DtsUsec, int TbNum, int TbDen, int Track, bool Key);

/// <summary>The ring wrapped over a clip being read: the clip is not written (never one with newer bytes in it).</summary>
public sealed class RingOvertakenException() : IOException("The replay file was written over while the clip was read");

/// <summary>
/// The replay kept on a drive instead of RAM: one file of a fixed size, reserved once and written in a loop, plus an
/// index in RAM (each packet's place and time, ~5 MB for 5 minutes). Packets go into 4 MB blocks; a writer thread writes
/// each full block (or a partial one after 2 s) in one sequential write, so every byte is written once, in large runs
/// over the same range: no files made and deleted, no fragmentation (what NVIDIA's temp files do). A packet never spans
/// the file's end: a block that does not fit starts again at the file's start.
/// <para>Positions are logical (they only grow): a byte at logical P lives at P % capacity, and is overwritten once a
/// block reaching past P + capacity is placed. The index keeps what is still in the file and at most
/// <c>keepSeconds</c> (a quiet desktop fills a large file slowly: hours of tiny packets would weigh in RAM).</para>
/// </summary>
public sealed class ReplayRing : IDisposable
{
    readonly record struct Write(long Offset, byte[] Buffer, int Length, long End);

    public const int DefaultBlockBytes = 4 << 20;
    static readonly TimeSpan PartialAfter = TimeSpan.FromSeconds(2);
    /// <summary>A clip's reads: contiguous packets in runs of up to this many bytes.</summary>
    const int ReadRun = 8 << 20;
    /// <summary>
    /// Blocks waiting for the drive past this (about 45 s of 90 Mb/s): a drive that cannot keep up for long, the one
    /// reason besides a write failing for good to go back to RAM. A slow moment only queues a few blocks.
    /// </summary>
    public const long DefaultBacklogLimit = 512L << 20;
    /// <summary>A failed write is tried again after these waits (about 2.5 s in all) before the ring gives up.</summary>
    static readonly int[] RetryWaitsMs = [100, 200, 400, 800, 1000];

    readonly SafeFileHandle _file;
    readonly long _capacity;
    readonly int _blockBytes;
    readonly long _keepUsec;
    readonly long _backlogLimit;
    long _queuedBytes;
    readonly Action<long, ReadOnlyMemory<byte>> _write;
    readonly object _gate = new();
    readonly List<RingEntry> _entries;
    int _first; // _entries before it are gone (compacted now and then)
    readonly BlockingCollection<Write> _queue = new();
    readonly ConcurrentBag<byte[]> _pool = [];
    readonly Thread _writer;

    byte[] _block;
    int _used;
    long _blockStart;    // logical position of the open block
    long _placedEnd;     // logical end of the newest placed block's room: what is written over up to there is gone
    DateTime _blockOpened;
    long _writtenEnd;    // logical end of what is on the drive
    long _bytesWritten;
    int _failed;

    /// <param name="keepSeconds">The longest clip asked of it (the replay, or the quick clip when longer).</param>
    public ReplayRing(string path, long capacity, double keepSeconds = 1200, int blockBytes = DefaultBlockBytes)
        : this(path, capacity, keepSeconds, blockBytes, null) { }

    /// <param name="write">Tests: what writing a block does (the file's write by default).</param>
    /// <param name="backlogLimit">Tests: <see cref="DefaultBacklogLimit"/>.</param>
    internal ReplayRing(string path, long capacity, double keepSeconds, int blockBytes, Action<long, ReadOnlyMemory<byte>>? write,
        long backlogLimit = DefaultBacklogLimit)
    {
        _backlogLimit = backlogLimit;
        if (capacity < 4L * blockBytes)
            throw new ArgumentOutOfRangeException(nameof(capacity), "the ring holds at least four blocks");
        _capacity = capacity;
        _blockBytes = blockBytes;
        _keepUsec = (long)((keepSeconds + 10) * 1_000_000); // and a keyframe interval's room to start from
        _entries = new List<RingEntry>((int)Math.Min(1 << 20, (keepSeconds + 10) * 400)); // ~60 frames and ~6 x 47 sound packets a second
        var dir = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (dir.Name.StartsWith('.'))
            dir.Attributes |= FileAttributes.Hidden;
        bool existed = File.Exists(path);
        _file = Open(path, existed, capacity);
        try
        {
            if (RandomAccess.GetLength(_file) != capacity)
                RandomAccess.SetLength(_file, capacity);
            if (!existed)
                File.SetAttributes(path, FileAttributes.NotContentIndexed); // Windows Search never reads it
        }
        catch
        {
            _file.Dispose();
            throw;
        }
        _write = write ?? ((offset, data) => RandomAccess.Write(_file, data.Span, offset));
        _block = Rent(blockBytes);
        Open(0, blockBytes);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Kairos replay ring", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }

    /// <summary>
    /// The ring's file: a new one reserved whole at once (one allocation, no zeros written), an existing one reused. A
    /// file still held (the last replay's writer finishing on a slow drive, an antivirus scanning it) is waited for, up to
    /// 10 s, before the replay gives up on the disk.
    /// </summary>
    static SafeFileHandle Open(string path, bool existed, long capacity) => HeldFile.Retry(() => existed
        ? File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
        : File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, FileOptions.None, preallocationSize: capacity), totalMs: 10_000);

    /// <summary>Bytes the ring has written to the drive (what the disk sees).</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>A write failed: the ring takes nothing more (the pipeline goes back to RAM).</summary>
    public bool HasFailed => Volatile.Read(ref _failed) != 0;

    /// <summary>Raised once, on the writer thread, when a write fails.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Adds one packet (libobs's encoder thread): a copy into the open block, never a wait on the drive.</summary>
    public void Append(in RingPacket packet, ReadOnlySpan<byte> data)
    {
        if (HasFailed)
            return;
        lock (_gate)
        {
            if (_used + data.Length > _block.Length || (_used > 0 && DateTime.UtcNow - _blockOpened > PartialAfter))
            {
                Seal();
                if (data.Length > _block.Length)
                {
                    Return(_block);
                    _block = new byte[data.Length]; // one packet larger than a block (a 4K keyframe): a block of its own
                    Open(_blockStart, data.Length);
                }
            }
            data.CopyTo(_block.AsSpan(_used));
            _entries.Add(new RingEntry(packet, _blockStart + _used, data.Length));
            _used += data.Length;
            if (packet.Key && packet.Track == 0)
                DropOlderThan(packet.DtsUsec - _keepUsec);
        }
    }

    /// <summary>
    /// The packets of the last <paramref name="seconds"/>, from the first video keyframe at or after that time (sound
    /// from the same moment) to the newest picture (sound the picture's encoder has not caught up with yet, a game
    /// holding the graphics card, would play over a frozen picture); null when there is none yet. The open block is handed to the writer first; only a copy of
    /// the index is made under the lock (the encoder thread waits on it).
    /// </summary>
    public RingSlice? Take(double seconds)
    {
        RingEntry[] all;
        lock (_gate)
        {
            Seal();
            all = new RingEntry[_entries.Count - _first];
            _entries.CopyTo(_first, all, 0, all.Length);
        }
        int newest = Array.FindLastIndex(all, e => e.Packet.Track == 0);
        if (newest < 0)
            return null;
        var last = all[newest].Packet;
        long from = last.DtsUsec - (long)(seconds * 1_000_000);
        int start = Array.FindIndex(all, e => e.Packet is { Track: 0, Key: true } p && p.DtsUsec >= from);
        if (start < 0)
            return null;
        long t0 = all[start].Packet.DtsUsec;
        int before = newest > 0 ? Array.FindLastIndex(all, newest - 1, e => e.Packet.Track == 0) : -1;
        long end = last.DtsUsec + (before >= 0 ? last.DtsUsec - all[before].Packet.DtsUsec : 0); // its frame's end
        var picked = new List<RingEntry>(all.Length - start);
        int pictures = 0;
        long newestSound = long.MinValue;
        for (int i = start; i < all.Length; i++)
        {
            var p = all[i].Packet;
            if (p.Track == 0)
                pictures++;
            else
                newestSound = Math.Max(newestSound, p.DtsUsec);
            if (p.Track == 0 || p.DtsUsec >= t0 && p.DtsUsec < end)
                picked.Add(all[i]);
        }
        double frames = (end - t0) / 1e6 * last.TbDen / Math.Max(1, last.TbNum);
        return new RingSlice(this, picked)
        {
            Pictures = pictures,
            PictureShare = frames > 0 ? Math.Min(1, pictures / frames) : 1,
            PictureBehind = newestSound > last.DtsUsec ? (newestSound - last.DtsUsec) / 1e6 : 0,
        };
    }

    /// <summary>Starts empty again (a start, or "start fresh after a save"): the index goes, nothing is written.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _first = 0;
            _used = 0;
            _blockOpened = DateTime.UtcNow;
        }
    }

    /// <summary>The open block goes to the writer and the next one is placed.</summary>
    void Seal()
    {
        if (_used == 0)
            return;
        long end = _blockStart + _used;
        if (Interlocked.Add(ref _queuedBytes, _used) > _backlogLimit)
            Fail(new IOException($"The drive has not kept up: {Interlocked.Read(ref _queuedBytes) >> 20} MB waiting"));
        _queue.Add(new Write(_blockStart % _capacity, _block, _used, end));
        _block = Rent(_blockBytes);
        Open(end, _blockBytes);
    }

    /// <summary>
    /// A block of <paramref name="room"/> bytes from logical <paramref name="at"/> (or the next lap's start when it would
    /// pass the file's end); the entries whose bytes it will write over leave the index.
    /// </summary>
    void Open(long at, int room)
    {
        if (at % _capacity + room > _capacity)
            at = (at / _capacity + 1) * _capacity;
        _blockStart = at;
        Volatile.Write(ref _placedEnd, at + room);
        _used = 0;
        _blockOpened = DateTime.UtcNow;
        long lost = at + room - _capacity;
        while (_first < _entries.Count && _entries[_first].Pos < lost)
            _first++;
        Compact();
    }

    void DropOlderThan(long usec)
    {
        while (_first < _entries.Count && _entries[_first].Packet.DtsUsec < usec)
            _first++;
        Compact();
    }

    void Compact()
    {
        if (_first > 4096 && _first > _entries.Count / 2)
        {
            _entries.RemoveRange(0, _first);
            _first = 0;
        }
    }

    /// <summary>Whether the bytes at logical <paramref name="pos"/> are still the ones written there.</summary>
    internal bool Intact(long pos) => Volatile.Read(ref _placedEnd) - _capacity <= pos;

    /// <summary>
    /// Waits for everything up to logical <paramref name="end"/> to be on the drive: 10 s, or longer when much is queued
    /// (a slow drive: 2 MB/s at the least), up to 2 minutes.
    /// </summary>
    internal void WaitWritten(long end)
    {
        double seconds = Math.Min(120, Math.Max(10, Interlocked.Read(ref _queuedBytes) / (2.0 * 1024 * 1024)));
        var limit = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (Interlocked.Read(ref _writtenEnd) < end)
        {
            if (HasFailed || DateTime.UtcNow > limit)
                throw new IOException("The replay file could not be written");
            Thread.Sleep(5);
        }
    }

    internal void ReadAt(long pos, Span<byte> into) => RandomAccess.Read(_file, into, pos % _capacity);

    /// <summary>
    /// Writes the queued blocks, then closes the file itself (a drive that stalls never has it closed under a write).
    /// Every error ends the ring's writes, reported once; none ends the process.
    /// </summary>
    void WriteLoop()
    {
        try
        {
            foreach (var w in _queue.GetConsumingEnumerable())
            {
                if (!HasFailed)
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            _write(w.Offset, w.Buffer.AsMemory(0, w.Length));
                            Interlocked.Add(ref _bytesWritten, w.Length);
                            Interlocked.Exchange(ref _writtenEnd, w.End);
                            if (attempt > 0)
                                ObsLog.Info($"replay on the disk: a block written after {attempt} more tries");
                            break;
                        }
                        catch (Exception e) when (attempt < RetryWaitsMs.Length)
                        {
                            ObsLog.Warn($"replay on the disk: a write failed ({e.Message}): trying again");
                            Thread.Sleep(RetryWaitsMs[attempt]); // a busy moment: the drive, an antivirus, the system short of memory
                        }
                        catch (Exception e)
                        {
                            Fail(e);
                            break;
                        }
                    }
                }
                Interlocked.Add(ref _queuedBytes, -w.Length);
                Return(w.Buffer);
            }
        }
        finally
        {
            _file.Dispose();
        }
    }

    /// <summary>The ring takes nothing more, said once (the pipeline goes back to RAM).</summary>
    void Fail(Exception e)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
            Failed?.Invoke(e);
    }

    byte[] Rent(int size) => size == _blockBytes && _pool.TryTake(out var b) ? b : new byte[size];

    void Return(byte[] buffer)
    {
        if (buffer.Length == _blockBytes && _pool.Count < 4)
            _pool.Add(buffer);
    }

    /// <summary>
    /// No more packets; the writer finishes what is queued and closes the file (waited for up to 30 s: the next replay
    /// opens the same file, and waits for it too).
    /// </summary>
    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(30));
    }

    /// <summary>Contiguous runs of a clip's packets (up to <see cref="ReadRun"/> bytes each, never across the file's end).</summary>
    internal static IEnumerable<(int From, int To, long Pos, int Bytes)> Runs(IReadOnlyList<RingEntry> entries, long capacity)
    {
        int from = 0;
        while (from < entries.Count)
        {
            long pos = entries[from].Pos;
            long end = pos + entries[from].Size;
            int to = from + 1;
            while (to < entries.Count && entries[to].Pos == end && end + entries[to].Size - pos <= ReadRun
                   && entries[to].Pos / capacity == pos / capacity)
                end += entries[to++].Size;
            yield return (from, to, pos, (int)(end - pos));
            from = to;
        }
    }

    internal long Capacity => _capacity;
}

/// <summary>A packet in a ring: its stamps, logical place and size.</summary>
internal readonly record struct RingEntry(RingPacket Packet, long Pos, int Size);

/// <summary>A clip's packets in a ring, read back once the writer has them on the drive.</summary>
public sealed class RingSlice
{
    readonly ReplayRing _ring;
    readonly List<RingEntry> _entries;

    internal RingSlice(ReplayRing ring, List<RingEntry> entries)
    {
        _ring = ring;
        _entries = entries;
        Packets = entries.ConvertAll(e => e.Packet);
    }

    public IReadOnlyList<RingPacket> Packets { get; }

    /// <summary>Its pictures (video packets).</summary>
    public int Pictures { get; init; }

    /// <summary>Its pictures over the frames its time holds at the frame rate: under 1 when the encoder skipped frames.</summary>
    public double PictureShare { get; init; } = 1;

    /// <summary>Seconds the newest sound was past the newest picture: an encoder a game starved (the clip ends at the picture).</summary>
    public double PictureBehind { get; init; }

    /// <summary>
    /// Each packet with its bytes, in order, read in runs of contiguous packets (one read per few MB). Throws
    /// <see cref="RingOvertakenException"/> when the ring wrapped over a run (checked before and after each read: what
    /// was read is the packets' own bytes). Each run has a buffer of its own: what was handed out stays valid.
    /// </summary>
    public IEnumerable<(RingPacket Packet, ReadOnlyMemory<byte> Data)> ReadAll()
    {
        if (_entries.Count == 0)
            yield break;
        var last = _entries[^1];
        _ring.WaitWritten(last.Pos + last.Size);
        foreach (var (from, to, pos, bytes) in ReplayRing.Runs(_entries, _ring.Capacity))
        {
            if (!_ring.Intact(pos))
                throw new RingOvertakenException();
            var buffer = new byte[bytes];
            _ring.ReadAt(pos, buffer);
            if (!_ring.Intact(pos))
                throw new RingOvertakenException();
            for (int i = from; i < to; i++)
                yield return (_entries[i].Packet, buffer.AsMemory((int)(_entries[i].Pos - pos), _entries[i].Size));
        }
    }
}
