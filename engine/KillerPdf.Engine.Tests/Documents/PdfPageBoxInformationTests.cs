using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageBoxInformationTests
{
    [Fact]
    public void ReadReturnsExplicitAndDefaultedPageBoundaries()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(612, 792, ReadOnlyMemory<byte>.Empty).Build());
        (PdfIndirectReference pageReference, PdfDictionary page) = Page(source);
        PdfDictionary replacement = new(page.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("CropBox"), Box(10, 20, 602, 772))).Append(
            new KeyValuePair<PdfName, PdfObject>(Name("BleedBox"), Box(5, 10, 607, 782))).Append(
            new KeyValuePair<PdfName, PdfObject>(Name("TrimBox"), Box(18, 28, 594, 764))));
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, replacement).Build());

        PdfPageBoxInformation info = Assert.Single(PdfPageBoxInformation.Read(document));

        Assert.Equal(new PdfPageBoxBounds(0, 0, 612, 792), info.MediaBox);
        Assert.Equal(new PdfPageBoxBounds(10, 20, 602, 772), info.CropBox);
        Assert.Equal(new PdfPageBoxBounds(5, 10, 607, 782), info.BleedBox);
        Assert.Equal(new PdfPageBoxBounds(18, 28, 594, 764), info.TrimBox);
        Assert.Equal(info.CropBox, info.ArtBox);
        Assert.True(info.HasExplicitCropBox);
        Assert.True(info.HasExplicitBleedBox);
        Assert.True(info.HasExplicitTrimBox);
        Assert.False(info.HasExplicitArtBox);
        Assert.Equal(576, info.TrimBox.Width);
        Assert.Equal(736, info.TrimBox.Height);
    }

    [Fact]
    public void ReadRejectsDegenerateExplicitBoxes()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().Build());
        (PdfIndirectReference pageReference, PdfDictionary page) = Page(source);
        PdfDictionary replacement = new(page.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("ArtBox"), Box(20, 20, 10, 30))));
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, replacement).Build());

        Assert.Throws<InvalidOperationException>(() => PdfPageBoxInformation.Read(document));
    }

    private static PdfArray Box(long left, long bottom, long right, long top) =>
        new([new PdfInteger(left), new PdfInteger(bottom),
            new PdfInteger(right), new PdfInteger(top)]);

    private static (PdfIndirectReference Reference, PdfDictionary Dictionary) Page(
        PdfDocument document)
    {
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            document.Resolve(catalogReference));
        PdfIndirectReference pagesReference = Assert.IsType<PdfIndirectReference>(
            catalog[Name("Pages")]);
        PdfDictionary pages = Assert.IsType<PdfDictionary>(document.Resolve(pagesReference));
        PdfArray kids = Assert.IsType<PdfArray>(pages[Name("Kids")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(kids));
        return (pageReference,
            Assert.IsType<PdfDictionary>(document.Resolve(pageReference)));
    }

    private static PdfName Name(string value) =>
        new(System.Text.Encoding.ASCII.GetBytes(value));
}
