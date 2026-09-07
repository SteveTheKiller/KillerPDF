[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $BaselineExe,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $CandidateExe,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string] $InputDirectory,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [ValidateRange(1, 25)]
    [int] $Runs = 5,

    [string] $BaselineLabel = 'Baseline',

    [string] $CandidateLabel = 'Candidate',

    # Resave times --batch-resave (open/save pipeline). Render times --batch-render
    # (first pages of every file to PNG at RenderSize).
    [ValidateSet('Resave', 'Render')]
    [string] $Mode = 'Resave',

    [ValidateRange(16, 8192)]
    [int] $RenderSize = 1024,

    [ValidateRange(1, 1000)]
    [int] $RenderPages = 1
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
$profileRoot = [System.IO.Path]::GetFullPath($env:USERPROFILE)

if (-not $resolvedOutput.StartsWith($profileRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must be inside the current user profile: $profileRoot"
}

if ($resolvedOutput -eq $profileRoot) {
    throw 'OutputDirectory cannot be the user profile root.'
}

New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-BenchmarkRun {
    param(
        [Parameter(Mandatory)][string] $Label,
        [Parameter(Mandatory)][string] $Executable,
        [Parameter(Mandatory)][string] $RunName,
        [Parameter(Mandatory)][bool] $Measured
    )

    $runOutput = Join-Path $resolvedOutput "output-$RunName"
    $runLog = Join-Path $resolvedOutput "$RunName.csv"
    $resolvedRunOutput = [System.IO.Path]::GetFullPath($runOutput)
    $requiredPrefix = $resolvedOutput + [System.IO.Path]::DirectorySeparatorChar + 'output-'

    if (-not $resolvedRunOutput.StartsWith($requiredPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe benchmark output path: $resolvedRunOutput"
    }

    if (Test-Path -LiteralPath $runOutput) {
        Remove-Item -LiteralPath $runOutput -Recurse -Force
    }
    if (Test-Path -LiteralPath $runLog) {
        Remove-Item -LiteralPath $runLog -Force
    }

    New-Item -ItemType Directory -Path $runOutput | Out-Null
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()

    if ($Mode -eq 'Render') {
        $arguments = @(
            '--batch-render', $InputDirectory, $runOutput,
            '--size', $RenderSize, '--pages', $RenderPages,
            '--log', $runLog, '--quiet'
        )
    }
    else {
        $arguments = @('--batch-resave', $InputDirectory, $runOutput, '--log', $runLog)
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -Wait
    $stopwatch.Stop()

    if (-not (Test-Path -LiteralPath $runLog)) {
        throw "$Label did not create its batch log: $runLog"
    }

    $rows = Import-Csv -LiteralPath $runLog
    $saved = @($rows | Where-Object Status -eq 'OK').Count
    $failed = @($rows | Where-Object Status -eq 'FAIL').Count
    $result = [pscustomobject]@{
        Version = $Label
        Run = $RunName
        Measured = $Measured
        Seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        Files = $saved
        Failed = $failed
        FilesPerSecond = [math]::Round($saved / $stopwatch.Elapsed.TotalSeconds, 2)
        ExitCode = $process.ExitCode
    }

    $results.Add($result)
    $result | Format-Table -AutoSize
    Remove-Item -LiteralPath $runOutput -Recurse -Force
}

Invoke-BenchmarkRun -Label $BaselineLabel -Executable $BaselineExe `
    -RunName 'warmup-baseline' -Measured $false
Invoke-BenchmarkRun -Label $CandidateLabel -Executable $CandidateExe `
    -RunName 'warmup-candidate' -Measured $false

for ($run = 1; $run -le $Runs; $run++) {
    if ($run % 2 -eq 1) {
        Invoke-BenchmarkRun -Label $BaselineLabel -Executable $BaselineExe `
            -RunName "run-$run-baseline" -Measured $true
        Invoke-BenchmarkRun -Label $CandidateLabel -Executable $CandidateExe `
            -RunName "run-$run-candidate" -Measured $true
    }
    else {
        Invoke-BenchmarkRun -Label $CandidateLabel -Executable $CandidateExe `
            -RunName "run-$run-candidate" -Measured $true
        Invoke-BenchmarkRun -Label $BaselineLabel -Executable $BaselineExe `
            -RunName "run-$run-baseline" -Measured $true
    }
}

$resultsPath = Join-Path $resolvedOutput 'benchmark-results.csv'
$results | Export-Csv -LiteralPath $resultsPath -NoTypeInformation

$summary = $results |
    Where-Object Measured |
    Group-Object Version |
    ForEach-Object {
        $times = @($_.Group.Seconds | Sort-Object)
        $rates = @($_.Group.FilesPerSecond | Sort-Object)
        $middle = [math]::Floor($times.Count / 2)
        [pscustomobject]@{
            Version = $_.Name
            Runs = $_.Count
            MedianSeconds = $times[$middle]
            MinimumSeconds = $times[0]
            MaximumSeconds = $times[-1]
            MedianFilesPerSecond = $rates[$middle]
        }
    }

$summaryPath = Join-Path $resolvedOutput 'benchmark-summary.csv'
$summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation

Write-Host "Results: $resultsPath"
Write-Host "Summary: $summaryPath"
Write-Host ($summary | Format-Table -AutoSize | Out-String)

# Per-file comparison (Render mode only: the resave log carries no timing).
# For every file, take the median per-run milliseconds of each build over the
# measured runs, then rank by the candidate's added time so the worst offenders
# surface without a full-corpus profile. Open time is included when the build
# logs an OpenMilliseconds column; older builds without it report render time only.
if ($Mode -eq 'Render') {
    $perFile = @{}
    foreach ($result in $results) {
        if (-not $result.Measured) { continue }
        $runLog = Join-Path $resolvedOutput "$($result.Run).csv"
        if (-not (Test-Path -LiteralPath $runLog)) { continue }
        $hasOpen = $false
        $rows = Import-Csv -LiteralPath $runLog
        if ($rows.Count -gt 0) {
            $hasOpen = $null -ne ($rows[0].PSObject.Properties['OpenMilliseconds'])
        }
        $byFile = @{}
        foreach ($row in $rows) {
            $ms = [double]$row.Milliseconds
            if ($hasOpen -and $row.OpenMilliseconds -ne '') { $ms += [double]$row.OpenMilliseconds }
            if (-not $byFile.ContainsKey($row.File)) {
                $byFile[$row.File] = @{ Ms = 0.0; Status = $row.Status }
            }
            $byFile[$row.File].Ms += $ms
            if ($row.Status -ne 'OK') { $byFile[$row.File].Status = $row.Status }
        }
        foreach ($file in $byFile.Keys) {
            if (-not $perFile.ContainsKey($file)) { $perFile[$file] = @{} }
            if (-not $perFile[$file].ContainsKey($result.Version)) {
                $perFile[$file][$result.Version] = @{ Times = [System.Collections.Generic.List[double]]::new(); Status = $byFile[$file].Status }
            }
            $perFile[$file][$result.Version].Times.Add($byFile[$file].Ms)
        }
    }

    function Get-Median([System.Collections.Generic.List[double]] $values) {
        if ($values.Count -eq 0) { return $null }
        $sorted = @($values | Sort-Object)
        return $sorted[[math]::Floor($sorted.Count / 2)]
    }

    $comparison = foreach ($file in $perFile.Keys) {
        $baseline = $perFile[$file][$BaselineLabel]
        $candidate = $perFile[$file][$CandidateLabel]
        $baselineMs = if ($baseline) { Get-Median $baseline.Times } else { $null }
        $candidateMs = if ($candidate) { Get-Median $candidate.Times } else { $null }
        $delta = if ($null -ne $baselineMs -and $null -ne $candidateMs) { $candidateMs - $baselineMs } else { $null }
        $ratio = if ($null -ne $delta -and $baselineMs -gt 0) { [math]::Round($candidateMs / $baselineMs, 3) } else { $null }
        [pscustomobject]@{
            File = $file
            BaselineStatus = if ($baseline) { $baseline.Status } else { 'MISSING' }
            CandidateStatus = if ($candidate) { $candidate.Status } else { 'MISSING' }
            BaselineMedianMs = $baselineMs
            CandidateMedianMs = $candidateMs
            DeltaMs = $delta
            Ratio = $ratio
        }
    }

    $comparisonPath = Join-Path $resolvedOutput 'per-file-comparison.csv'
    $comparison | Sort-Object { if ($null -eq $_.DeltaMs) { [double]::MinValue } else { $_.DeltaMs } } -Descending |
        Export-Csv -LiteralPath $comparisonPath -NoTypeInformation
    Write-Host "Per-file comparison: $comparisonPath"

    $shared = @($comparison | Where-Object { $_.BaselineStatus -eq 'OK' -and $_.CandidateStatus -eq 'OK' })
    if ($shared.Count -gt 0) {
        $sharedBaseline = ($shared | Measure-Object BaselineMedianMs -Sum).Sum
        $sharedCandidate = ($shared | Measure-Object CandidateMedianMs -Sum).Sum
        Write-Host ("Shared OK files: {0}, baseline {1:N0} ms, candidate {2:N0} ms, ratio {3:N4}" -f `
            $shared.Count, $sharedBaseline, $sharedCandidate, ($sharedCandidate / $sharedBaseline))
        Write-Host 'Worst 20 by added milliseconds:'
        Write-Host ($shared | Sort-Object DeltaMs -Descending | Select-Object -First 20 |
            Format-Table File, BaselineMedianMs, CandidateMedianMs, DeltaMs, Ratio -AutoSize | Out-String)
        Write-Host 'Best 10 by saved milliseconds:'
        Write-Host ($shared | Sort-Object DeltaMs | Select-Object -First 10 |
            Format-Table File, BaselineMedianMs, CandidateMedianMs, DeltaMs, Ratio -AutoSize | Out-String)
    }
}
