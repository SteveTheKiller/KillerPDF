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
}
