#Requires -Version 7
<#
  Builds a Kairos release into releases\ (git-ignored, and the local update folder for testing):
  - Kairos-Setup.exe, the install wizard people download (Inno Setup 6, installer\Kairos.iss), which carries
    Kairos-win-Setup.exe, Velopack's one-click installer, and runs it into the folder chosen in the wizard;
  - the update files (full and delta packages, releases.win.json); the Visual C++ runtime is installed by the
    Setup only when the PC lacks it;
  - Kairos-<version>-source.zip, the release's complete source (the GPL), from the commit it is built from;
  - captions-runtime\kairos-captions-runtime-1.9.5.zip, auto captions' engine, made once from third_party\whisper\bin.
  - SHA256SUMS.txt, and release-notes-<version>.md: the notes with every file's SHA-256, for the release page.
  Nothing is uploaded: publishing is a separate step, run by hand.

  Signing (optional): with a code-signing certificate in Windows' certificate store (a .pfx imported, or a hardware
  token), pass its thumbprint as -SignThumbprint or in KAIROS_SIGN_THUMBPRINT. Kairos.exe, Kairos.dll, Velopack's
  Update.exe and Setup, and the install wizard are then signed with SHA-256 and timestamped (signtool from the Windows
  SDK), and the build stops if any of them is not validly signed. Without one the build is unsigned, as before. No
  certificate, key or password ever goes in the repository.

  An important update: put these lines in its notes (GitHub's page does not show them). Kairos then shows a card on the
  Status page, says so every day until it is in (never over a game), downloads it by itself and puts it in at the next
  start (never during a recording). The summary lines are the card's, one set per language (others get the notes' first
  points); "|" between lines:
    <!-- important -->
    <!-- summary.ar: إصلاح الشاشة السوداء | التسجيل أخف -->
    <!-- summary.en: Black screen fix | Lighter recording -->
#>
param(
    [string]$PackId = 'Kairos',
    [string]$Version,
    [string]$Out = 'releases',
    [string]$ExpectedObs = '32.2.2',
    [string]$SignThumbprint = $env:KAIROS_SIGN_THUMBPRINT,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
# Auto captions' engine as CaptionRuntime pins it (AssetName, Files); this order makes the same zip again.
$captionsZip = 'kairos-captions-runtime-1.9.5.zip'
$whisperDlls = 'whisper.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll', 'ggml-vulkan.dll'
# The source zip is made from the last commit: refuse a build that differs from it.
if (git -C $repo status --porcelain --untracked-files=no) { throw 'Commit your changes first: the source zip must match the build' }
[xml]$proj = Get-Content (Join-Path $repo 'src\Kairos\Kairos.csproj')
if (-not $Version) { $Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$notes = Join-Path $repo "docs\release-notes\$Version.md"
if (-not (Test-Path $notes)) { throw "Write the release notes first: docs\release-notes\$Version.md" }
$important = [bool](Select-String -Path $notes -Pattern '<!--\s*important\s*-->' -Quiet)
Write-Host ("Important update: " + ($important ? 'yes (a Status card, daily notice, installs itself at the next start)' : 'no (one notice and the update bar)'))
$out = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path $repo $Out }
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue)?.Source ?? (Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe')
if (-not (Test-Path $vpk)) { throw 'vpk not found: dotnet tool install -g vpk --version 1.2.161' }

# Signing, when a certificate is named (see above); checked before the long build starts.
$signParams = $null
if ($SignThumbprint) {
    $SignThumbprint = $SignThumbprint -replace '\s', ''
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object Thumbprint -eq $SignThumbprint | Select-Object -First 1
    if (-not $cert) { throw "No certificate with thumbprint $SignThumbprint in the CurrentUser or LocalMachine store" }
    if (-not $cert.HasPrivateKey) { throw "The certificate $SignThumbprint has no private key on this PC" }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -ErrorAction SilentlyContinue | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool not found: install the Windows SDK (signing tools)' }
    $signParams = "/sha1 $SignThumbprint /fd sha256 /tr $TimestampUrl /td sha256"
    Write-Host "Signing with $($cert.Subject) (until $($cert.NotAfter.ToString('yyyy-MM-dd')))"
} else {
    Write-Warning 'Unsigned build (no -SignThumbprint or KAIROS_SIGN_THUMBPRINT): Windows will name no publisher.'
}

# The program, in one folder: libobs from the installed OBS (read only), then Kairos with .NET inside.
$stage = Join-Path $repo "dist\stage-$PackId-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
& (Join-Path $PSScriptRoot 'stage-runtime.ps1') -Dest $stage | Write-Output
$obsVersion = (Get-Content (Join-Path $stage 'libobs-version.txt')).Trim()
if ($obsVersion -ne $ExpectedObs) { throw "Installed OBS is $obsVersion, expected $ExpectedObs (pass -ExpectedObs after re-testing)" }
dotnet publish (Join-Path $repo 'src\Kairos') -c Release -o $stage --self-contained true "-p:Version=$Version" --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# Debug symbols and import libraries are only for developers.
Get-ChildItem $stage -File | Where-Object { $_.Extension -in '.pdb', '.lib', '.xml' } | Remove-Item
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md') { Copy-Item (Join-Path $repo $f) $stage }
Copy-Item (Join-Path $repo 'licenses') $stage -Recurse

# The installer and the update files.
New-Item -ItemType Directory -Force $out | Out-Null
# Velopack signs what is not signed yet (Kairos.exe, Kairos.dll, Update.exe, its Setup) before packing.
$signArgs = $signParams ? @('--signParams', $signParams) : @()
& $vpk pack --packId $PackId --packVersion $Version --packDir $stage --mainExe Kairos.exe --packTitle $PackId --packAuthors Kairos `
    --icon (Join-Path $repo 'src\Kairos\app.ico') --shortcuts StartMenuRoot --framework vcredist145-x64 --releaseNotes $notes --outputDir $out @signArgs
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }
# The release page carries the Setup only: no portable zip.
Get-ChildItem $out -Filter "$PackId-win-Portable.zip" | Remove-Item

# The install wizard around Velopack's Setup: what the release page offers.
$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe" |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup --scope user' }
# Microsoft's full Visual C++ runtime installer, which the wizard runs only on a PC without it (git-ignored: Microsoft's
# file, about 25 MB). Get it once from https://aka.ms/vc14/vc_redist.x64.exe into installer\redist.
$vcRedist = Join-Path $repo 'installer\redist\vc_redist.x64.exe'
if (-not (Test-Path $vcRedist)) { throw "Put Microsoft's vc_redist.x64.exe in installer\redist (https://aka.ms/vc14/vc_redist.x64.exe)" }
$vcSign = Get-AuthenticodeSignature $vcRedist
if ($vcSign.Status -ne 'Valid' -or $vcSign.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw "installer\redist\vc_redist.x64.exe is not signed by Microsoft" }
$vcVersion = [version](Get-Item $vcRedist).VersionInfo.ProductVersion
if ($vcVersion -lt [version]'14.50') { throw "installer\redist\vc_redist.x64.exe is ${vcVersion}: Kairos needs 14.50 or newer" }
if ((Get-Item $vcRedist).Length -lt 10MB) { throw 'installer\redist\vc_redist.x64.exe is the small web installer: use the full one' }
Write-Host "Visual C++ runtime in the wizard: $vcVersion (installed only where it is missing)"
& $iscc /Qp "/DAppVersion=$Version" "/DPackId=$PackId" "/DOutDir=$out" (Join-Path $repo 'installer\Kairos.iss')
if ($LASTEXITCODE -ne 0) { throw 'the install wizard did not build' }
$wizard = Join-Path $out "$PackId-Setup.exe"
if ($signParams) {
    & $signtool.FullName sign $signParams.Split(' ') $wizard
    if ($LASTEXITCODE -ne 0) { throw 'signing the install wizard failed' }
    # Kairos.exe and Kairos.dll as the update package carries them.
    $check = Join-Path ([IO.Path]::GetTempPath()) "kairos-signcheck-$Version"
    if (Test-Path $check) { Remove-Item $check -Recurse -Force }
    New-Item -ItemType Directory $check | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = [IO.Compression.ZipFile]::OpenRead((Join-Path $out "$PackId-$Version-full.nupkg"))
    foreach ($name in 'Kairos.exe', 'Kairos.dll') {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($package.GetEntry("lib/app/$name"), (Join-Path $check $name))
    }
    $package.Dispose()
    foreach ($file in $wizard, (Join-Path $out "$PackId-win-Setup.exe"), (Join-Path $check 'Kairos.exe'), (Join-Path $check 'Kairos.dll')) {
        $sign = Get-AuthenticodeSignature $file
        if ($sign.Status -ne 'Valid' -or $sign.SignerCertificate.Thumbprint -ne $SignThumbprint) { throw "$file is not signed with the certificate ($($sign.Status))" }
    }
    Remove-Item $check -Recurse -Force
    Write-Host 'Signed: the install wizard, Velopack''s Setup, Kairos.exe and Kairos.dll'
}

if ($PackId -eq 'Kairos') {
    # The GPL: this release's complete source, from the commit it was built from.
    $source = Join-Path $out "Kairos-$Version-source.zip"
    if (Test-Path $source) { Remove-Item $source }
    git -C $repo archive --format=zip --prefix="Kairos-$Version-source/" -o $source HEAD
    if ($LASTEXITCODE -ne 0) { throw 'git archive failed' }
}

# Auto captions' engine, downloaded on first use (made once: its size and fingerprint are pinned in CaptionRuntime).
$captions = Join-Path $out "captions-runtime\$captionsZip"
if (-not (Test-Path $captions)) {
    New-Item -ItemType Directory -Force (Split-Path $captions) | Out-Null
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($captions, 'Create')
    foreach ($dll in $whisperDlls) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $repo "third_party\whisper\bin\$dll"), $dll, 'SmallestSize')
    }
    $zip.Dispose()
    Write-Warning "Made ${captions}: pin its size and SHA-256 in CaptionRuntime if it changed."
}

$released = @(Get-ChildItem $out -File | Where-Object { $_.Name -like "$PackId-*" -or $_.Name -like 'releases.*' -or $_.Name -like 'assets.*' }) +
    @(Get-Item $captions)
$released | ForEach-Object { '{0,-48} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }

# Fingerprints, so anyone can check a download (Get-FileHash <file>): a file for the page, and the notes with them.
$sums = $released | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }
Set-Content (Join-Path $out 'SHA256SUMS.txt') $sums -Encoding utf8NoBOM
$pageNotes = Join-Path $out "release-notes-$Version.md"
Set-Content $pageNotes ((Get-Content $notes -Raw).TrimEnd() + "`n`n## SHA-256`n`n" + '```' + "`n" + ($sums -join "`n") + "`n" + '```' + "`n") -Encoding utf8NoBOM -NoNewline
Write-Host "SHA-256 of every file: SHA256SUMS.txt; the release page's notes with them: $pageNotes"
