#Requires -Version 5.1
param([Parameter(Mandatory)][string]$EvidenceRoot)
# Offline behavioral checks. All files belong to the supplied fresh fixture directory.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repository=Split-Path -Parent $PSScriptRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a fresh evidence directory.'}
[IO.Directory]::CreateDirectory($EvidenceRoot)|Out-Null
. (Join-Path $PSScriptRoot 'release-validation.ps1')
$publish=Join-Path $EvidenceRoot 'publish'
[IO.Directory]::CreateDirectory((Join-Path $publish 'release-validation'))|Out-Null
$head='1111111111111111111111111111111111111111'
foreach($name in @('KillerPDF.exe','KillerPDF-Portable.exe','KillerPDF-Store.exe','KillerPDF-1.9.2-src.zip','SHA256SUMS.txt')){
    [IO.File]::WriteAllText((Join-Path $publish $name),'original '+$name)
}
foreach($suite in @('engine','desktop')){
    $assembly=if($suite -eq 'engine'){'KillerPdf.Engine.Tests.dll'}else{'KillerPDF.Tests.dll'}
    $trx='<TestRun><TestDefinitions><UnitTest storage="C:/fixture/'+$assembly+'" /></TestDefinitions><Results><UnitTestResult outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>'
    [IO.File]::WriteAllText((Join-Path $publish ('release-validation/'+$suite+'.trx')),$trx)
}
Save-ReleaseDryRun -PublishDirectory $publish -Version '1.9.2' -SourceCommit $head -Signed $true
$proof=Read-ReleaseDryRun -PublishDirectory $publish -Version '1.9.2' -SourceCommit $head
if($proof.SourceCommit -ne $head){throw 'Matching signed dry run was not reused.'}
Write-Output 'PASS exact successful signed dry run is reusable.'

function Reject([scriptblock]$Action,[string]$Expected){
    try{& $Action}catch{if($_.Exception.Message -notlike ('*'+$Expected+'*')){throw};return}
    throw ('Expected rejection: '+$Expected)
}
$receipt=Join-Path $publish 'release-validation.json'
$receiptBytes=[IO.File]::ReadAllBytes($receipt)
Reject {Read-ReleaseDryRun $publish '1.9.2' ('2'*40)} 'source/version changed'
Reject {Read-ReleaseDryRun $publish '1.9.3' $head} 'source/version changed'
foreach($name in @('KillerPDF.exe','KillerPDF-Portable.exe','KillerPDF-Store.exe','KillerPDF-1.9.2-src.zip','SHA256SUMS.txt','release-validation/engine.trx','release-validation/desktop.trx')){
    $file=Join-Path $publish $name
    $bytes=[IO.File]::ReadAllBytes($file)
    try{
        [IO.File]::AppendAllText($file,'changed')
        Reject {Read-ReleaseDryRun $publish '1.9.2' $head} 'Dry-run file changed'
    }finally{[IO.File]::WriteAllBytes($file,$bytes)}
}
Write-Output 'PASS changed commit/version, three packages, source archive, checksums and both test receipts are rejected.'

Save-ReleaseDryRun $publish '1.9.2' $head $false
Reject {Read-ReleaseDryRun $publish '1.9.2' $head} 'unsigned dry run'
[IO.File]::WriteAllBytes($receipt,$receiptBytes)
$desktop=Join-Path $publish 'release-validation/desktop.trx'
$original=[IO.File]::ReadAllText($desktop)
foreach($bad in @(
    $original.Replace('outcome="Passed"','outcome="Failed"'),
    $original.Replace('total="1"','total="0"'),
    $original.Replace('notExecuted="0"','notExecuted="1"'),
    $original.Replace('KillerPDF.Tests.dll','Other.Tests.dll')
)){
    [IO.File]::WriteAllText($desktop,$bad)
    Reject {Save-ReleaseDryRun $publish '1.9.2' $head $true} 'complete, nonempty passing'
}
[IO.File]::WriteAllText($desktop,$original)
Reset-ReleaseDryRun $publish
if([IO.File]::Exists((Join-Path $publish 'release-validation/engine.trx')) -or [IO.File]::Exists($desktop)){
    throw 'A new run retained old test results.'
}
Reject {Read-ReleaseDryRun $publish '1.9.2' $head} 'did not complete'
$before=[IO.File]::ReadAllBytes($receipt)
[IO.File]::Delete($receipt)
Reject {Read-ReleaseDryRun $publish '1.9.2' $head} 'No reusable dry-run record'
[IO.File]::WriteAllBytes($receipt,$before)
Write-Output 'PASS unsigned, failing, zero-count, skipped, wrong-suite, interrupted and missing validation are rejected.'
