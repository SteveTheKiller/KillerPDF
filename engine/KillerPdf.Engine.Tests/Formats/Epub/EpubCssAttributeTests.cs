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

public sealed class EpubCssAttributeTests
{
    private const string Declaration = "@namespace epub 'http://www.idpf.org/2007/ops';";

    [Fact]
    public void NamespacedTokenSelectorPaintsNativeAndConvertedText()
        => Verify(Declaration + "span[epub|type~='bridgehead']{color:red}",
            "<p><span epub:type=\"bridgehead\">A</span></p>", true);

    [Fact]
    public void PresenceSelectsAnExistingEmptySemanticAttribute()
        => Verify(Declaration + "p[epub|type]{color:red}", "<p epub:type=\"\">A</p>", true);

    [Theory]
    [InlineData("p[epub|type~='bridgehead']", "epub:type=\"other&#x9;bridgehead&#xA;last\"", true)]
    [InlineData("p[epub|type~='bridgehead']", "epub:type=\"bridgeheads\"", false)]
    [InlineData("p[epub|type~='bridgehead']", "epub:type=\"prebridgehead\"", false)]
    [InlineData("p[epub|type~='bridgehead']", "epub:type=\"Bridgehead\"", false)]
    [InlineData("p[epub|type~='bridgehead']", "epub:type=\"other&#xA0;bridgehead\"", false)]
    [InlineData("p[epub|type~='bridgehead']", "", false)]
    [InlineData("p[epub|type~='bridgehead']", "other:type=\"bridgehead\"", false)]
    [InlineData("p[epub|type~='bridgehead']", "type=\"bridgehead\"", false)]
    [InlineData("p[epub|type~='bridgehead']", "alias:type=\"bridgehead\"", true)]
    [InlineData("p[alias|type~=bridgehead]", "epub:type=\"bridgehead\"", true)]
    [InlineData("p[epub|Type~='bridgehead']", "epub:type=\"bridgehead\"", false)]
    [InlineData("p[type~='bridgehead']", "epub:type=\"bridgehead\"", false)]
    [InlineData("p[type~='bridgehead']", "type=\"bridgehead\"", true)]
    [InlineData("p[epub|type~='']", "epub:type=\"bridgehead\"", false)]
    [InlineData("p[epub|type~='other bridgehead']", "epub:type=\"other bridgehead\"", false)]
    public void TokenMembershipUsesCaseSensitiveExpandedNames(string selector, string attributes, bool red)
        => Verify(Declaration + "@namespace alias 'http://www.idpf.org/2007/ops';" + selector + "{color:red}",
            "<p " + attributes + ">A</p>", red);

    [Theory]
    [InlineData("p[epub|type]", "epub:type=\"\"", true)]
    [InlineData("p[epub|type]", "alias:type=\"bridgehead\"", true)]
    [InlineData("p[epub|type]", "other:type=\"bridgehead\"", false)]
    [InlineData("p[epub|type]", "type=\"bridgehead\"", false)]
    [InlineData("p[epub|type]", "", false)]
    [InlineData("p[epub|Type]", "epub:type=\"bridgehead\"", false)]
    [InlineData("p[type]", "type=\"\"", true)]
    [InlineData("p[type]", "epub:type=\"bridgehead\"", false)]
    [InlineData("p[class]", "class=\"\"", true)]
    [InlineData("p[class]", "", false)]
    public void PresenceRequiresTheActualAttribute(string selector, string attributes, bool red)
        => Verify(Declaration + selector + "{color:red}", "<p " + attributes + ">A</p>", red);

    [Theory]
    [InlineData("p.note[epub|type~='bridgehead']{color:red}p.note{color:blue}")]
    [InlineData("p[epub|type]{color:red}.note{color:blue}")]
    [InlineData("p[epub|type]{color:blue}p.note{color:red}")]
    [InlineData("p.note{color:blue}p[epub|type]{color:red}")]
    [InlineData("p.note.other{color:blue}p.note[epub|type]{color:red}")]
    public void AttributesAndClassesUseConjunctionAndEqualSpecificity(string css)
        => Verify(Declaration + css, "<p class=\"note other\" epub:type=\"bridgehead\">A</p>", true);

    [Fact]
    public void ClassMissAndInlineOverrideRemainObservable()
    {
        Verify(Declaration + "p.note[epub|type]{color:red}", "<p class=\"other\" epub:type=\"bridgehead\">A</p>", false);
        Verify(Declaration + "p.note[epub|type]{color:red}", "<p class=\"note\">A</p>", false);
        Verify(Declaration + "p.note[epub|type]{color:blue}", "<p class=\"note\" epub:type=\"bridgehead\" style=\"color:red\">A</p>", true);
    }

    [Fact]
    public void DefaultNamespacesDoNotApplyToUnqualifiedAttributeNames()
    {
        Verify("@namespace 'http://www.w3.org/1999/xhtml';p[type]{color:red}", "<p type=\"\">A</p>", true);
        Verify("@namespace 'urn:other';p[type]{color:red}", "<p type=\"\">A</p>", false);
        Verify("@namespace other 'urn:other';p[other|type]{color:red}", "<p other:type=\"\">A</p>", true);
        Verify("@namespace remote 'https://127.0.0.1:1/unreachable';p[remote|type]{color:red}", "<p epub:type=\"\">A</p>", false);
    }

    [Theory]
    [InlineData("p[epub|type='bridgehead']")]
    [InlineData("p[epub|type^='bridgehead']")]
    [InlineData("p[epub|type$='bridgehead']")]
    [InlineData("p[epub|type*='bridgehead']")]
    [InlineData("p[epub|type|='bridgehead']")]
    [InlineData("p[*|type]")]
    [InlineData("p[|type]")]
    [InlineData("p[epub|type][class]")]
    [InlineData("p[epub|type].note")]
    [InlineData("p[epub|type~='bridgehead' i]")]
    [InlineData("p[epub|type")]
    public void UnsupportedAttributeSyntaxRetainsExplicitDiagnostic(string selector)
        => Reject(Declaration + selector + "{color:red}", "Native EPUB CSS selector is not implemented.");

    [Theory]
    [InlineData("missing")]
    [InlineData("EPUB")]
    public void AttributePrefixesMustBeDeclaredWithExactSpelling(string prefix)
        => Reject(Declaration + "p[" + prefix + "|type]{color:red}",
            "Native EPUB CSS selector namespace prefix is not declared: " + prefix + ".");

    [Fact]
    public void UnrelatedXhtmlAttributesKeepTheirDiagnostic()
        => Reject("p{color:red}", "Native EPUB XHTML attributes and styling are not implemented.", "<p onclick=\"alert(1)\">A</p>");

    [Fact]
    public void AttributeSelectorsRetainCountAndMatchingWorkLimits()
    {
        Reject<InvalidDataException>(string.Concat(Enumerable.Repeat("p[type]{}", 4097)),
            "Native EPUB CSS selectors exceed their 4,096-selector limit.");
        Reject<InvalidDataException>("@namespace e 'http://www.idpf.org/2007/ops';" + string.Concat(Enumerable.Repeat("p[e|type~='a']{}", 4096)),
            "Native EPUB CSS selector matching exceeds its work limit.", string.Concat(Enumerable.Repeat("<p epub:type=\"a\">A</p>", 16)));
    }

    [Fact]
    public void AttributeSelectorsPreservePublicCancellation()
    {
        byte[] source = Book(Declaration + "p[epub|type]{color:red}", "<p epub:type=\"bridgehead\">A</p>");
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

    private static void Reject(string css, string message, string body = "<p>A</p>")
        => Reject<NotSupportedException>(css, message, body);

    private static void Reject<T>(string css, string message, string body = "<p>A</p>") where T : Exception
    {
        byte[] source = Book(css, body);
        byte[] original = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal(message, Assert.Throws<T>(() => book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token)).Message);
        Assert.Equal(message, Assert.Throws<T>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), deadline.Token)).Message);
        Assert.Equal(original, source);
    }

    private static void Verify(string css, string body, bool red)
    {
        byte[] source = Book(css, body);
        byte[] original = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(red ? new PdfRgbColor(1, 0, 0) : (PdfRgbColor?)null, Assert.Single(page.TextPlacements).Color);
        var native = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(red ? new float[] { 1, 0, 0, 1 } : [0, 0, 0, 1], native.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        Assert.Equal("A", Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).Value);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16), deadline.Token);
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(red ? new byte[] { 0, 0, 255, 255 } : [0, 0, 0, 255], rendered.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(original, source);
        Assert.Equal(Encoding.UTF8.GetBytes(css), book.ReadResource("EPUB/style.css"));
    }

    private static byte[] Book(string css, string body)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:attribute</dc:identifier><dc:title>Attribute</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xmlns:alias=\"http://www.idpf.org/2007/ops\" xmlns:other=\"urn:other\"><head><title>Attribute</title><link rel=\"stylesheet\" href=\"style.css\"/></head><body>" + body + "</body></html>");
            Add("EPUB/style.css", css);
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
