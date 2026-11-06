using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    [Fact]
    public void ReadsUnscriptedHtml5XhtmlDeclarationWithoutChangingResource()
    {
        const string content = "<!DOCTYPE html><html xmlns='http://www.w3.org/1999/xhtml'><head/><body><p>Hello 世界 &amp; café</p></body></html>";
        var package = Package.Replace("href=\"one.xhtml\"", "href=\"html5.xhtml\"");
        var book = EpubDocument.Open(Book(package, ("EPUB/html5.xhtml", content)));

        Assert.Equal("Hello 世界 & café", book.ReadText(0));
        Assert.Equal(Encoding.UTF8.GetBytes(content), book.ReadResource("EPUB/html5.xhtml"));
    }

    [Fact]
    public void LaysOutAndRendersHtml5XhtmlWithoutChangingOriginalResource()
    {
        byte[] original = Encoding.UTF8.GetBytes("<!DOCTYPE html><html xmlns='http://www.w3.org/1999/xhtml'><head/><body><p>A</p></body></html>");
        var book = Html5Book(original);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });

        Assert.Equal("A", Assert.Single(layout.Pages).Text);
        var frame = layout.RenderPage(0);
        Assert.Equal(30, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(original, book.ReadResource("EPUB/one.xhtml"));
    }

    [Theory]
    [InlineData("utf-8", false)]
    [InlineData("utf-8", true)]
    [InlineData("utf-16le", false)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", false)]
    [InlineData("utf-16be", true)]
    public void ReadsHtml5DeclarationInXmlEncodingsWithoutChangingUnicodeOrResource(string name, bool bom)
    {
        Encoding encoding = name switch
        {
            "utf-16le" => new UnicodeEncoding(false, bom),
            "utf-16be" => new UnicodeEncoding(true, bom),
            _ => new UTF8Encoding(bom)
        };
        string xmlEncoding = name == "utf-8" ? "UTF-8" : "UTF-16";
        string content = "<?xml version='1.0' encoding='" + xmlEncoding + "'?>\n<!-- café 世界 -->\n"
            + "<!DOCTYPE\t\r\nhtml \t\r\n><html xmlns='http://www.w3.org/1999/xhtml'><head/><body><p>café 世界 e&#769; 😀 &amp; tail</p></body></html>";
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
        var book = Html5Book(original);

        Assert.Equal("café 世界 e\u0301 😀 & tail", book.ReadText(0));
        Assert.Equal(original, book.ReadResource("EPUB/one.xhtml"));
    }

    [Theory]
    [InlineData("<!DOCTYPE html []>")]
    [InlineData("<!DOCTYPE html [<!ENTITY text 'expanded'>]>")]
    [InlineData("<!DOCTYPE html [<!ENTITY text SYSTEM 'file:///C:/private.txt'>]>")]
    [InlineData("<!DOCTYPE html [<!ENTITY % external SYSTEM 'https://example.invalid/parameter.dtd'>%external;]>")]
    [InlineData("<!DOCTYPE html SYSTEM 'https://example.invalid/xhtml.dtd'>")]
    [InlineData("<!DOCTYPE html SYSTEM 'file:///C:/private.dtd'>")]
    [InlineData("<!DOCTYPE html SYSTEM 'about:legacy-compat'>")]
    [InlineData("<!DOCTYPE html PUBLIC '-//OTHER//DTD XHTML//EN' 'https://example.invalid/xhtml.dtd'>")]
    [InlineData("<!DOCTYPE html><!DOCTYPE html>")]
    [InlineData("<!doctype html>")]
    [InlineData("<!DOCTYPE HTML>")]
    [InlineData("<!DOCTYPE html\u00a0>")]
    [InlineData("<!DOCTYPE htmlx>")]
    public void RejectsHtml5DeclarationVariantsOutsideTheNoSubsetNoExternalGrammar(string declaration)
    {
        byte[] original = Encoding.UTF8.GetBytes(declaration
            + "<html xmlns='http://www.w3.org/1999/xhtml'><head/><body><p>A</p></body></html>");
        var book = Html5Book(original);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

        Assert.Throws<InvalidDataException>(() => book.ReadText(0));
        Assert.Throws<InvalidDataException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }));
        Assert.Equal(original, book.ReadResource("EPUB/one.xhtml"));
    }

    [Theory]
    [InlineData("<script>execute()</script><p>A</p>")]
    [InlineData("<p onclick='execute()'>A</p>")]
    [InlineData("<p><a href='https://example.invalid/page'>A</a></p>")]
    [InlineData("<p><a href='javascript:execute()'>A</a></p>")]
    public void Html5DeclarationDoesNotAdmitScriptsOrExternalReferences(string body)
    {
        var book = Html5Book(Encoding.UTF8.GetBytes("<!DOCTYPE html><html xmlns='http://www.w3.org/1999/xhtml'><head/><body>"
            + body + "</body></html>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false, includeOutlines: true));

        Assert.Throws<NotSupportedException>(() => book.ReadText(0));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }));
    }

    [Fact]
    public void PreservesHtml5DeclarationTextInCommentsAndCdataAndRejectsUnknownEntities()
    {
        const string content = "<!-- <!DOCTYPE html> -->\n<!DOCTYPE html>"
            + "<html xmlns='http://www.w3.org/1999/xhtml'><head/><body><p><![CDATA[<!DOCTYPE html>]]></p></body></html>";
        byte[] original = Encoding.UTF8.GetBytes(content);
        var book = Html5Book(original);

        Assert.Equal("<!DOCTYPE html>", book.ReadText(0));
        Assert.Equal(original, book.ReadResource("EPUB/one.xhtml"));
        var unknownEntity = Html5Book(Encoding.UTF8.GetBytes(content.Replace("<![CDATA[<!DOCTYPE html>]]>", "&unknown;")));
        Assert.Throws<InvalidDataException>(() => unknownEntity.ReadText(0));
    }

    private static EpubDocument Html5Book(byte[] content)
    {
        var package = Package.Replace("<itemref idref=\"second\"/>", "")
            .Replace("page-progression-direction=\"rtl\"", "page-progression-direction=\"ltr\"");
        using var output = new MemoryStream();
        output.Write(Book(package));
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            zip.GetEntry("EPUB/one.xhtml")!.Delete();
            using var entry = zip.CreateEntry("EPUB/one.xhtml").Open();
            entry.Write(content);
        }
        return EpubDocument.Open(output.ToArray());
    }
}
