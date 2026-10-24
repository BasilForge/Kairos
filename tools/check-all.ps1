#Requires -Version 7
# Every self-test and measure in Program.cs, hidden and muted, against a build folder (a stage copy with the new
# Kairos.dll). They talk to a pipe of their own (KAIROS_PIPE), never the running Kairos, except --perf startup, which
# reads the running engine's status as the baseline did. Writes <Out>\summary.txt: one line per check.
#   -SelfTests  every --selftest-* command (scenes, recording, screenshot, sound, share, sites, accounts, preview...)
#   -Matrix     clips recorded in each mode with each encoder, each checked with tools\mp4info.py
#   -Perf       every --perf command (three runs of the measured ones)
# Every step that opens a window runs on a Windows desktop of its own (run-hidden.ps1 -OwnDesktop): someone games and
# records on this PC, and Windows pulls an off-screen window's pop-ups onto the screen. Captures stay on the real desktop.
# Usage: check-all.ps1 -Build <folder with Kairos.exe> -Out <folder> [-SelfTests] [-Matrix] [-Perf]
param(
    [Parameter(Mandatory)][string]$Build,
    [Parameter(Mandatory)][string]$Out,
    [switch]$SelfTests,
    [switch]$Matrix,
    [switch]$Perf,
    [string]$Fixtures = (Join-Path $PSScriptRoot '..\tests\Kairos.Tests\bin\Debug\net10.0-windows\win-x64\Fixtures\Share')
)
. (Join-Path $PSScriptRoot 'parked-player.ps1')
$exe = Join-Path $Build 'Kairos.exe'
$run = Join-Path $PSScriptRoot 'run-hidden.ps1'
$mp4info = Join-Path $PSScriptRoot 'mp4info.py'
$Fixtures = (Resolve-Path $Fixtures).Path
$clip = Join-Path $Fixtures 'clip-h264.mp4'
New-Item -ItemType Directory -Force $Out | Out-Null
$summary = Join-Path $Out 'summary.txt'
Add-Content $summary "== Kairos checks $(Get-Date -Format 'yyyy-MM-dd HH:mm')"

function Check([string]$Name, [string[]]$Arguments, [string]$Pattern = 'exit=|FAIL|scene |camera|fresh|saved main|recording saved|encoder:|cpu:',
    [switch]$RealPipe, [scriptblock]$Prepare, [switch]$OwnDesktop) {
    $log = Join-Path $Out "$Name.txt"
    $data = Join-Path $Out "$Name-data"
    New-Item -ItemType Directory -Force $data | Out-Null
    if ($Prepare) { & $Prepare $data }
    if ($RealPipe) { Remove-Item Env:KAIROS_PIPE -ErrorAction SilentlyContinue } else { $env:KAIROS_PIPE = "KairosCheck$PID" }
    $started = Get-Date
    # A window (settings, editor, toast, sign-in page) opens on a desktop of its own: never on the user's screen.
    & $run -Exe $exe -Arguments $Arguments -Out $log -DataDir $data -TimeoutSec 400 -OwnDesktop:($OwnDesktop -or $Arguments[0] -eq '--perf')
    $took = [int]((Get-Date) - $started).TotalSeconds
    $lines = Get-Content $log -ErrorAction SilentlyContinue
    $exit = ($lines | Select-String '^exit=' | Select-Object -Last 1).Line
    $keys = ($lines | Select-String -Pattern $Pattern | Where-Object { $_.Line -notmatch '^exit=' } | Select-Object -Last 6 | ForEach-Object { $_.Line.Trim() }) -join ' | '
    $line = "{0,-34} {1,-8} {2,4}s  {3}" -f $Name, $exit, $took, $keys
    Add-Content $summary $line
    Write-Output $line
    return $lines
}

# The files a self-test saved ("saved main <path> (...)", "recording saved: <path> (...)"), each checked with mp4info.
function Inspect([string]$Name, $Lines) {
    $files = @($Lines | Select-String -Pattern '^saved (main|quick) (.+\.mp4) \(' | ForEach-Object { $_.Matches[0].Groups[2].Value }) +
             @($Lines | Select-String -Pattern '^recording saved: (.+\.mp4) \(' | ForEach-Object { $_.Matches[0].Groups[1].Value })
    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file)) { Add-Content $summary "    $Name file missing: $file"; continue }
        $info = (& python -I $mp4info $file | Select-String '^summary:').Line
        $line = "    {0,-30} {1} <- {2}" -f $Name, $info, (Split-Path $file -Leaf)
        Add-Content $summary $line
        Write-Output $line
    }
}

if ($SelfTests) {
    $picture = Join-Path $Out 'look-source.png'
    if (-not (Test-Path $picture)) {
        Add-Type -AssemblyName System.Drawing
        $bmp = [System.Drawing.Bitmap]::new(640, 360); $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::SteelBlue); $g.FillEllipse([System.Drawing.Brushes]::Orange, 200, 80, 240, 200); $g.Dispose()
        $bmp.Save($picture, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    }
    $null = Check 'selftest-host' @('--selftest-host') 'encoder|exit='
    $null = Check 'selftest-record' @('--selftest-record') 'record|pause|exit='
    $null = Check 'selftest-screenshot' @('--selftest-screenshot') 'shot|png|exit='
    foreach ($scene in 'window', 'window-empty', 'screen-game', 'game-guard') { $null = Check "selftest-scene-$scene" @('--selftest-scene', $scene) }
    $null = Check 'selftest-process-audio' @('--selftest-process-audio') 'tone|audio|exit='
    $null = Check 'selftest-reload' @('--selftest-reload') 'reload|exit=' -OwnDesktop -Prepare { param($d) Set-Content (Join-Path $d 'settings.json') '{}' }
    $null = Check 'selftest-toast' @('--selftest-toast') 'toast|exit=' -OwnDesktop
    $null = Check 'selftest-picture-look' @('--selftest-picture-look', $picture, (Join-Path $Out 'look.png')) 'look|exit=' -OwnDesktop
    $null = Check 'selftest-share' @('--selftest-share', $clip) 'share|exit=' -OwnDesktop
    $null = Check 'selftest-share-4k' @('--selftest-share', $clip, '2160') 'share|exit=' -OwnDesktop
    $null = Check 'selftest-share-10mb' @('--selftest-share', $clip, '--mb', '10') 'share|exit=' -OwnDesktop
    $null = Check 'selftest-share-site' @('--selftest-share-site', $clip) 'publish|exit=' -OwnDesktop
    $null = Check 'selftest-accounts' @('--selftest-accounts') 'accounts|exit=' -OwnDesktop
    foreach ($p in @(@('clip-h264.mp4', '0'), @('clip-h264-5tracks.mp4', '2'), @('clip-av1-2tracks.mp4', '1'))) {
        $null = Check "selftest-preview-$($p[0] -replace '\.mp4','')" @('--selftest-preview', (Join-Path $Fixtures $p[0]), '3', '0', $p[1]) 'preview|exit=' -OwnDesktop
    }
    # A hooked game: the screen under it is not copied, and is again (no black frame) the moment the game closes.
    foreach ($scene in 'screen-game', 'games-game', 'apps-game') {
        $a = Start-ParkedPlayer -Title 'Kairos hook test'
        try { $null = Check "selftest-scene-$scene-hook" @('--selftest-scene', $scene, 'Kairos hook test:SDL_app:ffplay.exe') 'exit=|FAIL|scene |hooked game' }
        finally { if ($a -and -not $a.HasExited) { $a.Kill($true) } }
    }
    $copy = Join-Path $Out 'kairosappb.exe'
    $ffplay = Get-Item (Get-Command ffplay).Source
    Copy-Item ($ffplay.ResolvedTarget ?? $ffplay.FullName) $copy -Force
    $a = Start-ParkedPlayer -Title 'Kairos app A'
    $b = Start-ParkedPlayer -Title 'Kairos app B' -Source 'color=c=red:size=640x360:rate=30' -Exe $copy
    try { $null = Check 'selftest-scene-apps' @('--selftest-scene', 'apps', 'Kairos app A:SDL_app:ffplay.exe|Kairos app B:SDL_app:kairosappb.exe') }
    finally { foreach ($p in $a, $b) { if ($p -and -not $p.HasExited) { $p.Kill($true) } } }
}

if ($Matrix) {
    # A window to record in Apps mode: a test pattern parked off-screen.
    $app = Start-ParkedPlayer -Title 'Kairos matrix app' -Source 'testsrc2=size=1280x720:rate=60'
    try {
        foreach ($mode in @(@('screen', @()), @('app', @('--window', 'Kairos matrix app:SDL_app:ffplay.exe')), @('game', @('--mode', 'game')))) {
            foreach ($codec in @(@('av1', @('--codec', 'av1')), @('hevc', @('--codec', 'hevc')), @('h264', @('--codec', 'h264')), @('x264', @('--codec', 'h264', '--only', 'obs_x264')))) {
                $name = "matrix-$($mode[0])-$($codec[0])"
                $lines = Check $name (@('--selftest', '8') + $mode[1] + $codec[1])
                Inspect $name $lines
            }
        }
        foreach ($v in @(@('matrix-separate-audio', @('--selftest', '8', '--audio', 'separate')), @('matrix-1080p30', @('--selftest', '8', '--res', 'P1080', '--fps', '30')),
                         @('matrix-120fps', @('--selftest', '8', '--fps', '120')), @('matrix-quick-and-record', @('--selftest', '8', '--quick', '--record')),
                         @('matrix-automatic', @('--selftest', '8', '--auto')),
                         # The replay in RAM (no longer the default), then on the disk (a ring file): each format, the quick clip
                         # from the same ring, the camera's own file.
                         @('ram-av1-quick', @('--selftest', '8', '--replay', 'ram', '--quick')), @('ram-h264', @('--selftest', '8', '--replay', 'ram', '--codec', 'h264')),
                         @('disk-av1-quick', @('--selftest', '8', '--replay', 'disk', '--quick')), @('disk-hevc', @('--selftest', '8', '--replay', 'disk', '--codec', 'hevc')),
                         @('disk-h264-fresh', @('--selftest', '8', '--replay', 'disk', '--codec', 'h264', '--fresh')),
                         @('disk-camera-file', @('--selftest', '8', '--replay', 'disk', '--camera', 'corner', '--camera-file')))) {
            $lines = Check $v[0] $v[1]
            Inspect $v[0] $lines
        }
        # The next encoder when one cannot be used: AV1 left out, then every NVENC one, then the chosen one failing to
        # start with its B-frames (an NVENC card before Turing).
        foreach ($f in @(@('fallback-no-av1', 'obs_nvenc_av1_tex'), @('fallback-no-nvenc', 'obs_nvenc_av1_tex,obs_nvenc_hevc_tex,obs_nvenc_h264_tex'))) {
            $lines = Check $f[0] @('--selftest', '8', '--no-encoder', $f[1]) 'fallback|encoder:|exit='
            Inspect $f[0] $lines
        }
    }
    finally { if ($app -and -not $app.HasExited) { $app.Kill($true) } }
}

if ($Perf) {
    $lib = Join-Path $Out 'library'
    New-Item -ItemType Directory -Force (Join-Path $lib 'Desktop') | Out-Null
    for ($i = 0; $i -lt 30; $i++) { Copy-Item $clip (Join-Path $lib "Desktop\Desktop 2026-10-20 10-00-$('{0:00}' -f $i).mp4") -Force }
    $saveClip = Join-Path $Out 'save\Valorant 2026-10-20 10-00-00.mp4'
    New-Item -ItemType Directory -Force (Split-Path $saveClip) | Out-Null
    Copy-Item $clip $saveClip -Force
    foreach ($n in 1..3) {
        $null = Check "perf-engine-$n" @('--selftest', '60') 'cpu:|encoder:|exit='
        $null = Check "perf-startup-$n" @('--perf', 'startup', $clip) 'PERF t (shown|engine-online|editor-first-still|editor-play-first-picture|closed) |exit=' -RealPipe
        $null = Check "perf-parts-$n" @('--perf', 'parts') 'window|exit='
        $null = Check "perf-library-$n" @('--perf', 'library', $lib) 'scan|refresh|pictures|exit='
        $null = Check "perf-grabs-$n" @('--perf', 'grabs', $clip, '50') 'exact|step|exit='
        $null = Check "perf-export-$n" @('--perf', 'export', $clip, (Join-Path $Out "export-$n.mp4")) 'export-done|exit='
    }
    $null = Check 'perf-grabbers' @('--perf', 'grabbers', $clip, '30') 'grabbers 30|exit='
    $null = Check 'perf-window' @('--perf', 'window', '5', 'audio') 'PERF|exit='
    $null = Check 'perf-cycle' @('--perf', 'cycle', '2', $clip, '5') 'PERF (round|after)|exit='
    $null = Check 'perf-editor' @('--perf', 'editor', $clip, '3', '3') 'PERF|exit='
    $null = Check 'perf-save' @('--perf', 'save', $saveClip, '3') 'PERF|saved|exit='
    $null = Check 'perf-walk-ar' @('--perf', 'walk', $Fixtures, (Join-Path $Out 'walk-ar.txt'), 'ar') 'controls pressed|exit='
    $null = Check 'perf-walk-en' @('--perf', 'walk', $Fixtures, (Join-Path $Out 'walk-en.txt'), 'en') 'controls pressed|exit='
}
Remove-Item Env:KAIROS_PIPE -ErrorAction SilentlyContinue
