using KillerPdf.Engine;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrSearchableTextWriterTests
{
    [Fact]
    public void WriterHandlesEveryPageRotationAndPreservesTheSourceRevision()
    {
        PdfDocument authored = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).AddBlankPage(300, 400)
            .AddBlankPage(300, 400).AddBlankPage(300, 400).Build());
        byte[] source = new PdfIncrementalPageEditor(authored)
            .SetRotation(0, 0).SetRotation(1, 90)
            .SetRotation(2, 180).SetRotation(3, 270).Build();
        PdfDocument document = PdfDocument.Open(source);
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        PdfOcrPixelPage[] pages = [.. Enumerable.Range(0, 4).Select(index =>
            new PdfOcrPixelPage(index % 2 == 0 ? 300 : 400,
                index % 2 == 0 ? 400 : 300,
                [new PdfOcrPixelWord("A", 1, 10, 10, 120, 35)]))];

        PdfOcrSearchableTextResult result = PdfOcrSearchableTextWriter.Write(
            document, pages, _ => font);

        Assert.Equal(4, result.WrittenWords);
        Assert.True(result.Document.AsSpan(0, source.Length).SequenceEqual(source));
        PdfDocument reopened = PdfDocument.Open(result.Document);
        var reader = new PdfPageContentReader(reopened);
        for (int index = 0; index < 4; index++)
            Assert.Contains("A", reader.Read(index).Text);
    }

    [Fact]
    public void WriterProducesIdenticalBytesForIdenticalInput()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        PdfOcrPixelPage[] pages =
        [
            new(200, 100, [new PdfOcrPixelWord("A", 1, 10, 10, 40, 30)])
        ];

        byte[] first = PdfOcrSearchableTextWriter.Write(document, pages, _ => font).Document;
        byte[] second = PdfOcrSearchableTextWriter.Write(document, pages, _ => font).Document;

        Assert.Equal(first, second);
    }

    [Fact]
    public void WriterPlacesPixelWordsAtTheNonzeroCropBoxOrigin()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
        PdfDocument cropped = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetCropBox(0, 20, 30, 100, 50).Build());
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        PdfOcrPixelPage[] pages =
        [
            new(100, 50, [new PdfOcrPixelWord("A", 1, 10, 10, 40, 25)])
        ];

        PdfPageContent extracted = new PdfPageContentReader(PdfDocument.Open(
            PdfOcrSearchableTextWriter.Write(cropped, pages, _ => font).Document)).Read(0);

        Assert.Equal(10, Assert.Single(extracted.Words).BoundingBox.Left, 6);
    }

    [Theory]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, -1, 10, 10)]
    [InlineData(10, 0, 10, 10)]
    [InlineData(0, 10, 10, 10)]
    [InlineData(0, 0, 201, 10)]
    [InlineData(0, 0, 10, 101)]
    public void WriterRejectsWordBoundsOutsideThePixelPage(
        int left, int top, int right, int bottom)
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        PdfOcrPixelPage[] pages =
        [
            new(200, 100, [new PdfOcrPixelWord(
                "A", 1, left, top, right, bottom)])
        ];

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOcrSearchableTextWriter.Write(document, pages, _ => font));
    }
}
