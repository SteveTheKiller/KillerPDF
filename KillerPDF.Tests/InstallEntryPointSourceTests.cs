using System.IO;
using System.Text.RegularExpressions;
using KillerLauncher;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Source-shape tests for installer entry points that need an elevated or windowed run to execute:
/// the launcher's failure report for the in-app machine update, and the silent install guard.
/// </summary>
public sealed class InstallEntryPointSourceTests
{
    [Fact]
    public void OnlyAnUpdateHandoffIsTreatedAsInteractiveWhenSilent()
    {
        Assert.True(UpdateRelaunchArguments.Requested(["/silent", "/install-dir64:C:\\Program Files\\KillerPDF", "/relaunch"]));
        Assert.False(UpdateRelaunchArguments.Requested(["/silent"]));
    }

    [Fact]
    public void ALauncherFailureDuringTheInAppUpdateIsShownAndBringsTheOldCopyBack()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Packaging", "KillerLauncher", "Program.cs"));
        int catchBlock = source.IndexOf("catch (Exception ex)", source.IndexOf("private static int Main", StringComparison.Ordinal), StringComparison.Ordinal);
        int endIf = source.IndexOf("#else", catchBlock, StringComparison.Ordinal);
        Assert.True(catchBlock > 0 && endIf > catchBlock);
        string installerCatch = source[catchBlock..endIf];

        int quiet = installerCatch.IndexOf("Console.Error.WriteLine(ProductName + \" setup failed", StringComparison.Ordinal);
        int guard = installerCatch.LastIndexOf("UpdateRelaunchArguments.Requested(args)", quiet, StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < quiet, "The console-only branch must exclude the in-app update handoff.");
        Assert.Matches(new Regex(@"!updateHandoff\s*&&\s*args\.Any\([^;]*""/silent"""), installerCatch);
        Assert.Contains("InstallerWizard.ShowFailure(", installerCatch, StringComparison.Ordinal);
        Assert.Contains("RelaunchAfterInstall(0, args", installerCatch, StringComparison.Ordinal);
    }

    [Fact]
    public void SilentInstallRefusesAnExeThatIsNotSelfContainedBeforeCopyingAnything()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "App.xaml.cs"));
        int method = source.IndexOf("private static int DoSilentInstall()", StringComparison.Ordinal);
        Assert.True(method >= 0);
        string body = source[method..];
        int guard = body.IndexOf("CurrentExeIsSelfContained()", StringComparison.Ordinal);
        int copy = body.IndexOf("File.Copy(", StringComparison.Ordinal);
        Assert.True(guard > 0 && guard < copy, "The self-contained check must come before the copy.");
        Assert.Contains("InstallerExitCodes.Failure", body[guard..copy], StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppWiresTheWatermarkMacroToItsSessionTempFolder()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "App.xaml.cs"));
        Assert.Contains("PdfEngineBurn.MacroTempFile = MakeTempFile;", source, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }
}
