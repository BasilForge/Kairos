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
                if (VendorOf(dxgi) == IntelVendor)
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

    static IntPtr* Vtable(IntPtr com) => *(IntPtr**)com;
}
