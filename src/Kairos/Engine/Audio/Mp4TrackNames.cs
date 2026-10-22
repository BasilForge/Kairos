using System.Buffers.Binary;
using System.Text;

namespace Kairos.Engine.Audio;

/// <summary>
/// The names of an MP4's audio tracks, in the name of each track's handler box (moov/trak/mdia/hdlr), where players and
/// editors read them. OBS's muxer leaves "SoundHandler" there, so Kairos writes its own names over it in place after
/// a save: no remux, nothing moves, a name is cut to the slot (12 characters for ffmpeg's files).
/// </summary>
public static class Mp4TrackNames
{
    /// <summary>The audio tracks' names, in track order (empty for a file that is not an MP4 or has none).</summary>
    public static IReadOnlyList<string> Read(string path)
    {
        using var file = File.OpenRead(path);
        return AudioSlots(file).Select(slot => ReadName(file, slot)).ToList();
    }

    /// <summary>Writes <paramref name="names"/> over the audio tracks' names, in order; false when there was nothing to write into.</summary>
    public static bool Write(string path, IReadOnlyList<string> names)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var slots = AudioSlots(file);
        int written = 0;
        for (int i = 0; i < slots.Count && i < names.Count; i++)
        {
            var (start, length) = slots[i];
            if (length < 2)
                continue;
            var bytes = new byte[length]; // zero padded: the name ends at its first zero
            int used = Fit(names[i], bytes.AsSpan(0, (int)length - 1));
            if (used == 0)
                continue;
            file.Position = start;
            file.Write(bytes);
            written++;
        }
        return written > 0;
    }

    /// <summary>UTF-8 of <paramref name="name"/> into <paramref name="slot"/>, cut at a whole character; the bytes used.</summary>
    static int Fit(string name, Span<byte> slot)
    {
        int used = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            int size = rune.Utf8SequenceLength;
            if (used + size > slot.Length)
                break;
            rune.EncodeToUtf8(slot[used..]);
            used += size;
        }
        return used;
    }

    static string ReadName(Stream file, (long Start, long Length) slot)
    {
        var bytes = new byte[Math.Min(slot.Length, 256)];
        file.Position = slot.Start;
        file.ReadExactly(bytes);
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    /// <summary>Where each audio track's handler name lies: (offset, bytes), in track order.</summary>
    static List<(long Start, long Length)> AudioSlots(Stream file)
    {
        var slots = new List<(long, long)>();
        // One moov, near the start or (a replay) after the media: stop at it, past a long recording's fragments.
        foreach (var moov in Boxes(file, 0, file.Length).Where(b => b.Type == "moov").Take(1))
            foreach (var trak in Boxes(file, moov.Body, moov.End).Where(b => b.Type == "trak"))
                foreach (var mdia in Boxes(file, trak.Body, trak.End).Where(b => b.Type == "mdia"))
                    foreach (var hdlr in Boxes(file, mdia.Body, mdia.End).Where(b => b.Type == "hdlr"))
                    {
                        // version and flags, pre_defined, handler_type, 3 reserved words, then the name.
                        long name = hdlr.Body + 24;
                        if (name > hdlr.End)
                            continue;
                        var type = new byte[4];
                        file.Position = hdlr.Body + 8;
                        file.ReadExactly(type);
                        if (Encoding.ASCII.GetString(type) == "soun")
                            slots.Add((name, hdlr.End - name));
                    }
        return slots;
    }

    readonly record struct Box(string Type, long Body, long End);

    /// <summary>The boxes between <paramref name="from"/> and <paramref name="to"/> (32- and 64-bit sizes, "to the end").</summary>
    static IEnumerable<Box> Boxes(Stream file, long from, long to)
    {
        var header = new byte[16];
        for (long at = from; at + 8 <= to;)
        {
            file.Position = at;
            file.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            long body = at + 8;
            if (size == 1)
            {
                file.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                body = at + 16;
            }
            else if (size == 0)
                size = to - at;
            if (size < body - at || at + size > to)
                yield break; // not a box: stop rather than guess
            yield return new Box(type, body, at + size);
            at += size;
        }
    }
}
