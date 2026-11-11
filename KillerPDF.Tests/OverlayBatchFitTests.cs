using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OverlayBatchFitTests
{
    // A text annotation serializes to 32 JSON values (object, 8 base properties, Position as object plus X and Y, and
    // 21 more fields) and its batch wrapper adds 3, so 8,700 of them are 304,500 values, past the 300,000 the batch
    // accepts. BatchFits used to count a flat 30 per item and said yes.
    private static List<PageAnnotation> TextItems(int count) =>
        [.. Enumerable.Range(0, count).Select(index => (PageAnnotation)new TextAnnotation
        {
            PageIndex = 0, Position = new Point(index % 500, index / 500), Content = "x", Width = 10, Height = 10
        })];

    [Theory]
    [InlineData(8_700)]
    [InlineData(8_571)]
    [InlineData(8_572)]
    public void BatchFitsAgreesWithWhatTheBatchWriterAccepts(int count)
    {
        var items = TextItems(count);
        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(600, 600).Build());
        bool fits = PdfOverlayAnnotations.BatchFits(items);
        if (fits)
        {
            byte[] saved = PdfOverlayAnnotations.AppendPageBatch(blank, 0, 600, 600, 0, items, new PdfContentStreamBuilder(), null);
            Assert.Equal(count, PdfOverlayAnnotations.Read(PdfDocument.Open(saved)).Count);
        }
        else Assert.Throws<ArgumentException>(() =>
            PdfOverlayAnnotations.AppendPageBatch(blank, 0, 600, 600, 0, items, new PdfContentStreamBuilder(), null));
    }

    [Fact]
    public void ATextPageTooBigForOneBatchIsReportedAsNotFitting()
    {
        Assert.False(PdfOverlayAnnotations.BatchFits(TextItems(8_700)));
        Assert.True(PdfOverlayAnnotations.BatchFits(TextItems(8_571)));
        Assert.False(PdfOverlayAnnotations.BatchFits(TextItems(8_572)));
    }

    // The oversized page is drawn as page content on its own; the document's other pages keep their editable text.
    [Fact]
    public void AnOversizedPageDoesNotFlattenTheEditableTextOnOtherPages()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-batchfit-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(600, 600).AddBlankPage(600, 600).Build());
            var big = TextItems(8_700);
            big[0].PairId = "edit-big";
            var annotations = new Dictionary<int, List<PageAnnotation>>
            {
                [0] = big,
                [1] = [new TextAnnotation { PageIndex = 1, PairId = "edit-small", Content = "Edited line", Position = new Point(60, 100), Width = 100, Height = 20 }]
            };
            var dimensions = new Dictionary<int, (int w, int h)> { [0] = (600, 600), [1] = (600, 600) };
            PdfEngineBurn.Burn(path, annotations, dimensions, measurementsAsAnnotations: true);
            var edits = PdfOverlayAnnotations.Read(PdfDocument.Open(File.ReadAllBytes(path)))
                .Select(record => record.Annotation).OfType<TextAnnotation>().ToList();
            Assert.Equal("Edited line", Assert.Single(edits, edit => edit.PageIndex == 1).Content);
        }
        finally { File.Delete(path); }
    }
}
