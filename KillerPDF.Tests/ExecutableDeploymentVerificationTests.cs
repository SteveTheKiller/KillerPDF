using System.IO;
using System.Security;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The elevated silent install checks the copy it is about to publish, not the source path it
/// copied from: a source in Downloads can be renamed and replaced between a check and a copy.
/// </summary>
public sealed class ExecutableDeploymentVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kp-deploy-" + Guid.NewGuid().ToString("N"));

    public ExecutableDeploymentVerificationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void TheStagedCopyBesideTheDestinationIsWhatGetsChecked()
    {
        string source = Path.Combine(_root, "Downloads", "KillerPDF.exe");
        string destination = Path.Combine(_root, "Program Files", "KillerPDF", "KillerPDF.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        string? checkedPath = null;

        ExecutableDeployment.Install(source, destination, new ExecutableDeployment.FileOperations(), staged =>
        {
            checkedPath = staged;
            Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(staged));
        });

        Assert.NotNull(checkedPath);
        Assert.Equal(Path.GetDirectoryName(destination), Path.GetDirectoryName(checkedPath));
        Assert.NotEqual(destination, checkedPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(destination));
    }

    [Fact]
    public void ARefusedStagedCopyLeavesTheInstalledCopyAndNoLeftovers()
    {
        string source = Path.Combine(_root, "new.exe");
        string destination = Path.Combine(_root, "installed", "KillerPDF.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(source, [9, 9]);
        File.WriteAllBytes(destination, [7]);

        Assert.Throws<SecurityException>(() => ExecutableDeployment.Install(source, destination,
            new ExecutableDeployment.FileOperations(), _ => throw new SecurityException("not the publisher")));

        Assert.Equal([7], File.ReadAllBytes(destination));
        Assert.Equal([destination], Directory.GetFiles(Path.GetDirectoryName(destination)!));
    }

    [Fact]
    public void TheElevatedSilentInstallPublishesOnlyAStagedCopyFromThePublisher()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        string app = File.ReadAllText(Path.Combine(directory!.FullName, "App.xaml.cs"));
        int method = app.IndexOf("private static int DoSilentInstall()", StringComparison.Ordinal);
        int end = app.IndexOf("return KillerFamily.Deployment.InstallerExitCodes.Success;", method, StringComparison.Ordinal);
        string body = app[method..end];

        Assert.DoesNotContain("File.Copy(", body, StringComparison.Ordinal);
        int install = body.IndexOf("Services.ExecutableDeployment.Install(src, installExe,", StringComparison.Ordinal);
        Assert.True(install > 0, "The silent install must publish through ExecutableDeployment.");
        Assert.Contains("Services.PublisherChain.Verify(staged)", body[install..], StringComparison.Ordinal);
        Assert.Contains("VerifyAuthenticode(staged)", body[install..], StringComparison.Ordinal);
    }
}
