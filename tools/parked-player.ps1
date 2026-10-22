#Requires -Version 7
# Dot-sourced by the capture tests: Start-ParkedPlayer plays an FFmpeg test source in an ffplay window (D3D11) parked
# off-screen, never activated, with no taskbar button and no sound; it stays a normal visible window, which window and
# game capture find. Someone may game on this PC: nothing shows or takes focus.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ParkedWindow {
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface ITaskbarList { void HrInit(); void AddTab(IntPtr h); void DeleteTab(IntPtr h); void ActivateTab(IntPtr h); void SetActiveAlt(IntPtr h); }
  [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarList { }

  // The process's visible top-level window, or zero.
  public static IntPtr Find(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  // Off-screen, at the bottom, not activated, its taskbar button removed.
  public static void Park(IntPtr h) {
    SetWindowPos(h, new IntPtr(1), -7000, -7000, 0, 0, 0x1 | 0x10); // NOSIZE NOACTIVATE
    var list = (ITaskbarList)new TaskbarList();
    list.HrInit();
    list.DeleteTab(h);
  }
}
"@

# Starts ffplay (or a renamed copy, -Exe) showing -Source in a window titled -Title, parks it; returns the process, or
# $null when no window showed.
function Start-ParkedPlayer([string]$Title, [string]$Source = 'testsrc2=size=640x360:rate=30', [string]$Exe = (Get-Command ffplay).Source) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($a in @('-hide_banner', '-loglevel', 'quiet', '-an', '-noborder', '-left', '-7000', '-top', '-7000',
                     '-window_title', $Title, '-f', 'lavfi', '-i', $Source)) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.Environment['SDL_RENDER_DRIVER'] = 'direct3d11'
    $psi.Environment['SDL_AUDIO_DRIVER'] = 'dummy'
    $psi.Environment['SDL_WINDOW_ACTIVATE_WHEN_SHOWN'] = '0'
    $player = [System.Diagnostics.Process]::Start($psi)
    try { $player.PriorityClass = 'BelowNormal' } catch { }
    $window = [IntPtr]::Zero
    for ($i = 0; $i -lt 200 -and $window -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 25
        $window = [ParkedWindow]::Find([uint32]$player.Id)
    }
    if ($window -eq [IntPtr]::Zero) {
        if (-not $player.HasExited) { $player.Kill($true) }
        return $null
    }
    [ParkedWindow]::Park($window)
    return $player
}
