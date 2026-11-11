using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// The Color Management dialog is not linked into this project, so these pin how it is set up. Its
// behavior is exercised by the window steps on a UI VM.
public sealed class ColorManagementDialogSourceTests
{
    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Controls", "ColorManagementDialog.cs"));
    }

    // APP-BF-010: Enter did nothing because Apply was not the default button.
    [Fact]
    public void ApplyIsTheDefaultButton()
    {
        Assert.Contains("apply.IsDefault = true;", Source(), StringComparison.Ordinal);
    }

    // color BF-009: a profile is read and parsed to resolve it, which a large file makes slow.
    [Fact]
    public void ProfileResolutionRunsOffTheUiThread()
    {
        string source = Source();
        int at = 0, found = 0;
        while ((at = source.IndexOf("library.Resolve(", at, StringComparison.Ordinal)) >= 0)
        {
            string before = source[Math.Max(0, at - 700)..at];
            Assert.True(before.Contains("Task.Run(", StringComparison.Ordinal),
                "library.Resolve is called outside Task.Run near offset " + at);
            found++;
            at++;
        }
        Assert.True(found >= 2);
    }
}
