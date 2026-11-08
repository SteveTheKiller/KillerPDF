using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    [Fact]
    public void ConversionPreservesNavigationLabelsHierarchyAndPageDestinations()
    {
        byte[] navigation = Encoding.UTF8.GetBytes("<!DOCTYPE html>" + NavList(
            "<li><a href='one.xhtml'>café 世界</a><ol><li><a href='two.xhtml'>Detail</a></li></ol></li>"
            + "<li><a href='two.xhtml'>Second chapter</a></li>"));
        var book = Html5NavigationBook(navigation);

        var pdf = PdfDocument.Open(book.ConvertToPdf(NavigationConversionOptions()));
        var bookmarks = PdfBookmarkReader.Read(pdf);

        Assert.Equal(new[] { "café 世界", "Second chapter" }, bookmarks.Select(item => item.Title));
        Assert.Equal(0, bookmarks[0].DestinationPageIndex);
        Assert.Equal(1, bookmarks[1].DestinationPageIndex);
        var detail = Assert.Single(bookmarks[0].Children);
        Assert.Equal("Detail", detail.Title);
        Assert.Equal(1, detail.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, detail.Destination!.Kind);
        Assert.Equal(new double?[] { 12, 168, null }, detail.Destination.Values);
        Assert.Equal(navigation, book.ReadResource("EPUB/nav.xhtml"));
    }

    [Fact]
    public void ConversionPreservesUnlinkedGroupingHeadingWithoutInventingDestination()
    {
        var book = Html5NavigationBook(Encoding.UTF8.GetBytes(NavList(
            "<li><span>Part one</span><ol><li><a href='two.xhtml'>Chapter two</a></li></ol></li>")));

        var pdf = PdfDocument.Open(book.ConvertToPdf(NavigationConversionOptions()));
        var heading = Assert.Single(PdfBookmarkReader.Read(pdf));

        Assert.Equal("Part one", heading.Title);
        Assert.Null(heading.DestinationPageIndex);
        Assert.Null(heading.Destination);
        Assert.Null(heading.NamedDestination);
        var chapter = Assert.Single(heading.Children);
        Assert.Equal("Chapter two", chapter.Title);
        Assert.Equal(1, chapter.DestinationPageIndex);
    }

    [Fact]
    public void ConversionPreservesAnchorOnLaterPaginatedPage()
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(
            entries: "<li><a href='../first.xhtml#second'>Detail</a></li>",
            firstBody: "<p>A <span id='second'>A</span></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubPdfConversionOptions(font)
        {
            PageWidthPoints = 12, PageHeightPoints = 17.25, MarginPoints = 1.5, FontSizePoints = 7.5
        };

        var pdf = PdfDocument.Open(book.ConvertToPdf(options));
        var bookmark = Assert.Single(PdfBookmarkReader.Read(pdf));

        Assert.Equal("Detail", bookmark.Title);
        Assert.Equal(1, bookmark.DestinationPageIndex);
        Assert.Equal(new double?[] { 1.5, 15.75, null }, bookmark.Destination!.Values);
        var text = new PdfPageContentReader(pdf).Read(1);
        Assert.Equal("A", string.Concat(text.Letters.Select(letter => letter.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversionPreservesEpub2And3NavigationOrderIndependentOfSpine(bool ncx)
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(ncx: ncx));
        var pdf = PdfDocument.Open(book.ConvertToPdf(NavigationConversionOptions()));

        var bookmarks = PdfBookmarkReader.Read(pdf);

        Assert.Equal(new[] { "Later", "First" }, bookmarks.Select(item => item.Title));
        Assert.Equal(new int?[] { 1, 0 }, bookmarks.Select(item => item.DestinationPageIndex));
    }

    [Fact]
    public void ConversionPointsABookmarkWithAMissingAnchorAtItsChapter()
    {
        var book = EpubDocument.Open(EpubNavigationDestinationTests.Book(
            entries: "<li><a href='../first.xhtml#missing'>Missing</a></li>"));
        var pdf = PdfDocument.Open(book.ConvertToPdf(NavigationConversionOptions()));

        var bookmark = Assert.Single(PdfBookmarkReader.Read(pdf));

        Assert.Equal("Missing", bookmark.Title);
        Assert.Equal(book.CreateLayout(new EpubLayoutOptions(100, 100, NavigationConversionOptions().DefaultFont))
            .ResolveNavigationTarget(book.Spine.Single(entry => entry.Item.Href.EndsWith("first.xhtml")).Item.Href).PageIndex,
            bookmark.DestinationPageIndex);
    }

    private static EpubPdfConversionOptions NavigationConversionOptions()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new EpubPdfConversionOptions(TrueTypeFont.Load(bytes.ToArray()))
        {
            PageWidthPoints = 240, PageHeightPoints = 180, MarginPoints = 12, FontSizePoints = 12
        };
    }
}
