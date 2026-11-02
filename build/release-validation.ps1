#Requires -Version 5.1
# Build-output-only library. Reuse requires the same clean source commit and exact tested bytes.
function Invoke-ReleaseDesktopTests([string]$TestProject,[string]$CliExecutable,[string]$ExpectedVersion,[string]$ResultsDirectory) {
    if(![IO.File]::Exists($CliExecutable)){throw "CLI test executable is missing: $CliExecutable"}
    $actual=[Diagnostics.FileVersionInfo]::GetVersionInfo($CliExecutable).FileVersion
    if($actual -ne $ExpectedVersion){throw "CLI test executable is version $actual, expected $ExpectedVersion`: $CliExecutable"}
    Write-Host "    CLI test executable: $CliExecutable ($actual)"
    $previous=[Environment]::GetEnvironmentVariable('KILLERPDF_CLI_TEST_EXE')
    # These window-opening cases belong on the owned UI VMs, not the main PC.
    $filter='FullyQualifiedName!~ComparisonBarLayoutTests&FullyQualifiedName!~WindowFxTests&FullyQualifiedName!~BottomTabFaceFlipsWhileItsLabelAndCloseTargetStayUpright&FullyQualifiedName!~TabTraversalEntersAndLeavesTheReadingDocument'
    try {
        [Environment]::SetEnvironmentVariable('KILLERPDF_CLI_TEST_EXE',$CliExecutable)
        & dotnet test $TestProject -c Release --nologo -v quiet --filter $filter --logger 'trx;LogFileName=desktop.trx' --results-directory $ResultsDirectory
        if($LASTEXITCODE -ne 0){throw 'Desktop unit tests failed - fix them before releasing'}
        Assert-ReleaseTestResult (Join-Path $ResultsDirectory 'desktop.trx') 'KillerPDF.Tests.dll'
    } finally {[Environment]::SetEnvironmentVariable('KILLERPDF_CLI_TEST_EXE',$previous)}
}

function Write-ReleaseValidationRecord([string]$Path, $Record) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null
    $temporary=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    $backup=$temporary+'.previous'
    try {
        [IO.File]::WriteAllText($temporary,($Record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
        if([IO.File]::Exists($Path)){[IO.File]::Replace($temporary,$Path,$backup)}
        else {[IO.File]::Move($temporary,$Path)}
    } finally {
        if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)}
        if([IO.File]::Exists($backup)){[IO.File]::Delete($backup)}
    }
}

function Reset-ReleaseDryRun([string]$PublishDirectory) {
    Write-ReleaseValidationRecord (Join-Path $PublishDirectory 'release-validation.json') @{
        Schema=1;Complete=$false;StartedUtc=[DateTime]::UtcNow.ToString('o')
    }
    # A runner that exits without producing its new TRX must not inherit an older pass.
    foreach($name in @('engine.trx','desktop.trx')) {
        $path=Join-Path $PublishDirectory ('release-validation/'+$name)
        if([IO.File]::Exists($path)){[IO.File]::Delete($path)}
    }
}

function Assert-ReleaseTestResult([string]$Path,[string]$AssemblyName) {
    [xml]$trx=[IO.File]::ReadAllText($Path)
    $definitions=@($trx.SelectNodes("//*[local-name()='UnitTest']"))
    $results=@($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $counters=$trx.SelectSingleNode("//*[local-name()='Counters']")
    $summary=$trx.SelectSingleNode("//*[local-name()='ResultSummary']")
    if(!$definitions.Count -or @($definitions|Where-Object{
        [IO.Path]::GetFileName(([string]$_.storage).Replace('/', '\')) -ine $AssemblyName
    }).Count -or !$results.Count -or !$counters -or !$summary -or
        $summary.outcome -notin @('Completed','Passed') -or
        @($results|Where-Object{$_.outcome -ne 'Passed'}).Count -or
        [int]$counters.total -ne $results.Count -or [int]$counters.executed -ne $results.Count -or
        [int]$counters.passed -ne $results.Count -or [int]$counters.failed -ne 0 -or
        [int]$counters.notExecuted -ne 0){
        throw "A complete, nonempty passing $AssemblyName result is required: $Path"
    }
}

function Get-ReleaseValidationFiles([string]$PublishDirectory,[string]$Version) {
    foreach($name in @('KillerPDF.exe','KillerPDF-Portable.exe','KillerPDF-Store.exe',
        ('KillerPDF-'+$Version+'-src.zip'),'SHA256SUMS.txt','release-validation/engine.trx','release-validation/desktop.trx')){
        $path=Join-Path $PublishDirectory $name
        if(![IO.File]::Exists($path)){throw "Dry-run input is missing: $path. Run a full signed -DryRun first."}
        [pscustomobject]@{Name=$name;Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    }
}

function Save-ReleaseDryRun([string]$PublishDirectory,[string]$Version,[string]$SourceCommit,[bool]$Signed) {
    Assert-ReleaseTestResult (Join-Path $PublishDirectory 'release-validation/engine.trx') 'KillerPdf.Engine.Tests.dll'
    Assert-ReleaseTestResult (Join-Path $PublishDirectory 'release-validation/desktop.trx') 'KillerPDF.Tests.dll'
    $files=@(Get-ReleaseValidationFiles $PublishDirectory $Version)
    Write-ReleaseValidationRecord (Join-Path $PublishDirectory 'release-validation.json') @{
        Schema=1;Complete=$true;Signed=$Signed;Version=$Version;SourceCommit=$SourceCommit;
        CompletedUtc=[DateTime]::UtcNow.ToString('o');Files=$files
    }
}

function Read-ReleaseDryRun([string]$PublishDirectory,[string]$Version,[string]$SourceCommit) {
    $path=Join-Path $PublishDirectory 'release-validation.json'
    if(![IO.File]::Exists($path)){throw 'No reusable dry-run record. Run a full signed -DryRun first.'}
    $proof=[IO.File]::ReadAllText($path)|ConvertFrom-Json
    if($proof.Schema -ne 1 -or $proof.Complete -isnot [bool] -or !$proof.Complete){throw 'The last dry run did not complete successfully.'}
    if($proof.Signed -isnot [bool] -or !$proof.Signed){throw 'An unsigned dry run cannot be reused for release.'}
    if($proof.Version -cne $Version -or $proof.SourceCommit -cne $SourceCommit){throw 'Dry-run source/version changed. Run a full signed -DryRun first.'}
    $current=@(Get-ReleaseValidationFiles $PublishDirectory $Version)
    if(@($proof.Files).Count -ne $current.Count){throw 'Dry-run file inventory changed.'}
    foreach($file in $current){
        $record=@($proof.Files|Where-Object{$_.Name -ceq $file.Name})
        if($record.Count -ne 1 -or $record[0].Sha256 -cne $file.Sha256){throw "Dry-run file changed: $($file.Name). Run a full signed -DryRun first."}
    }
    Assert-ReleaseTestResult (Join-Path $PublishDirectory 'release-validation/engine.trx') 'KillerPdf.Engine.Tests.dll'
    Assert-ReleaseTestResult (Join-Path $PublishDirectory 'release-validation/desktop.trx') 'KillerPDF.Tests.dll'
    return $proof
}
