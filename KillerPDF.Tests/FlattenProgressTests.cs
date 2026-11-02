using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// SN01 D12: Save Flattened on a 1,200-page file showed nothing for seconds, sat at page 0 while the
// whole file was read ahead of the first page, and took most of a minute to cancel. The flatten says
// where it is before it reads anything, reads each page as it goes, and stops at a cancel.
public sealed class FlattenProgressTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "KillerPDF-flatten-" + Guid.NewGuid().ToString("N"));

    public FlattenProgressTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string Pages(int count)
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < count; page++)
            builder.AddPage(100, 100, new PdfContentStreamBuilder().SetFillRgb(0.2, 0.4, 0.6).Rectangle(10, 10, 50, 50).Fill());
        string path = Path.Combine(_folder, $"pages-{count}.pdf");
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private static (double, double)[] Sizes(int count) => [.. Enumerable.Repeat((100.0, 100.0), count)];

    [Fact]
    public void ProgressStartsAtPageZeroBeforeAnyPageIsDrawn()
    {
        string source = Pages(3);
        var reports = new List<(int Done, int Total)>();

        PdfRasterize.FlattenToPdf(source, 3, Sizes(3), Path.Combine(_folder, "out.pdf"),
            new PdfRasterize.FlattenOptions(Dpi: 36), (done, total) => { lock (reports) reports.Add((done, total)); },
            CancellationToken.None);

        Assert.Equal((0, 3), reports[0]);
        Assert.Equal(3, reports.Max(report => report.Done));
        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(Path.Combine(_folder, "out.pdf")))).Count);
    }

    // A cancel the moment the progress shows reads no more of the file and writes nothing.
    [Fact]
    public void ACancelAsSoonAsProgressShowsDrawsNoPageAndWritesNothing()
    {
        string source = Pages(4);
        string output = Path.Combine(_folder, "canceled.pdf");
        using var cancel = new CancellationTokenSource();
        var reports = new List<int>();

        PdfRasterize.FlattenToPdf(source, 4, Sizes(4), output, new PdfRasterize.FlattenOptions(Dpi: 36),
            (done, _) => { lock (reports) reports.Add(done); cancel.Cancel(); }, cancel.Token);

        Assert.Equal([0], reports);
        Assert.False(File.Exists(output));
    }

    // R6-009: the flatten reads its source once. The parse it reads the page hints from is the one
    // the page renders come from, not a second read of the same file.
    [Fact]
    public void TheFlattenReadsItsSourceOnce()
    {
        string source = Pages(3);

        PdfDocument read = PdfRasterize.OpenFlattenSource(source);

        Assert.Same(read, PdfPageRenderSession.OpenDocument(source, null));
        Assert.Equal(3, PdfPageInformation.Read(read).Count);
    }

    // A cancel made before the flatten starts is honored before the source is even read.
    [Fact]
    public void ACancelBeforeTheStartReadsNothing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        string output = Path.Combine(_folder, "never.pdf");

        Exception? failure = Record.Exception(() => PdfRasterize.FlattenToPdf(Path.Combine(_folder, "not-there.pdf"), 2, Sizes(2),
            output, new PdfRasterize.FlattenOptions(), (_, _) => { }, cancel.Token));

        Assert.Null(failure);
        Assert.False(File.Exists(output));
    }
}
