using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubNavigationDestinationTests
{
    [Fact]
    public void ChapterTargetsUseDeclaredSpineOrderAndTheFirstPageOfEachChapter()
    {
        var book = EpubDocument.Open(Book());
        var navigation = book.ReadNavigation();
        var layout = book.CreateLayout(Options());
        Assert.Equal(new[] { "A ", "A", "AA" }, layout.Pages.Select(page => page.Text));
        Assert.Equal("EPUB/later.xhtml", navigation[0].Target);
        Assert.Equal(new EpubNavigationDestination(2, 1, 0, 2, 2),
            layout.ResolveNavigationTarget(navigation[0].Target));
        Assert.Equal(new EpubNavigationDestination(0, 0, 0, 2, 2),
            layout.ResolveNavigationTarget(navigation[1].Target));
    }

    [Fact]
    public void NonemptyNavigationFragmentsAreDiagnosedInsteadOfSilentlyOpeningTheChapter()
    {
        var book = EpubDocument.Open(Book("<li><a href='../first.xhtml#chapter'>Chapter</a></li>"));
        string target = Assert.Single(book.ReadNavigation()).Target;
        Assert.Equal("EPUB/first.xhtml#chapter", target);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options()).ResolveNavigationTarget(target));
    }

    [Fact]
    public void CancellationPrecedesTargetValidationAndResolution()
    {
        var book = EpubDocument.Open(Book());
        var layout = book.CreateLayout(Options());
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => layout.ResolveNavigationTarget(null!, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.ResolveNavigationTarget("", canceled));
        Assert.Throws<OperationCanceledException>(() => layout.ResolveNavigationTarget(book.ReadNavigation()[0].Target, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.ResolveNavigationTarget("EPUB/first.xhtml#anchor", canceled));
    }

    [Fact]
    public void DuplicateSpineTargetsAreAmbiguousInsteadOfSelectingOneOccurrence()
    {
        var book = EpubDocument.Open(Book(spine: "<itemref idref='first'/><itemref idref='later'/><itemref idref='first'/>"));
        var navigation = book.ReadNavigation();
        var layout = book.CreateLayout(Options());
        Assert.Equal(new[] { 0, 0, 1, 2, 2 }, layout.Pages.Select(page => page.SpineIndex));
        Assert.Throws<InvalidDataException>(() => layout.ResolveNavigationTarget(navigation[1].Target));
        Assert.Equal(new EpubNavigationDestination(2, 1, 0, 2, 2),
            layout.ResolveNavigationTarget(navigation[0].Target));
    }

    [Fact]
    public void TargetValidationBoundsHostileInputBeforeInterpretingFragments()
    {
        var layout = EpubDocument.Open(Book()).CreateLayout(Options());
        Assert.Throws<ArgumentException>(() => layout.ResolveNavigationTarget(new string('A', 4096) + "#anchor"));
        Assert.Throws<ArgumentNullException>(() => layout.ResolveNavigationTarget(null!));
    }

    [Fact]
    public void EmptyFragmentResolvesTheChapterStartEvenForAnEmptyChapter()
    {
        var book = EpubDocument.Open(Book("<li><a href='../first.xhtml#'>Empty chapter</a></li>", firstBody: "<p></p>"));
        var target = Assert.Single(book.ReadNavigation()).Target;
        var options = Options() with { Margin = 3, PageWidth = 18 };
        var layout = book.CreateLayout(options);
        Assert.Empty(layout.Pages[0].Text);
        Assert.Equal(new EpubNavigationDestination(0, 0, 0, 3, 3), layout.ResolveNavigationTarget(target));
    }

    [Fact]
    public void Epub2NcxTargetsResolveToTheSameNativeChapterStarts()
    {
        var book = EpubDocument.Open(Book(ncx: true));
        var navigation = book.ReadNavigation();
        Assert.Equal("EPUB/later.xhtml", navigation[0].Target);
        var layout = book.CreateLayout(Options());
        Assert.Equal(new EpubNavigationDestination(2, 1, 0, 2, 2), layout.ResolveNavigationTarget(navigation[0].Target));
        Assert.Equal(new EpubNavigationDestination(0, 0, 0, 2, 2), layout.ResolveNavigationTarget(navigation[1].Target));
    }

    [Fact]
    public void GroupHeadingsAndExistingNonspineResourcesHaveNoChapterDestination()
    {
        var book = EpubDocument.Open(Book("<li><span>Group</span><ol><li><a href='../first.xhtml'>First</a></li></ol></li><li><a href='../extra.xhtml'>Extra</a></li>"));
        var navigation = book.ReadNavigation();
        var layout = book.CreateLayout(Options());
        Assert.Equal("", navigation[0].Target);
        Assert.Throws<ArgumentException>(() => layout.ResolveNavigationTarget(navigation[0].Target));
        Assert.Throws<ArgumentException>(() => layout.ResolveNavigationTarget(navigation[1].Target));
        Assert.Equal(new EpubNavigationDestination(0, 0, 0, 2, 2),
            layout.ResolveNavigationTarget(Assert.Single(navigation[0].Children).Target));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("EPUB/FIRST.xhtml")]
    [InlineData("epub/first.xhtml")]
    [InlineData("EPUB/missing.xhtml")]
    [InlineData("EPUB/first%2Exhtml")]
    [InlineData("EPUB/./first.xhtml")]
    [InlineData("EPUB/../EPUB/first.xhtml")]
    [InlineData("https://example.invalid/first.xhtml")]
    [InlineData("file:///C:/first.xhtml")]
    [InlineData("//example.invalid/first.xhtml")]
    [InlineData("EPUB\\first.xhtml")]
    [InlineData("EPUB/first.xhtml?query")]
    [InlineData("EPUB/first.xhtml\0")]
    public void RawTargetsRequireExactResolvedPackageIdentity(string target)
    {
        var layout = EpubDocument.Open(Book()).CreateLayout(Options());
        Assert.Throws<ArgumentException>(() => layout.ResolveNavigationTarget(target));
    }

    [Fact]
    public void NavigationResolvesEncodedReferencesBeforeNativeTargetLookup()
    {
        var book = EpubDocument.Open(Book("<li><a href='../first%2Exhtml'>First</a></li>"));
        string target = Assert.Single(book.ReadNavigation()).Target;
        Assert.Equal("EPUB/first.xhtml", target);
        Assert.Equal(new EpubNavigationDestination(0, 0, 0, 2, 2),
            book.CreateLayout(Options()).ResolveNavigationTarget(target));
    }

    [Fact]
    public void ResolvedChapterTargetsAtTheExactLengthLimitAreAccepted()
    {
        var book = EpubDocument.Open(Book(firstHref: new string('a', 4085) + ".xhtml"));
        string target = book.ReadNavigation()[1].Target;
        Assert.Equal(4096, target.Length);
        Assert.Equal(new EpubNavigationDestination(0, 0, 0, 2, 2),
            book.CreateLayout(Options()).ResolveNavigationTarget(target));
    }

    private static EpubLayoutOptions Options() => new(16, 23,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true)))
    {
        FontSize = 10, Margin = 2
    };

    internal static byte[] Book(string? entries = null, string? spine = null, string firstBody = "<p>A A</p>",
        bool ncx = false, string firstHref = "first.xhtml")
    {
        entries ??= $"<li><a href='../later.xhtml'>Later</a></li><li><a href='../{firstHref}'>First</a></li>";
        spine ??= "<itemref idref='first'/><itemref idref='later'/>";
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='EPUB/book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>
                """);
            Add("EPUB/book.opf", $"""
                <package xmlns='http://www.idpf.org/2007/opf' version='{(ncx ? "2.0" : "3.0")}' unique-identifier='id'>
                  <metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:test:navigation-destination</dc:identifier><dc:title>Navigation</dc:title><dc:language>en</dc:language><meta property='dcterms:modified'>2026-11-04T00:00:00Z</meta></metadata>
                  <manifest><item id='later' href='later.xhtml' media-type='application/xhtml+xml'/><item id='first' href='{firstHref}' media-type='application/xhtml+xml'/><item id='extra' href='extra.xhtml' media-type='application/xhtml+xml'/><item id='nav' href='toc/nav.xhtml' media-type='application/xhtml+xml' properties='nav'/>{(ncx ? "<item id='ncx' href='toc/nav.ncx' media-type='application/x-dtbncx+xml'/>" : "")}</manifest>
                  <spine{(ncx ? " toc='ncx'" : "")}>{spine}</spine>
                </package>
                """);
            Add("EPUB/" + firstHref, Html(firstBody));
            Add("EPUB/later.xhtml", Html("<p>AA</p>"));
            Add("EPUB/extra.xhtml", Html("<p>A</p>"));
            if (ncx) Add("EPUB/toc/nav.ncx", $"""
                <ncx xmlns='http://www.daisy.org/z3986/2005/ncx/'><navMap><navPoint><navLabel><text>Later</text></navLabel><content src='../later.xhtml'/></navPoint><navPoint><navLabel><text>First</text></navLabel><content src='../{firstHref}'/></navPoint></navMap></ncx>
                """);
            Add("EPUB/toc/nav.xhtml", $"""
                <html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'><head><title>Contents</title></head><body><nav epub:type='toc'><ol>{entries}</ol></nav></body></html>
                """);
            void Add(string path, string text, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }

    private static string Html(string body) =>
        "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Chapter</title></head><body>" + body + "</body></html>";
}
