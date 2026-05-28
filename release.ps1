#Requires -Version 5.1
<#
.SYNOPSIS
    TDPdf release script: build → sign → SHA256 → print summary.
.DESCRIPTION
    1. Publishes for net9.0-windows/win-x64 — also runs bundle-source.ps1 to zip the source.
    2. Signs TDPdf.exe with your code-signing cert via signtool. Supports a
       Certum cert in the Windows cert store (default), any other store cert
       by thumbprint, or a .pfx file (e.g. an internal/Intune signing cert).
    3. Computes and prints the SHA256 for pasting into the landing pages.

.PARAMETER CertName
    CN (Subject) of the code-signing cert in the Windows cert store.
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Subject
    to find it. Defaults to "The Doodle Project" (Certum cert).

.PARAMETER CertThumbprint
    SHA1 thumbprint of a code-signing cert in the Windows cert store. When
    set, this is used instead of CertName. Works with ANY cert in the store
    (Certum, internal CA, self-signed, etc.) — signtool does not care about
    the issuer.

.PARAMETER PfxPath
    Path to a .pfx file containing the code-signing cert + private key.
    When set, signtool reads the cert from this file instead of the cert
    store. Useful for internal/Intune signing in CI or when the cert is
    not pre-installed.

.PARAMETER PfxPassword
    Password for the .pfx file. Required when -PfxPath is set unless the
    .pfx has no password. Pass as a plain string; this is only held in
    memory long enough to invoke signtool.

.PARAMETER PackageIntune
    After signing, package the EXE as an Intune Win32 app (.intunewin) using
    IntuneWinAppUtil.exe. Default: true. Pass -PackageIntune:$false to skip.

.PARAMETER IntuneWinAppUtilPath
    Path to IntuneWinAppUtil.exe. Defaults to C:\IntuneWinAppUtil.exe.

.PARAMETER SkipSign
    Skip signing (useful for a test build).

.PARAMETER Tag
    Release tag being published. Tagged releases cannot skip signing.

.EXAMPLE
    # Certum (default — requires SimplySign Desktop running)
    .\release.ps1 -CertName "The Doodle Project"

.EXAMPLE
    # Any store cert by thumbprint (no SimplySign needed)
    .\release.ps1 -CertThumbprint "ABCD1234...EF"

.EXAMPLE
    # Internal / Intune signing from a .pfx (prompts for password if not given)
    .\release.ps1 -PfxPath C:\certs\internal-signing.pfx
#>
param(
    [string]$CertName             = "The Doodle Project",
    [string]$CertThumbprint       = "",
    [string]$PfxPath              = "",
    [string]$PfxPassword          = "",
    [switch]$SkipSign,
    [string]$Tag                  = "",
    [bool]$PackageIntune          = $true,
    [string]$IntuneWinAppUtilPath = "C:\IntuneWinAppUtil.exe"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj       = Join-Path $PSScriptRoot "TDPdf.csproj"
$publishDir = Join-Path $PSScriptRoot "bin\Release\net9.0-windows\win-x64\publish"
$exe        = Join-Path $publishDir "TDPdf.exe"
$hash       = $null
$srcZip     = $null
$intunewin  = $null

# Parse <Version> and <AssemblyVersion> from csproj. <Version> (e.g. "1.0.0-tdpdf") is
# used for human-readable filenames; <AssemblyVersion> (e.g. "1.0.0.0") is what Intune
# detection rules compare against the published EXE's file version.
$projVersion      = "unknown"
$assemblyVersion  = "unknown"
try {
    [xml]$projXml = Get-Content -Raw $proj
    $verNode      = $projXml.SelectSingleNode('//Project/PropertyGroup/Version')
    $asmVerNode   = $projXml.SelectSingleNode('//Project/PropertyGroup/AssemblyVersion')
    if ($verNode)    { $projVersion     = $verNode.InnerText.Trim() }
    if ($asmVerNode) { $assemblyVersion = $asmVerNode.InnerText.Trim() }
} catch {
    Write-Host "    (Could not parse <Version> from ${proj}: $($_.Exception.Message))" -ForegroundColor Yellow
}

# If signing with a PFX and no password was supplied on the command line,
# prompt securely. We convert SecureString → plaintext only because signtool
# takes the password as a CLI arg; the plaintext lives in $PfxPassword for
# the duration of the signing step and is cleared at the end.
if (-not $SkipSign -and ($PfxPath -ne "") -and ($PfxPassword -eq "")) {
    Write-Host ""
    $secPwd = Read-Host -Prompt "PFX password for $PfxPath" -AsSecureString
    $PfxPassword = [System.Net.NetworkCredential]::new('', $secPwd).Password
}

try {
    if (($Tag -ne "") -and $SkipSign) {
        throw "Refusing to skip signing for a tagged release ($Tag). Pass without -Tag for a test build."
    }

    # ── 1. Build / Publish ──────────────────────────────────────────────────────
    try {
        Write-Host "`n==> Embedding telemetry key (if `$env:TDPDF_APPINSIGHTS_CONN set)..." -ForegroundColor Cyan
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build\embed-telemetry-key.ps1') -ProjectDir $PSScriptRoot
        if ($LASTEXITCODE -ne 0) { throw "embed-telemetry-key.ps1 failed." }

        Write-Host "`n==> Building (Release, net9.0-windows, win-x64)..." -ForegroundColor Cyan

        & dotnet publish $proj -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true

        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
        if (-not (Test-Path $exe)) { throw "EXE not found at: $exe" }
        Write-Host "    EXE: $exe" -ForegroundColor Green
    } catch {
        throw "Build / publish failed: $($_.Exception.Message)"
    } finally {
        # Always strip the generated key from the working tree, even on
        # failure, so a subsequent test build or `git status` doesn't
        # surface the secret.
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build\embed-telemetry-key.ps1') -ProjectDir $PSScriptRoot -Remove | Out-Null
    }

    # ── 2. Sign ─────────────────────────────────────────────────────────────────
    try {
        if (-not $SkipSign) {
            # Pick a cert source: PFX file > store thumbprint > store CN.
            # signtool happily accepts any of these; the Certum-specific bits
            # (SimplySign Desktop check, "Certum" wording) only apply when the
            # caller is actually using the default Certum store cert.
            $usingPfx          = ($PfxPath -ne "")
            $usingDefaultCertum = (-not $usingPfx) -and ($CertThumbprint -eq "") -and ($CertName -eq "The Doodle Project")

            if ($usingPfx) {
                if (-not (Test-Path $PfxPath)) { throw "PFX file not found: $PfxPath" }
                Write-Host "`n==> Signing with PFX: $PfxPath..." -ForegroundColor Cyan
                $certSelector = @("/f", $PfxPath)
                if ($PfxPassword -ne "") { $certSelector += @("/p", $PfxPassword) }
            } elseif ($CertThumbprint -ne "") {
                $normalizedThumbprint = $CertThumbprint -replace '\s', ''
                Write-Host "`n==> Signing with store cert thumbprint: $normalizedThumbprint..." -ForegroundColor Cyan
                $certSelector = @("/sha1", $normalizedThumbprint)
            } else {
                Write-Host "`n==> Signing with store cert: $CertName..." -ForegroundColor Cyan
                $certSelector = @("/n", $CertName)
            }

            # SimplySign Desktop is Certum-specific — only check when actually using the default Certum flow.
            if ($usingDefaultCertum) {
                $simplySign = Get-Process -Name "SimplySignDesktop" -ErrorAction SilentlyContinue
                if (-not $simplySign) {
                    Write-Host "    SimplySign Desktop process not detected. Signing will likely fail. Press Ctrl+C to abort or wait 10s to continue..." -ForegroundColor Yellow
                    Start-Sleep -Seconds 10
                }
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

            if (-not $signed) {
                if ($usingDefaultCertum) {
                    throw "Signing failed with all timestamp authorities. Is Certum SimplySign Desktop running?"
                } else {
                    throw "Signing failed with all timestamp authorities. Verify the cert is accessible and that signtool can read its private key."
                }
            }
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

    # ── 5. Intune Win32 package ─────────────────────────────────────────────────
    # Bundles the signed TDPdf.exe into a .intunewin so it can be uploaded as an
    # Intune Win32 app. We stage *only* the signed EXE (single-file publish bundles
    # pdfium.dll and the rest), then invoke IntuneWinAppUtil.exe in quiet mode.
    if ($PackageIntune) {
        try {
            if (-not (Test-Path $IntuneWinAppUtilPath)) {
                Write-Host "`n==> Skipping Intune packaging — IntuneWinAppUtil.exe not found at $IntuneWinAppUtilPath" -ForegroundColor Yellow
                Write-Host "    Download from: https://github.com/microsoft/Microsoft-Win32-Content-Prep-Tool" -ForegroundColor Yellow
            } else {
                Write-Host "`n==> Packaging for Intune (Win32 app)..." -ForegroundColor Cyan

                $stagingDir = Join-Path $publishDir "intune-staging"
                $intuneOut  = Join-Path $publishDir "intune"
                if (Test-Path $stagingDir) { Remove-Item -Recurse -Force $stagingDir }
                if (Test-Path $intuneOut)  { Remove-Item -Recurse -Force $intuneOut }
                New-Item -ItemType Directory -Path $stagingDir | Out-Null
                New-Item -ItemType Directory -Path $intuneOut  | Out-Null
                Copy-Item $exe $stagingDir

                & $IntuneWinAppUtilPath -c $stagingDir -s "TDPdf.exe" -o $intuneOut -q
                if ($LASTEXITCODE -ne 0) { throw "IntuneWinAppUtil.exe exited with code $LASTEXITCODE." }

                $generated = Join-Path $intuneOut "TDPdf.intunewin"
                if (-not (Test-Path $generated)) {
                    throw "Expected .intunewin at $generated was not produced."
                }

                $intunewin = Join-Path $publishDir "TDPdf-$projVersion.intunewin"
                if (Test-Path $intunewin) { Remove-Item -Force $intunewin }
                Move-Item -Force $generated $intunewin

                Remove-Item -Recurse -Force $stagingDir
                Remove-Item -Recurse -Force $intuneOut

                Write-Host "    Packaged: $intunewin" -ForegroundColor Green
            }
        } catch {
            throw "Intune packaging failed: $($_.Exception.Message)"
        }
    }

    # Clear the plaintext PFX password from memory now that signing/packaging is done.
    if ($PfxPassword -ne "") { $PfxPassword = "" }

    # ── 6. Summary ───────────────────────────────────────────────────────────────
    try {
        Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
        Write-Host   "  TDPdf v$projVersion release artifacts" -ForegroundColor White
        Write-Host   "  EXE  : $exe"
        if ($srcZip)    { Write-Host "  SRC  : $($srcZip.FullName)" }
        if ($intunewin) { Write-Host "  WIN32: $intunewin" -ForegroundColor Green }
        Write-Host   "  SHA256: $hash" -ForegroundColor Green
        Write-Host   ""
        Write-Host   "  Paste SHA256 into:"
        Write-Host   "    pdf-landing\index.html (search for SHA256)"

        if ($intunewin) {
            Write-Host ""
            Write-Host   "  Intune Win32 app settings:" -ForegroundColor Cyan
            Write-Host   "    Install command  : TDPdf.exe /install /silent"
            Write-Host   "    Uninstall command: `"%ProgramFiles%\TDPdf\TDPdf.exe`" /uninstall /silent"
            Write-Host   "    Install behavior : System"
            Write-Host   "    Device restart   : No specific action"
            Write-Host   "    Return codes     : 0 = success, 1 = failure (Intune will retry)"
            Write-Host   ""
            Write-Host   "    Detection rule (recommended - PowerShell script):"
            Write-Host   "      Rules format    : Use a custom detection script"
            Write-Host   "      Script file     : build\intune\Detect-TDPdf.ps1"
            Write-Host   "      Run as 32-bit   : No"
            Write-Host   "      Signature check : No"
            Write-Host   "      (Edit `$MinVersion in the script to $assemblyVersion)"
            Write-Host   ""
            Write-Host   "    Fallback A (manual registry rule):"
            Write-Host   "      Rule type       : Registry"
            Write-Host   "      Key path        : Software\TDPdf   (NO hive prefix, NO leading \)"
            Write-Host   "      Value name      : Version"
            Write-Host   "      Detection method: String comparison"
            Write-Host   "      Operator        : Greater than or equal to"
            Write-Host   "      Value           : $assemblyVersion"
            Write-Host   "      32-bit on 64    : No"
            Write-Host   ""
            Write-Host   "    Fallback B (simplest - value exists, no version gate):"
            Write-Host   "      Rule type       : Registry"
            Write-Host   "      Key path        : Software\TDPdf"
            Write-Host   "      Value name      : Installed"
            Write-Host   "      Detection method: Value exists"
            Write-Host   "      32-bit on 64    : No"
            Write-Host   ""
            Write-Host   "    Diagnostics (on the target device):"
            Write-Host   "      Install log     : %ProgramData%\TDPdf\install.log (SYSTEM install)"
            Write-Host   "                        %LocalAppData%\TDPdf\install.log (user install)"
            Write-Host   ""
            Write-Host   "    Requirements:"
            Write-Host   "      OS architecture : x64"
            Write-Host   "      Min Windows     : Windows 10 1809"
        }

        Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
    } catch {
        throw "Summary failed: $($_.Exception.Message)"
    }
} catch {
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
