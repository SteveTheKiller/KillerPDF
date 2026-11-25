#Requires -Version 5.1
# test.ps1 - the test steps that used to run inside release.ps1, and nothing else.
# Runs the engine unit tests and the host-safe desktop unit tests on the committed source, then
# writes test-results\engine.trx, desktop.trx and source.json. release.ps1 runs no tests: it takes
# these results only when source.json names the exact commit and version being released.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string]$Message) { Write-Host "RED: $Message" -ForegroundColor Red; exit 1 }
function Step([string]$Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }

$root = $PSScriptRoot
$results = Join-Path $root 'test-results'
. (Join-Path $root 'build\release-validation.ps1')

Push-Location $root
try {
    $head = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { Fail 'git rev-parse HEAD failed.' }
    $dirty = [bool](git status --porcelain)
} finally { Pop-Location }
$versionNode = ([xml](Get-Content -Raw (Join-Path $root 'KillerPDF.csproj'))).SelectSingleNode('//FileVersion')
if (-not $versionNode) { Fail 'FileVersion is missing from KillerPDF.csproj.' }
$version = $versionNode.InnerText.Trim()

# Old results never count for a new run.
if (Test-Path -LiteralPath $results) { Remove-Item -LiteralPath $results -Recurse -Force }
[IO.Directory]::CreateDirectory($results) | Out-Null

try {
    Step "Building KillerPDF $version (Release) for the CLI tests"
    dotnet build (Join-Path $root 'KillerPDF.csproj') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    Step 'Engine unit tests'
    dotnet test (Join-Path $root 'engine\KillerPdf.Engine.Tests\KillerPdf.Engine.Tests.csproj') -c Release --nologo -v quiet --logger 'trx;LogFileName=engine.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'Engine unit tests failed - fix them before releasing' }
    Assert-ReleaseTestResult (Join-Path $results 'engine.trx') 'KillerPdf.Engine.Tests.dll'
    Write-Host '    Engine unit tests passed' -ForegroundColor Green

    Step 'Host-safe desktop unit tests'
    Invoke-ReleaseDesktopTests -TestProject (Join-Path $root 'KillerPDF.Tests\KillerPDF.Tests.csproj') `
        -CliExecutable (Join-Path $root 'bin\Release\net10.0-windows\KillerPDF.exe') -ExpectedVersion $version -ResultsDirectory $results
    Write-Host '    Desktop unit tests passed' -ForegroundColor Green
} catch {
    Fail $_.Exception.Message
}

[IO.File]::WriteAllText((Join-Path $results 'source.json'),
    (@{ Commit = $head; Version = $version; Dirty = $dirty; Finished = (Get-Date).ToString('o') } | ConvertTo-Json),
    [Text.UTF8Encoding]::new($false))

if ($dirty) {
    Write-Host "`nGREEN, but the working tree had uncommitted changes, so release.ps1 will not accept these results. Commit and run test.ps1 again." -ForegroundColor Yellow
} else {
    Write-Host "`nGREEN: KillerPDF $version at $($head.Substring(0, 7)). release.ps1 will use these results." -ForegroundColor Green
}
