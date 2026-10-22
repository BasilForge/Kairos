#Requires -Version 7
# The game layer (Screen and Apps) hooks a real D3D11 window and shows its picture: ffplay plays a test pattern as the
# "game", parked off-screen (parked-player.ps1); then `--selftest-scene screen-game <window>`.
# Someone may game on this PC: nothing shows, takes focus or makes a sound. Returns the self-test's exit code.
# Usage: test-game-layer.ps1 -Exe <Kairos.exe> -Out <log> [-DataDir <dir>]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Out,
    [string]$DataDir
)
. (Join-Path $PSScriptRoot 'parked-player.ps1')

$player = Start-ParkedPlayer -Title 'Kairos hook test'
if ($null -eq $player) { "FAIL ffplay showed no window" | Set-Content $Out; return 3 }
try {
    # title:class:exe, as WindowTarget.Encode writes it (SDL2's window class)
    & (Join-Path $PSScriptRoot 'run-hidden.ps1') -Exe $Exe -Arguments @('--selftest-scene', 'screen-game', 'Kairos hook test:SDL_app:ffplay.exe') `
        -Out $Out -DataDir $DataDir -TimeoutSec 120
}
finally {
    if (-not $player.HasExited) { $player.Kill($true) }
}
