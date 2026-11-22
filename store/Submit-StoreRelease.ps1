#Requires -Version 5.1
<#
.SYNOPSIS
    Submits a new installer version to the Microsoft Store for an EXE app.

.DESCRIPTION
    Uses the Microsoft Store submission API for MSI or EXE apps. Points the draft's existing
    package at the new installer URL (every other package setting is kept), commits it, waits
    for the Store to fetch the installer, creates the submission and confirms certification
    started. Certification itself takes up to three business days and is not waited for.

    The tenant, client and seller IDs come from the user environment variables
    KILLER_STORE_TENANT_ID, KILLER_STORE_CLIENT_ID and KILLER_STORE_SELLER_ID. The client secret
    comes from Windows Credential Manager as the generic credential KillerTools.StoreClientSecret.

.PARAMETER ProductId
    The Store ID of the product (for example XP8JR4WJK03BJP).

.PARAMETER PackageUrl
    Versioned HTTPS URL of the signed installer. The Store requires the file at this URL to
    never change, so it must include the version.

.PARAMETER DryRun
    Signs in and reads everything, then stops before the first change.

.PARAMETER PreflightOnly
    For release preflight, before the installer exists: signs in and checks that the draft has one
    package and no submission in progress, without loading the installer URL.
#>
param(
    [Parameter(Mandatory)][string]$ProductId,
    [Parameter(Mandatory)][string]$PackageUrl,
    [switch]$DryRun,
    [switch]$PreflightOnly,
    [int]$UploadTimeoutMinutes = 30
)
$ErrorActionPreference = 'Stop'
$api = "https://api.store.microsoft.com/submission/v1/product/$ProductId"

# The client secret lives in Windows Credential Manager as KillerTools.StoreClientSecret, never in the
# environment that every process, agent clients included, inherits.
function Read-StoreSecret {
    if (-not ('KillerTools.StoreSecret' -as [type])) {
        Add-Type -Namespace KillerTools -Name StoreSecret -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
private struct CREDENTIAL
{
    public int Flags; public int Type; public System.IntPtr TargetName; public System.IntPtr Comment;
    public long LastWritten; public int CredentialBlobSize; public System.IntPtr CredentialBlob;
    public int Persist; public int AttributeCount; public System.IntPtr Attributes;
    public System.IntPtr TargetAlias; public System.IntPtr UserName;
}
[System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
private static extern bool CredReadW(string target, int type, int flags, out System.IntPtr credential);
[System.Runtime.InteropServices.DllImport("advapi32.dll")]
private static extern void CredFree(System.IntPtr credential);
public static string Read(string target)
{
    System.IntPtr handle;
    if (!CredReadW(target, 1, 0, out handle)) return null;
    try
    {
        var credential = (CREDENTIAL)System.Runtime.InteropServices.Marshal.PtrToStructure(handle, typeof(CREDENTIAL));
        var bytes = new byte[credential.CredentialBlobSize];
        System.Runtime.InteropServices.Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
        return System.Text.Encoding.Unicode.GetString(bytes);
    }
    finally { CredFree(handle); }
}
'@
    }
    $secret = [KillerTools.StoreSecret]::Read('KillerTools.StoreClientSecret')
    if (-not $secret) { throw 'The Store client secret is not in Credential Manager as KillerTools.StoreClientSecret. See the release notes in the harness for setup.' }
    return $secret
}

function Get-Setting([string]$Name) {
    if ($Name -eq 'KILLER_STORE_CLIENT_SECRET') { return Read-StoreSecret }
    $value =[Environment]::GetEnvironmentVariable($Name, 'Process')
    if (-not $value) { $value = [Environment]::GetEnvironmentVariable($Name, 'User') }
    if (-not $value) { throw "Store credential $Name is not set. See the release notes in the harness for setup." }
    return $value
}

# release.ps1 runs this under Set-StrictMode -Version Latest, where reading a property the API left
# out of its JSON throws. The API omits empty fields (a reply without "errors" is normal), so every
# response field is read through this.
function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Invoke-Store([string]$Method, [string]$Path, $Body = $null) {
    $params = @{ Method = $Method; Uri = "$api$Path"; Headers = $script:headers; UseBasicParsing = $true }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 10 -Compress); $params.ContentType = 'application/json' }
    for ($attempt = 1; ; $attempt++) {
        try {
            $response = Invoke-WebRequest @params
            $result = $response.Content | ConvertFrom-Json
        } catch {
            $failedResponse = Get-Field $_.Exception 'Response'
            $status = [int](Get-Field $failedResponse 'StatusCode')
            $detail = if ($_.ErrorDetails) { $_.ErrorDetails.Message } else { $_.Exception.Message }
            if ($status -eq 429 -and $attempt -lt 5) { Start-Sleep -Seconds (15 * $attempt); continue }
            throw "Store API $Method $Path failed ($status): $detail"
        }
        foreach ($e in @(Get-Field $result 'errors')) {
            if ($e) { Write-Host "  Store $(Get-Field $e 'code'): $(Get-Field $e 'message') [$(Get-Field $e 'target')]" -ForegroundColor Yellow }
        }
        if (-not (Get-Field $result 'isSuccess')) { throw "Store API $Method $Path was not accepted." }
        return Get-Field $result 'responseData'
    }
}

if ($PackageUrl -notmatch '^https://') { throw "PackageUrl must be an https URL: $PackageUrl" }

Write-Host "==> Signing in to the Microsoft Store submission API"
$tenant = Get-Setting 'KILLER_STORE_TENANT_ID'
$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" `
    -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type    = 'client_credentials'
        client_id     = Get-Setting 'KILLER_STORE_CLIENT_ID'
        client_secret = Get-Setting 'KILLER_STORE_CLIENT_SECRET'
        scope         = 'https://api.store.microsoft.com/.default'
    }).access_token
$script:headers = @{ Authorization = "Bearer $token"; 'X-Seller-Account-Id' = (Get-Setting 'KILLER_STORE_SELLER_ID') }

if (-not $PreflightOnly) {
    Write-Host "==> Checking the installer is reachable"
    try { $head = Invoke-WebRequest -Uri $PackageUrl -Method Head -UseBasicParsing }
    catch { throw "The installer URL does not load: $PackageUrl ($($_.Exception.Message))" }
    Write-Host "  $PackageUrl ($($head.Headers['Content-Length']) bytes)"
}

Write-Host "==> Reading the current draft"
$state = Invoke-Store GET '/status'
$ongoing = Get-Field $state 'ongoingSubmissionId'
if ($ongoing -and $PreflightOnly) {
    Write-Host "WARNING: Store submission $ongoing is still in certification. The release can go ahead; submit this version to the Store once it finishes." -ForegroundColor Yellow
    return
}
if ($ongoing) {
    throw "Submission $ongoing is already in progress. Wait for it to finish or cancel it in Partner Center, then run this again."
}
$packages = @(Get-Field (Invoke-Store GET '/packages') 'packages' | Where-Object { $null -ne $_ })
if ($packages.Count -ne 1) { throw "Expected one package in the draft, found $($packages.Count). Update it in Partner Center." }
$package = $packages[0]
$packageId = Get-Field $package 'packageId'
$existingPackageUrl = Get-Field $package 'packageUrl'
Write-Host "  Package ${packageId}: $existingPackageUrl"
if ($PreflightOnly) { Write-Host "  Store preflight OK."; return }

# Sync translated listings before submitting the installer, including newly added languages.
$listingScript = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'KillerHarness\scripts\StoreUpdate.ps1'
if (-not (Test-Path -LiteralPath $listingScript)) { throw "Store listing updater is missing: $listingScript" }
$listingArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $listingScript,
    '-App', 'KillerPDF', '-ListingsOnly', '-StoreProductId', $ProductId, '-StoreDirectory', $PSScriptRoot)
if (-not $DryRun) { $listingArgs += '-DraftOnly' }
& powershell @listingArgs
if ($LASTEXITCODE -ne 0) { throw 'Store listing sync failed. The installer was not submitted.' }

if ($existingPackageUrl -eq $PackageUrl) {
    Write-Host "  The draft already points at this installer."
} elseif ($DryRun) {
    Write-Host "DryRun: would change the package URL to $PackageUrl, commit it and submit." -ForegroundColor Cyan
    return
} else {
    Write-Host "==> Pointing the package at the new installer"
    $null = Invoke-Store PATCH "/packages/$packageId" @{ packageUrl = $PackageUrl }
    $null = Invoke-Store POST '/packages/commit'
}
if ($DryRun) { Write-Host "DryRun: would submit." -ForegroundColor Cyan; return }

Write-Host "==> Waiting for the Store to fetch the installer"
$deadline = (Get-Date).AddMinutes($UploadTimeoutMinutes)
do {
    Start-Sleep -Seconds 10
    $state = Invoke-Store GET '/status'
    if ((Get-Date) -gt $deadline) { throw "The Store did not finish fetching the installer within $UploadTimeoutMinutes minutes. Run this again to continue." }
} until (Get-Field $state 'isReady')

Write-Host "==> Submitting for certification"
$submission = Invoke-Store POST '/submit'
$id = Get-Field $submission 'submissionId'
if (-not $id) { throw "The Store did not return a submission ID." }
$status = Invoke-Store GET "/submission/$id/status"
$publishing = Get-Field $status 'publishingStatus'
if (Get-Field $status 'hasFailed') { throw "Submission $id failed immediately with status $publishing. See Partner Center." }
Write-Host "Submitted $id for certification ($publishing). Certification takes up to three business days." -ForegroundColor Green
