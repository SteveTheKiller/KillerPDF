#Requires -Version 5.1
param([Parameter(Mandatory)][string]$EvidenceRoot)
# Offline runner tests. Only the external dotnet test command is modeled.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a fresh evidence directory.'}
[IO.Directory]::CreateDirectory($EvidenceRoot)|Out-Null
. (Join-Path $PSScriptRoot 'release-validation.ps1')
$exe=Join-Path $EvidenceRoot 'KillerPDF.App.exe'
$typeName='DesktopRunnerFixture'+[guid]::NewGuid().ToString('N')
$assembly=Join-Path $EvidenceRoot 'fixture.dll'
$source=Join-Path $EvidenceRoot 'fixture.cs'
[IO.File]::WriteAllText($source,('[assembly:System.Reflection.AssemblyFileVersion("1.9.2.0")] public class '+$typeName+' {}'))
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:library ('/out:'+$assembly) $source
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed.'}
Copy-Item -LiteralPath $assembly -Destination $exe
$global:desktopRunnerObserved=''
$global:desktopRunnerExit=0
function global:dotnet {
    $global:desktopRunnerObserved=[Environment]::GetEnvironmentVariable('KILLERPDF_CLI_TEST_EXE')
    $filterIndex=[Array]::IndexOf([object[]]$args,'--filter')
    if($filterIndex -lt 0){throw 'Host desktop test filter is missing.'}
    foreach($test in @('ComparisonBarLayoutTests','WindowFxTests','BottomTabFaceFlipsWhileItsLabelAndCloseTargetStayUpright','TabTraversalEntersAndLeavesTheReadingDocument')){
        if($args[$filterIndex+1] -notlike ('*FullyQualifiedName!~'+$test+'*')){throw 'A VM-only test was not excluded.'}
    }
    $resultIndex=[Array]::IndexOf([object[]]$args,'--results-directory')
    $output=Join-Path $args[$resultIndex+1] 'desktop.trx'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))|Out-Null
    [IO.File]::WriteAllText($output,'<TestRun><TestDefinitions><UnitTest storage="C:/fixture/KillerPDF.Tests.dll" /></TestDefinitions><Results><UnitTestResult outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>')
    $global:LASTEXITCODE=$global:desktopRunnerExit
}
$before=[Environment]::GetEnvironmentVariable('KILLERPDF_CLI_TEST_EXE')
try{
    [Environment]::SetEnvironmentVariable('KILLERPDF_CLI_TEST_EXE','prior-cli-value')
    Invoke-ReleaseDesktopTests -TestProject 'fixture.csproj' -CliExecutable $exe -ExpectedVersion '1.9.2.0' -ResultsDirectory $EvidenceRoot
    if($global:desktopRunnerObserved -ne $exe -or $env:KILLERPDF_CLI_TEST_EXE -ne 'prior-cli-value'){throw 'Fresh CLI selection or environment restoration failed.'}
    $global:desktopRunnerExit=1
    $refused=$false
    try{Invoke-ReleaseDesktopTests 'fixture.csproj' $exe '1.9.2.0' $EvidenceRoot}catch{
        if($_.Exception.Message -notlike '*Desktop unit tests failed*'){throw};$refused=$true
    }
    if(!$refused -or $env:KILLERPDF_CLI_TEST_EXE -ne 'prior-cli-value'){throw 'Failed tests were accepted or the caller environment was changed.'}
    $global:desktopRunnerObserved='not-run'
    $refused=$false
    try{Invoke-ReleaseDesktopTests 'fixture.csproj' $exe '1.9.3.0' $EvidenceRoot}catch{
        if($_.Exception.Message -notlike '*CLI test executable*version*'){throw};$refused=$true
    }
    if(!$refused -or $global:desktopRunnerObserved -ne 'not-run'){throw 'Stale CLI reached the test command.'}
}finally{[Environment]::SetEnvironmentVariable('KILLERPDF_CLI_TEST_EXE',$before)}
Write-Output 'PASS fresh payload CLI, host window exclusions, failure propagation, stale-version refusal and environment restoration.'
