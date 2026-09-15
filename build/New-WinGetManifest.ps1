param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$Tag,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$version = $Tag.Substring(1)
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'KillerPDF-WinGet-Release'
    'X-GitHub-Api-Version' = '2022-11-28'
}
if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }
$release = Invoke-RestMethod "https://api.github.com/repos/SteveTheKiller/KillerPDF/releases/tags/$Tag" -Headers $headers
if ($release.draft -or $release.prerelease -or $release.tag_name -ne $Tag) {
    throw 'WinGet requires a published stable release matching the requested tag.'
}
$assets = @($release.assets | Where-Object { $_.name -eq 'KillerPDF.exe' })
if ($assets.Count -ne 1) { throw 'The release must contain exactly one KillerPDF.exe installer.' }
$url = "https://github.com/SteveTheKiller/KillerPDF/releases/download/$Tag/KillerPDF.exe"
if ($assets[0].browser_download_url -ne $url) { throw 'Unexpected installer download URL.' }

$directory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $directory) { throw 'Use a new output directory to avoid submitting stale manifests.' }
[IO.Directory]::CreateDirectory($directory) | Out-Null
$installerPath = "$directory.exe"
if (Test-Path -LiteralPath $installerPath) { throw 'The installer download path already exists.' }
Invoke-WebRequest -Uri $url -OutFile $installerPath -UseBasicParsing
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
if ($assets[0].digest -and $assets[0].digest -ne "sha256:$($hash.ToLowerInvariant())") {
    throw 'Downloaded installer does not match the GitHub release digest.'
}
$date = ([datetime]$release.published_at).ToUniversalTime().ToString('yyyy-MM-dd')
$utf8 = New-Object System.Text.UTF8Encoding($false)

# The launcher installs machine-wide with /silent. Its interactive wizard can
# select a user install, so this machine-scoped manifest advertises silent modes only.
$installer = @"
# Created by the KillerPDF release workflow
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json

PackageIdentifier: SteveTheKiller.KillerPDF
PackageVersion: $version
InstallerType: exe
Scope: machine
InstallModes:
- silent
- silentWithProgress
InstallerSwitches:
  Silent: /silent
  SilentWithProgress: /silent
UpgradeBehavior: install
Dependencies:
  PackageDependencies:
  - PackageIdentifier: Microsoft.DotNet.DesktopRuntime.10
    MinimumVersion: 10.0.0
AppsAndFeaturesEntries:
- DisplayName: KillerPDF
  Publisher: Steve the Killer
  DisplayVersion: $version
  ProductCode: KillerPDF
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $hash
ManifestType: installer
ManifestVersion: 1.12.0
"@
$locale = @"
# Created by the KillerPDF release workflow
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json

PackageIdentifier: SteveTheKiller.KillerPDF
PackageVersion: $version
PackageLocale: en-US
Publisher: Steve the Killer
PublisherUrl: https://github.com/SteveTheKiller
PublisherSupportUrl: https://github.com/SteveTheKiller/KillerPDF/issues
Author: Steve the Killer
PackageName: KillerPDF
PackageUrl: https://github.com/SteveTheKiller/KillerPDF
License: GPL-3.0
LicenseUrl: https://github.com/SteveTheKiller/KillerPDF/blob/HEAD/LICENSE
ShortDescription: PDF editor for Windows. No account, no subscription, no telemetry.
Moniker: killerpdf
ReleaseNotesUrl: https://github.com/SteveTheKiller/KillerPDF/releases/tag/$Tag
ManifestType: defaultLocale
ManifestVersion: 1.12.0
"@
$manifest = @"
# Created by the KillerPDF release workflow
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json

PackageIdentifier: SteveTheKiller.KillerPDF
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.12.0
"@
[IO.File]::WriteAllText((Join-Path $directory 'SteveTheKiller.KillerPDF.installer.yaml'), $installer, $utf8)
[IO.File]::WriteAllText((Join-Path $directory 'SteveTheKiller.KillerPDF.locale.en-US.yaml'), $locale, $utf8)
[IO.File]::WriteAllText((Join-Path $directory 'SteveTheKiller.KillerPDF.yaml'), $manifest, $utf8)
Write-Host "Generated WinGet manifests for $Tag with SHA256 $hash in $directory"
