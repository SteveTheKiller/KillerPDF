[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $AppExe,
    [Parameter(Mandatory)][string] $InputPdf,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $Language = 'eng'
)

$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $AppExe).Path
$inputPath = (Resolve-Path -LiteralPath $InputPdf).Path
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a new output directory.' }
New-Item -ItemType Directory -Path $root | Out-Null

# Use a profile with the native language model installed and no engine model for
# this language, so the application selects its Tesseract fallback.
function Invoke-Step([string[]] $arguments) {
    $quoted = $arguments | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath $exe -WindowStyle Hidden -PassThru -ArgumentList $quoted
    if (-not $process.WaitForExit(60000)) {
        Stop-Process -Id $process.Id
        throw 'Owned OCR test process timed out.'
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw ('OCR test step failed with exit ' + $process.ExitCode) }
}

$scan = Join-Path $root 'scan.pdf'
$searchable = Join-Path $root 'searchable.pdf'
Invoke-Step @('--flatten', $inputPath, $scan, '--dpi', '150')
Invoke-Step @('--ocr', $scan, $searchable, '--lang', $Language)
if (-not (Test-Path -LiteralPath $searchable -PathType Leaf)) { throw 'OCR output is missing.' }
$render = Join-Path $root 'render'
$log = Join-Path $root 'render.csv'
Invoke-Step @('--batch-render', $searchable, $render, '--size', '1024', '--pages', '1', '--log', $log, '--quiet')
$rows = @(Import-Csv -LiteralPath $log)
if ($rows.Count -ne 1 -or $rows[0].Status -ne 'OK') { throw 'OCR output did not reopen and render.' }
Write-Output 'OCR output created, reopened and rendered. Verify recognized text separately.'
