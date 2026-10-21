using System.Runtime.InteropServices;
using Kairos.Engine;

namespace Kairos;

/// <summary>Tells libobs which windowing system the program runs under, and hands it the X connection.</summary>
static class NixPlatform
{
    const int PlatformX11Egl = 1; // OBS_NIX_PLATFORM_X11_EGL (0 is INVALID)

    static IntPtr _display;

    public static void Prepare()
    {
        if (_display != IntPtr.Zero)
            return;
        // NVIDIA's driver waits for the GPU by spinning on sched_yield, which showed up as a full 10% of a core in libobs's encode
        // thread at 1440p60; this makes it sleep instead (measured: 12.6% -> 2.5% there). It reads it when the first GL context
        // is made, and it must be set natively: Environment.SetEnvironmentVariable never reaches getenv().
        if (Environment.GetEnvironmentVariable("__GL_YIELD") == null)
            setenv("__GL_YIELD", "USLEEP", 0);
        XInitThreads(); // libobs uses the connection from its own threads (Qt does this in OBS Studio)
        _display = XOpenDisplay(IntPtr.Zero);
        if (_display == IntPtr.Zero)
            throw new InvalidOperationException("Could not open the X display (is DISPLAY set? Wayland needs XWayland)");
        obs_set_nix_platform(PlatformX11Egl);
        obs_set_nix_platform_display(_display);
        ObsLog.Info("libobs platform: X11/EGL");
    }

    [DllImport("libc")] static extern int setenv([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string value, int overwrite);
    [DllImport("obs")] static extern void obs_set_nix_platform(int platform);
    [DllImport("obs")] static extern void obs_set_nix_platform_display(IntPtr display);
    [DllImport("libX11.so.6")] static extern int XInitThreads();
    [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
}
