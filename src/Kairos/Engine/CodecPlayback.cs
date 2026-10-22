using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Kairos.Settings;

namespace Kairos.Engine;

/// <summary>
/// Whether Windows itself plays a format: Media Foundation has a decoder for it (the Store's AV1 or HEVC extension, or
/// one a PC maker installed). Windows' player, Photos and Explorer's pictures need it; without the HEVC one an HEVC clip
/// shows black (a user's report). Kairos's own player decodes every format itself.
/// </summary>
public static class CodecPlayback
{
    /// <summary>The Microsoft Store's free AV1 Video Extension.</summary>
    public const string Av1ExtensionId = "9MVZQVXJBQ9V";

    static readonly ConcurrentDictionary<VideoCodec, bool> Known = new();

    public static bool CanPlay(VideoCodec codec) => codec == VideoCodec.H264 || Known.GetOrAdd(codec, HasDecoder);

    /// <summary>Asks again next time (its extension was just installed).</summary>
    public static void Forget(VideoCodec codec) => Known.TryRemove(codec, out _);

    static bool HasDecoder(VideoCodec codec)
    {
        // FourCC subtypes on Media Foundation's base GUID: 'AV01', 'HEVC'.
        uint fourCc = codec == VideoCodec.Av1 ? 0x31305641u : 0x43564548u;
        var input = new TypeInfo
        {
            Major = new Guid(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71), // MFMediaType_Video
            Sub = new Guid(fourCc, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71),
        };
        try
        {
            if (MFTEnumEx(VideoDecoders, SyncMft | AsyncMft | HardwareMft | SortAndFilter, ref input, IntPtr.Zero, out IntPtr list, out uint count) < 0)
                return true; // unknown: nothing changes
            for (int i = 0; i < count; i++)
                Marshal.Release(Marshal.ReadIntPtr(list, i * IntPtr.Size));
            Marshal.FreeCoTaskMem(list);
            return count > 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    static readonly Guid VideoDecoders = new("d6c02d4b-6833-45b4-971a-05a4b04bab91"); // MFT_CATEGORY_VIDEO_DECODER
    const uint SyncMft = 0x1, AsyncMft = 0x2, HardwareMft = 0x4, SortAndFilter = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    struct TypeInfo
    {
        public Guid Major;
        public Guid Sub;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    static extern int MFTEnumEx(Guid category, uint flags, ref TypeInfo input, IntPtr output, out IntPtr activates, out uint count);
}
