using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// --redact checks the finished file for every word it found, after removing hidden information,
/// so a word repeated in a bookmark, a destination name or a script can't ship.
/// </summary>
public sealed class CliRedactProofTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "killerpdf-cli-redact-" + Guid.NewGuid().ToString("N"));

    private string lastConsole = string.Empty;

    public CliRedactProofTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ByDefaultTheWordIsGoneFromBookmarksDestinationsAndScriptsToo()
    {
        string source = Write("hidden.pdf", HiddenCopies("SECRET plan"));
        string output = Path.Combine(folder, "out.pdf");

        Assert.True(Run("--redact", source, output, "--text", "SECRET") == 0, lastConsole);

        Assert.True(PdfContentRedaction.FindSurvivingText(File.ReadAllBytes(output), ["SECRET"]).Succeeded);
    }

    [Fact]
    public void WithoutTheCleanupAWordLeftInHiddenPlacesFailsAndSavesNothing()
    {
        string source = Write("hidden.pdf", HiddenCopies("SECRET plan"));
        string output = Path.Combine(folder, "out.pdf");

        Assert.True(Run("--redact", source, output, "--text", "SECRET", "--no-sanitize") == 1, lastConsole);

        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public void SanitizeAndNoSanitizeTogetherAreRefused()
    {
        string source = Write("hidden.pdf", HiddenCopies("SECRET plan"));
        Assert.Equal(2, Run("--redact", source, Path.Combine(folder, "out.pdf"), "--text", "SECRET",
            "--sanitize", "all", "--no-sanitize"));
    }

    [Fact]
    public void AShortWordInsideALongerOneIsRedactedThereTooAndPasses()
    {
        string source = Write("plain.pdf", PlainPage("Ann wrote the Annual report"));
        string output = Path.Combine(folder, "out.pdf");

        Assert.True(Run("--redact", source, output, "--text", "Ann") == 0, lastConsole);
    }

    [Fact]
    public void MatchCaseLeavesADifferentlyCasedWordAndStillPasses()
    {
        string source = Write("plain.pdf", PlainPage("Secret meets SECRET"));
        string output = Path.Combine(folder, "out.pdf");

        Assert.True(Run("--redact", source, output, "--text", "Secret", "--match-case") == 0, lastConsole);
    }

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] PlainPage(string text) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        Stream($"BT /F1 12 Tf 10 100 Td ({text}) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    // The word is on the page and repeated in a bookmark title, a destination name and a script.
    private static byte[] HiddenCopies(string text) => Pdf(
        "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R /Names << /Dests << /Names [(SECRET-clause) [3 0 R /Fit]] >> /JavaScript << /Names [(a) 8 0 R] >> >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        Stream($"BT /F1 12 Tf 10 100 Td ({text}) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
        "<< /Title (The SECRET plan) /Parent 6 0 R /Dest (SECRET-clause) >>",
        "<< /S /JavaScript /JS (app.alert('SECRET')) >>");

    private static byte[] Pdf(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        foreach (string value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    private int Run(params string[] arguments)
    {
        string exe = Environment.GetEnvironmentVariable("KILLERPDF_CLI_TEST_EXE")
            ?? Path.Combine(RepositoryRoot(), "bin", "Release", "net10.0-windows", "KillerPDF.exe");
        Assert.True(File.Exists(exe), "Build KillerPDF.csproj in Release first: " + exe);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = folder
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        string console = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The command did not finish.");
        }
        lastConsole = console;
        return process.ExitCode;
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }
}
