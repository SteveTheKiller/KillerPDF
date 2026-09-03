# Read-only. Requires full Git history and current refs; never fetches or changes files.
# Exits 1 for missing ports, invalid records, or unavailable history.
[CmdletBinding()]
param(
    [string]$MaintenanceRef = 'origin/main',
    [string]$DevelopmentRef = 'origin/dev/1.9-overkill',
    [string]$PolicyPath
)
$ErrorActionPreference = 'Stop'
if (!$PolicyPath) { $PolicyPath = Join-Path $PSScriptRoot '../.github/maintenance-forward-ports.json' }
function Read-Git([string[]]$Arguments) {
    $output = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Git failed: $($output -join ' ')" }
    return $output
}
function Resolve-Commit([string]$Ref) {
    return [string](Read-Git @('rev-parse', '--verify', '--end-of-options', "$Ref^{commit}"))
}
function Test-Ancestor([string]$Older, [string]$Newer) {
    & git merge-base --is-ancestor $Older $Newer
    if ($LASTEXITCODE -gt 1) { throw 'Could not inspect commit ancestry.' }
    return $LASTEXITCODE -eq 0
}
try {
    $policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
    if ($policy.schemaVersion -ne 1) { throw 'Unsupported forward-port policy version.' }
    $maintenance = Resolve-Commit $MaintenanceRef
    $development = Resolve-Commit $DevelopmentRef
    $baseline = Resolve-Commit $policy.maintenanceBaseline
    $devBaseline = Resolve-Commit $policy.developmentBaseline
    if (!(Test-Ancestor $baseline $maintenance) -or !(Test-Ancestor $devBaseline $development)) {
        throw 'A branch does not contain its reviewed baseline. Check the refs and full history.'
    }
    $commits = @(Read-Git @('rev-list', '--reverse', "$baseline..$maintenance"))
    $equivalent = @{}
    foreach ($line in (Read-Git @('cherry', $development, $maintenance, $baseline))) {
        if ($line -match '^- ([0-9a-f]+)$') { $equivalent[$Matches[1]] = $true }
    }
    $records = @{}
    foreach ($port in $policy.ports) {
        if ($port.maintenanceCommit -notmatch '^[0-9a-f]{40}$' -or $port.developmentCommit -notmatch '^[0-9a-f]{40}$') {
            throw 'Forward-port records require full commit hashes.'
        }
        if ([string]::IsNullOrWhiteSpace($port.reason)) { throw 'Each adapted port requires a reason.' }
        if ($records.ContainsKey($port.maintenanceCommit)) { throw 'Duplicate maintenance commit in forward-port records.' }
        $source = Resolve-Commit $port.maintenanceCommit
        $target = Resolve-Commit $port.developmentCommit
        if (!(Test-Ancestor $source $maintenance) -or !(Test-Ancestor $target $development)) {
            throw "Recorded port is not on the checked branches: $source -> $target"
        }
        if (!(Test-Ancestor $baseline $source) -or $source -eq $baseline) {
            throw 'Recorded maintenance commit must follow the reviewed baseline.'
        }
        $records[$source] = $true
    }
    $missing = @()
    foreach ($commit in $commits) {
        # Enumerating rev-list also catches merge commits omitted by git cherry.
        if ((Test-Ancestor $commit $development) -or $equivalent.ContainsKey($commit) -or $records.ContainsKey($commit)) { continue }
        $missing += [string](Read-Git @('show', '-s', '--format=%h %s', $commit))
    }
    if ($missing.Count) {
        Write-Host 'Maintenance commits missing from Overkill:'
        $missing | ForEach-Object { Write-Host "  $_" }
        throw 'Port these changes to 1.9.0. For an adapted fix, record its exact development commit and reason in .github/maintenance-forward-ports.json.'
    }
    Write-Host "Forward-port check passed: $($commits.Count) maintenance commits covered."
    # Git ancestry probes can return 1 without failing the coverage check.
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
