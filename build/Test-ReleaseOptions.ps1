#Requires -Version 5.1
param([Parameter(Mandatory)][string]$EvidenceRoot,[switch]$RequireStore,[switch]$Preview)
# Execute the real release entry point against synthetic packages. Only external command,
# signing API and network boundaries are modeled. No actual publishing command can run.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repository=Split-Path -Parent $PSScriptRoot
if(Test-Path -LiteralPath $EvidenceRoot){throw 'Use a fresh evidence directory.'}
$root=Join-Path $EvidenceRoot 'fixture'
foreach($directory in @('build','store','engine/KillerPdf.Engine','pdf-landing/engine','validation/benchmarks/1.9.1','bin/Release/net10.0-windows/publish/release-validation')){
    [IO.Directory]::CreateDirectory((Join-Path $root $directory))|Out-Null
}
foreach($name in @('release.ps1','KillerPDF.csproj','CHANGELOG.md','README.md','store/KillerPDF-Store-listing.csv','store/Submit-StoreRelease.ps1',
    'engine/KillerPdf.Engine/KillerPdf.Engine.csproj','build/release-validation.ps1','build/payload-security.ps1','build/Update-EngineSiteFacts.ps1','build/Test-GuiStartup.ps1')){
    Copy-Item -LiteralPath (Join-Path $repository $name) -Destination (Join-Path $root $name)
}
Get-ChildItem -LiteralPath (Join-Path $repository 'pdf-landing') -File|Where-Object{$_.Extension -in @('.html','.js')}|Copy-Item -Destination (Join-Path $root 'pdf-landing')
[IO.File]::WriteAllText((Join-Path $root 'engine/Fixture.cs'),'// fixture engine source')
[IO.File]::WriteAllText((Join-Path $root 'validation/benchmarks/1.9.1/benchmark-summary.csv'),"Version,Inputs`n1.9.1,20`n")
[IO.File]::WriteAllText((Join-Path $root 'validation/benchmarks/1.9.1/CORPUS.md'),'| Damaged-file safety | 80 |')
[IO.Directory]::CreateDirectory((Join-Path $root 'Strings'))|Out-Null
Get-ChildItem -LiteralPath (Join-Path $repository 'Strings') -File -Filter '*.xaml'|Copy-Item -Destination (Join-Path $root 'Strings')
$publish=Join-Path $root 'bin/Release/net10.0-windows/publish'
$payload=Join-Path $EvidenceRoot 'payload'
[IO.Directory]::CreateDirectory($payload)|Out-Null
foreach($name in @('KillerPDF.App.exe','KillerPDF.App.dll','KillerPdf.Engine.dll')){[IO.File]::WriteAllBytes((Join-Path $payload $name),[byte[]]@(1,2,3))}
$zip=Join-Path $EvidenceRoot 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip)
$compiler=@(& dotnet --list-sdks|ForEach-Object{if($_ -match '^(?<version>[^ ]+) \[(?<root>.+)\]$'){Join-Path $Matches.root ($Matches.version+'/Roslyn/bincore/csc.dll')}})[-1]
$source=Join-Path $EvidenceRoot 'Package.cs'
[IO.File]::WriteAllText($source,'[assembly:System.Reflection.AssemblyVersion("1.9.2.0")] public class Package {}')
$package=Join-Path $EvidenceRoot 'Package.dll'
& dotnet exec $compiler -nologo -target:library ('-out:'+$package) ('-resource:'+$zip+',KillerLauncher.payload.zip') ('-r:'+ [object].Assembly.Location) $source
if($LASTEXITCODE -ne 0){throw 'Fixture compilation failed.'}
foreach($name in @('KillerPDF.exe','KillerPDF-Portable.exe','KillerPDF-Store.exe')){Copy-Item -LiteralPath $package -Destination (Join-Path $publish $name)}
Copy-Item -LiteralPath $zip -Destination (Join-Path $publish 'KillerPDF-1.9.2-src.zip')
$sums=foreach($name in @('KillerPDF.exe','KillerPDF-Portable.exe','KillerPDF-Store.exe','KillerPDF-1.9.2-src.zip')){
    $name+' '+(Get-FileHash -LiteralPath (Join-Path $publish $name)).Hash
}
[IO.File]::WriteAllLines((Join-Path $publish 'SHA256SUMS.txt'),[string[]]$sums)
foreach($suite in @('engine','desktop')){
    $assembly=if($suite -eq 'engine'){'KillerPdf.Engine.Tests.dll'}else{'KillerPDF.Tests.dll'}
    $trx='<TestRun><TestDefinitions><UnitTest storage="C:/fixture/'+$assembly+'" /></TestDefinitions><Results><UnitTestResult outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" /></ResultSummary></TestRun>'
    [IO.File]::WriteAllText((Join-Path $publish ('release-validation/'+$suite+'.trx')),$trx)
}
. (Join-Path $root 'build/release-validation.ps1')
$head='1111111111111111111111111111111111111111'
Save-ReleaseDryRun -PublishDirectory $publish -Version '1.9.2' -SourceCommit $head -Signed $true
$global:releaseOptionEvents=[Collections.Generic.List[string]]::new()
function global:git {
    $command=$args -join ' '
    $global:LASTEXITCODE=0
    if($command -eq 'ls-remote --heads origin'){return "1111111111111111111111111111111111111111`trefs/heads/main"}
    if($command -like 'symbolic-ref *'){return 'refs/remotes/origin/main'}
    if($command -eq 'rev-parse --abbrev-ref HEAD'){return 'main'}
    if($command -like 'rev-parse *'){return '1111111111111111111111111111111111111111'}
    if($command -match 'ls-files'){return 'engine/Fixture.cs'}
    if($command -like 'status *' -or $command -like 'fetch *' -or $command -like 'ls-remote --tags *' -or $command -like 'tag --list *'){return}
    if($args[0] -in @('add','commit','push','tag')){$global:releaseOptionEvents.Add('git '+$command);return}
    throw 'Unexpected offline git command.'
}
function global:dotnet {
    $global:LASTEXITCODE=0
    if($args[0] -eq 'list'){$global:releaseOptionEvents.Add('audit');return 'No vulnerable packages.'}
    throw 'The reusable release reran a build or test command.'
}
function global:gh {$global:releaseOptionEvents.Add('gh '+($args -join ' '));$global:LASTEXITCODE=0}
function global:Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    $global:releaseOptionEvents.Add('signature '+[IO.Path]::GetFileName($LiteralPath))
    [pscustomobject]@{Status=[Management.Automation.SignatureStatus]::Valid;SignatureType='Authenticode'}
}
function global:Start-Process {throw 'The reusable release reran a startup or installer test.'}
function global:Invoke-WebRequest {$global:releaseOptionEvents.Add('store-network');throw 'Store network boundary reached.'}
function global:Invoke-RestMethod {$global:releaseOptionEvents.Add('store-network');throw 'Store network boundary reached.'}
$options=@{ReuseDryRun=$true;SkipStoreSubmission=(!$RequireStore);DryRun=[bool]$Preview}
$failure=$null
$previousTemp=$env:TEMP
$env:TEMP=$EvidenceRoot
$previousSettings=@{}
foreach($name in @('KILLER_STORE_TENANT_ID','KILLER_STORE_CLIENT_ID','KILLER_STORE_CLIENT_SECRET','KILLER_STORE_SELLER_ID')){
    $previousSettings[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
    [Environment]::SetEnvironmentVariable($name,'offline-fixture','Process')
}
try {& (Join-Path $root 'release.ps1') @options}
catch {$failure=$_}
finally {
    $env:TEMP=$previousTemp
    foreach($name in $previousSettings.Keys){[Environment]::SetEnvironmentVariable($name,$previousSettings[$name],'Process')}
}
$events=@($global:releaseOptionEvents)
[IO.File]::WriteAllLines((Join-Path $EvidenceRoot 'boundary-events.txt'),[string[]]$events)
if($RequireStore){
    if(!$failure -or @($events|Where-Object{$_ -like 'gh *' -or $_ -like 'git push *'}).Count){throw 'Default Store preflight did not stop before simulated publication.'}
    if($failure.Exception.Message -notmatch 'Store (credential|network)'){throw $failure}
    Write-Output 'PASS Store preflight remains enabled by default.'
}elseif($Preview){
    if($failure){throw $failure}
    if(@($events|Where-Object{$_ -like 'gh *' -or $_ -like 'git push *' -or $_ -like 'git tag *' -or $_ -eq 'store-network'}).Count){throw 'DryRun reached a modeled publication or Store boundary.'}
    Write-Output 'PASS reusable DryRun stops before publication and skips the Store.'
}else{
    if($failure){throw $failure}
    if(@($events|Where-Object{$_ -like 'gh release create *'}).Count -ne 1){throw 'Release did not reach the modeled GitHub publication boundary.'}
    if(@($events|Where-Object{$_ -eq 'audit'}).Count -ne 2){throw 'Live dependency audits were not retained.'}
    if(@($events|Where-Object{$_ -like 'signature *'}).Count -ne 12){throw 'Package and embedded signature checks were not retained.'}
    if(@($events|Where-Object{$_ -eq 'store-network'}).Count){throw 'The skipped Store was contacted.'}
    Write-Output 'PASS reusable command skips tests and both Store calls, retaining audits, signatures, checksums and release flow. All publication boundaries were modeled.'
}
