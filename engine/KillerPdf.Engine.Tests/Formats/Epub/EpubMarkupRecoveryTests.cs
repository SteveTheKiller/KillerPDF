using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubMarkupRecoveryTests
{
    [Theory]
    [InlineData("<ul><li/></ul>", "A", 1, false)]
    [InlineData("<ul><li><a id='empty'/></li></ul>", "A", 1, false)]
    [InlineData("<ul><li><ul><li>A</li></ul></li></ul>", "AA", 1, false)]
    [InlineData("<ul><li><img style='display:block' src='../images/c.png'/></li></ul>", "A", 1, true)]
    [InlineData("<ul><li style='visibility:hidden'/></ul>", "A", 0, false)]
    public void ItemsWithoutAMarkerLineOmitOnlyTheMarkerAndPreserveTheirContent(
        string body, string expected, int expectedDiagnostics, bool image)
    {
        var source = new Book
        {
            Chapters = { Chapter(body + "<p>A</p>") },
            ExtraManifest = "<item id='image' href='images/c.png' media-type='image/png'/>"
        };
        source.Resources.Add(("images/c.png", EpubImageCompatibilityTests.Png(10, 20, 2)));
        var book = EpubDocument.Open(source.Build());
        var layout = book.CreateLayout(Options());

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(expected.Length, layout.Pages.Sum(page => page.TextPlacements.Count));
        Assert.Equal(expectedDiagnostics, layout.ContentDiagnostics.Count);
        Assert.All(layout.ContentDiagnostics, diagnostic =>
        {
            Assert.Contains("marker", diagnostic.Message);
            Assert.Equal(EpubOmissionReason.UnsupportedLayout, diagnostic.Reason);
        });
        Assert.Equal(image ? 1 : 0, layout.Pages.SelectMany(page => page.Glyphs).OfType<NativeImagePaint>().Count());
        var diagnostics = new List<EpubChapterDiagnostic>();
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { DiagnosticReported = diagnostics.Add })));
        Assert.Equal(expected, string.Concat(Enumerable.Range(0, reader.PageCount)
            .SelectMany(page => reader.Read(page).Letters).Select(letter => letter.Value)));
        Assert.Equal(image ? 1 : 0, Enumerable.Range(0, reader.PageCount).Sum(page => reader.Read(page).Images.Count));
        Assert.Equal(expectedDiagnostics, diagnostics.Count);
    }

    [Theory]
    [InlineData("<ul>A<li>A</li></ul>", "AA")]
    [InlineData("<pre>A<pre>A</pre>A</pre>", "AAA")]
    public void RecoverableListAndPreformattedMarkupPreservesItsTextAndConversion(string body, string expected)
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter(body) } }.Build());
        Assert.Equal(expected, new string(book.ReadText(0).Where(char.IsLetter).ToArray()));

        var layout = book.CreateLayout(Options());

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(expected.Length, layout.Pages.Sum(page => page.TextPlacements.Count));
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal(expected, string.Concat(Enumerable.Range(0, reader.PageCount)
            .SelectMany(page => reader.Read(page).Letters).Select(letter => letter.Value)));
    }
}
