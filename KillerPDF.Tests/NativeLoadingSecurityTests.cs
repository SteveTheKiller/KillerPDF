using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeLoadingSecurityTests
{
    [Fact]
    public void NativeLibrariesUseRestrictedSearchPathsAndVerifiedPayloads()
    {
        string root = FindRepositoryRoot();
        string launcher = File.ReadAllText(
            Path.Combine(root, "Packaging", "KillerLauncher", "Program.cs"));
        string ocr = File.ReadAllText(
            Path.Combine(root, "Services", "OcrNativeBootstrap.cs"));

        Assert.Contains("DefaultDllImportSearchPaths(DllImportSearchPath.System32)", launcher,
            StringComparison.Ordinal);
        Assert.Contains("LoadLibraryExW", ocr, StringComparison.Ordinal);
        Assert.Contains("LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32", ocr,
            StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", ocr, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDllDirectory", ocr, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.GetFiles(nativeDir", ocr, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherSearchesOnlySystem32ForLaterDllLoads()
    {
        // The setup exe usually runs from Downloads. With APPLICATION_DIR in the default search,
        // a planted non-KnownDLL next to it wins every later delay-load or LoadLibrary call.
        string launcher = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Packaging", "KillerLauncher", "Program.cs"));

        Assert.Contains("SetDefaultDllDirectories(LoadLibrarySearchSystem32);", launcher,
            StringComparison.Ordinal);
        Assert.DoesNotContain("LoadLibrarySearchApplicationDir", launcher, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
