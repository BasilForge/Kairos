using System.Runtime.InteropServices;
using Kairos.Native;

namespace Kairos.Engine;

/// <summary>
/// The graphics device's GPU thread priority raised as OBS raises it (libobs-d3d11's set_priority), which libobs manages
/// only when it runs as administrator: it logs "D3D11 GPU priority setup failed (not admin?)". Windows lets any process
/// raise its own (measured 2026-10-22, Windows 11 26300 with hardware scheduling on: the process class was HIGH already,
/// the thread priority went 0 → 7), so a game that takes the graphics card leaves the recording more of it. Intel
/// graphics are left as they are, as OBS leaves them.
/// </summary>
static unsafe class GpuPriority
{
    const int Highest = 7;
    const uint IntelVendor = 0x8086;
    const int ClassHigh = 4, ClassRealtime = 5; // D3DKMT_SCHEDULINGPRIORITYCLASS

    /// <summary>libobs's graphics are Intel's (no priority raised, as OBS leaves them).</summary>
    public static bool Intel { get; private set; }

    /// <summary>The class libobs set (HIGH with hardware scheduling on, REALTIME without), read once; and the class now.</summary>
    static int? _libobs;
    static int _class;

    [DllImport("gdi32.dll")]
    static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);

    [DllImport("gdi32.dll")]
    static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);

    /// <summary>
    /// The process's GPU scheduling class: REALTIME for <see cref="LagRelief.Realtime"/> (any process may take it on
    /// Windows 11, measured 2026-10-22), else the one libobs set; what changed, for the log, or null when nothing did.
    /// </summary>
    public static string? SetClass(bool realtime)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        IntPtr self = process.Handle;
        if (_libobs == null)
        {
            D3DKMTGetProcessSchedulingPriorityClass(self, out int set);
            _libobs = _class = set;
        }
        int wanted = realtime && !Intel ? ClassRealtime : _libobs.Value;
        if (wanted == _class)
            return null;
        int status = D3DKMTSetProcessSchedulingPriorityClass(self, wanted);
        D3DKMTGetProcessSchedulingPriorityClass(self, out int now);
        _class = now;
        string name = now switch { ClassRealtime => "REALTIME (the recording fell behind a game)", ClassHigh => "HIGH", _ => now.ToString() };
        return status != 0 ? $"class {wanted} refused (0x{status:X8}), now {name}" : $"class {name}";
    }
    static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>Raises it on libobs's device (after the first video reset made it); what happened, for the log.</summary>
    public static string Raise()
    {
        Obs.obs_enter_graphics();
        try
        {
            IntPtr device = Obs.gs_get_device_obj();
            if (device == IntPtr.Zero)
                return "no graphics device";
            if (Marshal.QueryInterface(device, in DxgiDevice, out IntPtr dxgi) < 0)
                return "no DXGI device";
            try
            {
                GpuMemory.Watch(dxgi);
                Intel = VendorOf(dxgi) == IntelVendor;
                if (Intel)
                    return "Intel graphics: left as they are";
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Vtable(dxgi)[10])(dxgi, Highest); // SetGPUThreadPriority
                int now;
                ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Vtable(dxgi)[11])(dxgi, &now); // GetGPUThreadPriority
                return hr < 0 ? $"refused (0x{hr:X8})" : $"thread priority {now}";
            }
            finally
            {
                Marshal.Release(dxgi);
            }
        }
        finally
        {
            Obs.obs_leave_graphics();
        }
    }

    /// <summary>The PCI vendor of the device's adapter (IDXGIDevice::GetAdapter, IDXGIAdapter::GetDesc), or 0.</summary>
    static uint VendorOf(IntPtr dxgi)
    {
        IntPtr adapter;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtable(dxgi)[7])(dxgi, &adapter) < 0)
            return 0;
        try
        {
            byte* desc = stackalloc byte[320]; // DXGI_ADAPTER_DESC: Description[128] (256 bytes), then VendorId
            return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)Vtable(adapter)[8])(adapter, desc) < 0 ? 0 : *(uint*)(desc + 256);
        }
        finally
        {
            Marshal.Release(adapter);
        }
    }

    internal static IntPtr* Vtable(IntPtr com) => *(IntPtr**)com;
}

/// <summary>
/// The graphics card's memory as Windows shares it out to Kairos (IDXGIAdapter3::QueryVideoMemoryInfo on libobs's
/// adapter): what Kairos uses of its budget on the card, and what Windows moved to system memory. A game that fills an
/// 8 GB card shrinks that budget; over it, Windows pages Kairos's surfaces out and every frame waits on them, a cause of
/// skipped frames no priority helps with. Read for the log; no administrator needed.
/// </summary>
static unsafe class GpuMemory
{
    static readonly Guid Adapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");
    static IntPtr _adapter;

    [StructLayout(LayoutKind.Sequential)]
    struct Info
    {
        public ulong Budget, CurrentUsage, AvailableForReservation, CurrentReservation;
    }

    /// <summary>Keeps libobs's adapter (from its DXGI device, inside the graphics context) for later reads.</summary>
    public static void Watch(IntPtr dxgiDevice)
    {
        IntPtr adapter;
        if (_adapter != IntPtr.Zero || ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)GpuPriority.Vtable(dxgiDevice)[7])(dxgiDevice, &adapter) < 0)
            return; // IDXGIDevice::GetAdapter
        Marshal.QueryInterface(adapter, in Adapter3, out _adapter); // none on drivers older than WDDM 2.0: no reads
        Marshal.Release(adapter);
    }

    /// <summary>Kairos's use of the card's memory, its budget there, and its use of system memory; null when unknown.</summary>
    public static (ulong Used, ulong Budget, ulong InSystem)? Read()
    {
        if (_adapter == IntPtr.Zero)
            return null;
        Info local, shared;
        var query = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, Info*, int>)GpuPriority.Vtable(_adapter)[14]; // QueryVideoMemoryInfo
        if (query(_adapter, 0, 0 /* LOCAL */, &local) < 0 || query(_adapter, 0, 1 /* NON_LOCAL */, &shared) < 0)
            return null;
        return (local.CurrentUsage, local.Budget, shared.CurrentUsage);
    }
}
