using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A print started from Windows (right-click Print, Start-Process -Verb Print, a mail app) has no console, so the
// headless print's reason for failing was thrown away and nothing printed with no word to the user. Started from the
// shell, a failed print says so in a message naming the file, with the print's own reason under it.
public sealed class ShellPrintFailureTests
{
    [Fact]
    public void TheMessageNamesTheFileAndGivesTheReason()
    {
        string message = ShellPrintFailure.Message(@"C:\Docs\Quarterly report.pdf",
            "Printer not found: Office.\r\n", "KillerPDF could not print {0}.");

        Assert.Equal("KillerPDF could not print Quarterly report.pdf.\n\nPrinter not found: Office.", message);
    }

    [Fact]
    public void WithoutAReasonTheMessageStillNamesTheFile()
    {
        string message = ShellPrintFailure.Message(@"C:\Docs\a.pdf", "   ", "KillerPDF could not print {0}.");

        Assert.Equal("KillerPDF could not print a.pdf.", message);
    }

    [Fact]
    public void WithoutATranslationTheEnglishWordingIsUsed()
    {
        string message = ShellPrintFailure.Message(@"C:\Docs\a.pdf", "Input not found", null);

        Assert.StartsWith("KillerPDF could not print a.pdf.", message);
    }

    // CliRunner is not compiled into this project, so its wiring is read from source: a print started with --shell
    // writes its output to a buffer instead of the missing console and shows that buffer when the print fails,
    // after every catch has run.
    [Fact]
    public void AShellPrintShowsItsFailureInsteadOfDroppingIt()
    {
        string root = FindRepositoryRoot();
        string cli = File.ReadAllText(Path.Combine(root, "Features", "Cli", "CliRunner.cs"));
        int start = cli.IndexOf("internal static bool TryRunCli(", System.StringComparison.Ordinal);
        int end = cli.IndexOf("private static bool Eq(", start, System.StringComparison.Ordinal);
        string body = cli[start..end];
        Assert.Contains("\"--shell\"", body, System.StringComparison.Ordinal);
        int finallyAt = body.LastIndexOf("finally", System.StringComparison.Ordinal);
        int show = body.IndexOf("ShellPrintFailure.Show(", System.StringComparison.Ordinal);
        Assert.True(finallyAt > 0 && show > finallyAt, "The failure is not shown after the print and its catches.");
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
