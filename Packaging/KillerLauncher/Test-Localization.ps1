param([string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
[xml]$document = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Strings.xml') -Raw -Encoding UTF8
$tables = @{}
foreach ($locale in $document.translations.locale) {
    if ($tables.ContainsKey($locale.name)) { throw "Duplicate locale: $($locale.name)" }
    $table = @{}
    foreach ($entry in $locale.string) {
        if ($table.ContainsKey($entry.key)) { throw "Duplicate key: $($locale.name)/$($entry.key)" }
        if ([string]::IsNullOrWhiteSpace($entry.InnerText)) { throw "Empty value: $($locale.name)/$($entry.key)" }
        $table[$entry.key] = $entry.InnerText
    }
    $tables[$locale.name] = $table
}
$expected = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '../../Strings') -Filter '*.xaml' | ForEach-Object { $_.BaseName })
if (@(Compare-Object @($tables.Keys | Sort-Object) @($expected | Sort-Object)).Count) { throw 'Installer languages differ from app languages.' }
$english = $tables['en-US']
foreach ($locale in $tables.Keys) {
    $table = $tables[$locale]
    if (@(Compare-Object @($english.Keys | Sort-Object) @($table.Keys | Sort-Object)).Count) { throw "Key mismatch: $locale" }
    foreach ($key in $english.Keys) {
        $sourceSlots = @([regex]::Matches($english[$key], '\{[0-9]+(?:[^}]*)\}') | ForEach-Object { $_.Value } | Sort-Object)
        $targetSlots = @([regex]::Matches($table[$key], '\{[0-9]+(?:[^}]*)\}') | ForEach-Object { $_.Value } | Sort-Object)
        if (($sourceSlots -join '|') -cne ($targetSlots -join '|')) { throw "Placeholder mismatch: $locale/$key" }
        [void][string]::Format($table[$key], [object[]]@('sample'))
        if ($table[$key].IndexOf([char]0x2013) -ge 0 -or $table[$key].IndexOf([char]0x2014) -ge 0) { throw "Prohibited dash: $locale/$key" }
    }
}
[xml]$xaml = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'InstallerWizard.xaml') -Raw -Encoding UTF8
$allowedDisplayLiterals = @('Killer', 'PDF', 'Steve the Killer', ([string][char]0x00A9 + ' 2026 '))
foreach ($node in $xaml.SelectNodes('//*')) {
    foreach ($attribute in $node.Attributes) {
        if ($attribute.LocalName -notin @('Text', 'Content', 'Title', 'ToolTip')) { continue }
        $value = $attribute.Value
        if ($value -match '^\{local:Loc ([A-Za-z]+)\}$') {
            if (!$english.ContainsKey($Matches[1])) { throw "Unknown XAML key: $value" }
        } elseif ($value -notin $allowedDisplayLiterals -and $value -notmatch '^\{') {
            throw "Unlocalized XAML value: $value"
        }
    }
}
$used = @{}
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs') {
    $source = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($source, 'LauncherStrings\.(?:Get|Format)\("([^"]+)"')) {
        $key = $match.Groups[1].Value
        if (!$english.ContainsKey($key)) { throw "Unknown source key: $key" }
        $used[$key] = $true
    }
    foreach ($line in $source -split '\r?\n') {
        if ($line.TrimStart().StartsWith('//')) { continue }
        # CLI diagnostics retain their existing text. OS exception details are not authored here.
        if ($line.Contains('Console.Error.WriteLine')) { continue }
        if ($line -match '(?:\.Text|\.Content|Description)\s*=\s*"[A-Za-z]' -or
            $line -match '(?:ShowNotice|MessageBox\.Show|new [A-Za-z]+Exception)\(\s*"[A-Za-z]') {
            throw "Unlocalized UI assignment in $($file.Name): $($line.Trim())"
        }
        foreach ($literal in [regex]::Matches($line, '"(?:\\.|[^"\\])*"')) {
            if ($literal.Value -match '[A-Za-z]{2,} [A-Za-z]{2,}') { throw "Review source literal in $($file.Name): $($literal.Value)" }
        }
    }
}
foreach ($match in [regex]::Matches($xaml.OuterXml, '\{local:Loc ([A-Za-z]+)\}')) { $used[$match.Groups[1].Value] = $true }
foreach ($key in $english.Keys) { if (!$used.ContainsKey($key)) { throw "Unused installer key: $key" } }
if ($AssemblyPath) {
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
    $type = $assembly.GetType('KillerLauncher.LauncherStrings', $true)
    $flags = [Reflection.BindingFlags]'NonPublic,Static'
    $resolve = $type.GetMethod('ResolveLocale', $flags)
    $get = $type.GetMethod('Get', $flags)
    $original = [Threading.Thread]::CurrentThread.CurrentUICulture
    try {
        foreach ($locale in $tables.Keys) {
            [Threading.Thread]::CurrentThread.CurrentUICulture = [Globalization.CultureInfo]::GetCultureInfo($locale)
            foreach ($key in $english.Keys) {
                if ($get.Invoke($null, @($key)) -cne $tables[$locale][$key]) { throw "Embedded value mismatch: $locale/$key" }
            }
        }
        $variants = @{ 'en-GB'='en-US'; 'de-AT'='de-DE'; 'fr-CA'='fr-FR'; 'es-MX'='es'; 'bn-BD'='bn'; 'zh-HK'='zh-TW'; 'zh-Hant'='zh-TW'; 'zh-SG'='zh-CN'; 'zh-Hans'='zh-CN'; 'pt-BR'='en-US' }
        foreach ($culture in $variants.Keys) {
            if ($resolve.Invoke($null, @([Globalization.CultureInfo]::GetCultureInfo($culture))) -cne $variants[$culture]) { throw "Culture fallback mismatch: $culture" }
        }
    } finally { [Threading.Thread]::CurrentThread.CurrentUICulture = $original }
}
Write-Output ("Installer localization passed: {0} languages, {1} keys, matching placeholders and no unlocalized UI literals." -f $tables.Count, $english.Count)
