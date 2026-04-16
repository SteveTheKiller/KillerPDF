# Called automatically by the csproj after Publish.
# Produces <AppName>-<Version>-src.zip inside the publish folder.
# PS 5.1 / PS 7 compatible. Uses git to list tracked files so bin/obj/.vs never ship.
param(
    [Parameter(Mandatory)][string]$ProjectDir,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$AppName,
    [Parameter(Mandatory)][string]$PublishDir
)

$ErrorActionPreference = 'Stop'

$projectDirFull = (Resolve-Path $ProjectDir).Path
$publishDirFull = if ([System.IO.Path]::IsPathRooted($PublishDir)) {
    $PublishDir
} else {
    Join-Path $projectDirFull $PublishDir
}

if (-not (Test-Path $publishDirFull)) {
    New-Item -ItemType Directory -Force -Path $publishDirFull | Out-Null
}

$zip = Join-Path $publishDirFull "$AppName-$Version-src.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

$staging = Join-Path $env:TEMP "$AppName-src-$([guid]::NewGuid())"
try {
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Push-Location $projectDirFull
    try {
        $files = @(& git ls-files 2>$null)
        if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) {
            Write-Warning "Source bundle skipped: git ls-files returned no tracked files (is git installed and is this a repo?)."
            return
        }
        foreach ($f in $files) {
            $dst = Join-Path $staging $f
            $parent = Split-Path $dst -Parent
            if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
            Copy-Item $f $dst -Force
        }
        $rootLicense = Join-Path $projectDirFull 'LICENSE'
        if (Test-Path $rootLicense) { Copy-Item $rootLicense (Join-Path $staging 'LICENSE') -Force }
    } finally {
        Pop-Location
    }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
    Write-Host "Source bundle: $zip" -ForegroundColor Green
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
