using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageInformationTests
{
    [Fact]
    public void Read_ReturnsEffectiveCropGeometryAndNormalizedRotation()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(200, 300, ReadOnlyMemory<byte>.Empty)
            .AddPage(400, 500, ReadOnlyMemory<byte>.Empty)
            .Build();
        byte[] edited = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetCropBox(0, 10, 20, 100, 150)
            .SetRotation(0, 270)
            .Build();

        IReadOnlyList<PdfPageInformation> pages =
            PdfPageInformation.Read(PdfDocument.Open(edited));

        Assert.Equal(2, pages.Count);
        Assert.Equal(10, pages[0].Left);
        Assert.Equal(20, pages[0].Bottom);
        Assert.Equal(100, pages[0].Width);
        Assert.Equal(150, pages[0].Height);
        Assert.Equal(270, pages[0].Rotation);
        Assert.Equal(400, pages[1].Width);
        Assert.Equal(500, pages[1].Height);
        Assert.Equal(0, pages[1].Rotation);
    }

    [Fact]
    public void Read_CompatibilityRecoveryUsesLetterPageForMissingMediaBox()
    {
        PdfDocument strict = MissingMediaBoxDocument();

        Assert.Throws<InvalidOperationException>(() => PdfPageInformation.Read(strict));

        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(
            PdfDocument.OpenWithCompatibilityRecovery(strict.Source));

        PdfPageInformation page = Assert.Single(pages);
        Assert.Equal(0, page.Left);
        Assert.Equal(0, page.Bottom);
        Assert.Equal(612, page.Width);
        Assert.Equal(792, page.Height);
    }

    private static PdfDocument MissingMediaBoxDocument()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(200, 300).Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[new PdfName("Root"u8)])));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)])));
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary withoutMediaBox = new(page.Where(entry =>
            !entry.Key.Equals(new PdfName("MediaBox"u8))));

        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, withoutMediaBox).Build());
    }
}
