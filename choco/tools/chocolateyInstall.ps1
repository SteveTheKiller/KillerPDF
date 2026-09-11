$ErrorActionPreference = 'Stop'
$version   = $env:ChocolateyPackageVersion

$packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileType       = 'exe'
    silentArgs     = '/silent'
    url64bit       = "https://github.com/Rafie-kun/KillerPDF/releases/download/v$version/KillerPDF.exe"
    checksum64     = 'REPLACE_HASH'
    checksumType64 = 'sha256'
    validExitCodes = @(0)
}

Install-ChocolateyPackage @packageArgs
