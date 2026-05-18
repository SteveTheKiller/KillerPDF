#Requires -Version 5.1
<#
.SYNOPSIS
    TDPdf release script: build → sign → SHA256 → print summary.
.DESCRIPTION
    1. Publishes for net9.0-windows/win-x64 — also runs bundle-source.ps1 to zip the source.
    2. Signs TDPdf.exe with your Certum cert via signtool.
    3. Computes and prints the SHA256 for pasting into the landing pages.

.PARAMETER CertName
    CN (Subject) of your Certum certificate as it appears in the Windows cert store.
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Subject
    to find it. Defaults to the placeholder below.

.PARAMETER CertThumbprint
    SHA1 thumbprint of your Certum certificate. When set, this is used instead
    of CertName.

.PARAMETER SkipSign
    Skip signing (useful for a test build).

.PARAMETER Tag
    Release tag being published. Tagged releases cannot skip signing.

.EXAMPLE
    .\release.ps1 -CertName "The Doodle Project"
#>
param(
    [string]$CertName       = "The Doodle Project",
    [string]$CertThumbprint = "",
    [switch]$SkipSign,
    [string]$Tag            = ""
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj       = Join-Path $PSScriptRoot "TDPdf.csproj"
$publishDir = Join-Path $PSScriptRoot "bin\Release\net9.0-windows\win-x64\publish"
$exe        = Join-Path $publishDir "TDPdf.exe"
$hash       = $null
$srcZip     = $null

# Parse <Version> from csproj so we don't drift between release.ps1 and the build.
$projVersion = "unknown"
try {
    [xml]$projXml = Get-Content $proj
    $verNode = $projXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($verNode) { $projVersion = "$verNode".Trim() }
} catch {
    Write-Host "    (Could not parse <Version> from $proj: $($_.Exception.Message))" -ForegroundColor Yellow
}

try {
    if (($Tag -ne "") -and $SkipSign) {
        throw "Refusing to skip signing for a tagged release ($Tag). Pass without -Tag for a test build."
    }

    # ── 1. Build / Publish ──────────────────────────────────────────────────────
    try {
        Write-Host "`n==> Building (Release, net9.0-windows, win-x64)..." -ForegroundColor Cyan

        & dotnet publish $proj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true

        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
        if (-not (Test-Path $exe)) { throw "EXE not found at: $exe" }
        Write-Host "    EXE: $exe" -ForegroundColor Green
    } catch {
        throw "Build / publish failed: $($_.Exception.Message)"
    }

    # ── 2. Sign ─────────────────────────────────────────────────────────────────
    try {
        if (-not $SkipSign) {
            if ($CertThumbprint -ne "") {
                $normalizedThumbprint = $CertThumbprint -replace '\s', ''
                Write-Host "`n==> Signing with Certum cert thumbprint: $normalizedThumbprint..." -ForegroundColor Cyan
                $certSelector = @("/sha1", $normalizedThumbprint)
            } else {
                Write-Host "`n==> Signing with Certum cert: $CertName..." -ForegroundColor Cyan
                $certSelector = @("/n", $CertName)
            }

            $simplySign = Get-Process -Name "SimplySignDesktop" -ErrorAction SilentlyContinue
            if (-not $simplySign) {
                Write-Host "    SimplySign Desktop process not detected. Signing will likely fail. Press Ctrl+C to abort or wait 10s to continue..." -ForegroundColor Yellow
                Start-Sleep -Seconds 10
            }

            # Find signtool
            $signtool = $null
            $kitBase  = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
            if (Test-Path $kitBase) {
                $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                            Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
            }
            if (-not $signtool) { throw "signtool.exe not found. Install Windows SDK." }
            Write-Host "    signtool: $signtool"

            $timestampUrls = @(
                "http://timestamp.digicert.com",
                "http://timestamp.sectigo.com",
                "http://ts.ssl.com"
            )
            $signed = $false
            foreach ($timestampUrl in $timestampUrls) {
                Write-Host "    Trying timestamp authority: $timestampUrl"

                $signArgs = @(
                    "sign",
                    "/fd", "sha256",
                    "/tr", $timestampUrl,
                    "/td", "sha256"
                ) + $certSelector + @(
                    "/d", "TDPdf",
                    "/du", "https://thedoodleproject.com/tdpdf",
                    "/v", $exe
                )

                & $signtool @signArgs
                $signExitCode = $LASTEXITCODE
                if ($signExitCode -eq 0) {
                    $signed = $true
                    break
                }

                & $signtool verify /pa /v $exe *> $null
                if ($LASTEXITCODE -eq 0) {
                    throw "Signing failed using timestamp authority '$timestampUrl' (exit code $signExitCode), but the EXE now has a valid signature. Not retrying to avoid adding a dual signature; rebuild the EXE before trying again."
                }

                Write-Host "    Signing failed with timestamp authority: $timestampUrl" -ForegroundColor Yellow
            }

            if (-not $signed) { throw "Signing failed with all timestamp authorities. Is Certum SimplySign Desktop running?" }
            Write-Host "    Signed OK" -ForegroundColor Green

            & $signtool verify /pa /v $exe
            if ($LASTEXITCODE -ne 0) { throw "Signature verification failed." }
            Write-Host "    Signature valid" -ForegroundColor Green
        } else {
            Write-Host "`n==> Skipping signing (-SkipSign)" -ForegroundColor Yellow
        }
    } catch {
        throw "Signing failed: $($_.Exception.Message)"
    }

    # ── 3. SHA256 ────────────────────────────────────────────────────────────────
    try {
        Write-Host "`n==> Computing SHA256..." -ForegroundColor Cyan
        $hash = (Get-FileHash $exe -Algorithm SHA256).Hash
        Write-Host "    SHA256: $hash" -ForegroundColor Green
    } catch {
        throw "SHA256 computation failed: $($_.Exception.Message)"
    }

    # ── 4. Source zip ────────────────────────────────────────────────────────────
    try {
        $srcZip = Get-ChildItem $publishDir -Filter "*-src.zip" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($srcZip) {
            Write-Host "`n==> Source zip: $($srcZip.FullName)" -ForegroundColor Green
        } else {
            Write-Host "`n    (No source zip found — did bundle-source.ps1 run?)" -ForegroundColor Yellow
        }
    } catch {
        throw "Source zip lookup failed: $($_.Exception.Message)"
    }

    # ── 5. Summary ───────────────────────────────────────────────────────────────
    try {
        Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
        Write-Host   "  TDPdf v$projVersion release artifacts" -ForegroundColor White
        Write-Host   "  EXE  : $exe"
        if ($srcZip) { Write-Host "  SRC  : $($srcZip.FullName)" }
        Write-Host   "  SHA256: $hash" -ForegroundColor Green
        Write-Host   ""
        Write-Host   "  Paste SHA256 into:"
        Write-Host   "    pdf-landing\index.html (search for SHA256)"
        Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    } catch {
        throw "Summary failed: $($_.Exception.Message)"
    }
} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
