#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [int]$Iterations = 5,
    [string]$OutputCsv = "",
    [int]$TimeoutSeconds = 45,
    [switch]$Launcher
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (-not $OutputCsv) {
    $OutputCsv = Join-Path $PSScriptRoot "startup-results.csv"
}
$outputParent = Split-Path -Parent $OutputCsv
if ($outputParent) { [System.IO.Directory]::CreateDirectory($outputParent) | Out-Null }

function Read-Trace([string]$Path) {
    $lines = @(Get-Content -LiteralPath $Path)
    if ($lines.Count -lt 2) { throw "Startup trace is incomplete: $Path" }

    $header = $lines[0]
    $processStartText = [regex]::Match($header, 'processStartUtc=([^|]+)').Groups[1].Value.Trim()
    $traceStartText = [regex]::Match($header, 'traceStartUtc=([^|]+)').Groups[1].Value.Trim()
    $processStart = [datetimeoffset]::Parse($processStartText, [Globalization.CultureInfo]::InvariantCulture)
    $traceStart = [datetimeoffset]::Parse($traceStartText, [Globalization.CultureInfo]::InvariantCulture)
    $loaderMs = ($traceStart - $processStart).TotalMilliseconds

    $marks = @{}
    foreach ($line in $lines | Select-Object -Skip 1) {
        $parts = $line -split "`t", 2
        if ($parts.Count -eq 2) {
            $marks[$parts[1]] = [double]::Parse($parts[0], [Globalization.CultureInfo]::InvariantCulture)
        }
    }

    if (-not $marks.ContainsKey('MainWindow ready')) { throw "Trace has no ready marker: $Path" }
    [pscustomobject]@{
        LoaderToOnStartupMs = [math]::Round($loaderMs, 1)
        OnStartupToReadyMs = [math]::Round($marks['MainWindow ready'], 1)
        ProcessToReadyMs = [math]::Round($loaderMs + $marks['MainWindow ready'], 1)
        MainWindowConstructMs = if ($marks.ContainsKey('Locale initialized') -and
                                      $marks.ContainsKey('MainWindow constructed')) {
            [math]::Round($marks['MainWindow constructed'] - $marks['Locale initialized'], 1)
        } else { 0 }
        ReadyUtc = $traceStart.AddMilliseconds($marks['MainWindow ready'])
    }
}

$results = @()
$oldTrace = [Environment]::GetEnvironmentVariable('KILLERPDF_STARTUP_TRACE', 'Process')
try {
    for ($i = 1; $i -le $Iterations; $i++) {
        $trace = Join-Path ([IO.Path]::GetTempPath()) ("killerpdf-startup-{0}.trace" -f [guid]::NewGuid().ToString('N'))
        [Environment]::SetEnvironmentVariable('KILLERPDF_STARTUP_TRACE', $trace, 'Process')
        $process = Start-Process -FilePath $ExePath -PassThru
        $launchedUtc = $process.StartTime.ToUniversalTime()
        $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)

        try {
            while ([datetime]::UtcNow -lt $deadline) {
                if ($process.HasExited) { throw "KillerPDF exited before reaching the ready marker (exit $($process.ExitCode))." }
                if ((Test-Path -LiteralPath $trace) -and
                    (Select-String -LiteralPath $trace -SimpleMatch 'MainWindow ready' -Quiet)) { break }
                Start-Sleep -Milliseconds 25
                $process.Refresh()
            }
            if (-not (Test-Path -LiteralPath $trace) -or
                -not (Select-String -LiteralPath $trace -SimpleMatch 'MainWindow ready' -Quiet)) {
                throw "Timed out after $TimeoutSeconds seconds waiting for KillerPDF startup."
            }

            $timing = Read-Trace $trace
            $processToReady = if ($Launcher) {
                ($timing.ReadyUtc.UtcDateTime - $launchedUtc).TotalMilliseconds
            } else { $timing.ProcessToReadyMs }
            $results += [pscustomobject]@{
                Iteration = $i
                CacheState = if ($i -eq 1) { 'first' } else { 'warm' }
                Exe = $ExePath
                ExeBytes = (Get-Item -LiteralPath $ExePath).Length
                LoaderToOnStartupMs = $timing.LoaderToOnStartupMs
                OnStartupToReadyMs = $timing.OnStartupToReadyMs
                ProcessToReadyMs = [math]::Round($processToReady, 1)
                MainWindowConstructMs = $timing.MainWindowConstructMs
            }
        }
        finally {
            if ($Launcher -and -not $process.HasExited) {
                $children = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                    Where-Object { $_.ParentProcessId -eq $process.Id -and $_.Name -eq 'KillerPDF.App.exe' })
                foreach ($child in $children) {
                    Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
                }
                $null = $process.WaitForExit(5000)
            }
            if (-not $process.HasExited) {
                $null = $process.CloseMainWindow()
                if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force }
            }
            Remove-Item -LiteralPath $trace -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable('KILLERPDF_STARTUP_TRACE', $oldTrace, 'Process')
}

$results | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Encoding UTF8
$results | Format-Table -AutoSize

$warm = @($results | Where-Object CacheState -eq 'warm')
if ($warm.Count -gt 0) {
    Write-Host ("Warm mean process-to-ready: {0:N1} ms" -f (($warm | Measure-Object ProcessToReadyMs -Average).Average))
}
Write-Host "Results: $OutputCsv"
