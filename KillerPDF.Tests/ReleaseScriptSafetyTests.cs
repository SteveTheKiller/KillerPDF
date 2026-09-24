using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReleaseScriptSafetyTests
{
    [Fact]
    public void ReleaseScriptRejectsUnverifiedOrStaleArtifacts()
    {
        string root = FindRepositoryRoot();
        string script = File.ReadAllText(Path.Combine(root, "release.ps1"));

        Assert.Contains("$SkipSign -and -not $DryRun", script, StringComparison.Ordinal);
        Assert.Contains("dotnet list $proj package --vulnerable --include-transitive", script,
            StringComparison.Ordinal);
        Assert.Contains("Assert-ReleaseArtifactVersion", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("KILLERPDF_TEST_INSTALL_ROOT", script, StringComparison.Ordinal);
        Assert.Contains("Installed application startup smoke test failed", script,
            StringComparison.Ordinal);
        Assert.Contains("KillerPDF-$Version-src.zip", script, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS.txt does not match", script, StringComparison.Ordinal);
        Assert.Contains("DryRun: skipping WinGet fork synchronization", script,
            StringComparison.Ordinal);
        Assert.DoesNotMatch("[^\\x00-\\x7F]", script);
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
