<#
.SYNOPSIS
    qpdf structural sweep: runs `qpdf --check` on every original/resave pair and flags any
    file whose exit code worsened.

.DESCRIPTION
    The second half of the release validation (Compare-VeraPDF.ps1 is the first). Reads the
    resave log written by `KillerPDF.exe --batch-resave` and checks only the rows marked OK -
    skipped files were never written and have nothing to compare.

    qpdf exit codes: 0 = clean, 2 = errors, 3 = warnings only. "Worsened" is any pair whose
    after-code is higher than its before-code. The release bar is zero worsened.

    .\QpdfSweep.ps1 -Corpus C:\pdf-corpus -Resaved C:\pdf-corpus-resaved `
        -ResaveLog ..\resave.csv -CsvOut qpdf-results.csv

    Exit code 0 = no worsened pairs, 1 = worsened pairs found, 2 = usage/input error.

.NOTES
    Compatible with Windows PowerShell 5.1 and PowerShell 7.
    Part of the KillerPDF validation harness (validation/).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Corpus,
    [Parameter(Mandatory = $true)] [string]$Resaved,
    [Parameter(Mandatory = $true)] [string]$ResaveLog,
    [Parameter(Mandatory = $true)] [string]$CsvOut
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command qpdf -ErrorAction SilentlyContinue)) { Write-Error 'qpdf not on PATH'; exit 2 }
if (-not (Test-Path -LiteralPath $ResaveLog)) { Write-Error "Resave log not found: $ResaveLog"; exit 2 }

$rows = Import-Csv -LiteralPath $ResaveLog | Where-Object { $_.Status -eq 'OK' }
$results = New-Object System.Collections.Generic.List[object]
$i = 0

foreach ($r in $rows) {
    $i++
    if ($i % 100 -eq 0) { Write-Host ("{0} / {1}" -f $i, $rows.Count) }
    $o = Join-Path $Corpus  $r.File
    $n = Join-Path $Resaved $r.File
    if (-not (Test-Path -LiteralPath $n)) {
        $results.Add([pscustomobject]@{ File = $r.File; Before = -1; After = -1; Worsened = 'MISSING' })
        continue
    }
    & qpdf --check $o *> $null; $b = $LASTEXITCODE
    & qpdf --check $n *> $null; $a = $LASTEXITCODE
    $results.Add([pscustomobject]@{ File = $r.File; Before = $b; After = $a; Worsened = ($a -gt $b) })
}

$results | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8

Write-Host ''
Write-Host ('Pairs checked : {0}' -f $results.Count)
$results | Group-Object { '{0} -> {1}' -f $_.Before, $_.After } | Sort-Object Count -Descending |
    ForEach-Object { Write-Host ('  {0,-10} {1}' -f $_.Name, $_.Count) }
$worse = @($results | Where-Object { $_.Worsened -eq $true -or $_.Worsened -eq 'MISSING' })
Write-Host ('Worsened      : {0}' -f $worse.Count)

if ($worse.Count -gt 0) {
    Write-Host 'RESULT: FAIL - structural health worsened on at least one file.' -ForegroundColor Red
    exit 1
} else {
    Write-Host 'RESULT: PASS - no file''s structural health got worse.' -ForegroundColor Green
    exit 0
}
