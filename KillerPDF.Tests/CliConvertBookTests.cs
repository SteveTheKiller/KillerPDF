using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Runs the selected fresh app with convert-book, which turns an EPUB or DjVu book into a PDF through the same
/// conversion Convert to PDF uses. The command never opens a window; these tests read its exit code and output.
/// </summary>
public sealed class CliConvertBookTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "killerpdf-cli-book-" + Guid.NewGuid().ToString("N"));

    public CliConvertBookTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void AnEpubConvertsToAPdfThatOpensWithItsPages()
    {
        string book = Write("book.epub", NativeDiagnosticsTests.Book("<p>One</p>", "<p>Two</p>"));
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run("--convert-book", book, pdf);

        Assert.True(exit == 0, output);
        Assert.Contains(pdf, output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, PageCount(pdf));
        // A4 by default, as in the Convert to PDF dialog.
        Assert.Equal((595.28, 841.89), PageSize(pdf));
    }

    [Fact]
    public void AnEpubChapterLeftOutIsNamedAfterTheLineNamingTheOutput()
    {
        // The second chapter uses scripting, which the reader refuses; it converts as its unavailable-chapter page.
        string book = Write("book.epub", NativeDiagnosticsTests.Book("<p>A</p>", "<p onclick='go()'>A</p>"));
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run("--convert-book", book, pdf);

        Assert.True(exit == 0, output);
        string[] lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(pdf, lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Warning: Chapter 2, EPUB/c2.xhtml: ", Assert.Single(lines[1..]), StringComparison.Ordinal);
        Assert.Equal(2, PageCount(pdf));
    }

    [Fact]
    public void ADjvuBookConvertsToAPdf()
    {
        string book = Write("book.djvu", NativeWorkingDocumentTests.DjvuPage());
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run("--convert-book", book, pdf);

        Assert.True(exit == 0, output);
        Assert.Contains(pdf, output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void ADjvuPageWhoseTextWasLeftOutIsNamedAsConvertToPdfNamesIt()
    {
        string book = Write("book.djv", NativeWorkingDocumentTests.DjvuPageWithText("AB"u8.ToArray(), zoned: false));
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run("--convert-book", book, pdf);

        Assert.True(exit == 0, output);
        Assert.Contains("Warning: The text of pages 1 could not be kept", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExistingOutputIsRefusedAndLeftAloneWithoutOverwrite()
    {
        string book = Write("book.djvu", NativeWorkingDocumentTests.DjvuPage());
        string pdf = Write("book.pdf", "older"u8.ToArray());

        var (exit, output) = Run("--convert-book", book, pdf);

        Assert.Equal(2, exit);
        Assert.Contains("already exists", output, StringComparison.Ordinal);
        Assert.Equal("older", File.ReadAllText(pdf));
    }

    [Fact]
    public void OverwriteReplacesAnExistingOutput()
    {
        string book = Write("book.epub", NativeDiagnosticsTests.Book("<p>A</p>"));
        string pdf = Write("book.pdf", "older"u8.ToArray());

        var (exit, output) = Run("--convert-book", book, pdf, "--overwrite");

        Assert.True(exit == 0, output);
        Assert.Equal(1, PageCount(pdf));
    }

    [Fact]
    public void TheBookItselfCannotBeTheOutput()
    {
        string book = Write("book.epub", NativeDiagnosticsTests.Book("<p>A</p>"));
        byte[] before = File.ReadAllBytes(book);

        var (exit, output) = Run("--convert-book", book, Path.Combine(folder, "BOOK.EPUB"), "--overwrite");

        Assert.Equal(2, exit);
        Assert.Contains("cannot replace the book", output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(book));
    }

    [Fact]
    public void AnOutputThatIsNotAPdfNameIsRefused()
    {
        string book = Write("book.djvu", NativeWorkingDocumentTests.DjvuPage());
        string text = Path.Combine(folder, "book.txt");

        var (exit, output) = Run("--convert-book", book, text);

        Assert.Equal(2, exit);
        Assert.Contains("must be saved with a .pdf name", output, StringComparison.Ordinal);
        Assert.False(File.Exists(text));
    }

    [Fact]
    public void APdfInputIsNotABook()
    {
        string pdf = Path.Combine(folder, "in.pdf");
        Assert.Equal(0, Run("--convert-book", Write("book.djvu", NativeWorkingDocumentTests.DjvuPage()), pdf).ExitCode);

        var (exit, output) = Run("--convert-book", pdf, Path.Combine(folder, "out.pdf"));

        Assert.Equal(2, exit);
        Assert.Contains("must be an .epub, .djvu or .djv book", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--paper", "letter")]
    [InlineData("--margin", "10")]
    [InlineData("--width", "100")]
    public void PaperOptionsWithADjvuBookAreBadUsage(string option, string value)
    {
        string book = Write("book.djvu", NativeWorkingDocumentTests.DjvuPage());
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run("--convert-book", book, pdf, option, value);

        Assert.Equal(2, exit);
        Assert.Contains(option + " applies only to EPUB books", output, StringComparison.Ordinal);
        Assert.False(File.Exists(pdf));
    }

    [Theory]
    [InlineData("go together", "--width", "100")]
    [InlineData("go together", "--height", "100")]
    [InlineData("--paper must be a4, letter or legal", "--paper", "a5")]
    [InlineData("not both", "--paper", "a4", "--width", "100", "--height", "150")]
    [InlineData("numbers of millimeters", "--width", "wide", "--height", "150")]
    [InlineData("--margin must be", "--margin", "thin")]
    // The layout and a PDF reader refuse these, exactly as the dialog does: a page too large to lay out, and a
    // margin that leaves no room for the text.
    [InlineData("Enter a valid paper size", "--width", "2000", "--height", "2000")]
    [InlineData("Enter a valid paper size", "--width", "25", "--height", "25", "--margin", "11")]
    public void AnUnusablePaperForAnEpubIsBadUsage(string message, params string[] paper)
    {
        string book = Write("book.epub", NativeDiagnosticsTests.Book("<p>A</p>"));
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run(["--convert-book", book, pdf, .. paper]);

        Assert.Equal(2, exit);
        Assert.Contains(message, output, StringComparison.Ordinal);
        Assert.False(File.Exists(pdf));
    }

    [Theory]
    [InlineData(612, 792, "--paper", "letter")]
    [InlineData(612, 1008, "--paper", "LEGAL")]
    [InlineData(283.46, 425.2, "--width", "100", "--height", "150", "--margin", "10")]
    [InlineData(70.87, 70.87, "--width", "25", "--height", "25", "--margin", "10")]
    public void AnEpubConvertsOnTheChosenPaper(double width, double height, params string[] paper)
    {
        string book = Write("book.epub", NativeDiagnosticsTests.Book("<p>A</p>"));
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run(["--convert-book", book, pdf, .. paper]);

        Assert.True(exit == 0, output);
        Assert.Equal((width, height), PageSize(pdf));
    }

    [Fact]
    public void JsonDescribesTheConversion()
    {
        string book = Write("book.epub", NativeDiagnosticsTests.Book("<p>A</p>", "<p onclick='go()'>A</p>"));
        string pdf = Path.Combine(folder, "book.pdf");

        var (exit, output) = Run("--convert-book", book, pdf, "--json", "--paper", "letter");

        Assert.True(exit == 0, output);
        using var json = System.Text.Json.JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal(book, root.GetProperty("input").GetString(), ignoreCase: true);
        Assert.Equal(pdf, root.GetProperty("output").GetString(), ignoreCase: true);
        Assert.Equal("epub", root.GetProperty("format").GetString());
        Assert.Equal(2, root.GetProperty("pageCount").GetInt32());
        Assert.Equal(215.9, root.GetProperty("paper").GetProperty("widthMm").GetDouble());
        Assert.Equal(279.4, root.GetProperty("paper").GetProperty("heightMm").GetDouble());
        Assert.Equal(15, root.GetProperty("paper").GetProperty("marginMm").GetDouble());
        Assert.StartsWith("Chapter 2, EPUB/c2.xhtml: ",
            Assert.Single(root.GetProperty("notices").EnumerateArray()).GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonForADjvuBookHasNoPaper()
    {
        string book = Write("book.djvu", NativeWorkingDocumentTests.DjvuPage());

        var (exit, output) = Run("--convert-book", book, Path.Combine(folder, "book.pdf"), "--json");

        Assert.True(exit == 0, output);
        using var json = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal("djvu", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("paper").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("notices").EnumerateArray());
    }

    private static (double Width, double Height) PageSize(string pdf)
    {
        var page = KillerPdf.Engine.Documents.PdfPageInformation
            .Read(KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(pdf)))[0];
        return (Math.Round(page.Width, 2), Math.Round(page.Height, 2));
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static int PageCount(string pdf) => KillerPdf.Engine.Documents.PdfDocumentInformation
        .Read(KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(pdf))).PageCount;

    private (int ExitCode, string Output) Run(params string[] arguments)
    {
        string exe = Environment.GetEnvironmentVariable("KILLERPDF_CLI_TEST_EXE")
            ?? Path.Combine(RepositoryRoot(), "bin", "Release", "net10.0-windows", "KillerPDF.exe");
        Assert.True(File.Exists(exe), "Build KillerPDF.csproj in Release first: " + exe);
        string expectedVersion = System.Xml.Linq.XDocument.Load(Path.Combine(RepositoryRoot(), "KillerPDF.csproj"))
            .Descendants("FileVersion").Single().Value;
        string? actualVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        Assert.True(actualVersion == expectedVersion,
            $"CLI tests need version {expectedVersion}, but {exe} is {actualVersion}. Build the current app or set KILLERPDF_CLI_TEST_EXE to its fresh payload EXE.");
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The command did not finish.");
        }
        return (process.ExitCode, output.Result + error.Result);
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }
}
