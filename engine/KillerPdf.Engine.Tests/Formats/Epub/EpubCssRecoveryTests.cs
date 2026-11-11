using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubCssRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TooManyFontFamiliesDropOnlyTheirDeclaration(bool inline)
    {
        string declaration = "font-family:" + string.Join(',', Enumerable.Range(0, 33).Select(index => "Missing" + index))
            + ";color:red;font-size:12px";
        var source = new Book
        {
            Css = inline ? "" : "p{" + declaration + "}",
            Chapters = { Chapter(inline ? "<p style='" + declaration + "'>A</p>" : "<p>A</p>") }
        };
        var book = EpubDocument.Open(source.Build());
        int requests = 0;
        var layout = book.CreateLayout(Options() with { FontFamilyResolver = _ => { requests++; return null; } });

        Assert.Empty(layout.ChapterDiagnostics);
        var letter = Assert.Single(Assert.Single(layout.Pages).TextPlacements);
        Assert.Equal(new PdfRgbColor(1, 0, 0), letter.Color);
        Assert.Equal(12, letter.Height);
        Assert.Equal(0, requests);
        Assert.Equal(1, Assert.Single(layout.IgnoredDeclarations).Count);
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }

    [Theory]
    [InlineData("p{color:red}")]
    [InlineData("@media screen {p{color:red}}")]
    public void NamespaceAfterStyleRulesIsIgnoredWithoutDiscardingTheLinkedChapters(string before)
    {
        var source = new Book
        {
            Css = before + "@namespace 'urn:other';p{font-size:12px}",
            Chapters = { Chapter("<p>A</p>"), Chapter("<p>A</p>") }
        };
        var book = EpubDocument.Open(source.Build());

        var layout = book.CreateLayout(Options());

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(2, layout.Pages.Count);
        Assert.All(layout.Pages, page =>
        {
            var letter = Assert.Single(page.TextPlacements);
            Assert.Equal(new PdfRgbColor(1, 0, 0), letter.Color);
            Assert.Equal(12, letter.Height);
        });
        Assert.Equal(new[] { 1, 1 }, layout.IgnoredDeclarations.Select(item => item.Count));
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal(2, reader.PageCount);
        Assert.All(Enumerable.Range(0, 2), page => Assert.Equal("A", string.Concat(reader.Read(page).Letters.Select(letter => letter.Value))));
    }
}
