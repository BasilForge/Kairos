#Requires -Version 7
<#
  Offline A/B of Kairos's Ultra preset against candidate NVENC settings, with ffmpeg's own NVENC (the same encoder OBS
  drives) and VMAF. A reference clip (high motion, 1440p60) is encoded once with Kairos's Ultra parameters (CQVBR 22,
  60 Mb/s cap, tune hq, multipass qres, 1 s key frames, 2 B-frames, no lookahead) and once per candidate; each is
  scored against the reference. Without -Reference, a 5 s synthetic one is made (moving test pattern with grain).
  Runs hidden-friendly (no window) at below-normal priority. Prints a table and writes quality-ab.csv to -Out.
  A preset changes only for at least 1 VMAF point at no more than 10 % more size, and never for longer key frames
  (they are why trims start where asked).
#>
param(
    [string]$Reference,
    [string]$Out = (Join-Path $env:TEMP 'kairos-quality-ab'),
    [string]$Codec = 'av1_nvenc'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'

function Run([string[]]$ffArgs) {
    $p = Start-Process ffmpeg -ArgumentList (@('-hide_banner', '-loglevel', 'error', '-y') + $ffArgs) -NoNewWindow -Wait -PassThru `
        -RedirectStandardError (Join-Path $Out 'ffmpeg.log')
    if ($p.ExitCode -ne 0) { throw "ffmpeg failed: $(Get-Content (Join-Path $Out 'ffmpeg.log') -Raw)" }
}

if (-not $Reference) {
    $Reference = Join-Path $Out 'reference.mkv'
    if (-not (Test-Path $Reference)) {
        # Motion everywhere and fine grain: the hardest case for a bitrate cap, like a fast game.
        Run @('-f', 'lavfi', '-i', 'testsrc2=size=2560x1440:rate=60,rotate=angle=t*0.8:fillcolor=0x202020,noise=alls=8:allf=t',
            '-t', '5', '-c:v', 'libx264', '-qp', '0', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', $Reference)
    }
}

# Kairos's Ultra (EncoderPresets.Plan + EncoderCatalog.Settings at 1440p60 AV1), and the candidates.
function Settings([int]$lookahead = 0, [string]$multipass = 'qres', [int]$gop = 60) {
    @('-c:v', $Codec, '-preset', 'p5', '-tune', 'hq', '-rc', 'vbr', '-cq', '22', '-b:v', '0', '-maxrate', '60M', '-bufsize', '120M',
        '-bf', '2', '-g', "$gop", '-rc-lookahead', "$lookahead", '-multipass', $multipass, '-pix_fmt', 'yuv420p')
}
$variants = [ordered]@{
    'kairos-ultra'      = Settings
    'multipass-fullres' = Settings -multipass 'fullres'
    'lookahead-20'      = Settings -lookahead 20
    'keyframes-2s'      = Settings -gop 120
}

$rows = foreach ($name in $variants.Keys) {
    $file = Join-Path $Out "$name.mp4"
    Run (@('-i', $Reference) + $variants[$name] + @('-an', $file))
    $json = Join-Path $Out "$name.vmaf.json"
    $vmafPath = $json.Replace('\', '/').Replace(':', '\:')
    Run @('-i', $file, '-i', $Reference, '-lavfi', "[0:v][1:v]libvmaf=log_fmt=json:log_path='$vmafPath':n_threads=8", '-f', 'null', '-')
    $vmaf = (Get-Content $json -Raw | ConvertFrom-Json).pooled_metrics.vmaf.mean
    [pscustomobject]@{ Variant = $name; VMAF = [math]::Round($vmaf, 2); MB = [math]::Round((Get-Item $file).Length / 1MB, 1) }
}
$rows | Format-Table -AutoSize | Out-String -Width 120
$rows | Export-Csv (Join-Path $Out 'quality-ab.csv') -NoTypeInformation
$ref = $rows | Where-Object Variant -eq 'kairos-ultra'
foreach ($r in $rows | Where-Object Variant -ne 'kairos-ultra') {
    $gain = $r.VMAF - $ref.VMAF
    $more = ($r.MB - $ref.MB) / [math]::Max(0.1, $ref.MB)
    $verdict = if ($r.Variant -like 'keyframes*') { 'not taken (trims start where asked)' }
               elseif ($gain -ge 1 -and $more -le 0.10) { 'worth taking' } else { 'not worth it' }
    "{0,-18} {1,6:+0.00;-0.00} VMAF {2,6:+0%;-0%} size  {3}" -f $r.Variant, $gain, $more, $verdict
}
