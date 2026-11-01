using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Runs the built KillerPDF.exe with command-line verbs. These verbs never open a window; the
/// tests read only the process exit code, because the console is attached to a parent console.
/// </summary>
public sealed class CliExitCodeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "killerpdf-cli-exit-" + Guid.NewGuid().ToString("N"));

    public CliExitCodeTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SearchTextOnADocumentThatCannotBeReadFailsInsteadOfReportingNoHits()
    {
        string garbage = Path.Combine(folder, "garbage.pdf");
        File.WriteAllBytes(garbage, Encoding.ASCII.GetBytes("not a pdf"));
        Assert.Equal(1, Run("--search-text", garbage, "x"));
    }

    [Fact]
    public void BatchResaveRefusesAnOutputFolderThatIsTheInputFolder()
    {
        string corpus = Path.Combine(folder, "corpus");
        Directory.CreateDirectory(corpus);
        string pdf = Path.Combine(corpus, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        byte[] before = File.ReadAllBytes(pdf);
        Assert.Equal(2, Run("--batch-resave", corpus, corpus));
        Assert.Equal(2, Run("--batch-resave", corpus, corpus + Path.DirectorySeparatorChar));
        Assert.Equal(before, File.ReadAllBytes(pdf));
    }

    [Fact]
    public void BatchResaveRefusesAnOutputFileThatIsTheInputFile()
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        byte[] before = File.ReadAllBytes(pdf);
        Assert.Equal(2, Run("--batch-resave", pdf, pdf));
        Assert.Equal(before, File.ReadAllBytes(pdf));
    }

    [Fact]
    public void BatchResaveToASiblingFolderStillSucceeds()
    {
        string corpus = Path.Combine(folder, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllBytes(Path.Combine(corpus, "one.pdf"), BlankPdf());
        string result = Path.Combine(folder, "result");
        Assert.Equal(0, Run("--batch-resave", corpus, result));
        Assert.True(File.Exists(Path.Combine(result, "one.pdf")));
    }

    [Theory]
    [InlineData("BatchRunner.cs", "Batch mode failed: ")]
    [InlineData("BatchRenderRunner.cs", "Render batch failed: ")]
    public void ABatchRunThatFailsOutsideItsFileLoopIsAnOperationFailureNotBadUsage(string file, string message)
    {
        // The per-file failures are caught inside the loop; this catch is the run itself failing.
        string source = ReadSource("Features", "Cli", file);
        int start = source.IndexOf(message, StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Matches(@"exitCode = 1;", source[start..Math.Min(source.Length, start + 200)]);
    }

    [Theory]
    [InlineData("--color-mode", "purple")]
    [InlineData("--jpeg-quality", "500")]
    [InlineData("--compression", "zip")]
    public void FlattenWithAnUnusableOptionValueIsBadUsage(string option, string value)
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(2, Run("--flatten", pdf, Path.Combine(folder, "flat.pdf"), option, value));
    }

    [Theory]
    [InlineData("5000")]
    [InlineData("abc")]
    [InlineData("7,5")]
    public void ToImageWithAnUnusableDpiIsBadUsage(string dpi)
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(2, Run("--to-image", pdf, Path.Combine(folder, "images"), "--dpi", dpi));
    }

    [Fact]
    public void ToImageAcceptsAFractionalDpiWithAPeriod()
    {
        string pdf = Path.Combine(folder, "one.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(0, Run("--to-image", pdf, Path.Combine(folder, "images"), "--dpi", "72.5"));
    }

    [Fact]
    public void ToImageReadsTheDpiWithTheInvariantCulture()
    {
        string source = ReadSource("Features", "Cli", "CliRunner.cs");
        int start = source.IndexOf("TryReadDpi(", StringComparison.Ordinal);
        Assert.True(start >= 0, "CliRunner.cs must define TryReadDpi.");
        string body = source[start..Math.Min(source.Length, start + 900)];
        Assert.Contains("CultureInfo.InvariantCulture", body, StringComparison.Ordinal);
        Assert.DoesNotContain("double.TryParse(s, out", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoBookmarksHelpDocumentsItsNothingToDoExitCode()
    {
        string source = ReadSource("Features", "Cli", "CliRunner.cs");
        int line = source.IndexOf("killerpdf auto-bookmarks", StringComparison.Ordinal);
        Assert.True(line >= 0);
        Assert.Contains("exit 3 when no headings are found", source[line..Math.Min(source.Length, line + 400)], StringComparison.Ordinal);
        Assert.Contains("Exit codes: 0 success, 1 operation failed, 2 bad usage, 3 findings or nothing to do (where noted).",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void PrintWithAnUnusableDuplexIsBadUsage()
    {
        string pdf = Path.Combine(folder, "print.pdf");
        File.WriteAllBytes(pdf, BlankPdf());
        Assert.Equal(2, Run("--print", pdf, "--duplex", "sideways"));
    }

    private int Run(params string[] arguments)
    {
        string exe = Path.Combine(RepositoryRoot(), "bin", "Release", "net10.0-windows", "KillerPDF.exe");
        Assert.True(File.Exists(exe), "Build KillerPDF.csproj in Release first: " + exe);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = folder
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The command did not finish.");
        }
        return process.ExitCode;
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. parts]));

    private static byte[] BlankPdf()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
