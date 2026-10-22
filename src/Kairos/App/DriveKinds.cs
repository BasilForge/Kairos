using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kairos.App;

/// <summary>
/// The drives a replay can be kept on (fixed, ready), each with its free space and whether it is a hard disk: Windows's
/// "seek penalty" answer for the volume (no administrator needed). Null when Windows does not say.
/// </summary>
public static class DriveKinds
{
    public sealed record Drive(string Name, long FreeBytes, bool? Hdd);

    public static IReadOnlyList<Drive> Fixed() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => new Drive(d.Name.TrimEnd('\\'), d.AvailableFreeSpace, IsHdd(d.Name.TrimEnd('\\'))))];

    /// <summary>Whether the volume <paramref name="drive"/> ("D:") sits on a disk with a seek penalty (a hard disk).</summary>
    public static bool? IsHdd(string drive)
    {
        using SafeFileHandle volume = CreateFileW($@"\\.\{drive}", 0, 3 /* share read, write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (volume.IsInvalid)
            return null;
        var query = new PropertyQuery { PropertyId = 7 /* StorageDeviceSeekPenaltyProperty */, QueryType = 0 };
        return DeviceIoControl(volume, 0x2D1400 /* IOCTL_STORAGE_QUERY_PROPERTY */, ref query, Marshal.SizeOf<PropertyQuery>(),
                out SeekPenalty answer, Marshal.SizeOf<SeekPenalty>(), out _, IntPtr.Zero)
            ? answer.IncursSeekPenalty != 0
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SeekPenalty
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, ref PropertyQuery input, int inputSize,
        out SeekPenalty output, int outputSize, out int returned, IntPtr overlapped);
}
