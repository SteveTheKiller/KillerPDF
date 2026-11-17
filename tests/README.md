# Standalone desktop font resolver regression

`InstalledPdfFontResolverRegression.cs` compiles the production resolver with a
deterministic installed-font catalog substitute. Its checks cover requested
font precedence, Helvetica, Times, and Courier aliases and styles, emoji fallback, unrelated
fonts, unavailable fonts, and caching.
It needs .NET 10 and no installed fonts or additional packages. It is separate
from the app test suite and is excluded from the application build.

First build the engine in Release. In a temporary folder outside the checkout,
save this as `Check.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="KillerPdf.Engine">
      <HintPath>$(RepositoryRoot)/engine/KillerPdf.Engine/bin/Release/net10.0/KillerPdf.Engine.dll</HintPath>
    </Reference>
    <Compile Include="$(RepositoryRoot)/Services/InstalledPdfFontResolver.cs" />
    <Compile Include="$(RepositoryRoot)/Services/PdfFontStyle.cs" />
    <Compile Include="$(RepositoryRoot)/tests/InstalledPdfFontResolverRegression.cs" />
  </ItemGroup>
</Project>
```

Supply the absolute checkout path when running it from that temporary folder:

```powershell
dotnet run --project Check.csproj -c Release -p:RepositoryRoot="C:\path\to\KillerPDF"
```

Every check must print `PASS`, and the process must exit with code zero.

## Crash report dialog

After a normal Debug build, use PowerShell 7 on .NET 10 with STA enabled:

```powershell
pwsh -NoProfile -Sta -CommandWithArgs '& "build/Test-CrashDialog.ps1" -AssemblyPath "bin/Debug/net10.0-windows/KillerPDF.dll" -EvidenceDirectory "C:/Users/steve/scratch/kp-bench-render/crash-report-check"' --startup-smoke
```

Use a fresh evidence directory. The literal process argument `--startup-smoke`
is required to keep preferences isolated. The check measures real controls in
all 14 themes with grain on and off, two widths and fatal/recoverable states.
It asserts caption fit, family rest and hover colors, clipped cards, scrolling,
and Continue behavior. PNGs are captured before closing the tested window.
These are synthetic offscreen previews, not physical input or screen-reader QA.
