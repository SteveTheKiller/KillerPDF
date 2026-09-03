# Creates only temporary Git fixtures. Does not modify application branches or push.
$ErrorActionPreference = 'Stop'
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$checker = Join-Path $PSScriptRoot 'Check-MaintenanceForwardPorts.ps1'
$identityName = (& git config user.name)
$identityEmail = (& git config user.email)
if (!$identityName -or !$identityEmail) { throw 'Configure the established Git identity before running fixtures.' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('killerpdf-forward-ports-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $fixture | Out-Null
Push-Location $fixture
try {
    & git init -q
    & git config user.name $identityName
    & git config user.email $identityEmail
    function New-Commit([string]$Value, [string]$Parent, [string]$Subject) {
        Set-Content -LiteralPath (Join-Path $fixture 'fixture.txt') -Value $Value -Encoding ASCII
        & git add -- fixture.txt
        $tree = & git write-tree
        $args = @('commit-tree', $tree, '-m', $Subject)
        if ($Parent) { $args += @('-p', $Parent) }
        $commit = & git @args
        if ($LASTEXITCODE) { throw 'Fixture commit failed.' }
        return $commit
    }
    $baseline = New-Commit 'baseline' '' 'baseline'
    $source = New-Commit 'fixed' $baseline 'v1.8.4: fix'
    $equivalent = New-Commit 'fixed' $baseline 'v1.9.0: fix'
    $adapted = New-Commit 'adapted fix' $baseline 'v1.9.0: adapted fix'
    $policy = @{ schemaVersion = 1; maintenanceBaseline = $baseline; developmentBaseline = $baseline; ports = @() }
    $policyPath = Join-Path $fixture 'policy.json'
    $hostExe = (Get-Process -Id $PID).Path
    function Check([string]$Name, [string]$Main, [string]$Dev, [bool]$Expected) {
        $policy | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $policyPath -Encoding UTF8
        $ErrorActionPreference = 'Continue'
        $output = & $hostExe -NoProfile -File $checker -MaintenanceRef $Main -DevelopmentRef $Dev -PolicyPath $policyPath 2>&1
        $actual = $LASTEXITCODE -eq 0
        $ErrorActionPreference = 'Stop'
        if ($actual -ne $Expected) { throw "${Name}: unexpected result: $output" }
        Write-Host "PASS: $Name"
    }
    Check 'reviewed baseline' $baseline $baseline $true
    Check 'missing fix fails' $source $baseline $false
    Check 'equivalent patch with new version prefix' $source $equivalent $true
    Check 'shared ancestor' $source $source $true
    Check 'adaptation requires record' $source $adapted $false
    $policy.ports = @(@{maintenanceCommit=$source; developmentCommit=$adapted; reason='Ported through the new engine API.'})
    Check 'recorded adaptation' $source $adapted $true
    Check 'unmerged target fails' $source $baseline $false
    $policy.ports[0].reason = ''
    Check 'empty reason fails' $source $adapted $false
    $policy.ports = @()
    Check 'missing ref fails closed' $source 'missing-ref' $false
    $reverted = New-Commit 'baseline' $source 'v1.8.4: revert fix'
    Check 'unported revert fails' $reverted $equivalent $false
    $tree = & git rev-parse "$source^{tree}"
    $merge = & git commit-tree $tree -p $source -p $adapted -m 'merge fixture'
    Check 'unported merge is not silently skipped' $merge $equivalent $false
    Write-Host "Fixtures retained at $fixture"
} finally { Pop-Location }
