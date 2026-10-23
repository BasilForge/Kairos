#Requires -Version 7
# Runs a Kairos build for a measurement or a self-test: hidden, below-normal priority, no window, no sound, output to a file;
# returns the exit code. Someone may game on this PC while tests run: nothing may show or take focus.
#   -OwnDesktop  runs it on a Windows desktop of its own ("KairosTests"), never shown: for anything that opens a window.
#                An off-screen window is not enough: Windows pulls its pop-ups (lists, menus, toasts) onto the screen.
#                Screen and game captures cannot run there (Windows hands the screen only to the desktop in front).
param(
    [Parameter(Mandatory)][string]$Exe,
    [string[]]$Arguments = @(),
    [string]$ArgumentsFile,
    [Parameter(Mandatory)][string]$Out,
    [string]$DataDir,
    [int]$TimeoutSec = 600,
    [switch]$OwnDesktop
)
if ($ArgumentsFile) { $Arguments = @(Get-Content $ArgumentsFile -Raw | ConvertFrom-Json) }

if ($OwnDesktop) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class KairosTestDesktop
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, width, height, columns, rows, fill, flags;
        public short show, reserved2;
        public IntPtr reserved3, input, output, error;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public IntPtr process, thread; public int pid, tid; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr mode, int flags, uint access, IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    /// <summary>Runs <paramref name="command"/> on the hidden desktop (its children inherit it); its exit code, -1 on timeout.</summary>
    public static int Run(string command, string directory, uint timeoutMs)
    {
        IntPtr desktop = CreateDesktopW("KairosTests", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000 /* GENERIC_ALL */, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        try
        {
            var startup = new StartupInfo { desktop = @"WinSta0\KairosTests" };
            startup.cb = Marshal.SizeOf(startup);
            ProcessInfo info;
            if (!CreateProcessW(null, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* CREATE_NO_WINDOW */,
                    IntPtr.Zero, directory, ref startup, out info))
                throw new System.ComponentModel.Win32Exception();
            try
            {
                if (WaitForSingleObject(info.process, timeoutMs) != 0)
                {
                    TerminateProcess(info.process, 1);
                    return -1;
                }
                uint code;
                GetExitCodeProcess(info.process, out code);
                return (int)code;
            }
            finally
            {
                CloseHandle(info.process);
                CloseHandle(info.thread);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }
}
'@
    # This script again, on that desktop, with the same choices (the arguments in a file: a command line keeps no list).
    $argsFile = [System.IO.Path]::GetTempFileName()
    ConvertTo-Json -InputObject @($Arguments) | Set-Content $argsFile -Encoding utf8
    $pwsh = (Get-Process -Id $PID).Path
    $command = "`"$pwsh`" -NoProfile -NonInteractive -File `"$PSCommandPath`" -Exe `"$Exe`" -ArgumentsFile `"$argsFile`" -Out `"$Out`" -TimeoutSec $TimeoutSec"
    if ($DataDir) { $command += " -DataDir `"$DataDir`"" }
    try { $code = [KairosTestDesktop]::Run($command, (Split-Path $Exe), [uint32](($TimeoutSec + 60) * 1000)) }
    finally { Remove-Item $argsFile -ErrorAction SilentlyContinue }
    if ($code -eq -1) { "TIMEOUT (test desktop)" | Add-Content $Out }
    exit $code
}

$psi = [System.Diagnostics.ProcessStartInfo]::new($Exe)
foreach ($a in $Arguments) { $psi.ArgumentList.Add($a) }
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.WorkingDirectory = Split-Path $Exe
if ($DataDir) { $psi.Environment['KAIROS_DATA'] = $DataDir }
$psi.Environment['SDL_AUDIO_DRIVER'] = 'dummy'
$p = [System.Diagnostics.Process]::Start($psi)
try { $p.PriorityClass = 'BelowNormal' } catch { }
$stdout = $p.StandardOutput.ReadToEndAsync()
$stderr = $p.StandardError.ReadToEndAsync()
if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill($true); "TIMEOUT" | Add-Content $Out }
$p.WaitForExit()
$stdout.Result + $stderr.Result | Set-Content $Out -Encoding utf8
"exit=$($p.ExitCode)" | Add-Content $Out
exit $p.ExitCode
