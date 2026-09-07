# Standalone desktop font resolver regression

`InstalledPdfFontResolverRegression.cs` compiles the production resolver with a
deterministic installed-font catalog substitute. Its checks cover requested
font precedence, Courier PostScript aliases and styles, emoji fallback, unrelated
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
