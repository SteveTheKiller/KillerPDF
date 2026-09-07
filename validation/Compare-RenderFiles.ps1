[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $BaselineExe,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $EngineExe,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $InputDirectory,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [ValidateRange(1, 1000000)]
    [int] $MaximumFiles = 100,

    [ValidateRange(1, 86400)]
    [int] $TimeoutSeconds = 30,

    [ValidateRange(16, 8192)]
    [int] $Size = 1024,

    [ValidateRange(1, 1000)]
    [int] $Pages = 1
)

$ErrorActionPreference = 'Stop'

<#
Runs a focused per-file render comparison between two KillerPDF builds.

Measurements include per-file cold process startup, JIT, PDF open, render, PNG
write, and process shutdown time. They are useful for diagnosing individual
files. They are not collection throughput measurements.

The input PDFs are never modified. The output directory must not already exist.
Each input gets one numbered folder that retains each build's PNG output and
raw batch-render CSV log.
#>

function Resolve-FullPath {
    param([Parameter(Mandatory)][string] $Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function ConvertTo-RelativePath {
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $Path
    )

    $rootPath = Resolve-FullPath -Path $Root
    $filePath = Resolve-FullPath -Path $Path
    if (-not $rootPath.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $rootPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $rootUri = [Uri]::new($rootPath)
    $fileUri = [Uri]::new($filePath)
    $relative = $rootUri.MakeRelativeUri($fileUri).ToString()
    return [Uri]::UnescapeDataString($relative).Replace('/', [System.IO.Path]::DirectorySeparatorChar)
}

function Get-BuildHashes {
    param([Parameter(Mandatory)][string] $Executable)

    $directory = [System.IO.Path]::GetDirectoryName($Executable)
    $appDllPath = Join-Path $directory 'KillerPDF.dll'
    $engineDllPath = Join-Path $directory 'KillerPdf.Engine.dll'
    $appHashPath = if (Test-Path -LiteralPath $appDllPath -PathType Leaf) { $appDllPath } else { $Executable }
    $engineHash = if (Test-Path -LiteralPath $engineDllPath -PathType Leaf) {
        (Get-FileHash -LiteralPath $engineDllPath -Algorithm SHA256).Hash
    }
    else {
        ''
    }

    return [pscustomobject]@{
        BuildHash = (Get-FileHash -LiteralPath $appHashPath -Algorithm SHA256).Hash
        EngineHash = $engineHash
    }
}

function Format-CsvField {
    param([AllowNull()][string] $Value)

    $text = if ($null -eq $Value) { '' } else { [string]$Value }
    if ($text.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -lt 0) {
        return $text
    }

    return '"' + $text.Replace('"', '""') + '"'
}

function ConvertTo-CsvLine {
    param([Parameter(Mandatory)][object] $Record)

    $values = foreach ($column in $script:SummaryColumns) {
        Format-CsvField -Value ([string]$Record.$column)
    }

    return [string]::Join(',', $values)
}

function Add-SummaryRecord {
    param(
        [Parameter(Mandatory)][string] $SummaryPath,
        [Parameter(Mandatory)][object] $Record
    )

    $line = ConvertTo-CsvLine -Record $Record
    Add-Content -LiteralPath $SummaryPath -Value $line -Encoding UTF8
}

function Set-ProcessArguments {
    param(
        [Parameter(Mandatory)][System.Diagnostics.ProcessStartInfo] $StartInfo,
        [Parameter(Mandatory)][string[]] $Arguments
    )

    $argumentListProperty = $StartInfo.GetType().GetProperty('ArgumentList')
    if ($null -ne $argumentListProperty) {
        $argumentList = $argumentListProperty.GetValue($StartInfo, $null)
        foreach ($argument in $Arguments) {
            [void]$argumentList.Add($argument)
        }
        return
    }

    $quoted = foreach ($argument in $Arguments) {
        ConvertTo-WindowsCommandArgument -Argument $argument
    }
    $StartInfo.Arguments = [string]::Join(' ', $quoted)
}

function ConvertTo-WindowsCommandArgument {
    param([AllowNull()][string] $Argument)

    if ($null -eq $Argument) {
        return '""'
    }

    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $slashCount++
            continue
        }

        if ($character -eq '"') {
            [void]$builder.Append('\', ($slashCount * 2) + 1)
            [void]$builder.Append('"')
            $slashCount = 0
            continue
        }

        if ($slashCount -gt 0) {
            [void]$builder.Append('\', $slashCount)
            $slashCount = 0
        }
        [void]$builder.Append($character)
    }

    if ($slashCount -gt 0) {
        [void]$builder.Append('\', $slashCount * 2)
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-BatchRender {
    param(
        [Parameter(Mandatory)][string] $BuildName,
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string] $BuildHash,
        [Parameter(Mandatory)][AllowEmptyString()][string] $EngineHash,
        [Parameter(Mandatory)][string] $InputPath,
        [Parameter(Mandatory)][string] $RelativeFile,
        [Parameter(Mandatory)][string] $InputHash,
        [Parameter(Mandatory)][string] $FileOutputRoot,
        [Parameter(Mandatory)][string] $SummaryPath
    )

    $buildRoot = Join-Path $FileOutputRoot $BuildName
    $imageRoot = Join-Path $buildRoot 'png'
    $logPath = Join-Path $buildRoot 'batch-render.csv'
    New-Item -ItemType Directory -Path $imageRoot -Force | Out-Null

    $arguments = @(
        '--batch-render',
        $InputPath,
        $imageRoot,
        '--size',
        ([string]$Size),
        '--pages',
        ([string]$Pages),
        '--log',
        $logPath,
        '--quiet'
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    Set-ProcessArguments -StartInfo $startInfo -Arguments $arguments

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $started = $false
    $timedOut = $false
    $exitCode = $null
    $processStatus = 'NOT_STARTED'
    $processDetail = ''
    $fatalProcessDetail = ''

    try {
        $started = $process.Start()
        if ($started) {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $timedOut = $true
                $processStatus = 'TIMEOUT'
                try {
                    $process.Kill()
                    if (-not $process.WaitForExit(5000)) {
                        throw "Timed out and could not stop process $($process.Id)."
                    }
                }
                catch {
                    $processDetail = $_.Exception.Message
                    $fatalProcessDetail = $processDetail
                }
            }
            else {
                $exitCode = $process.ExitCode
                $processStatus = 'EXITED'
            }
        }
    }
    catch {
        $processStatus = 'START_FAILED'
        $processDetail = $_.Exception.Message
    }
    finally {
        $watch.Stop()
        $process.Dispose()
    }

    if ($fatalProcessDetail.Length -gt 0) {
        throw $fatalProcessDetail
    }

    $cliRows = @()
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        try {
            $cliRows = @(Import-Csv -LiteralPath $logPath)
        }
        catch {
            $processDetail = if ($processDetail.Length -gt 0) {
                $processDetail + ' | log parse failed: ' + $_.Exception.Message
            }
            else {
                'log parse failed: ' + $_.Exception.Message
            }
        }
    }

    if ($cliRows.Count -eq 0) {
        Add-SummaryRecord -SummaryPath $SummaryPath -Record ([pscustomobject]@{
            RelativeFile = $RelativeFile
            InputSHA256 = $InputHash
            Build = $BuildName
            BuildHash = $BuildHash
            EngineHash = $EngineHash
            TimedOut = $timedOut
            ProcessStatus = $processStatus
            ExitCode = $exitCode
            WallMilliseconds = $watch.ElapsedMilliseconds
            LogPresent = (Test-Path -LiteralPath $logPath -PathType Leaf)
            CliFile = ''
            CliPage = ''
            CliStatus = 'NO_LOG_ROW'
            CliRenderMilliseconds = ''
            CliWidth = ''
            CliHeight = ''
            CliDetail = $processDetail
        })
        return
    }

    foreach ($row in $cliRows) {
        Add-SummaryRecord -SummaryPath $SummaryPath -Record ([pscustomobject]@{
            RelativeFile = $RelativeFile
            InputSHA256 = $InputHash
            Build = $BuildName
            BuildHash = $BuildHash
            EngineHash = $EngineHash
            TimedOut = $timedOut
            ProcessStatus = $processStatus
            ExitCode = $exitCode
            WallMilliseconds = $watch.ElapsedMilliseconds
            LogPresent = $true
            CliFile = $row.File
            CliPage = $row.Page
            CliStatus = $row.Status
            CliRenderMilliseconds = $row.Milliseconds
            CliWidth = $row.Width
            CliHeight = $row.Height
            CliDetail = if ($processDetail.Length -gt 0 -and [string]::IsNullOrWhiteSpace($row.Detail)) {
                $processDetail
            }
            else {
                $row.Detail
            }
        })
    }
}

$resolvedBaselineExe = Resolve-FullPath -Path $BaselineExe
$resolvedEngineExe = Resolve-FullPath -Path $EngineExe
$resolvedInput = Resolve-FullPath -Path $InputDirectory
$resolvedOutput = Resolve-FullPath -Path $OutputDirectory

if (Test-Path -LiteralPath $resolvedOutput) {
    throw "OutputDirectory already exists: $resolvedOutput"
}

New-Item -ItemType Directory -Path $resolvedOutput | Out-Null

$script:SummaryColumns = @(
    'RelativeFile',
    'InputSHA256',
    'Build',
    'BuildHash',
    'EngineHash',
    'TimedOut',
    'ProcessStatus',
    'ExitCode',
    'WallMilliseconds',
    'LogPresent',
    'CliFile',
    'CliPage',
    'CliStatus',
    'CliRenderMilliseconds',
    'CliWidth',
    'CliHeight',
    'CliDetail'
)

$summaryPath = Join-Path $resolvedOutput 'render-comparison-summary.csv'
Set-Content -LiteralPath $summaryPath -Value ([string]::Join(',', $script:SummaryColumns)) -Encoding UTF8

$baselineHashes = Get-BuildHashes -Executable $resolvedBaselineExe
$engineHashes = Get-BuildHashes -Executable $resolvedEngineExe

$files = @(Get-ChildItem -LiteralPath $resolvedInput -Filter '*.pdf' -File -Recurse |
    ForEach-Object {
        [pscustomobject]@{
            FullName = $_.FullName
            Relative = ConvertTo-RelativePath -Root $resolvedInput -Path $_.FullName
        }
    } |
    Sort-Object Relative |
    Select-Object -First $MaximumFiles)

for ($index = 0; $index -lt $files.Count; $index++) {
    $file = $files[$index]
    $ordinal = ($index + 1).ToString('000000', [System.Globalization.CultureInfo]::InvariantCulture)
    $fileRoot = Join-Path $resolvedOutput $ordinal
    New-Item -ItemType Directory -Path $fileRoot | Out-Null

    $inputHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $order = if (($index % 2) -eq 0) {
        @(
            @{ Name = 'baseline'; Exe = $resolvedBaselineExe; BuildHash = $baselineHashes.BuildHash; EngineHash = $baselineHashes.EngineHash },
            @{ Name = 'engine'; Exe = $resolvedEngineExe; BuildHash = $engineHashes.BuildHash; EngineHash = $engineHashes.EngineHash }
        )
    }
    else {
        @(
            @{ Name = 'engine'; Exe = $resolvedEngineExe; BuildHash = $engineHashes.BuildHash; EngineHash = $engineHashes.EngineHash },
            @{ Name = 'baseline'; Exe = $resolvedBaselineExe; BuildHash = $baselineHashes.BuildHash; EngineHash = $baselineHashes.EngineHash }
        )
    }

    foreach ($build in $order) {
        Invoke-BatchRender -BuildName $build.Name `
            -Executable $build.Exe `
            -BuildHash $build.BuildHash `
            -EngineHash $build.EngineHash `
            -InputPath $file.FullName `
            -RelativeFile $file.Relative `
            -InputHash $inputHash `
            -FileOutputRoot $fileRoot `
            -SummaryPath $summaryPath
    }
}

Write-Host "Render comparison summary: $summaryPath"
