using System.Diagnostics;
using System.IO;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// --attachments writes extracted files with the Internet zone tag and leaves existing files
/// alone unless --overwrite is given.
/// </summary>
public sealed class CliAttachmentExtractTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "killerpdf-cli-attach-" + Guid.NewGuid().ToString("N"));
    private string lastConsole = string.Empty;

    public CliAttachmentExtractTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ExtractedFilesCarryTheInternetZone()
    {
        string source = Source();
        string output = Path.Combine(folder, "out");

        Assert.True(Run("--attachments", source, output) == 0, lastConsole);

        string zone = File.ReadAllText(Path.Combine(output, "notes.txt") + ":Zone.Identifier");
        Assert.Contains("ZoneId=3", zone);
    }

    [Fact]
    public void AnExistingFileStaysUnlessOverwriteIsGiven()
    {
        string source = Source();
        string output = Directory.CreateDirectory(Path.Combine(folder, "out")).FullName;
        string existing = Path.Combine(output, "notes.txt");
        File.WriteAllText(existing, "mine");

        Assert.True(Run("--attachments", source, output) == 1, lastConsole);
        Assert.Equal("mine", File.ReadAllText(existing));

        Assert.True(Run("--attachments", source, output, "--overwrite") == 0, lastConsole);
        Assert.Equal("hello", File.ReadAllText(existing));
    }

    private string Source()
    {
        string path = Path.Combine(folder, "with-file.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("notes.txt", "hello"u8.ToArray()).Build());
        return path;
    }

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
