#Requires -Version 5.1
<#
.SYNOPSIS
    KillerPDF release script: build payload → sign inner app → pack launcher → sign launcher → verify → publish.
.DESCRIPTION
    1. Locates and hashes pdfium.dll for the published checksum summary.
    2. Builds the ordinary multi-file KillerPDF.App payload without Costura/Fody weaving.
    3. Signs KillerPDF.App.exe, regenerates its hash manifest, compresses that payload once,
       and embeds it in the public portable and installer executables.
    4. Signs both public executables. Prefers CertThumbprint (exact match) over CertName (CN match)
       and retries the timestamp across three TSA endpoints.
    5. Runs "signtool verify /pa /v" as a post-sign gate - aborts if either signature chain
       is not trusted to an accepted root.
    6. Builds the GPL source archive, checksums, and publish summary.

.PARAMETER CertThumbprint
    Preferred. SHA1 thumbprint of your code-signing certificate (40 hex chars, no spaces).
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Thumbprint, Subject
    Omit if using CertName instead.

.PARAMETER CertName
    Fallback. CN (Subject) of your certificate as it appears in the Windows cert store.
    Ignored when CertThumbprint is supplied.

.PARAMETER SkipSign
    Skip signing for local test builds. Prints a red warning banner.

.EXAMPLE
    .\release.ps1 -CertThumbprint "AABBCC..."
.EXAMPLE
    .\release.ps1 -CertName "Open Source Developer, Stephen Riley"
.EXAMPLE
    .\release.ps1 -SkipSign
#>
param(
    [string]$CertThumbprint = "",
    [string]$CertName       = "Open Source Developer Stephen Riley",
    [switch]$SkipSign,
    # Everything except tag push and GitHub release creation.
    [switch]$DryRun,
    # Skip build + sign and publish the artifacts already in the publish folder
    # (use after a completed normal run, so the signed exe is not rebuilt).
    [switch]$PublishOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj         = Join-Path $PSScriptRoot "KillerPDF.csproj"
$publishDir   = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\publish"
$portableExe   = Join-Path $publishDir "KillerPDF-Portable.exe"
$installerExe  = Join-Path $publishDir "KillerPDF.exe"
$packageBuild  = Join-Path $PSScriptRoot "build\build-packages.ps1"
$portablePayloadDir  = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\portable-package\payload"
$installerPayloadDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\installer-package\payload"
$innerExes = @(
    (Join-Path $portablePayloadDir "KillerPDF.App.exe"),
    (Join-Path $installerPayloadDir "KillerPDF.App.exe")
)

# TSA endpoints - tried in order; first success wins.
$tsaList = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
    "http://ts.ssl.com"
)

# Resolve the repo's default branch instead of hardcoding it, so the same script works across
# the Killer family. origin/HEAD is the best hint but it can go stale - it keeps naming a
# branch that was renamed away, which is exactly the state this repo was in - so a candidate
# is only accepted if it still exists on the remote. Order: origin/HEAD, then main, then master.
# Call it from inside the Push-Location block so git runs against this repo.
function Get-DefaultBranch {
    $remoteHeads = @(git ls-remote --heads origin 2>$null) |
        ForEach-Object { ($_ -split '\s+')[-1] -replace '^refs/heads/', '' }
    if (-not $remoteHeads) { return $null }

    $candidates = @()
    $originHead = git symbolic-ref --quiet refs/remotes/origin/HEAD 2>$null
    if ($originHead) { $candidates += (($originHead -replace '^refs/remotes/origin/', '').Trim()) }
    foreach ($c in @('main', 'master')) { if ($candidates -notcontains $c) { $candidates += $c } }

    foreach ($c in $candidates) {
        if ($c -and $remoteHeads -contains $c) { return $c }
    }
    return $null
}

Write-Host "`n==> Release metadata preflight..." -ForegroundColor Cyan
$csprojRaw = Get-Content -Path $proj -Raw
if ($csprojRaw -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') {
    throw "No <Version>x.y.z</Version> found in KillerPDF.csproj"
}
$Version = $Matches[1]
$Tag = "v$Version"
$engineProject = Join-Path $PSScriptRoot 'engine\KillerPdf.Engine\KillerPdf.Engine.csproj'
[xml]$engineMetadata = Get-Content -LiteralPath $engineProject -Raw
$engineVersion = [string]$engineMetadata.Project.PropertyGroup.Version
if ($engineVersion -ne $Version) {
    throw "Engine version $engineVersion does not match app version $Version. Update both before releasing."
}
$changelog = Get-Content -Path (Join-Path $PSScriptRoot 'CHANGELOG.md') -Raw
if ($changelog -match ('(?im)^## \[' + [regex]::Escape($Version) + '\] - UNRELEASED\s*$')) {
    throw "CHANGELOG.md section [$Version] is still marked Unreleased"
}
if ($changelog -notmatch [regex]::Escape("## [$Version]")) {
    throw "CHANGELOG.md has no [$Version] section"
}

# The About card shows <ReleaseDate> beside the version so users can tell how old
# their build is. It is a hand-edited csproj field, so it silently goes stale unless
# something checks it - that something is here. It must equal the date on this
# version's CHANGELOG section, which is the date the release actually goes out.
if ($csprojRaw -notmatch '<ReleaseDate>([0-9]{4}-[0-9]{2}-[0-9]{2})</ReleaseDate>') {
    throw "No <ReleaseDate>yyyy-MM-dd</ReleaseDate> found in KillerPDF.csproj"
}
$releaseDate = $Matches[1]
if ($changelog -notmatch ('## \[' + [regex]::Escape($Version) + '\] - ([0-9]{4}-[0-9]{2}-[0-9]{2})')) {
    throw "CHANGELOG.md section [$Version] has no yyyy-MM-dd date"
}
$changelogDate = $Matches[1]
if ($releaseDate -ne $changelogDate) {
    throw "csproj <ReleaseDate> is $releaseDate but CHANGELOG [$Version] is dated $changelogDate. Bump the csproj."
}
Write-Host "    Release date: $releaseDate"

Write-Host "`n==> Git preflight..." -ForegroundColor Cyan
Push-Location $PSScriptRoot
try {
    $defaultBranch = Get-DefaultBranch
    if (-not $defaultBranch) { throw "Could not determine the default branch from origin" }
    $branch = git rev-parse --abbrev-ref HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not read the current branch" }
    if ($branch.Trim() -ne $defaultBranch) { throw "On branch '$branch', expected $defaultBranch" }
    $dirty = git status --porcelain
    if ($LASTEXITCODE -ne 0) { throw "Could not read the working tree status" }
    if ($dirty) { throw "Working tree is not clean. Commit your changes first:`n$($dirty -join "`n")" }
    git fetch origin $defaultBranch --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not fetch origin/$defaultBranch; cannot verify release source" }
    $localHead = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not read the local commit" }
    $remoteHead = git rev-parse "refs/remotes/origin/$defaultBranch"
    if ($LASTEXITCODE -ne 0) { throw "Could not read origin/$defaultBranch" }
    if ($localHead.Trim() -ne $remoteHead.Trim()) {
        throw "Local $defaultBranch and origin/$defaultBranch differ. Push or pull first."
    }
} finally {
    Pop-Location
}

if (-not $PublishOnly) {

# ── 0. SimplySign preflight ──────────────────────────────────────────────────
if (-not $SkipSign) {
    $ssProc = Get-Process -Name "SimplySignDesktop" -ErrorAction SilentlyContinue
    if (-not $ssProc) {
        Write-Host ""
        Write-Warning "SimplySign Desktop does not appear to be running."
        Write-Host    "    Start it and wait for it to show 'Connected', then press Enter to continue."
        Write-Host    "    Or press Ctrl+C to abort."
        $null = Read-Host
    } else {
        Write-Host "`n==> SimplySign Desktop is running (PID $($ssProc.Id))." -ForegroundColor Green
    }
}

# ── 0. Translation parity ───────────────────────────────────────────────────
# Every localization must carry the complete English key set, and the placeholders have to match:
# a translation loads perfectly and still throws at runtime when string.Format is handed a value
# the translation dropped or renumbered. Ported from KillerNotes, which took it from Killendar.
#
# First, because it is a pure source check and costs nothing. Without it this tree silently drifted
# to ten locales missing the same 20 keys - the whole crash dialog among them - and a pl-PL key that
# does not exist in English. Nothing reported it until users did.
Write-Host "`n==> Checking translations..." -ForegroundColor Cyan

function Read-StringMap([string]$Path) {
    [xml]$document = Get-Content -Path $Path -Raw
    $map = @{}
    foreach ($node in $document.ResourceDictionary.ChildNodes) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
        $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ($key) { $map[$key] = [string]$node.InnerText }
    }
    return $map
}

$stringsDir = Join-Path $PSScriptRoot 'Strings'
$englishStrings = Read-StringMap (Join-Path $stringsDir 'en-US.xaml')
if ($englishStrings.Count -eq 0) { throw "English translation file contains no resource keys." }
foreach ($localeFile in Get-ChildItem $stringsDir -Filter '*.xaml') {
    if ($localeFile.Name -eq 'en-US.xaml') { continue }
    $localized = Read-StringMap $localeFile.FullName
    $missing = @($englishStrings.Keys | Where-Object { -not $localized.ContainsKey($_) })
    $extra   = @($localized.Keys | Where-Object { -not $englishStrings.ContainsKey($_) })
    $empty   = @($localized.Keys | Where-Object { [string]::IsNullOrWhiteSpace($localized[$_]) })
    $placeholderMismatch = @()
    foreach ($key in $englishStrings.Keys) {
        if (-not $localized.ContainsKey($key)) { continue }
        $englishPlaceholders = @([regex]::Matches($englishStrings[$key], '\{\d+(?::[^}]*)?\}') |
            ForEach-Object Value | Sort-Object)
        $localizedPlaceholders = @([regex]::Matches($localized[$key], '\{\d+(?::[^}]*)?\}') |
            ForEach-Object Value | Sort-Object)
        if ([string]::Join('|', $englishPlaceholders) -ne
            [string]::Join('|', $localizedPlaceholders)) {
            $placeholderMismatch += $key
        }
    }
    if ($missing.Count -or $extra.Count -or $empty.Count -or $placeholderMismatch.Count) {
        if ($missing.Count)             { Write-Host "    missing: $($missing -join ', ')" -ForegroundColor Yellow }
        if ($extra.Count)               { Write-Host "    extra:   $($extra -join ', ')" -ForegroundColor Yellow }
        if ($empty.Count)               { Write-Host "    empty:   $($empty -join ', ')" -ForegroundColor Yellow }
        if ($placeholderMismatch.Count) { Write-Host "    placeholders: $($placeholderMismatch -join ', ')" -ForegroundColor Yellow }
        throw "$($localeFile.Name) is incomplete: missing=$($missing.Count), extra=$($extra.Count), empty=$($empty.Count), placeholder mismatches=$($placeholderMismatch.Count)"
    }
}
Write-Host "    Translations OK: $($englishStrings.Count) keys across $((Get-ChildItem $stringsDir -Filter '*.xaml').Count) languages" -ForegroundColor Green

# ── 1. Hash pdfium.dll for the published checksum summary ──────────────────
Write-Host "`n==> Locating pdfium.dll for checksum reporting..." -ForegroundColor Cyan

# Look in the NuGet package cache for Docnet.Core's pdfium
$nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"
$pdfiumNuget = Get-ChildItem "$nugetCache\docnet.core\*\runtimes\win-x64\native\pdfium.dll" `
                   -ErrorAction SilentlyContinue |
               Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

# Also check the build output as a fallback
$pdfiumBuild = Join-Path $PSScriptRoot "bin\Release\net10.0-windows\win-x64\pdfium.dll"

$pdfiumPath = $null
if ($pdfiumNuget -and (Test-Path $pdfiumNuget)) {
    $pdfiumPath = $pdfiumNuget
    Write-Host "    Using NuGet cache: $pdfiumPath"
} elseif (Test-Path $pdfiumBuild) {
    $pdfiumPath = $pdfiumBuild
    Write-Host "    Using build output: $pdfiumPath"
} else {
    Write-Warning "    pdfium.dll not found - its checksum will be unavailable."
}

$pdfiumHash = "0000000000000000000000000000000000000000000000000000000000000000"
if ($pdfiumPath) {
    $pdfiumHash = (Get-FileHash $pdfiumPath -Algorithm SHA256).Hash
    Write-Host "    pdfium SHA256: $pdfiumHash" -ForegroundColor Green
}

# ── 2. Build the portable and installed packages ─────────────────────────────
Write-Host "`n==> Building portable and installer packages..." -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File $packageBuild -RequireSignature
if ($LASTEXITCODE -ne 0) { throw "Package build failed." }
foreach ($artifact in @($portableExe, $installerExe) + $innerExes) {
    if (-not (Test-Path $artifact)) { throw "Release artifact not found at: $artifact" }
}
Write-Host "    Portable : $portableExe" -ForegroundColor Green
Write-Host "    Installer: $installerExe" -ForegroundColor Green

# ── 3. Sign ─────────────────────────────────────────────────────────────────
if (-not $SkipSign) {
    Write-Host "`n==> Locating signtool..." -ForegroundColor Cyan
    $signtool = $null
    $kitBase  = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kitBase) {
        $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK." }
    Write-Host "    $signtool"

    # Build cert selector args
    $certArgs = if ($CertThumbprint) {
        Write-Host "`n==> Signing with thumbprint $CertThumbprint..." -ForegroundColor Cyan
        @("/sha1", $CertThumbprint)
    } else {
        Write-Host "`n==> Signing with CN: $CertName..." -ForegroundColor Cyan
        @("/n", $CertName)
    }

    # Sign the real installed application before it is compressed into the public launcher.
    # Third-party binaries retain their publishers' signatures; only Killer-owned binaries are signed here.
    Write-Host "`n==> Signing inner applications before payload packaging..." -ForegroundColor Cyan
    foreach ($innerExe in $innerExes) {
        $innerSigned = $false
        foreach ($tsa in $tsaList) {
            & $signtool sign /fd sha256 /tr $tsa /td sha256 @certArgs `
                /d "KillerPDF Application" /du "https://killerpdf.net" /v $innerExe
            if ($LASTEXITCODE -eq 0) { $innerSigned = $true; break }
            Start-Sleep -Seconds 3
        }
        if (-not $innerSigned) { throw "Signing the inner application failed on all TSA endpoints: $innerExe" }
        & $signtool verify /pa /v $innerExe
        if ($LASTEXITCODE -ne 0) { throw "Inner application signature verification failed: $innerExe" }
    }

    # Signing changes the inner EXE hash. Rebuild the manifest, compressed payload, and outer
    # launcher so the payload contains the signed bytes and verifies them after extraction.
    Write-Host "`n==> Repacking signed payload..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $packageBuild -RepackOnly -RequireSignature
    if ($LASTEXITCODE -ne 0) { throw "Signed payload repack failed." }

    # Timestamp both public artifacts with retry across TSA list.
    foreach ($publicExe in @($portableExe, $installerExe)) {
        $signed = $false
        foreach ($tsa in $tsaList) {
            Write-Host "    Trying TSA for $([IO.Path]::GetFileName($publicExe)): $tsa"
            & $signtool sign /fd sha256 /tr $tsa /td sha256 @certArgs `
                /d "KillerPDF" /du "https://killerpdf.net" /v $publicExe
            if ($LASTEXITCODE -eq 0) { $signed = $true; break }
            Start-Sleep -Seconds 3
        }
        if (-not $signed) { throw "Signing failed on all TSA endpoints: $publicExe" }
    }

    # ── Post-sign verification gate ─────────────────────────────────────────
    Write-Host "`n==> Verifying signature chain (/pa)..." -ForegroundColor Cyan
    foreach ($publicExe in @($portableExe, $installerExe)) {
        & $signtool verify /pa /v $publicExe
        if ($LASTEXITCODE -ne 0) {
            throw "signtool verify FAILED for $publicExe. DO NOT RELEASE."
        }
    }
    Write-Host "    Signature chain OK." -ForegroundColor Green

    # Print the thumbprint of the cert that was actually used
    try {
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($installerExe)
        $cert2 = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert)
        $actualThumb = $cert2.Thumbprint
        $actualCN    = $cert2.GetNameInfo(
            [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
        Write-Host "    Signer : $actualCN" -ForegroundColor Green
        Write-Host "    Thumbprint: $actualThumb" -ForegroundColor Green
    } catch {
        Write-Warning "    Could not read signer info from signed EXE: $_"
        $actualThumb = "(unknown)"
        $actualCN    = "(unknown)"
    }
} else {
    Write-Host ""
    Write-Host "  #####################################################" -ForegroundColor Red
    Write-Host "  ##  WARNING: -SkipSign is set. EXE IS NOT SIGNED.  ##" -ForegroundColor Red
    Write-Host "  ##  DO NOT DISTRIBUTE this build as a release.     ##" -ForegroundColor Red
    Write-Host "  #####################################################" -ForegroundColor Red
    $actualThumb = "(not signed)"
    $actualCN    = "(not signed)"
}

# The payload build deliberately suppresses the app project's AfterPublish source target.
# Generate the GPL source artifact once, beside the final public launcher.
$projectXml = [xml](Get-Content -Raw -LiteralPath $proj)
$releaseVersionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $releaseVersionNode) { throw "Version missing from KillerPDF.csproj." }
$sourceBundleScript = Join-Path $PSScriptRoot 'build\bundle-source.ps1'
$releaseBuildVersion = $releaseVersionNode.InnerText
& powershell -NoProfile -ExecutionPolicy Bypass -File $sourceBundleScript `
    -ProjectDir $PSScriptRoot -Version $releaseBuildVersion -AppName 'KillerPDF' -PublishDir $publishDir
if ($LASTEXITCODE -ne 0) { throw "Source bundle failed." }

} else {
    # PublishOnly: the artifacts from the last full run are the release.
    Write-Host "`n==> PublishOnly: skipping build and sign, using existing artifacts." -ForegroundColor Yellow
    foreach ($artifact in @($portableExe, $installerExe)) {
        if (-not (Test-Path $artifact)) { throw "PublishOnly: no built artifact at $artifact" }
    }
    $pdfiumPath  = $null
    $pdfiumHash  = ""
    $actualThumb = "(existing signature)"
    $actualCN    = "(existing signature)"
}

# ── 4. SHA256 (final EXEs) ──────────────────────────────────────────────────
Write-Host "`n==> Computing final EXE SHA256 values..." -ForegroundColor Cyan
$portableHash  = (Get-FileHash $portableExe -Algorithm SHA256).Hash
$installerHash = (Get-FileHash $installerExe -Algorithm SHA256).Hash
Write-Host "    KillerPDF-Portable.exe : $portableHash" -ForegroundColor Green
Write-Host "    KillerPDF.exe          : $installerHash" -ForegroundColor Green
if ($pdfiumPath) {
    Write-Host "    pdfium.dll    : $pdfiumHash" -ForegroundColor Green
}

# ── 5. Source zip ────────────────────────────────────────────────────────────
$srcZip = Get-ChildItem $publishDir -Filter "*-src.zip" -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($srcZip) {
    Write-Host "`n==> Source zip: $($srcZip.FullName)" -ForegroundColor Green
} else {
    Write-Host "`n    (No source zip found - did bundle-source.ps1 run?)" -ForegroundColor Yellow
}

# ── 6. Write SHA256SUMS.txt ──────────────────────────────────────────────────
# Written into the publish folder next to both public executables and the source archive, so every file you
# upload to the GitHub release is in one place. The updater reads this from the release assets.
$sumsPath = Join-Path $publishDir "SHA256SUMS.txt"
if ($PublishOnly -and (Test-Path $sumsPath)) {
    # Keep the full-run file: rewriting here would drop the pdfium line (not recomputed).
    Write-Host "`n==> PublishOnly: keeping existing SHA256SUMS.txt." -ForegroundColor Yellow
} else {
$lines    = [System.Collections.Generic.List[string]]::new()
$lines.Add("KillerPDF.exe           $installerHash")
$lines.Add("KillerPDF-Portable.exe  $portableHash")
if ($pdfiumPath) { $lines.Add("pdfium.dll              $pdfiumHash") }
if ($srcZip) {
    $srcHash = (Get-FileHash $srcZip.FullName -Algorithm SHA256).Hash
    $lines.Add("$($srcZip.Name.PadRight(24))$srcHash")
}
[System.IO.File]::WriteAllLines($sumsPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "`n==> SHA256SUMS.txt written to: $sumsPath" -ForegroundColor Green
}

# ── 7. Summary ───────────────────────────────────────────────────────────────
Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "  KillerPDF release artifacts" -ForegroundColor White
Write-Host   "  SETUP   : $installerExe"
Write-Host   "  PORTABLE: $portableExe"
if ($srcZip) { Write-Host "  SRC  : $($srcZip.FullName)" }
Write-Host   ""
Write-Host   "  SHA256 (Setup)      : $installerHash" -ForegroundColor Green
Write-Host   "  SHA256 (Portable)   : $portableHash" -ForegroundColor Green
if ($pdfiumPath) {
Write-Host   "  SHA256 (pdfium.dll): $pdfiumHash" -ForegroundColor Green }
Write-Host   ""
Write-Host   "  Signer : $actualCN"
Write-Host   "  Thumbprint: $actualThumb"
Write-Host   ""
Write-Host   "  pdf-landing's hero (version/date/size/sha256) is updated automatically"
Write-Host   "  in the publish preflight below - no hand-pasting."
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

# ============================================================================
# Publish phases (ported from the KillerNotes release script): notes from the
# CHANGELOG, git preflight, tag + push, and GitHub release. Publishing the release triggers
# .github/workflows/winget-release.yml, the single WinGet submission path.
# ============================================================================

# ── 8. Version + publish preflight ───────────────────────────────────────────
Write-Host "`n==> Publish preflight..." -ForegroundColor Cyan
$csprojRaw = Get-Content -Path $proj -Raw
if ($csprojRaw -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') {
    throw "No <Version>x.y.z</Version> found in KillerPDF.csproj"
}
$Version = $Matches[1]
$Tag = "v$Version"
Write-Host "    Version: $Version (tag $Tag)"

Push-Location $PSScriptRoot
try {
    $defaultBranch = Get-DefaultBranch
    if (-not $defaultBranch) { throw "Could not determine the default branch from origin" }
    Write-Host "    Default branch: $defaultBranch"
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne $defaultBranch) { throw "On branch '$branch', expected $defaultBranch" }
    $dirty = git status --porcelain
    if ($dirty) { throw "Working tree is not clean. Commit or stash first:`n$($dirty -join "`n")" }

    # Keep the README's GPL3 source link pointed at the current release - it
    # names the versioned -src.zip, so it goes stale on every version bump.
    # ReadAllText/WriteAllText: UTF-8 no BOM, PS 5.1-safe (absolute paths).
    $readmePath = Join-Path $PSScriptRoot 'README.md'
    $readmeRaw  = [System.IO.File]::ReadAllText($readmePath)
    $readmeNew  = $readmeRaw -replace 'releases/download/v[0-9]+\.[0-9]+\.[0-9]+/KillerPDF-[0-9]+\.[0-9]+\.[0-9]+-src\.zip', "releases/download/v$Version/KillerPDF-$Version-src.zip"
    if ($readmeNew -ne $readmeRaw) {
        if ($DryRun) {
            Write-Host "    DryRun: README source link is stale, would update it to $Tag" -ForegroundColor Yellow
        } else {
            Write-Host "    Updating README source link to $Tag"
            [System.IO.File]::WriteAllText($readmePath, $readmeNew)
            git commit README.md -m "Point README source link at $Tag" --quiet
            git push origin $defaultBranch --quiet
            if ($LASTEXITCODE -ne 0) { throw "README source-link commit failed to push" }
        }
    }

    # ── Landing page release info (pdf-landing) ──────────────────────────────
    # Ported from Killendar's release.ps1 step 7 (the family standard - KillerNotes has it
    # too; KillerPDF was the odd one out and its hero went stale by hand every release).
    # killerpdf.net is a MANUAL Cloudflare Pages drop, so nothing here deploys - the hero
    # block (version, released, size, sha256), the verEgg footer on every page, and the
    # translated footers in kp-i18n.js are rewritten and committed BEFORE the tag.
    # Two site-specific differences from Killendar's copy: the hash is stored LOWERCASE
    # here, and the size row carries a '~' prefix. ReadAllText/WriteAllText keep the files
    # BOM-less UTF-8 (PS 5.1 Set-Content -Encoding UTF8 adds a BOM).
    # ONE source of truth for the release date: the csproj <ReleaseDate> the preflight
    # already checked against the CHANGELOG - Get-Date would stamp whatever day the script
    # happened to run.
    if ($csprojRaw -notmatch '<ReleaseDate>([0-9]{4}-[0-9]{2}-[0-9]{2})</ReleaseDate>') {
        throw "No <ReleaseDate>yyyy-MM-dd</ReleaseDate> found in KillerPDF.csproj"
    }
    $releaseDate = $Matches[1]
    $hashLower   = $installerHash.ToLower()
    $exeMB       = [math]::Round((Get-Item $installerExe).Length / 1MB, 2)
    $siteDir     = Join-Path $PSScriptRoot 'pdf-landing'

    $indexPath = Join-Path $siteDir 'index.html'
    $indexRaw  = [System.IO.File]::ReadAllText($indexPath)
    $indexNew  = $indexRaw
    $indexNew  = $indexNew -replace '(<span class="k">version</span>&nbsp;<span class="v">)KillerPDF v[0-9]+\.[0-9]+\.[0-9]+', ('${1}' + "KillerPDF v$Version")
    $indexNew  = $indexNew -replace '(<span class="k">released</span>&nbsp;<span class="v">)[0-9]{4}-[0-9]{2}-[0-9]{2}', ('${1}' + $releaseDate)
    $indexNew  = $indexNew -replace '(<span class="k">size</span>&nbsp;<span class="v">)[^<]*', ('${1}' + "~$exeMB MB exe")
    $indexNew  = $indexNew -replace '(<span class="v hash">)[0-9A-Fa-f]{32}<br>[0-9A-Fa-f]{32}', ('${1}' + $hashLower.Substring(0, 32) + '<br>' + $hashLower.Substring(32, 32))
    if ($indexNew -eq $indexRaw) {
        Write-Warning 'index.html hero block did not change - check the release-info markup still matches the patterns in this script.'
    }

    if ($DryRun) {
        Write-Host "    DryRun: would write these release facts to pdf-landing and commit:" -ForegroundColor Yellow
        Write-Host "      version  : KillerPDF v$Version"
        Write-Host "      released : $releaseDate"
        Write-Host "      size     : ~$exeMB MB exe"
        Write-Host "      sha256   : $hashLower"
        Write-Host "      verEgg   : v$Version on index, help, technical, engine, about + kp-i18n.js"
    } else {
        if ($indexNew -ne $indexRaw) { [System.IO.File]::WriteAllText($indexPath, $indexNew) }

        # Footer version on every page, plus the translated footer strings in kp-i18n.js
        # (their verEgg span is spelled with escaped quotes there, hence the \\? in the
        # pattern matching both id="verEgg" and id=\"verEgg\").
        foreach ($page in 'index.html', 'help.html', 'technical.html', 'engine.html', 'about.html', 'kp-i18n.js') {
            $p = Join-Path $siteDir $page
            if (-not (Test-Path $p)) { continue }
            $raw = [System.IO.File]::ReadAllText($p)
            $new = $raw -replace '(id=\\?"verEgg\\?"[^>]*>)v[0-9]+\.[0-9]+\.[0-9]+', ('${1}' + "v$Version")
            if ($new -ne $raw) { [System.IO.File]::WriteAllText($p, $new) }
        }

        $siteDirty = git status --porcelain pdf-landing
        if ($siteDirty) {
            git add pdf-landing
            git commit -m "v${Version}: landing release info" --quiet
            git push origin $defaultBranch --quiet
            if ($LASTEXITCODE -ne 0) { throw "Landing page commit failed to push" }
            Write-Host "    pdf-landing updated to v$Version and pushed"
            Write-Host "    Remember: killerpdf.net does NOT auto-deploy. Drag pdf-landing/ into Cloudflare Pages." -ForegroundColor Yellow
        } else {
            Write-Host "    pdf-landing already current"
        }

        # Facts the site states in PROSE, which no other gate can reach. Edit-SiteFact keeps
        # version, size and hash honest, and OcrCatalogTests keeps the app's OCR list matching
        # Strings\, but a sentence like "OCR supports ten languages" is just words in a paragraph.
        # That one was wrong for two releases and nothing noticed, so the count is compared to the
        # copy here. Silent when the page agrees; only speaks up on a mismatch.
        $localeCount = @(Get-ChildItem (Join-Path $PSScriptRoot 'Strings') -Filter '*.xaml').Count
        $numberWords = @{
            'eight' = 8; 'nine' = 9; 'ten' = 10; 'eleven' = 11; 'twelve' = 12
            'thirteen' = 13; 'fourteen' = 14; 'fifteen' = 15; 'sixteen' = 16
        }
        $sitePages = @('index.html', 'help.html', 'technical.html', 'about.html') |
            ForEach-Object { Join-Path $PSScriptRoot "pdf-landing\$_" } | Where-Object { Test-Path $_ }
        $claimMismatches = @()
        foreach ($page in $sitePages) {
            # Only sentences about INTERFACE or OCR languages. "fifteen languages" in a sentence
            # about syntax highlighting is a different count and must not be flagged.
            $hits = Select-String -Path $page -Pattern '(?i)\b(\w+)\s+(?:languages|locales)\b' -AllMatches
            foreach ($hit in $hits) {
                if ($hit.Line -notmatch '(?i)OCR|interface|localiz|locale|translated') { continue }
                foreach ($m in $hit.Matches) {
                    $word = $m.Groups[1].Value
                    $claimed = if ($numberWords.ContainsKey($word.ToLower())) { $numberWords[$word.ToLower()] }
                               elseif ($word -match '^\d+$') { [int]$word } else { $null }
                    if ($null -ne $claimed -and $claimed -ne $localeCount) {
                        $claimMismatches += "      $(Split-Path $page -Leaf):$($hit.LineNumber) says '$($m.Value)' but $localeCount locales ship"
                    }
                }
            }
        }
        if ($claimMismatches.Count) {
            Write-Host ""
            Write-Warning "Landing-page copy disagrees with the shipped locale count:"
            $claimMismatches | Sort-Object -Unique | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
            Write-Host "      Fix the copy, or confirm the sentence is about something else." -ForegroundColor Yellow
        }
    }

    git fetch origin $defaultBranch --quiet
    if ((git rev-parse HEAD).Trim() -ne (git rev-parse "origin/$defaultBranch").Trim()) {
        throw "Local $defaultBranch and origin/$defaultBranch differ. Push or pull first."
    }
    if (git tag --list $Tag) { throw "Tag $Tag already exists" }
    if (git ls-remote --tags origin $Tag) { throw "Tag $Tag already exists on origin" }

    # A red test cannot ship. Same gate as the date checks above - fail the release, not a reminder.
    Write-Host "    Running desktop unit tests..."
    dotnet test (Join-Path $PSScriptRoot 'KillerPDF.Tests\KillerPDF.Tests.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Desktop unit tests failed - fix them before releasing" }
    Write-Host "    Desktop unit tests passed" -ForegroundColor Green

    Write-Host "    Running engine unit tests..."
    dotnet test (Join-Path $PSScriptRoot 'engine\KillerPdf.Engine.Tests\KillerPdf.Engine.Tests.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Engine unit tests failed - fix them before releasing" }
    Write-Host "    Engine unit tests passed" -ForegroundColor Green

    Write-Host "    Preflight OK" -ForegroundColor Green

    # ── 9. Release notes from the CHANGELOG section ──────────────────────────
    Write-Host "`n==> Extracting release notes from CHANGELOG.md..." -ForegroundColor Cyan
    $clLines = Get-Content -Path (Join-Path $PSScriptRoot 'CHANGELOG.md')
    $notes = New-Object System.Collections.Generic.List[string]
    $inSection = $false
    foreach ($line in $clLines) {
        if ($line -match "^## \[$([regex]::Escape($Version))\]") { $inSection = $true; continue }
        if ($inSection -and $line -match '^## \[') { break }
        if ($inSection) { $notes.Add($line) }
    }
    if ($notes.Count -eq 0) { throw "Could not extract [$Version] notes from CHANGELOG.md" }
    $notesFile = Join-Path $env:TEMP "KillerPDF-$Version-notes.md"
    $notes -join "`r`n" | Set-Content -Path $notesFile -Encoding UTF8
    Write-Host "    Notes written to $notesFile ($($notes.Count) lines)"

    if ($DryRun) {
        Write-Host "`n==> DryRun: stopping before tag and release." -ForegroundColor Yellow
        Write-Host "    Would tag $Tag, push it, and publish Setup, Portable, the source archive, and SHA256SUMS.txt."
        exit 0
    }

    # ── 10. Tag + push ───────────────────────────────────────────────────────
    Write-Host "`n==> Tagging $Tag..." -ForegroundColor Cyan
    git tag -a $Tag -m "KillerPDF $Tag"
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "Tag push failed" }

    # ── 11. GitHub release ───────────────────────────────────────────────────
    Write-Host "`n==> Creating GitHub release..." -ForegroundColor Cyan
    $assets = @($installerExe, $portableExe)
    if ($srcZip) { $assets += $srcZip.FullName }
    if (Test-Path $sumsPath) { $assets += $sumsPath }
    gh release create $Tag @assets --title "KillerPDF $Tag" --notes-file $notesFile --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

    Write-Host "`n==> Refreshing thekiller.net software page..." -ForegroundColor Cyan
    gh workflow run deploy.yml --repo SteveTheKiller/thekiller-site
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "The release is published, but thekiller.net refresh could not be started. Run: gh workflow run deploy.yml --repo SteveTheKiller/thekiller-site"
    }

    Write-Host "`n==> Release $Tag published:" -ForegroundColor Green
    Write-Host "    The WinGet Release workflow will submit this version once from GitHub Actions."
    gh release view $Tag --json url --jq '.url'
} finally {
    Pop-Location
}
