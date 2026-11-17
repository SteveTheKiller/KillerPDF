#Requires -Version 5.1
param(
    [string]$Configuration = "Release",
    [ValidateSet("Portable", "Installer", "Store")]
    [string]$PackageKind = "Portable",
    [switch]$KeepSymbols,
    [switch]$RepackOnly,
    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'payload-security.ps1')

$projectDir = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectDir 'KillerPDF.csproj'
$launcherProject = Join-Path $projectDir 'Packaging\KillerLauncher\KillerLauncher.csproj'
# The Store package is the installer with the portable edition's bundled runtime, because the
# Microsoft Store installs silently on machines that may not have the .NET 10 Desktop Runtime.
$isStore = $PackageKind -eq 'Store'
$isInstaller = $PackageKind -ne 'Portable'
$bundlesRuntime = $isStore -or -not $isInstaller
$packageSlug = if ($isStore) { 'store-package' } elseif ($isInstaller) { 'installer-package' } else { 'portable-package' }
$artifactRoot = Join-Path $projectDir "bin\$Configuration\net10.0-windows\$packageSlug"
$payloadDir = Join-Path $artifactRoot 'payload'
$payloadZip = Join-Path $artifactRoot 'payload.zip'
$launcherOutput = Join-Path $artifactRoot 'launcher'
$publicDir = Join-Path $projectDir "bin\$Configuration\net10.0-windows\publish"
$publicExe = Join-Path $publicDir $(if ($isStore) { 'KillerPDF-Store.exe' } elseif ($isInstaller) { 'KillerPDF.exe' } else { 'KillerPDF-Portable.exe' })

if (-not $RepackOnly) {
    if ([IO.Directory]::Exists($artifactRoot)) { [IO.Directory]::Delete($artifactRoot, $true) }
}
if ([IO.Directory]::Exists($launcherOutput)) { [IO.Directory]::Delete($launcherOutput, $true) }
[IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
[IO.Directory]::CreateDirectory($payloadDir) | Out-Null
[IO.Directory]::CreateDirectory($launcherOutput) | Out-Null
[IO.Directory]::CreateDirectory($publicDir) | Out-Null
if ($isInstaller -and -not $isStore) {
    $obsoleteSetupExe = Join-Path $publicDir 'KillerPDF-Setup.exe'
    if ([IO.File]::Exists($obsoleteSetupExe)) { [IO.File]::Delete($obsoleteSetupExe) }
}

$versionXml = [xml](Get-Content -Raw -LiteralPath $appProject)
$versionNode = $versionXml.SelectSingleNode('/Project/PropertyGroup/Version')
$version = if ($versionNode) { [string]$versionNode.InnerText } else { '' }
if (-not $version) { throw 'KillerPDF.csproj has no Version.' }
$fileVersionNode = $versionXml.SelectSingleNode('/Project/PropertyGroup/FileVersion')
$fileVersion = if ($fileVersionNode) { [string]$fileVersionNode.InnerText } else { '' }
if (-not $fileVersion) { throw 'KillerPDF.csproj has no FileVersion.' }

if (-not $RepackOnly) {
    Write-Host "==> Building loose KillerPDF payload $version..." -ForegroundColor Cyan
    & dotnet publish $appProject -c $Configuration `
        -r win-x64 `
        --self-contained $bundlesRuntime `
        -p:PublishReadyToRun=true `
        -p:KillerPayloadBuild=true `
        -p:PublishDir="$payloadDir\"
    if ($LASTEXITCODE -ne 0) { throw 'Payload build failed.' }

    $engineProject = Join-Path $projectDir 'engine\KillerPdf.Engine\KillerPdf.Engine.csproj'
    $enginePublishDir = Join-Path $artifactRoot 'engine-r2r'
    & dotnet publish $engineProject -c $Configuration `
        -r win-x64 `
        --self-contained false `
        -p:PublishReadyToRun=true `
        -o $enginePublishDir
    if ($LASTEXITCODE -ne 0) { throw 'KillerPdf.Engine ReadyToRun build failed.' }
    $engineAssembly = Join-Path $enginePublishDir 'KillerPdf.Engine.dll'
    if (-not [IO.File]::Exists($engineAssembly)) { throw "KillerPdf.Engine ReadyToRun assembly is missing: $engineAssembly" }
    [IO.File]::Copy($engineAssembly, (Join-Path $payloadDir 'KillerPdf.Engine.dll'), $true)

    # The File Explorer and Outlook preview handler (#410) ships with the installed edition only.
    # Its own files join the payload; the engine and fonts it needs are already there. The Store
    # package leaves it out because the handler loads the shared runtime, not the bundled one.
    if ($isInstaller -and -not $isStore) {
        $previewProject = Join-Path $projectDir 'PreviewHandler\KillerPDF.PreviewHandler.csproj'
        $previewPublishDir = Join-Path $artifactRoot 'preview-handler'
        & dotnet publish $previewProject -c $Configuration `
            -r win-x64 `
            --self-contained false `
            -p:PublishReadyToRun=true `
            -o $previewPublishDir
        if ($LASTEXITCODE -ne 0) { throw 'Preview handler build failed.' }
        foreach ($previewFile in 'KillerPDF.PreviewHandler.dll', 'KillerPDF.PreviewHandler.comhost.dll',
                                 'KillerPDF.PreviewHandler.runtimeconfig.json', 'KillerPDF.PreviewHandler.deps.json') {
            $previewPath = Join-Path $previewPublishDir $previewFile
            if (-not [IO.File]::Exists($previewPath)) { throw "Preview handler output is missing: $previewPath" }
            [IO.File]::Copy($previewPath, (Join-Path $payloadDir $previewFile), $true)
        }
    }

    if (-not $KeepSymbols) {
        foreach ($symbol in [IO.Directory]::GetFiles($payloadDir, '*.pdb', [IO.SearchOption]::AllDirectories)) {
            [IO.File]::Delete($symbol)
        }
    }

    # The PDF file-type icon is a loose installed asset even though the application also embeds it.
    [IO.File]::Copy((Join-Path $projectDir 'Resources\pdf-file.ico'),
                    (Join-Path $payloadDir 'pdf-file.ico'), $true)
    [IO.File]::Copy((Join-Path $projectDir 'LICENSE'),
                    (Join-Path $payloadDir 'LICENSE'), $true)
} elseif (-not [IO.File]::Exists((Join-Path $payloadDir 'KillerPDF.App.exe'))) {
    throw 'RepackOnly requested but the prepared payload is missing.'
}

$payloadAssembly = Join-Path $payloadDir 'KillerPDF.App.dll'
if (-not [IO.File]::Exists($payloadAssembly)) {
    throw "Payload assembly is missing: $payloadAssembly"
}
$actualAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($payloadAssembly).Version.ToString()
if ($actualAssemblyVersion -ne $fileVersion) {
    throw "Payload assembly version $actualAssemblyVersion does not match project file version $fileVersion."
}

$payloadApp = Join-Path $payloadDir 'KillerPDF.App.exe'
# Publish (including ReadyToRun) must finish before signing. A signed repack refuses
# unsigned/invalid first-party bytes before generating a manifest or launcher.
$null = @(Get-FirstPartyPayloadFiles $payloadDir)
if ($RepackOnly -and $RequireSignature) { Assert-FirstPartyPayloadSignatures $payloadDir }
# release.ps1 checks the final portable and installed app once after packaging.

$manifestPath = Join-Path $payloadDir 'payload.manifest'
$payloadFiles = @([IO.Directory]::GetFiles($payloadDir, '*', [IO.SearchOption]::AllDirectories) |
    Where-Object { -not [string]::Equals($_, $manifestPath, [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object { $_.Substring($payloadDir.Length + 1) })
# The runtime's DAC file carries the .NET servicing version in its name, so it changes with
# every runtime patch. Compare it by shape instead of by exact version.
$runtimeVersionedName = '^mscordaccore_amd64_amd64_[0-9]+(\.[0-9]+)+\.dll$'
$actualPayloadNames = @($payloadFiles | ForEach-Object {
    $_.Substring($payloadDir.Length + 1).Replace('\', '/') -replace $runtimeVersionedName, 'mscordaccore_amd64_amd64_<runtime>.dll'
})
$payloadList = if ($isInstaller -and -not $isStore) { 'installed-payload-files.txt' } else { 'payload-files.txt' }
$expectedPayloadNames = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot $payloadList) |
    Where-Object { $_ -and -not $_.StartsWith('#') } | Sort-Object)
$payloadDifference = @(Compare-Object $expectedPayloadNames $actualPayloadNames)
if ($payloadDifference.Count -gt 0) {
    $details = ($payloadDifference | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }) -join [Environment]::NewLine
    throw "Payload file set changed. Review dependencies and update build\payload-files.txt deliberately:`n$details"
}
$requiredFiles = @('KillerPDF.App.exe', 'KillerPdf.Engine.dll')
if ($bundlesRuntime) { $requiredFiles += 'System.Text.Json.dll' }
foreach ($required in $requiredFiles) {
    if ($actualPayloadNames -notcontains $required) {
        throw "Required loose payload file is missing ($required). Costura/Fody may have run accidentally."
    }
}
Write-PayloadManifest $manifestPath $payloadDir $payloadFiles

Write-Host "==> Compressing one verified payload..." -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression
if ([IO.File]::Exists($payloadZip)) { [IO.File]::Delete($payloadZip) }
$zipStream = [IO.File]::Open($payloadZip, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $allFiles = @([IO.Directory]::GetFiles($payloadDir, '*', [IO.SearchOption]::AllDirectories) |
            Sort-Object { $_.Substring($payloadDir.Length + 1) })
        foreach ($file in $allFiles) {
            $relative = $file.Substring($payloadDir.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($file)
            $output = $entry.Open()
            try { $input.CopyTo($output) }
            finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $zipStream.Dispose() }

Write-Host "==> Building the public portable launcher..." -ForegroundColor Cyan
$launcherTitle = if ($isInstaller) { 'KillerPDF Installer' } else { 'KillerPDF' }
& dotnet publish $launcherProject -c $Configuration `
    -p:LauncherAssemblyName=KillerPDF `
    -p:AssemblyTitle="$launcherTitle" `
    -p:Copyright="Copyright $([DateTime]::Now.Year) Steve the Killer" `
    -p:LauncherVersion=$version `
    -p:LauncherFileVersion=$fileVersion `
    -p:LauncherIcon="$(Join-Path $projectDir 'Resources\kp-icon.ico')" `
    -p:PayloadZip="$payloadZip" `
    -p:InstallerPackage="$isInstaller" `
    -p:StorePackage="$isStore" `
    -p:AllowUnsignedInstall="$(!$RequireSignature)" `
    -p:PublishDir="$launcherOutput\"
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed.' }

$builtLauncher = Join-Path $launcherOutput 'KillerPDF.exe'
if (-not [IO.File]::Exists($builtLauncher)) { throw "Launcher output is missing: $builtLauncher" }
[IO.File]::Copy($builtLauncher, $publicExe, $true)

# Regression gate for #238: installer commands must install exactly once and exit. They must not
# fall through to the portable launch path or start the installed UI. A disposable install root
# keeps this safe for developer and CI builds without touching registry or installed copies.
# Keep it outside the active build tree so publish cleanup cannot remove the staging payload.
$installSmokeRoot = Join-Path ([IO.Path]::GetTempPath()) ('KillerPDF-install-smoke-' + [Guid]::NewGuid().ToString('N'))
$previousTestRoot = [Environment]::GetEnvironmentVariable('KILLERPDF_TEST_INSTALL_ROOT')
$previousSkipRegistration = [Environment]::GetEnvironmentVariable('KILLERPDF_SKIP_REGISTRATION')
if ($isInstaller -and -not $RequireSignature) { try {
    [Environment]::SetEnvironmentVariable('KILLERPDF_TEST_INSTALL_ROOT', $installSmokeRoot)
    [Environment]::SetEnvironmentVariable('KILLERPDF_SKIP_REGISTRATION', '1')
    # The Store runs the installer with /silent, so that is the path its package is tested on.
    $smokeArgument = if ($isStore) { '/silent' } else { '/install-user' }
    $installProcess = Start-Process -FilePath $builtLauncher -ArgumentList $smokeArgument -Wait -PassThru
    if ($installProcess.ExitCode -ne 0) {
        throw "Launcher install smoke test failed with exit code $($installProcess.ExitCode)."
    }
    foreach ($required in 'KillerPDF.App.exe', 'payload.manifest') {
        if (-not [IO.File]::Exists((Join-Path $installSmokeRoot $required))) {
            throw "Launcher install smoke test did not install $required."
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('KILLERPDF_TEST_INSTALL_ROOT', $previousTestRoot)
    [Environment]::SetEnvironmentVariable('KILLERPDF_SKIP_REGISTRATION', $previousSkipRegistration)
    if ([IO.Directory]::Exists($installSmokeRoot)) { [IO.Directory]::Delete($installSmokeRoot, $true) }
} }

$payloadBytes = ([IO.FileInfo]$payloadZip).Length
$publicBytes = ([IO.FileInfo]$publicExe).Length
Write-Host "    Payload files : $($payloadFiles.Count)" -ForegroundColor Green
Write-Host "    Payload zip   : $payloadBytes bytes" -ForegroundColor Green
Write-Host "    Public EXE    : $publicBytes bytes" -ForegroundColor Green
Write-Host "    Output        : $publicExe" -ForegroundColor Green

[pscustomobject]@{
    Version = $version
    PayloadFiles = $payloadFiles.Count
    PayloadZipBytes = $payloadBytes
    PublicExeBytes = $publicBytes
    PublicExe = $publicExe
}
