#Requires -Version 5.1
<#
.SYNOPSIS
    KillerPDF release script: build → sign → verify → hash → update BuildInfo → publish summary.
.DESCRIPTION
    1. Locates pdfium.dll in the NuGet cache, hashes it, and writes BuildInfo.cs so the
       embedded integrity check at startup knows the expected value.
    2. Publishes using FolderProfile1 (net48, win-x64); bundle-source.ps1 also runs.
    3. Signs KillerPDF.exe. Prefers CertThumbprint (exact match) over CertName (CN match).
       Retries the timestamp across three TSA endpoints if the first attempt fails.
    4. Runs "signtool verify /pa /v" as a post-sign gate — aborts if the cert chain
       is not trusted to an accepted root.
    5. Prints thumbprint, SHA256s, and paste targets in the summary.

.PARAMETER CertThumbprint
    Preferred. SHA1 thumbprint of your code-signing certificate (40 hex chars, no spaces).
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Thumbprint, Subject
    Omit if using CertName instead.

.PARAMETER CertName
    Fallback. CN (Subject) of your certificate as it appears in the Windows cert store.
    Ignored when CertThumbprint is supplied.

.PARAMETER SkipSign
    Skip signing. Writes all-zeros into BuildInfo.cs (disables runtime pdfium check).
    Useful for local test builds. Prints a red warning banner.

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
    # Everything except tag push, gh release, and winget submit.
    [switch]$DryRun,
    # Skip build + sign and publish the artifacts already in the publish folder
    # (use after a completed normal run, so the signed exe is not rebuilt).
    [switch]$PublishOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj         = Join-Path $PSScriptRoot "KillerPDF.csproj"
$buildInfoPath = Join-Path $PSScriptRoot "BuildInfo.cs"
$publishDir   = Join-Path $PSScriptRoot "bin\Release\net48\publish"
$exe          = Join-Path $publishDir "KillerPDF.exe"

# TSA endpoints — tried in order; first success wins.
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

# ── 1. Hash pdfium.dll and update BuildInfo.cs ──────────────────────────────
Write-Host "`n==> Locating pdfium.dll for integrity pre-hash..." -ForegroundColor Cyan

# Look in the NuGet package cache for Docnet.Core's pdfium
$nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"
$pdfiumNuget = Get-ChildItem "$nugetCache\docnet.core\*\runtimes\win-x64\native\pdfium.dll" `
                   -ErrorAction SilentlyContinue |
               Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName

# Also check the build output as a fallback
$pdfiumBuild = Join-Path $PSScriptRoot "bin\Release\net48\win-x64\pdfium.dll"

$pdfiumPath = $null
if ($pdfiumNuget -and (Test-Path $pdfiumNuget)) {
    $pdfiumPath = $pdfiumNuget
    Write-Host "    Using NuGet cache: $pdfiumPath"
} elseif (Test-Path $pdfiumBuild) {
    $pdfiumPath = $pdfiumBuild
    Write-Host "    Using build output: $pdfiumPath"
} else {
    Write-Warning "    pdfium.dll not found — BuildInfo.cs will retain all-zeros (check disabled)."
}

$pdfiumHash = "0000000000000000000000000000000000000000000000000000000000000000"
if ($pdfiumPath) {
    $pdfiumHash = (Get-FileHash $pdfiumPath -Algorithm SHA256).Hash
    Write-Host "    pdfium SHA256: $pdfiumHash" -ForegroundColor Green
}

if ($SkipSign) {
    # Leave all-zeros so the runtime check is disabled
    $pdfiumHash = "0000000000000000000000000000000000000000000000000000000000000000"
    Write-Host "    SkipSign: BuildInfo.cs will keep all-zeros (check disabled)." -ForegroundColor Yellow
}

Write-Host "`n==> Writing BuildInfo.cs..." -ForegroundColor Cyan
$buildInfoContent = @"
namespace KillerPDF
{
    /// <summary>
    /// Build-time constants written or verified by release.ps1.
    /// </summary>
    internal static class BuildInfo
    {
        /// <summary>
        /// SHA256 of pdfium.dll (original bytes, before Costura compression).
        /// Updated by release.ps1 immediately before each build.
        /// All-zeros means the check is disabled (dev / SkipSign builds).
        /// </summary>
        internal const string PdfiumSha256 = "$pdfiumHash";

        internal const string PdfiumSha256Disabled = "0000000000000000000000000000000000000000000000000000000000000000";
    }
}
"@
[System.IO.File]::WriteAllText($buildInfoPath, $buildInfoContent, [System.Text.UTF8Encoding]::new($false))
Write-Host "    BuildInfo.cs updated." -ForegroundColor Green

# ── 2. Build / Publish ──────────────────────────────────────────────────────
Write-Host "`n==> Building (Release, net48, win-x64)..." -ForegroundColor Cyan

$msbuild = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $vsPath = & $vsWhere -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($vsPath) {
        $candidate = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $candidate) { $msbuild = $candidate }
    }
}
if (-not $msbuild) { $msbuild = "dotnet" }

if ($msbuild -eq "dotnet") {
    & dotnet publish $proj /p:PublishProfile=FolderProfile1 -c Release
} else {
    & $msbuild $proj /t:Publish /p:PublishProfile=FolderProfile1 /p:Configuration=Release /m /nologo /v:m
}

if ($LASTEXITCODE -ne 0) { throw "Build failed." }
if (-not (Test-Path $exe)) { throw "EXE not found at: $exe" }
Write-Host "    EXE: $exe" -ForegroundColor Green

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

    # Timestamp with retry across TSA list
    $signed = $false
    foreach ($tsa in $tsaList) {
        Write-Host "    Trying TSA: $tsa"
        & $signtool sign `
            /fd  sha256 `
            /tr  $tsa `
            /td  sha256 `
            @certArgs `
            /d   "KillerPDF" `
            /du  "https://killerpdf.net" `
            /v   $exe

        if ($LASTEXITCODE -eq 0) {
            Write-Host "    Signed and timestamped via $tsa" -ForegroundColor Green
            $signed = $true
            break
        }
        Write-Warning "    TSA $tsa failed (exit $LASTEXITCODE). Trying next..."
        Start-Sleep -Seconds 3
    }
    if (-not $signed) { throw "Signing failed on all TSA endpoints. Is SimplySign Desktop connected?" }

    # ── Post-sign verification gate ─────────────────────────────────────────
    Write-Host "`n==> Verifying signature chain (/pa)..." -ForegroundColor Cyan
    & $signtool verify /pa /v $exe
    if ($LASTEXITCODE -ne 0) {
        throw "signtool verify FAILED. The signed EXE does not pass trust validation. DO NOT RELEASE."
    }
    Write-Host "    Signature chain OK." -ForegroundColor Green

    # Print the thumbprint of the cert that was actually used
    try {
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($exe)
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

} else {
    # PublishOnly: the artifacts from the last full run are the release.
    Write-Host "`n==> PublishOnly: skipping build and sign, using existing artifacts." -ForegroundColor Yellow
    if (-not (Test-Path $exe)) { throw "PublishOnly: no built exe at $exe - run the full script first." }
    $pdfiumPath  = $null
    $pdfiumHash  = ""
    $actualThumb = "(existing signature)"
    $actualCN    = "(existing signature)"
}

# ── 4. SHA256 (final EXE) ─────────────────────────────────────────────────
Write-Host "`n==> Computing final EXE SHA256..." -ForegroundColor Cyan
$exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host "    KillerPDF.exe : $exeHash" -ForegroundColor Green
if ($pdfiumPath) {
    Write-Host "    pdfium.dll    : $pdfiumHash" -ForegroundColor Green
}

# ── 5. Source zip ────────────────────────────────────────────────────────────
$srcZip = Get-ChildItem $publishDir -Filter "*-src.zip" -ErrorAction SilentlyContinue |
          Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($srcZip) {
    Write-Host "`n==> Source zip: $($srcZip.FullName)" -ForegroundColor Green
} else {
    Write-Host "`n    (No source zip found — did bundle-source.ps1 run?)" -ForegroundColor Yellow
}

# ── 6. Write SHA256SUMS.txt ──────────────────────────────────────────────────
# Written into the publish folder next to KillerPDF.exe and the -src.zip, so every file you
# upload to the GitHub release is in one place. The updater reads this from the release assets.
$sumsPath = Join-Path $publishDir "SHA256SUMS.txt"
if ($PublishOnly -and (Test-Path $sumsPath)) {
    # Keep the full-run file: rewriting here would drop the pdfium line (not recomputed).
    Write-Host "`n==> PublishOnly: keeping existing SHA256SUMS.txt." -ForegroundColor Yellow
} else {
$lines    = [System.Collections.Generic.List[string]]::new()
$lines.Add("KillerPDF.exe           $exeHash")
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
Write-Host   "  EXE  : $exe"
if ($srcZip) { Write-Host "  SRC  : $($srcZip.FullName)" }
Write-Host   ""
Write-Host   "  SHA256 (EXE)       : $exeHash" -ForegroundColor Green
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
# CHANGELOG, git preflight, tag + push, GitHub release, winget submit.
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
    # block (version, released, size, sha256), the verEgg footer on every page, and the ten
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
    $hashLower   = $exeHash.ToLower()
    $exeMB       = [math]::Round((Get-Item $exe).Length / 1MB, 2)
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
        Write-Host "      verEgg   : v$Version on index, help, technical, about + kp-i18n.js"
    } else {
        if ($indexNew -ne $indexRaw) { [System.IO.File]::WriteAllText($indexPath, $indexNew) }

        # Footer version on every page, plus the ten translated footer strings in kp-i18n.js
        # (their verEgg span is spelled with escaped quotes there, hence the \\? in the
        # pattern matching both id="verEgg" and id=\"verEgg\").
        foreach ($page in 'index.html', 'help.html', 'technical.html', 'about.html', 'kp-i18n.js') {
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
    }

    git fetch origin $defaultBranch --quiet
    if ((git rev-parse HEAD).Trim() -ne (git rev-parse "origin/$defaultBranch").Trim()) {
        throw "Local $defaultBranch and origin/$defaultBranch differ. Push or pull first."
    }
    if (git tag --list $Tag) { throw "Tag $Tag already exists" }
    if (git ls-remote --tags origin $Tag) { throw "Tag $Tag already exists on origin" }
    $changelog = Get-Content -Path (Join-Path $PSScriptRoot 'CHANGELOG.md') -Raw
    if ($changelog -match [regex]::Escape("## [$Version] - Unreleased")) {
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
        Write-Host "    Would tag $Tag, push it, and publish KillerPDF.exe, the -src.zip, and SHA256SUMS.txt."
        exit 0
    }

    # ── 10. Tag + push ───────────────────────────────────────────────────────
    Write-Host "`n==> Tagging $Tag..." -ForegroundColor Cyan
    git tag -a $Tag -m "KillerPDF $Tag"
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "Tag push failed" }

    # ── 11. GitHub release ───────────────────────────────────────────────────
    Write-Host "`n==> Creating GitHub release..." -ForegroundColor Cyan
    $assets = @($exe)
    if ($srcZip) { $assets += $srcZip.FullName }
    if (Test-Path $sumsPath) { $assets += $sumsPath }
    gh release create $Tag @assets --title "KillerPDF $Tag" --notes-file $notesFile --verify-tag
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }

    # ── 12. Submit to winget-pkgs (komac) ────────────────────────────────────
    # Non-fatal: the GitHub release is already out, so a winget hiccup must not
    # fail the run. Requires the previous version's winget PR to be merged.
    Write-Host "`n==> Submitting to winget-pkgs (komac)..." -ForegroundColor Cyan
    $exeUrl = "https://github.com/SteveTheKiller/KillerPDF/releases/download/$Tag/KillerPDF.exe"
    komac update SteveTheKiller.KillerPDF --version $Version --urls $exeUrl --submit
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "winget submit failed. Run it by hand once the previous version's PR is merged:"
        Write-Warning "  komac update SteveTheKiller.KillerPDF --version $Version --urls $exeUrl --submit"
    }

    Write-Host "`n==> Release $Tag published:" -ForegroundColor Green
    gh release view $Tag --json url --jq '.url'
} finally {
    Pop-Location
}
