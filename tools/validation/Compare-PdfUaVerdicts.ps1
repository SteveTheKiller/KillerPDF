<#
.SYNOPSIS
    Runs veraPDF's PDF/UA-1 profile and KillerPDF's --pdfua check over one
    corpus and reports every file where their pass or fail verdicts disagree.

.DESCRIPTION
    KillerPDF's PDF/UA-1 validator never claims full conformance, because
    the Matterhorn Protocol leaves some conditions to a person and some
    machine conditions are not implemented yet. So the comparison uses one
    question per file: did the validator find a failure?

      veraPDF fails   = the ua1 profile reports compliant=false
      KillerPDF fails = --pdfua reports verdict "failed"

    A KillerPDF verdict of "incomplete" or "passedMachineChecks" counts as
    not failed. Each file gets one result:

      AGREE_FAIL        both validators found failures
      AGREE_PASS        neither found a failure
      VERA_ONLY_FAIL    veraPDF failed it and KillerPDF did not (a check KillerPDF misses)
      KILLER_ONLY_FAIL  KillerPDF failed it and veraPDF did not (a possible false failure)
      VERA_PARSE_ERROR  veraPDF could not validate the file
      KILLER_UNREADABLE KillerPDF could not read the file
      MISSING           the file is absent from one of the two reports

    Disagreements list the veraPDF rules that failed (clause and test
    number) next to the Matterhorn conditions KillerPDF failed, so the gap
    can be traced to a specific check.

    Run veraPDF here:
        .\Compare-PdfUaVerdicts.ps1 -Corpus C:\pdf-corpus -VeraPdf C:\tools\verapdf\verapdf.bat

    Or reuse a report made earlier with
        verapdf --flavour ua1 --recurse --format json C:\pdf-corpus > ua1.json
    and pass it:
        .\Compare-PdfUaVerdicts.ps1 -Corpus C:\pdf-corpus -VeraReport ua1.json

    Exit code 0 = the validators agree on every file, 1 = at least one
    disagreement, missing file or read failure, 2 = usage or input error.

.NOTES
    Compatible with Windows PowerShell 5.1 and PowerShell 7.
    Part of the KillerPDF validation harness (tools/validation/).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Corpus,
    [string]$KillerPdf,
    [string]$VeraPdf,
    [string]$VeraReport,
    [string]$KillerReport,
    [string]$CsvOut,
    [switch]$ShowAgreements
)

$ErrorActionPreference = 'Stop'

# Usage and input problems exit 2; Write-Error would throw under Stop and exit 1.
function Stop-Usage {
    param([string]$Message)
    [Console]::Error.WriteLine($Message)
    exit 2
}

if (-not (Test-Path -LiteralPath $Corpus -PathType Container)) {
    Stop-Usage -Message "Corpus folder not found: $Corpus"
    exit 2
}
$Corpus = (Resolve-Path -LiteralPath $Corpus).ProviderPath.TrimEnd('\', '/')
if (-not $VeraPdf -and -not $VeraReport) {
    Stop-Usage -Message 'Pass -VeraPdf to run veraPDF, or -VeraReport with a saved veraPDF ua1 JSON report.'
    exit 2
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('kp-pdfua-compare-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

function Invoke-ToFile {
    param([string]$FilePath, [string[]]$Arguments, [string]$OutFile)
    $quoted = $Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }
    $errFile = $OutFile + '.err'
    $process = Start-Process -FilePath $FilePath -ArgumentList $quoted -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $OutFile -RedirectStandardError $errFile
    return $process.ExitCode
}

function Get-RelativeKey {
    param([string]$Path)
    $full = $Path
    try { $full = [IO.Path]::GetFullPath($Path) } catch { }
    $key = $full.ToLowerInvariant()
    $root = $Corpus.ToLowerInvariant()
    if ($key.StartsWith($root)) { $key = $key.Substring($root.Length).TrimStart('\', '/') }
    return $key.Replace('/', '\')
}

function Read-Json {
    param([string]$Path, [string]$What)
    if (-not (Test-Path -LiteralPath $Path)) {
        Stop-Usage -Message "$What not found: $Path"
        exit 2
    }
    $raw = [IO.File]::ReadAllText($Path)
    $start = $raw.IndexOf('{')
    if ($start -lt 0) {
        Stop-Usage -Message "$What has no JSON: $Path"
        exit 2
    }
    try { return $raw.Substring($start) | ConvertFrom-Json }
    catch {
        Stop-Usage -Message "Could not parse $What ${Path}: $($_.Exception.Message)"
        exit 2
    }
}

try {
    # ---- KillerPDF ----
    if (-not $KillerReport) {
        if (-not $KillerPdf) {
            $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
            $KillerPdf = Join-Path $repo 'bin\Release\net10.0-windows\KillerPDF.exe'
        }
        if (-not (Test-Path -LiteralPath $KillerPdf)) {
            Stop-Usage -Message "KillerPDF.exe not found: $KillerPdf. Build KillerPDF.csproj in Release or pass -KillerPdf."
            exit 2
        }
        $KillerReport = Join-Path $work 'killerpdf.json'
        Write-Host "Running KillerPDF --pdfua over $Corpus"
        $code = Invoke-ToFile -FilePath $KillerPdf -Arguments @('--pdfua', $Corpus, '--json') -OutFile $KillerReport
        if ($code -eq 2) {
            Stop-Usage -Message "KillerPDF refused the corpus: $([IO.File]::ReadAllText($KillerReport).Trim())"
            exit 2
        }
    }
    $killerJson = Read-Json -Path $KillerReport -What 'KillerPDF report'
    if ($null -eq $killerJson.files) {
        Stop-Usage -Message "No files array in $KillerReport - is this KillerPDF --pdfua --json output?"
        exit 2
    }
    $killer = @{}
    foreach ($file in @($killerJson.files)) {
        $killer[(Get-RelativeKey ([string]$file.path))] = [pscustomobject]@{
            Verdict    = [string]$file.verdict
            Conditions = @($file.failedConditions | Where-Object { $_ })
            Error      = [string]$file.error
        }
    }

    # ---- veraPDF ----
    if (-not $VeraReport) {
        if (-not (Test-Path -LiteralPath $VeraPdf)) {
            Stop-Usage -Message "veraPDF not found: $VeraPdf"
            exit 2
        }
        $VeraReport = Join-Path $work 'verapdf.json'
        Write-Host "Running veraPDF --flavour ua1 over $Corpus"
        [void](Invoke-ToFile -FilePath $VeraPdf `
            -Arguments @('--flavour', 'ua1', '--recurse', '--format', 'json', $Corpus) -OutFile $VeraReport)
    }
    $veraJson = Read-Json -Path $VeraReport -What 'veraPDF report'
    if (-not $veraJson.report -or $null -eq $veraJson.report.jobs) {
        Stop-Usage -Message "No report.jobs in $VeraReport - is this a veraPDF --format json report?"
        exit 2
    }
    $vera = @{}
    foreach ($job in @($veraJson.report.jobs)) {
        $vr = $null
        $vrProp = $job.PSObject.Properties['validationResult']
        if ($null -ne $vrProp -and $null -ne $job.validationResult) {
            $vrArr = @($job.validationResult)
            if ($vrArr.Count -gt 0) { $vr = $vrArr[0] }
        }
        $rules = @()
        if ($null -ne $vr -and $vr.details -and $vr.details.ruleSummaries) {
            foreach ($rs in @($vr.details.ruleSummaries)) {
                if ([string]$rs.ruleStatus -eq 'FAILED') { $rules += ('{0}-{1}' -f $rs.clause, $rs.testNumber) }
            }
        }
        if ($null -ne $vr -and $vr.profileName -and ([string]$vr.profileName) -notmatch 'UA') {
            Stop-Usage -Message "veraPDF report used profile '$($vr.profileName)', not PDF/UA-1. Run it with --flavour ua1."
            exit 2
        }
        $vera[(Get-RelativeKey ([string]$job.itemDetails.name))] = [pscustomobject]@{
            ParseError = ($null -eq $vr)
            Compliant  = if ($null -ne $vr) { [bool]$vr.compliant } else { $null }
            Rules      = @($rules | Sort-Object -Unique)
        }
    }

    # ---- compare ----
    $results = New-Object System.Collections.Generic.List[object]
    $counts = [ordered]@{}
    $keys = @(@($killer.Keys) + @($vera.Keys) | Sort-Object -Unique)
    foreach ($key in $keys) {
        $k = $killer[$key]
        $v = $vera[$key]
        $detail = ''
        if ($null -eq $k -or $null -eq $v) {
            $change = 'MISSING'
            $detail = if ($null -eq $k) { 'Absent from the KillerPDF report' } else { 'Absent from the veraPDF report' }
        } elseif ($k.Verdict -eq 'unreadable') {
            $change = 'KILLER_UNREADABLE'
            $detail = $k.Error
        } elseif ($v.ParseError) {
            $change = 'VERA_PARSE_ERROR'
            $detail = 'veraPDF returned no validation result'
        } else {
            $killerFails = $k.Verdict -eq 'failed'
            $veraFails = -not $v.Compliant
            $change = if ($killerFails -and $veraFails) { 'AGREE_FAIL' }
                elseif (-not $killerFails -and -not $veraFails) { 'AGREE_PASS' }
                elseif ($veraFails) { 'VERA_ONLY_FAIL' }
                else { 'KILLER_ONLY_FAIL' }
            $detail = 'veraPDF: ' + $(if ($v.Rules.Count) { $v.Rules -join ', ' } else { 'none' }) +
                ' | KillerPDF: ' + $(if ($k.Conditions.Count) { $k.Conditions -join ', ' } else { 'none' })
        }
        if ($counts.Contains($change)) { $counts[$change]++ } else { $counts[$change] = 1 }
        $results.Add([pscustomobject]@{ File = $key; Result = $change; Detail = $detail })
    }

    $problems = @($results | Where-Object { $_.Result -notin 'AGREE_FAIL', 'AGREE_PASS' })
    $shown = if ($ShowAgreements) { $results } else { $problems }
    if ($shown.Count -gt 0) { $shown | Format-Table -AutoSize -Wrap | Out-String -Width 400 | Write-Host }
    $tally = ($counts.GetEnumerator() | ForEach-Object { '{0} {1}' -f $_.Value, $_.Key }) -join ', '
    Write-Host ('Compared {0} files: {1}' -f $results.Count, $tally)
    if ($CsvOut) { $results | Export-Csv -LiteralPath $CsvOut -NoTypeInformation -Encoding UTF8 }

    if ($problems.Count -gt 0) { exit 1 }
    exit 0
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
