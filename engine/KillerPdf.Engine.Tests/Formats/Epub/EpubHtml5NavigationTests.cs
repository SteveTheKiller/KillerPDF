using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    [Fact]
    public void ReadsHtml5NavigationWithNestedUnicodeLabelsAndPreservesResource()
    {
        string navigation = "<!DOCTYPE html>" + Navigation.Replace("First <em>chapter</em>", "café <em>世界</em>")
            .Replace("Detail", "e&#769; 😀");
        var book = NavigationBook(navigation);

        var entries = book.ReadNavigation();

        Assert.Equal(new[] { "café 世界", "Second chapter" }, entries.Select(x => x.Label));
        Assert.Equal("EPUB/one.xhtml#start", entries[0].Target);
        var child = Assert.Single(entries[0].Children);
        Assert.Equal("e\u0301 😀", child.Label);
        Assert.Equal("EPUB/two.xhtml#detail", child.Target);
        Assert.Equal(Encoding.UTF8.GetBytes(navigation), book.ReadResource("EPUB/nav.xhtml"));
    }

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16le", false)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", false)]
    [InlineData("utf-16be", true)]
    public void ReadsHtml5NavigationInXmlEncodingsWithoutChangingResource(string name, bool bom)
    {
        Encoding encoding = name switch
        {
            "utf-16le" => new UnicodeEncoding(false, bom),
            "utf-16be" => new UnicodeEncoding(true, bom),
            _ => new UTF8Encoding(bom)
        };
        string declaration = "<?xml version='1.0' encoding='" + (name == "utf-8" ? "UTF-8" : "UTF-16") + "'?>";
        string navigation = declaration + "<!-- café 世界 --><!DOCTYPE\t\r\nhtml \t\r\n>"
            + Navigation.Replace("Detail", "café 世界 e&#769; 😀");
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes(navigation)];
        var book = Html5NavigationBook(original);

        var entries = book.ReadNavigation();

        Assert.Equal("First chapter", entries[0].Label);
        Assert.Equal("café 世界 e\u0301 😀", Assert.Single(entries[0].Children).Label);
        Assert.Equal("EPUB/two.xhtml#detail", Assert.Single(entries[0].Children).Target);
        Assert.Equal(original, book.ReadResource("EPUB/nav.xhtml"));
    }

    [Theory]
    [InlineData("<!DOCTYPE html []>")]
    [InlineData("<!DOCTYPE html [<!ENTITY label 'expanded'>]>")]
    [InlineData("<!DOCTYPE html [<!ENTITY label SYSTEM 'file:///C:/private.txt'>]>")]
    [InlineData("<!DOCTYPE html [<!ENTITY % external SYSTEM 'https://example.invalid/parameter.dtd'>%external;]>")]
    [InlineData("<!DOCTYPE html SYSTEM 'https://example.invalid/nav.dtd'>")]
    [InlineData("<!DOCTYPE html SYSTEM 'file:///C:/private.dtd'>")]
    [InlineData("<!DOCTYPE html SYSTEM 'about:legacy-compat'>")]
    [InlineData("<!DOCTYPE html PUBLIC '-//OTHER//DTD XHTML//EN' 'https://example.invalid/nav.dtd'>")]
    [InlineData("<!DOCTYPE html><!DOCTYPE html>")]
    [InlineData("<!doctype html>")]
    [InlineData("<!DOCTYPE HTML>")]
    [InlineData("<!DOCTYPE html\u00a0>")]
    [InlineData("<!DOCTYPE htmlx>")]
    public void RejectsUnsafeOrNoncanonicalDeclarationsInHtml5Navigation(string declaration)
    {
        string navigation = declaration + Navigation;
        var book = NavigationBook(navigation);

        Assert.Throws<InvalidDataException>(() => book.ReadNavigation());
        Assert.Equal(Encoding.UTF8.GetBytes(navigation), book.ReadResource("EPUB/nav.xhtml"));
    }

    [Theory]
    [InlineData("https://example.invalid/chapter.xhtml")]
    [InlineData("file:///C:/chapter.xhtml")]
    [InlineData("javascript:execute()")]
    [InlineData("../../outside.xhtml")]
    [InlineData("missing.xhtml#target")]
    [InlineData("one.xhtml#%0Aunsafe")]
    public void Html5NavigationLeavesUnsafeOrMissingDestinationsUnlinked(string target)
    {
        var book = NavigationBook("<!DOCTYPE html>" + NavList("<li><a href='" + target + "'>Chapter</a></li>"));

        var entry = Assert.Single(book.ReadNavigation());
        Assert.Equal(("Chapter", ""), (entry.Label, entry.Target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.1//EN' 'http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd'>")]
    public void ReadsExistingXhtmlNavigationDeclarationForms(string declaration)
    {
        var entries = NavigationBook(declaration + Navigation).ReadNavigation();

        Assert.Equal("First chapter", entries[0].Label);
        Assert.Equal("EPUB/two.xhtml#detail", Assert.Single(entries[0].Children).Target);
    }

    [Fact]
    public void ExplicitlyConvertsBookWithHtml5NavigationAndIndependentlyReopensPdf()
    {
        byte[] navigation = Encoding.UTF8.GetBytes("<!DOCTYPE html>" + Navigation);
        var book = Html5NavigationBook(navigation,
            ("EPUB/one.xhtml", "<!DOCTYPE html><html xmlns='http://www.w3.org/1999/xhtml'><body id='start'>One</body></html>"),
            ("EPUB/two.xhtml", "<!DOCTYPE html><html xmlns='http://www.w3.org/1999/xhtml'><body id='detail'>Two</body></html>"));
        using Stream fontStream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        fontStream.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        var entries = book.ReadNavigation();

        byte[] converted = book.ConvertToPdf(new EpubPdfConversionOptions(font));
        var reopened = PdfDocument.Open(converted);
        var reader = new PdfPageContentReader(reopened);
        string text = string.Concat(Enumerable.Range(0, reader.PageCount)
            .SelectMany(index => reader.Read(index).Letters).Select(letter => letter.Value));

        Assert.Contains("One", text);
        Assert.Contains("Two", text);
        Assert.Equal("First chapter", entries[0].Label);
        Assert.Equal(navigation, book.ReadResource("EPUB/nav.xhtml"));
        var bookmarks = PdfBookmarkReader.Read(reopened);
        Assert.Equal(new[] { "First chapter", "Second chapter" }, bookmarks.Select(item => item.Title));
        Assert.Equal(0, bookmarks[0].DestinationPageIndex);
        Assert.Equal(1, Assert.Single(bookmarks[0].Children).DestinationPageIndex);
    }

    private static EpubDocument Html5NavigationBook(byte[] navigation,
        params (string Path, string Content)[] chapters)
    {
        string package = Package.Replace("page-progression-direction=\"rtl\"", "page-progression-direction=\"ltr\"")
            .Replace("</manifest>", "<item id='nav' href='nav.xhtml' media-type='application/xhtml+xml' properties='nav'/></manifest>");
        using var output = new MemoryStream();
        output.Write(Book(package));
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            using (var resource = zip.CreateEntry("EPUB/nav.xhtml").Open())
                resource.Write(navigation);
            foreach (var chapter in chapters)
            {
                zip.GetEntry(chapter.Path)!.Delete();
                using var resource = zip.CreateEntry(chapter.Path).Open();
                resource.Write(Encoding.UTF8.GetBytes(chapter.Content));
            }
        }
        return EpubDocument.Open(output.ToArray());
    }
}
