using System.Diagnostics;
using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// #484: Extract Pages also splits into several files. The app names its files as the command line's
// split does, builds every part from the saved copy with the pending page turns applied once, and
// leaves the folder as it was when a file cannot be written.
public sealed class PageSplitTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-split-" + Guid.NewGuid().ToString("N"));

    public PageSplitTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    // Each page is a different width, so a part's pages can be told apart.
    private string Document(int pages, string name = "report")
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < pages; page++) builder.AddPage(100 + page, 200, ReadOnlyMemory<byte>.Empty);
        string path = Path.Combine(_folder, name + ".pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static IReadOnlyList<PdfPageInformation> Pages(string path) =>
        PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));

    // The real command line, as CliExitCodeTests runs it: KillerPDF.exe split, in no window.
    private string[] CliSplit(string source, string folder, params string[] options)
    {
        string exe = Environment.GetEnvironmentVariable("KILLERPDF_CLI_TEST_EXE")
            ?? Path.Combine(RepositoryRoot(), "bin", "Release", "net10.0-windows", "KillerPDF.exe");
        Assert.True(File.Exists(exe), "Build KillerPDF.csproj in Release first: " + exe);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = _folder };
        foreach (string argument in (string[])["--split", source, folder, .. options]) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The split did not finish.");
        }
        Assert.Equal(0, process.ExitCode);
        return [.. Directory.GetFiles(folder).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KillerPDF.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("KillerPDF.csproj");
        return root;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    [InlineData(1000)]
    public void EveryPageNamesItsFilesAsTheCommandLineSplitDoes(int pageCount)
    {
        string cliFolder = Path.Combine(_folder, "cli");
        string[] cli = CliSplit(Document(pageCount), cliFolder);

        IReadOnlyList<PageSplitPart> parts = PageSplit.Plan(PageSplitMode.EveryPage, pageCount, 1, "report");

        Assert.Equal(cli, parts.Select(part => part.FileName).Order(StringComparer.Ordinal));
        Assert.Equal(pageCount < 1000 ? "report-page-001.pdf" : "report-page-0001.pdf", parts[0].FileName);
        Assert.All(parts, (part, index) => Assert.Equal([index], part.PageIndices));
    }

    [Theory]
    [InlineData(5, 2)]
    [InlineData(25, 2)]
    [InlineData(7, 7)]
    [InlineData(7, 9)]
    public void EveryFewPagesNamesAndGroupsItsFilesAsTheCommandLineEveryOptionDoes(int pageCount, int every)
    {
        string source = Document(pageCount);
        string[] cli = CliSplit(source, Path.Combine(_folder, "cli"), "--every", every.ToString(System.Globalization.CultureInfo.InvariantCulture));

        IReadOnlyList<PageSplitPart> parts = PageSplit.Plan(PageSplitMode.EveryN, pageCount, every, "report");
        PdfSplitPlan engine = PdfDocumentSplitter.Plan(PdfDocument.Open(File.ReadAllBytes(source)),
            new PdfSplitOptions { Mode = PdfSplitMode.PageCount, PagesPerPart = every }, "report");

        Assert.Equal(cli, parts.Select(part => part.FileName).Order(StringComparer.Ordinal));
        Assert.Equal(engine.Parts.Select(part => part.Name + ".pdf"), parts.Select(part => part.FileName));
        Assert.Equal(engine.Parts.Select(part => part.SourcePageIndices.ToArray()), parts.Select(part => part.PageIndices.ToArray()));
    }

    [Fact]
    public void WritingBuildsEachPartWithThePendingTurnsAppliedOnce()
    {
        string source = Document(5);
        string output = Path.Combine(_folder, "out");
        IReadOnlyList<PageSplitPart> parts = PageSplit.Plan(PageSplitMode.EveryN, 5, 2, "report");

        int written = PageSplit.Write(source, output, parts, new Dictionary<int, int> { [1] = 90, [4] = 270 }, overwrite: false);

        Assert.Equal(3, written);
        Assert.Equal(["report-1.pdf", "report-2.pdf", "report-3.pdf"],
            Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        IReadOnlyList<PdfPageInformation> first = Pages(Path.Combine(output, "report-1.pdf"));
        Assert.Equal([100d, 101d], first.Select(page => page.Width));
        Assert.Equal([0, 90], first.Select(page => page.Rotation));
        IReadOnlyList<PdfPageInformation> last = Pages(Path.Combine(output, "report-3.pdf"));
        Assert.Equal([104d], last.Select(page => page.Width));
        Assert.Equal([270], last.Select(page => page.Rotation));
    }

    [Fact]
    public void AnExistingFileIsReportedAndKeptUnlessReplacingWasChosen()
    {
        string source = Document(3);
        string output = Path.Combine(_folder, "out");
        Directory.CreateDirectory(output);
        string taken = Path.Combine(output, "report-page-002.pdf");
        File.WriteAllText(taken, "keep me");
        IReadOnlyList<PageSplitPart> parts = PageSplit.Plan(PageSplitMode.EveryPage, 3, 1, "report");

        Assert.Equal([taken], PageSplit.Existing(output, parts));
        Assert.Throws<IOException>(() => PageSplit.Write(source, output, parts, new Dictionary<int, int>(), overwrite: false));
        Assert.Equal("keep me", File.ReadAllText(taken));
        Assert.Equal([taken], Directory.GetFiles(output));

        Assert.Equal(3, PageSplit.Write(source, output, parts, new Dictionary<int, int>(), overwrite: true));
        Assert.Equal([101d], Pages(taken).Select(page => page.Width));
    }

    [Fact]
    public void AFolderWithAFilesNameStopsTheSplitBeforeAnythingIsReplaced()
    {
        string source = Document(3);
        string output = Path.Combine(_folder, "out");
        Directory.CreateDirectory(Path.Combine(output, "report-page-003.pdf"));
        string first = Path.Combine(output, "report-page-001.pdf");
        File.WriteAllText(first, "keep me");
        IReadOnlyList<PageSplitPart> parts = PageSplit.Plan(PageSplitMode.EveryPage, 3, 1, "report");

        Assert.Throws<IOException>(() => PageSplit.Write(source, output, parts, new Dictionary<int, int>(), overwrite: true));
        Assert.Equal("keep me", File.ReadAllText(first));
        Assert.Equal([first], Directory.GetFiles(output));
    }

    [Fact]
    public void APartThatWouldReplaceTheOpenDocumentIsFound()
    {
        IReadOnlyList<PageSplitPart> parts = PageSplit.Plan(PageSplitMode.EveryPage, 3, 1, "report");
        string source = Path.Combine(_folder, "report-page-002.pdf");

        Assert.True(PageSplit.WouldReplace(_folder, parts, source));
        Assert.False(PageSplit.WouldReplace(_folder, parts, Path.Combine(_folder, "report.pdf")));
        Assert.False(PageSplit.WouldReplace(_folder, parts, null));
    }

    [Theory]
    [InlineData("report", "report")]
    [InlineData("  my  report. ", "my report")]
    [InlineData("a/b", null)]
    [InlineData("what?", null)]
    [InlineData("   ", null)]
    [InlineData("..", null)]
    public void TheBaseNameIsTidiedOrRefused(string typed, string? expected) =>
        Assert.Equal(expected, PageSplit.BaseName(typed));

    [Theory]
    [InlineData(2, 4, 5, new[] { 1, 2, 3 })]
    [InlineData(4, 2, 5, new[] { 1, 2, 3 })]
    [InlineData(3, 3, 5, new[] { 2 })]
    [InlineData(0, 2, 5, null)]
    [InlineData(1, 6, 5, null)]
    public void FromAndToGiveTheirPages(int from, int to, int pageCount, int[]? expected) =>
        Assert.Equal(expected, PageSplit.FromTo(from, to, pageCount)?.ToArray());

    [Theory]
    [InlineData(new int[0], 8, 1, 8, true)]
    [InlineData(new[] { 3 }, 8, 3, 3, true)]
    [InlineData(new[] { 4, 2, 3 }, 8, 2, 4, true)]
    [InlineData(new[] { 2, 4 }, 8, 2, 4, false)]
    public void TheDialogStartsFromTheSelectionOrTheWholeDocument(int[] selected, int pageCount, int from, int to, bool contiguous)
    {
        var start = PageSplit.Start(selected, pageCount);

        Assert.Equal((from, to, contiguous), (start.From, start.To, start.Contiguous));
    }

    [Theory]
    [InlineData(true, 12, 1, 12)]
    [InlineData(false, 12, 5, 3)]
    [InlineData(false, 12, 0, 0)]
    public void TheFileCountMatchesThePlan(bool everyPage, int pageCount, int every, int expected) =>
        Assert.Equal(expected, PageSplit.FileCount(everyPage ? PageSplitMode.EveryPage : PageSplitMode.EveryN, pageCount, every));
}
