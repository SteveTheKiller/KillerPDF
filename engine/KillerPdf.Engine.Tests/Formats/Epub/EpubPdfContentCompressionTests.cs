using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Objects;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// A converted book is mostly page content: text operators for every glyph run. Stored plain, a long novel's
// PDF is many times larger than it needs to be, so converted pages store their content Flate-compressed.
public sealed class EpubPdfContentCompressionTests
{
    [Fact]
    public void ConvertedPagesStoreTheirContentCompressedAndReadTheSameText()
    {
        var source = new Book();
        for (int chapter = 0; chapter < 4; chapter++)
            source.Chapters.Add(Chapter(string.Concat(Enumerable.Repeat(
                "<p>Chapter " + chapter + " carries on with plain words for the page layout to wrap and break.</p>", 30))));
        byte[] converted = EpubDocument.Open(source.Build()).ConvertToPdf(new EpubPdfConversionOptions(Liberation())
            { PageWidthPoints = 300, PageHeightPoints = 300, MarginPoints = 10, FontSizePoints = 9 });

        PdfDocument document = PdfDocument.Open(converted);
        var pages = PdfPageTree.Read(document).Pages;
        Assert.True(pages.Count > 4);
        foreach (var page in pages)
        {
            var content = Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(page.Dictionary[Name("Contents")])));
            Assert.Equal("FlateDecode", Assert.IsType<PdfName>(content.Dictionary[Name("Filter")]).ValueAsLatin1());
        }
        var reader = new PdfPageContentReader(document);
        string text = string.Concat(Enumerable.Range(0, pages.Count).Select(index => reader.Read(index).Text));
        Assert.Contains("Chapter 3 carries on with plain words", text.Replace("  ", " "));
    }

    [Fact]
    public void ABuilderThatIsNotAskedToCompressWritesPlainPageContent()
    {
        byte[] built = new PdfDocumentBuilder().AddPage(100, 100, Encoding.ASCII.GetBytes("0 0 10 10 re f")).Build();
        PdfDocument document = PdfDocument.Open(built);
        var page = PdfPageTree.Read(document).Pages[0].Dictionary;
        var content = Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(page[Name("Contents")])));
        Assert.False(content.Dictionary.ContainsKey(Name("Filter")));
    }

    private static TrueTypeFont Liberation()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream("KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return TrueTypeFont.Load(bytes.ToArray());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
