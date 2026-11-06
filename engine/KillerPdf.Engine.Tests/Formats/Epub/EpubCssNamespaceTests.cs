using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubCssNamespaceTests
{
    [Fact]
    public void ExplicitAnyAndEmptyNamespacesOverrideDefaultBinding()
    {
        Verify(["@namespace 'urn:other';*|p.note{color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255]);
        Verify(["@namespace 'http://www.w3.org/1999/xhtml';|p.note{color:red}"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
    }

    [Fact]
    public void LiteralNamespaceNamesAreNotNormalizedOrFetched()
    {
        Verify(["@namespace h \"https://127.0.0.1:1/(unreachable)'\";h|p{color:red}"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
        Verify(["@namespace h 'HTTP://www.w3.org/1999/xhtml';h|p{color:red}"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
        Verify(["@namespace h '';h|p{color:red}"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
    }

    [Theory]
    [InlineData("p span")]
    [InlineData("p>span")]
    [InlineData("p[epub|type]")]
    [InlineData("p:first-child")]
    public void UnsupportedSelectorFamiliesRetainExplicitDiagnostic(string selector)
    {
        Reject(["@namespace epub 'http://www.idpf.org/2007/ops';" + selector + "{color:red}"], "Native EPUB CSS selector is not implemented.");
    }

    [Theory]
    [InlineData("@namespace")]
    [InlineData("@namespace h;")]
    [InlineData("@namespace h 'unterminated;")]
    [InlineData("@namespace h url(urn:test;")]
    [InlineData("@namespace h 'urn:test' garbage;")]
    [InlineData("body{}@namespace h 'urn:test';")]
    public void MalformedOrMisplacedNamespaceUsesStrictRefusalPolicy(string css)
    {
        Reject([css], "Native EPUB CSS namespace syntax is not implemented.");
    }

    [Fact]
    public void NamespaceLimitsAndMatchingWorkAreBounded()
    {
        RejectBudget(string.Concat(Enumerable.Repeat("@namespace h 'urn:test';", 257)), "Native EPUB CSS namespace declarations exceed their 256-declaration limit.");
        RejectBudget("@namespace h '" + new string('x', 2049) + "';", "Native EPUB CSS namespace name exceeds its 2,048-character limit.");
        RejectBudget("@namespace h 'http://www.w3.org/1999/xhtml';" + string.Concat(Enumerable.Repeat("h|p{}", 4096)),
            "Native EPUB CSS selector matching exceeds its work limit.", string.Concat(Enumerable.Repeat("<p>A</p>", 16)));
        Reject([new string('p', 129) + "{}"], "Native EPUB CSS selector is not implemented.");
    }

    [Fact]
    public void CancellationRemainsObservableAtEveryPublicStage()
    {
        byte[] source = Book(["@namespace h 'http://www.w3.org/1999/xhtml';h|p{color:red}"]);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 };
        var layout = book.CreateLayout(options);
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => EpubDocument.Open(source, canceled));
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(options, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, cancellationToken: canceled));
        Assert.Throws<OperationCanceledException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), canceled));
        Assert.Equal(original, source);
    }

    [Fact]
    public void SelectorCountHasAnExplicitBoundAtBothPublicSeams()
    {
        RejectBudget(string.Concat(Enumerable.Repeat("p{}", 4097)), "Native EPUB CSS selectors exceed their 4,096-selector limit.");
    }

    private static void RejectBudget(string css, string message, string body = "<p>A</p>")
    {
        byte[] source = Book([css], body);
        byte[] original = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token)).Message);
        Assert.Equal(message, Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), deadline.Token)).Message);
        Assert.Equal(original, source);
    }

    [Fact]
    public void BindingsAreLocalToEachSheetAndLastDeclarationWins()
    {
        Verify(["@namespace 'urn:other';body{color:lime}", "p{color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255]);
        Verify(["@namespace h 'http://www.w3.org/1999/xhtml';h|p{color:red}", "@namespace h 'urn:other';h|p{color:lime}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255]);
        Verify(["@namespace h 'urn:other';@namespace h 'http://www.w3.org/1999/xhtml';h|p{color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255]);
        Verify(["@namespace 'http://www.w3.org/1999/xhtml';@namespace 'urn:other';p{color:red}"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
        Reject(["@namespace h 'http://www.w3.org/1999/xhtml';", "h|p{color:red}"], "Native EPUB CSS selector namespace prefix is not declared: h.");
    }

    [Fact]
    public void NamespacePrefixAddsNoSpecificityAndPropertiesCascadeIndependently()
    {
        const string declaration = "@namespace longprefix 'http://www.w3.org/1999/xhtml';";
        Verify([declaration + "longprefix|p{color:lime}p{color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255]);
        Verify([declaration + "p{color:lime}longprefix|p{color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255]);
        Verify([declaration + "longprefix|body{background-color:blue;color:lime}body{color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255], true);
        Verify([declaration + "body{background-color:blue}longprefix|p.note{color:red}p{color:lime}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255], true);
        Verify([declaration + "longprefix|p.note{color:red}p{color:lime}"], new(0, 0, 1), [0, 0, 1, 1], [255, 0, 0, 255],
            body: "<p class=\"note\" style=\"color:blue\"><span><span>A</span></span></p>");
        Verify([declaration + "longprefix|p.note{color:red}p{color:lime}longprefix|span{color:blue}"], new(0, 0, 1), [0, 0, 1, 1], [255, 0, 0, 255]);
    }

    [Fact]
    public void DefaultNamespaceConstrainsUnqualifiedTypesAndPreservesDeclarationOnlySheets()
    {
        Verify(["@namespace 'http://www.w3.org/1999/xhtml';body{background-color:blue;color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255], true);
        Verify(["@namespace 'urn:other';body{background-color:blue;color:red}"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
        Verify(["@namespace epub 'http://www.idpf.org/2007/ops';"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
    }

    [Fact]
    public void QualifiedTypeMatchesExpandedNameIndependentlyOfXmlPrefix()
    {
        Verify(["@namespace h url(http://www.w3.org/1999/xhtml);h|p.note{color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255],
            body: "<xmlspelling:p class=\"note\"><span>A</span></xmlspelling:p>");
        Verify(["@namespace h 'urn:other';h|p.note{color:red}"], null, [0, 0, 0, 1], [0, 0, 0, 255]);
    }

    [Fact]
    public void UnusedNamespacePreservesEveryPlacementAndPixel()
    {
        var control = Verify(["body{background-color:blue;color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255], true);
        var namespaced = Verify(["@namespace epub \"http://www.idpf.org/2007/ops\";body{background-color:blue;color:red}"], new(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255], true);
        Assert.Equal(control.Native, namespaced.Native);
        Assert.Equal(control.Pdf, namespaced.Pdf);
    }

    private static (float[] Native, byte[] Pdf) Verify(string[] sheets, PdfRgbColor? expected, float[] ink, byte[] pdfInk,
        bool blue = false, string body = "<p class=\"note\"><span><span>A</span></span></p>")
    {
        byte[] source = Book(sheets, body);
        byte[] original = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10) { Color = expected }, Assert.Single(page.TextPlacements));
        var native = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(ink, native.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(blue ? [0, 0, 1, 1] : new float[] { 1, 1, 1, 1 }, native.Pixels.Slice(0, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(1.5, letter.StartBaseLine.X);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16), deadline.Token);
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(pdfInk, rendered.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(blue ? [255, 0, 0, 255] : new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice(0, 4).ToArray());
        Assert.Equal(original, source);
        for (int i = 0; i < sheets.Length; i++) Assert.Equal(Encoding.UTF8.GetBytes(sheets[i]), book.ReadResource($"EPUB/{i}.css"));
        return (native.Pixels.ToArray(), rendered.Pixels.ToArray());
    }

    private static void Reject(string[] sheets, string message)
    {
        byte[] source = Book(sheets);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal(message, Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal(message, Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font))).Message);
        Assert.Equal(original, source);
    }

    private static byte[] Book(string[] sheets, string body = "<p>A</p>")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:namespace</dc:identifier><dc:title>Namespace</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/>"
                + string.Concat(sheets.Select((_, i) => $"<item id=\"css{i}\" href=\"{i}.css\" media-type=\"text/css\"/>"))
                + "</manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:xmlspelling=\"http://www.w3.org/1999/xhtml\"><head><title>Namespace</title>"
                + string.Concat(sheets.Select((_, i) => $"<link rel=\"stylesheet\" href=\"{i}.css\"/>"))
                + "</head><body>" + body + "</body></html>");
            for (int i = 0; i < sheets.Length; i++) Add($"EPUB/{i}.css", sheets[i]);
            void Add(string path, string text)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return buffer.ToArray();
    }
}
