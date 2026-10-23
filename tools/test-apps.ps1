#Requires -Version 7
# Apps mode with two chosen apps: the one in front is recorded. Two players parked off-screen (parked-player.ps1), a test
# pattern (ffplay) and a plain red picture (a renamed copy, another exe); then `--selftest-scene apps "a|b"`.
# Someone may game on this PC: nothing shows, takes focus or makes a sound. Returns the self-test's exit code.
# Usage: test-apps.ps1 -Exe <Kairos.exe> -Out <log> -Scratch <dir> [-DataDir <dir>]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$Scratch,
    [string]$DataDir
)
. (Join-Path $PSScriptRoot 'parked-player.ps1')

New-Item -ItemType Directory -Force $Scratch | Out-Null
$copy = Join-Path $Scratch 'kairosappb.exe'
$ffplay = Get-Item (Get-Command ffplay).Source
Copy-Item ($ffplay.ResolvedTarget ?? $ffplay.FullName) $copy -Force # WinGet's ffplay is a link to the real one

$a = Start-ParkedPlayer -Title 'Kairos app A'
$b = Start-ParkedPlayer -Title 'Kairos app B' -Source 'color=c=red:size=640x360:rate=30' -Exe $copy
try {
    if ($null -eq $a -or $null -eq $b) { "FAIL a player showed no window" | Set-Content $Out; return 3 }
    & (Join-Path $PSScriptRoot 'run-hidden.ps1') -Exe $Exe `
        -Arguments @('--selftest-scene', 'apps', 'Kairos app A:SDL_app:ffplay.exe|Kairos app B:SDL_app:kairosappb.exe') `
        -Out $Out -DataDir $DataDir -TimeoutSec 120
}
finally {
    foreach ($p in @($a, $b)) { if ($null -ne $p -and -not $p.HasExited) { $p.Kill($true) } }
}
