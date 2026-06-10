# Intune Win32 detection script for TDPdf.
#
# How Intune evaluates this script:
#   * Exit code 0 AND any stdout output  => app is DETECTED
#   * Exit code 0 AND no stdout output   => app is NOT detected
#   * Non-zero exit code                 => app is NOT detected (Intune
#                                            logs the script as failed)
#
# This script checks the install marker the TDPdf installer writes LAST
# in DoInstall (see App.xaml.cs):
#   HKLM\Software\TDPdf  Installed (DWORD) = 1
#   HKLM\Software\TDPdf  Version    (REG_SZ) = "<AssemblyVersion>"
#
# To require a minimum version, edit $MinVersion below to the release
# you are deploying. Set it to $null to accept any installed version.

$ErrorActionPreference = 'Stop'
$MinVersion = [Version]'1.8.2.0'

try {
    $key = 'HKLM:\Software\TDPdf'
    if (-not (Test-Path $key)) { exit 0 }

    $props = Get-ItemProperty -Path $key -ErrorAction Stop
    if ($props.Installed -ne 1) { exit 0 }

    if ($null -ne $MinVersion) {
        $raw = [string]$props.Version
        $parsed = $null
        if (-not [Version]::TryParse($raw, [ref]$parsed)) { exit 0 }
        if ($parsed -lt $MinVersion) { exit 0 }
        Write-Output "TDPdf $parsed detected"
    } else {
        Write-Output "TDPdf detected"
    }
    exit 0
}
catch {
    # Treat any unexpected failure as "not detected" so Intune retries.
    exit 0
}
