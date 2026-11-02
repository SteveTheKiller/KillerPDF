#Requires -Version 7.0
param([Parameter(Mandatory)][string]$EvidenceRoot,
    [ValidateSet('unsigned-store', 'missing-store-payload', 'unsigned-store-inner', 'mismatched-store-version',
        'valid-store', 'checksum-create', 'checksum-replaced-store', 'checksum-missing-store',
        'checksum-unchanged-store')][string]$Mode = 'unsigned-store')

# Exercise the final release gate offline. No release entry point or package is run.
# Only Windows signature API results are modeled. Version reads, embedded ZIP inspection,
# the actual shared gate invocation and checksum commands run on synthetic packages.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
$releasePath = Join-Path $repository 'release.ps1'
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
if (Test-Path -LiteralPath $evidence) { throw 'Use a fresh evidence directory.' }
$null = [IO.Directory]::CreateDirectory($evidence)
$fixtureRoot = Join-Path $evidence 'fixture'
$null = [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot 'build'))
$payload = Join-Path $fixtureRoot 'payload'
$null = [IO.Directory]::CreateDirectory($payload)
foreach ($name in @('KillerPDF.App.exe', 'KillerPDF.App.dll', 'KillerPdf.Engine.dll')) {
    [IO.File]::WriteAllBytes((Join-Path $payload $name), [byte[]]@(1, 2, 3))
}
$zip = Join-Path $fixtureRoot 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$compilers = @(& $dotnet --list-sdks | ForEach-Object {
    if ($_ -match '^(?<version>[^ ]+) \[(?<root>.+)\]$') {
        Join-Path $Matches.root ($Matches.version + '/Roslyn/bincore/csc.dll')
    }
} | Where-Object { Test-Path -LiteralPath $_ })
if ($compilers.Count -eq 0) { throw 'A local .NET SDK compiler is required for the package fixtures.' }
$compiler = $compilers[-1]
foreach ($name in @('KillerPDF.exe', 'KillerPDF-Portable.exe', 'KillerPDF-Store.exe')) {
    $path = Join-Path $fixtureRoot $name
    $typeName = 'SyntheticPackage' + [guid]::NewGuid().ToString('N')
    $fixtureVersion = if ($Mode -eq 'mismatched-store-version' -and $name -eq 'KillerPDF-Store.exe') { '1.8.91.0' } else { '1.9.0.0' }
    $resourceName = if ($Mode -eq 'missing-store-payload' -and $name -eq 'KillerPDF-Store.exe') { 'Wrong.payload.zip' } else { 'KillerLauncher.payload.zip' }
    $definition = '[assembly: System.Reflection.AssemblyVersion("' + $fixtureVersion + '")] public static class ' + $typeName + ' {}'
    $sourcePath = Join-Path $fixtureRoot ($typeName + '.cs')
    [IO.File]::WriteAllText($sourcePath, $definition, [Text.UTF8Encoding]::new($false))
    & $dotnet exec $compiler -nologo -target:library ('-out:' + $path) `
        ('-resource:' + $zip + ',' + $resourceName) `
        ('-r:' + [object].Assembly.Location) $sourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic package compilation failed.' }
    if ([Reflection.AssemblyName]::GetAssemblyName($path).Version.ToString() -ne $fixtureVersion) {
        throw 'Synthetic package version control failed.'
    }
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path))
    $resource = $assembly.GetManifestResourceStream('KillerLauncher.payload.zip')
    if (($resourceName -eq 'KillerLauncher.payload.zip') -ne ($null -ne $resource)) {
        throw 'Synthetic embedded-payload presence/absence control failed.'
    }
    if ($null -ne $resource) { $resource.Dispose() }
}
[IO.File]::WriteAllText((Join-Path $fixtureRoot 'build/Test-GuiStartup.ps1'),
    "throw 'Offline GUI boundary reached.'", [Text.UTF8Encoding]::new($false))

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($releasePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Release script has syntax errors.' }
$definitions = [Collections.Generic.List[string]]::new()
foreach ($name in @('Assert-ReleaseArtifactVersion', 'Test-ReleaseArtifacts')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if ($null -eq $function) { throw "Public release function is missing: $name" }
    $definitions.Add($function.Extent.Text)
}
$functionsPath = Join-Path $fixtureRoot 'final-gate-functions.ps1'
[IO.File]::WriteAllText($functionsPath, ($definitions -join "`n"), [Text.UTF8Encoding]::new($false))
. $functionsPath
$calls = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Test-ReleaseArtifacts'
}, $true))
if ($calls.Count -ne 1) { throw 'Expected one shared final-artifact gate invocation.' }
. (Join-Path $repository 'build/payload-security.ps1')

$script:SignatureChecks = [Collections.Generic.List[string]]::new()
$script:CurrentPackage = ''
function Get-AuthenticodeSignature {
    param([Parameter(Mandatory)][string]$LiteralPath)
    $name = [IO.Path]::GetFileName($LiteralPath)
    $script:SignatureChecks.Add($name)
    if ($name -in @('KillerPDF.exe', 'KillerPDF-Portable.exe', 'KillerPDF-Store.exe')) { $script:CurrentPackage = $name }
    [pscustomobject]@{
        Status = if (($Mode -eq 'unsigned-store' -and $name -eq 'KillerPDF-Store.exe') -or
            ($Mode -eq 'unsigned-store-inner' -and $script:CurrentPackage -eq 'KillerPDF-Store.exe' -and $name -eq 'KillerPdf.Engine.dll')) {
            [Management.Automation.SignatureStatus]::NotSigned
        } else { [Management.Automation.SignatureStatus]::Valid }
        SignatureType = 'Authenticode'
    }
}
function Start-Process { throw 'An offline artifact test attempted to launch a process.' }

$installerExe = Join-Path $fixtureRoot 'KillerPDF.exe'
$portableExe = Join-Path $fixtureRoot 'KillerPDF-Portable.exe'
$storeExe = Join-Path $fixtureRoot 'KillerPDF-Store.exe'
$expectedAssemblyVersion = '1.9.0.0'
$SkipSign = $false
$ReuseDryRun = $false
if ($Mode.StartsWith('checksum-', [StringComparison]::Ordinal)) {
    $publishDir = $fixtureRoot
    $srcPath = Join-Path $fixtureRoot 'KillerPDF-1.9.0-src.zip'
    [IO.File]::Copy($zip, $srcPath)
    $srcZip = Get-Item -LiteralPath $srcPath
    $sumsFile = Join-Path $fixtureRoot 'SHA256SUMS.txt'
    $PublishOnly = $Mode -ne 'checksum-create'
    if ($PublishOnly) {
        $seed = foreach ($file in @($installerExe, $portableExe, $storeExe, $srcPath)) {
            if ($Mode -eq 'checksum-missing-store' -and $file -eq $storeExe) { continue }
            [IO.Path]::GetFileName($file) + '  ' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        }
        [IO.File]::WriteAllLines($sumsFile, [string[]]$seed, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllLines((Join-Path $evidence 'baseline-sums.txt'), [string[]]$seed)
        if ($Mode -eq 'checksum-replaced-store') {
            $append = [IO.File]::Open($storeExe, [IO.FileMode]::Append, [IO.FileAccess]::Write)
            try { $append.WriteByte(10) } finally { $append.Dispose() }
        }
        if ([Reflection.AssemblyName]::GetAssemblyName($storeExe).Version.ToString() -ne '1.9.0.0') {
            throw 'Replaced Store fixture no longer has the same version.'
        }
    }
    $assignments = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -in @('$portableHash', '$installerHash', '$storeHash', '$sumsPath')
    }, $true) | Sort-Object { $_.Extent.StartOffset })
    foreach ($assignment in $assignments) { . ([scriptblock]::Create($assignment.Extent.Text)) }
    $checksum = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -like '*$PublishOnly*Test-Path*$sumsPath*'
    }, $true)
    if ($null -eq $checksum) { throw 'Shared checksum creation/verification command is missing.' }
    $observed = ''
    try { . ([scriptblock]::Create($checksum.Extent.Text)) }
    catch { $observed = $_.Exception.Message }
    [IO.File]::WriteAllText((Join-Path $evidence 'observed.txt'), $observed)
    if ($Mode -eq 'checksum-unchanged-store') {
        if ($observed) { throw "Valid existing checksums were rejected: $observed" }
        if ((Get-FileHash -LiteralPath $sumsFile -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $evidence 'baseline-sums.txt') -Algorithm SHA256).Hash) {
            throw 'PublishOnly rewrote the valid checksum file.'
        }
    } elseif ($PublishOnly) {
        if ($observed -notlike '*SHA256SUMS.txt does not match KillerPDF-Store.exe*') {
            throw "WIRE-003: PublishOnly accepted a replaced same-version Store artifact. Observed: $observed"
        }
    } else {
        if ($observed) { throw "Checksum creation failed: $observed" }
        $expectedHash = (Get-FileHash -LiteralPath $storeExe -Algorithm SHA256).Hash
        if ([IO.File]::ReadAllText($sumsFile) -notmatch ('(?m)^KillerPDF-Store\.exe\s+' + $expectedHash + '\s*$')) {
            throw 'WIRE-003: newly created checksums omit the exact Store artifact.'
        }
    }
    Write-Output ("PASS final release checksum case: $Mode")
    return
}
$temporary = Join-Path $evidence 'temp'
$null = [IO.Directory]::CreateDirectory($temporary)
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $temporary
$env:TMP = $temporary
$observed = ''
try { & ([scriptblock]::Create($calls[0].Extent.Text)) }
catch { $observed = $_.Exception.Message }
finally { $env:TEMP = $previousTemp; $env:TMP = $previousTmp }
[IO.File]::WriteAllText((Join-Path $evidence 'signature-checks.txt'),
    ($script:SignatureChecks -join "`n"), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $evidence 'observed.txt'), $observed, [Text.UTF8Encoding]::new($false))
$expected = switch ($Mode) {
    'unsigned-store' { '*Authenticode validation failed for*KillerPDF-Store.exe*NotSigned*' }
    'missing-store-payload' { '*Missing embedded payload:*KillerPDF-Store.exe*' }
    'unsigned-store-inner' { '*First-party Authenticode validation failed for*KillerPdf.Engine.dll*NotSigned*' }
    'mismatched-store-version' { '*KillerPDF-Store.exe is version 1.8.91.0, expected 1.9.0.0*' }
    'valid-store' { 'Offline GUI boundary reached.' }
}
if ($observed -notlike $expected) { throw "Final release gate case $Mode did not produce its specific outcome. Observed: $observed" }
if ($Mode -ne 'mismatched-store-version' -and -not $script:SignatureChecks.Contains('KillerPDF-Store.exe')) {
    throw 'Store signature boundary was not inspected.'
}
if ($Mode -eq 'valid-store' -and $script:SignatureChecks.Count -ne 12) {
    throw 'The valid control did not inspect all three outer packages and their first-party payloads.'
}
Write-Output ("PASS final release gate case: $Mode. No package or GUI process was launched.")
