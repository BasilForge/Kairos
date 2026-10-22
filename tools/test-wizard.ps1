#Requires -Version 7
<#
  Checks what the install wizard tells Kairos when it installs over an existing copy: only the choices the user changed
  (install-choices.txt), nothing for the ones left as they were. Then where installs went wrong for people: a folder with
  Arabic letters in its path installs; a half-finished install whose folder holds a file in use fails with the explained
  message (what to do, the end of Velopack's log, where the log is) in the wizard's own log, not a bare "error 1".
  Runs a KairosTest wizard (tools\release.ps1 -PackId KairosTest) once per scenario, silently, hidden and at below-normal
  priority, into a scratch folder, then uninstalls it and removes everything it made. Prints "wizard ok" or the first
  mismatch.
#>
param(
    [Parameter(Mandatory)][string]$Setup,
    [string]$Scratch = (Join-Path $env:TEMP 'kairos-wizard-test')
)

$ErrorActionPreference = 'Stop'
$pack = 'KairosTest'
$dataDir = Join-Path $env:APPDATA $pack
$choices = Join-Path $dataDir 'install-choices.txt'
$keyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$pack.Wizard_is1"
$app = Join-Path $Scratch 'app'
$arabic = Join-Path $Scratch 'مجلد عربي\app'
$unfinished = Join-Path $Scratch 'unfinished\app'

function Prepare([string]$settings, [string]$prevLang) {
    Remove-Item $choices -ErrorAction SilentlyContinue
    # A [string] parameter turns $null into '': an empty value means no settings file.
    if (-not $settings) { Remove-Item (Join-Path $dataDir 'settings.json') -ErrorAction SilentlyContinue }
    else {
        New-Item -ItemType Directory -Force $dataDir | Out-Null
        Set-Content (Join-Path $dataDir 'settings.json') $settings -Encoding utf8
    }
    Remove-Item $keyPath -Recurse -ErrorAction SilentlyContinue
    if ($prevLang) {
        New-Item $keyPath -Force | Out-Null
        New-ItemProperty $keyPath -Name 'Inno Setup: Language' -Value $prevLang | Out-Null
    }
}

# The wizard's exit code.
function Wizard([string[]]$wizardArgs) {
    $psi = [System.Diagnostics.ProcessStartInfo]::new($Setup)
    foreach ($a in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') + $wizardArgs) { $psi.ArgumentList.Add($a) }
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    try { $p.PriorityClass = 'BelowNormal' } catch { }
    if (-not $p.WaitForExit(300000)) { $p.Kill($true); throw 'the wizard did not finish in 5 minutes' }
    $p.ExitCode
}

function Install([string[]]$extra) {
    $code = Wizard (@("/DIR=$app") + $extra)
    if ($code -ne 0) { throw "the wizard exited with $code" }
}

function Uninstall([string]$dir) {
    if (Test-Path (Join-Path $dir 'current')) {
        $u = Start-Process (Join-Path $dir 'Update.exe') -ArgumentList '--silent', '--uninstall' -WindowStyle Hidden -PassThru
        $u.WaitForExit(120000) | Out-Null
    }
}

function Got() {
    if (-not (Test-Path $choices)) { return '(no file)' }
    ((Get-Content $choices) | Where-Object { $_ }) -join ', '
}

$scenarios = @(
    # /TASKS= without desktopicon: the runs never put a shortcut on the real desktop.
    @{ Name = 'A: first install'; Settings = $null; Key = $null; Args = @('/LANG=de', '/TASKS='); Want = 'language=de, startWithWindows=false' },
    @{ Name = 'B: nothing changed'; Settings = '{ "general": { "startWithWindows": false } }'; Key = 'de'; Args = @('/LANG=de'); Want = '(no file)' },
    @{ Name = 'C: both changed'; Settings = '{ "general": { "startWithWindows": true } }'; Key = 'de'; Args = @('/LANG=en', '/TASKS='); Want = 'language=en, startWithWindows=false' },
    @{ Name = 'D: upgrade from 1.0 (no key)'; Settings = '{ "general": { "startWithWindows": true } }'; Key = $null; Args = @('/LANG=ru'); Want = '(no file)' },
    @{ Name = 'E: unreadable settings'; Settings = 'garbage{'; Key = $null; Args = @('/LANG=es'); Want = '(no file)' },
    # No /LANG: the wizard must open in the language Kairos left in the key (UsePreviousLanguage), not in Windows's.
    @{ Name = 'F: language from the key'; Settings = '{ "general": { "startWithWindows": true } }'; Key = 'de'; Args = @(); Want = '(no file)' },
    # /MERGETASKS= is the user's choice too: seeding keeps out of it.
    @{ Name = 'G: /MERGETASKS'; Settings = '{ "general": { "startWithWindows": false } }'; Key = 'de'; Args = @('/LANG=de', '/MERGETASKS=autostart,!desktopicon'); Want = 'startWithWindows=true' },
    # An option /MERGETASKS= leaves out keeps what Kairos has (here start with Windows stays off).
    @{ Name = 'H: /MERGETASKS leaves the rest'; Settings = '{ "general": { "startWithWindows": false } }'; Key = 'de'; Args = @('/LANG=de', '/MERGETASKS=!desktopicon'); Want = '(no file)' }
)

$failed = $null
New-Item -ItemType Directory -Force $Scratch | Out-Null
try {
    foreach ($s in $scenarios) {
        Prepare $s.Settings $s.Key
        Install $s.Args
        $got = Got
        "{0,-30} {1}" -f $s.Name, $got
        if ($got -ne $s.Want -and -not $failed) { $failed = "$($s.Name): wanted '$($s.Want)', got '$got'" }
    }

    $code = Wizard @("/DIR=$arabic", '/TASKS=', '/LANG=en', "/LOG=$(Join-Path $Scratch 'arabic.log')")
    $ok = $code -eq 0 -and (Test-Path (Join-Path $arabic 'current\Kairos.exe'))
    "{0,-30} exit={1} installed={2}" -f 'I: Arabic letters in the path', $code, $ok
    if (-not $ok -and -not $failed) { $failed = "I: the install into $arabic failed (exit $code)" }

    # What an install stopped half way leaves, with a file another program holds open.
    New-Item -ItemType Directory -Force (Join-Path $unfinished 'current') | Out-Null
    Set-Content (Join-Path $unfinished 'Update.exe') 'left from an install that stopped'
    $held = Join-Path $unfinished 'current\held.bin'
    Set-Content $held 'x'
    $lock = [IO.File]::Open($held, 'Open', 'ReadWrite', 'None')
    try { $code = Wizard @("/DIR=$unfinished", '/TASKS=', '/LANG=en', "/LOG=$(Join-Path $Scratch 'unfinished.log')") } finally { $lock.Dispose() }
    $log = Get-Content (Join-Path $Scratch 'unfinished.log') -Raw
    $explained = $log -match 'could not be installed \(error 1\)' -and $log -match 'A file in this folder is in use' -and
        $log -match 'The setup log ends with' -and $log -match "$pack-setup\.log"
    "{0,-30} exit={1} explained={2}" -f 'J: unfinished, file in use', $code, $explained
    if (($code -eq 0 -or -not $explained) -and -not $failed) { $failed = 'J: the failure was not explained in the wizard log' }
}
finally {
    # Uninstall the test copies and remove what the runs made (the real Kairos is never touched).
    Uninstall $app
    Uninstall $arabic
    Remove-Item (Join-Path $env:TEMP "$pack-setup.log") -ErrorAction SilentlyContinue
    Get-Process -Name $pack -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$app*" } | Stop-Process -Force
    Remove-Item $keyPath -Recurse -ErrorAction SilentlyContinue
    # The wizard's own start-with-Windows value (named after the pack; Kairos's uninstall hook removes only "Kairos").
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if ((Get-ItemProperty $run -Name $pack -ErrorAction SilentlyContinue).$pack -like "*$app*") { Remove-ItemProperty $run -Name $pack }
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
    # Velopack's uninstaller may still be closing files: try for a while.
    for ($i = 0; $i -lt 20 -and (Test-Path $Scratch); $i++) {
        Remove-Item $Scratch -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $Scratch) { Start-Sleep -Milliseconds 500 }
    }
}

if ($failed) { "wizard: FAIL $failed"; exit 1 }
'wizard ok'
