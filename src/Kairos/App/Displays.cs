using System.Runtime.InteropServices;

namespace Kairos.App;

/// <summary>The screens Windows has: each one's device ("\\.\DISPLAY1"), the GPU that drives it, and its id as libobs names it.</summary>
public static class Displays
{
    public sealed record Screen(string Device, string Gpu, string MonitorId, bool Primary);

    /// <summary>Every attached screen (no handle opened: EnumDisplayDevices only).</summary>
    public static List<Screen> All()
    {
        var list = new List<Screen>();
        var adapter = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if ((adapter.StateFlags & AttachedToDesktop) == 0)
                continue;
            var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            string id = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */) ? monitor.DeviceID : "";
            list.Add(new Screen(adapter.DeviceName, adapter.DeviceString, id, (adapter.StateFlags & PrimaryDevice) != 0));
        }
        return list;
    }

    /// <summary>The screen libobs captures for <paramref name="monitorId"/> (empty: the primary one); null when unknown.</summary>
    public static Screen? For(string? monitorId) => Pick(All(), monitorId);

    public static Screen? Pick(IReadOnlyList<Screen> screens, string? monitorId) =>
        screens.FirstOrDefault(s => monitorId is { Length: > 0 } && s.MonitorId.Equals(monitorId, StringComparison.OrdinalIgnoreCase))
        ?? (monitorId is { Length: > 0 } && screens.Count > 1 ? null : screens.FirstOrDefault(s => s.Primary) ?? screens.FirstOrDefault());

    /// <summary>
    /// The screen's own mode: its size and refresh rate as set in Windows (60 Hz and 1080p when unknown). Not the mode a
    /// game in exclusive full screen holds for a while: the hardware profile (and what failed on it) stays the same.
    /// </summary>
    public static (int Width, int Height, int Hz) Mode(string? device)
    {
        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        bool read = EnumDisplaySettings(device, -2 /* ENUM_REGISTRY_SETTINGS */, ref mode) && mode.dmPelsWidth > 0
                    || EnumDisplaySettings(device, -1 /* ENUM_CURRENT_SETTINGS */, ref mode);
        return read && mode.dmPelsWidth > 0
            ? (mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency > 1 ? NominalHz(mode.dmDisplayFrequency) : 60)
            : (1920, 1080, 60);
    }

    static readonly int[] Rates = [24, 25, 30, 48, 50, 60, 72, 75, 90, 100, 120, 144, 165, 170, 175, 180, 200, 240, 280, 300, 360, 480, 500, 540];

    /// <summary>Windows says 59 for a 59.94 Hz screen (and 119, 143...): the rate it is sold as.</summary>
    public static int NominalHz(int hz) => Array.IndexOf(Rates, hz) < 0 && Array.IndexOf(Rates, hz + 1) >= 0 ? hz + 1 : hz;

    /// <summary>Every display adapter Windows has a driver for: its name and driver version (from the registry, read only).</summary>
    public static List<(string Name, string Driver)> Adapters()
    {
        var list = new List<(string, string)>();
        using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (string sub in cls?.GetSubKeyNames() ?? [])
        {
            if (sub.Length != 4 || !char.IsDigit(sub[0]))
                continue;
            try
            {
                using var key = cls!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is string name && !list.Any(a => a.Item1 == name))
                    list.Add((name, key.GetValue("DriverVersion") as string ?? ""));
            }
            catch (System.Security.SecurityException)
            {
                // A key Windows keeps to itself: not a display adapter Kairos needs.
            }
        }
        return list;
    }

    public static string CpuName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "";
    }

    /// <summary>Running on a battery now (a laptop unplugged).</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var power) && power.ACLineStatus == 0 && power.BatteryFlag != 128;

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE devMode);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    const uint AttachedToDesktop = 0x1, PrimaryDevice = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE info, uint flags);
}
