using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    private const string CanonicalNcxDoctype = "<!DOCTYPE ncx PUBLIC \"-//NISO//DTD ncx 2005-1//EN\" \"http://www.daisy.org/z3986/2005/ncx-2005-1.dtd\">";
    private const string CanonicalNcxBody = """
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <head><meta name="dtb:uid" content="urn:test:book"/></head>
          <docTitle><text>Reading order</text></docTitle>
          <navMap><navPoint id="chapter" playOrder="1"><navLabel><text>First &amp; second</text></navLabel><content src="one.xhtml#start"/></navPoint></navMap>
        </ncx>
        """;

    [Fact]
    public void ReadsStandardEpub2NcxExternalDoctypeWithoutRequiringDtdContent()
    {
        var book = NcxBook("<?xml version='1.0' encoding='UTF-8'?>\n" + CanonicalNcxDoctype + CanonicalNcxBody);
        var entry = Assert.Single(book.ReadNavigation());
        Assert.Equal("First & second", entry.Label);
        Assert.Equal("EPUB/one.xhtml#start", entry.Target);
    }
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public void ReadsCanonicalNcxSystemIdentifierInSupportedXmlEncodings(string encodingName)
    {
        Encoding encoding = encodingName switch
        {
            "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => new UTF8Encoding(true)
        };
        string declaration = encodingName == "utf-8" ? "UTF-8" : "UTF-16";
        var xml = "<?xml version='1.0' encoding='" + declaration + "'?>\n<!-- prolog comment -->\n" +
            "<!DOCTYPE ncx SYSTEM\n'http://www.daisy.org/z3986/2005/ncx-2005-1.dtd'>" + CanonicalNcxBody;
        var package = Package.Replace("version=\"3.0\"", "version=\"2.0\"")
            .Replace("</manifest>", "<item id='toc' href='toc.ncx' media-type='application/x-dtbncx+xml'/></manifest>")
            .Replace("<spine ", "<spine toc='toc' ");
        using var output = new MemoryStream();
        output.Write(Book(package, ("EPUB/toc.ncx", "placeholder")));
        using (var zip = new ZipArchive(output, ZipArchiveMode.Update, true))
        {
            zip.GetEntry("EPUB/toc.ncx")!.Delete();
            using var entry = zip.CreateEntry("EPUB/toc.ncx").Open();
            entry.Write(encoding.GetPreamble());
            entry.Write(encoding.GetBytes(xml));
        }
        var entryResult = Assert.Single(EpubDocument.Open(output.ToArray()).ReadNavigation());
        Assert.Equal("First & second", entryResult.Label);
        Assert.Equal("EPUB/one.xhtml#start", entryResult.Target);
    }
    [Theory]
    [InlineData("")]
    [InlineData("<!ENTITY label 'expanded'>")]
    [InlineData("<!ENTITY label SYSTEM 'file:///C:/private.txt'>")]
    [InlineData("<!ENTITY % external SYSTEM 'https://example.invalid/parameter.dtd'>%external;")]
    [InlineData("<!ENTITY a 'expanded'><!ENTITY b '&a;&a;&a;&a;'><!ENTITY c '&b;&b;&b;&b;'>")]
    public void RejectsNcxInternalSubsetsBeforeTheyCanDefineOrExpandEntities(string subset)
    {
        var declaration = CanonicalNcxDoctype[..^1] + " [" + subset + "]>";
        Assert.Throws<InvalidDataException>(() => NcxBook(declaration + CanonicalNcxBody).ReadNavigation());
    }

    [Theory]
    [InlineData("<!DOCTYPE ncx SYSTEM 'https://example.invalid/unknown.dtd'>")]
    [InlineData("<!DOCTYPE ncx SYSTEM 'file:///C:/private.dtd'>")]
    [InlineData("<!DOCTYPE ncx PUBLIC '-//OTHER//DTD ncx//EN' 'http://www.daisy.org/z3986/2005/ncx-2005-1.dtd'>")]
    [InlineData("<!DOCTYPE html PUBLIC '-//NISO//DTD ncx 2005-1//EN' 'http://www.daisy.org/z3986/2005/ncx-2005-1.dtd'>")]
    public void RejectsUnrecognizedNcxExternalDeclarations(string declaration)
    {
        Assert.Throws<InvalidDataException>(() => NcxBook(declaration + CanonicalNcxBody).ReadNavigation());
    }

    [Fact]
    public void RejectsUndeclaredEntityReferencesAndRepeatedDoctypes()
    {
        var entities = CanonicalNcxBody.Replace("First &amp; second", "&unknown;");
        Assert.Throws<InvalidDataException>(() => NcxBook(CanonicalNcxDoctype + entities).ReadNavigation());
        Assert.Throws<InvalidDataException>(() => NcxBook(CanonicalNcxDoctype + CanonicalNcxDoctype + CanonicalNcxBody).ReadNavigation());
    }

    [Fact]
    public void PreservesDoctypeTextInsideCommentsAndCdata()
    {
        var body = CanonicalNcxBody.Replace("First &amp; second", "<![CDATA[" + CanonicalNcxDoctype + "]]>");
        var xml = "<!-- café " + CanonicalNcxDoctype + " -->\n" + CanonicalNcxDoctype + body;
        Assert.Equal(CanonicalNcxDoctype, Assert.Single(NcxBook(xml).ReadNavigation()).Label);
    }

    [Fact]
    public void TheNcxDoctypeIsRefusedInThePackageAndDroppedUnreadInXhtml()
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(CanonicalNcxDoctype + Package)));
        // An XHTML document drops any external-only declaration without reading it (EP-BF-015).
        Assert.NotEmpty(NavigationBook(CanonicalNcxDoctype + Navigation).ReadNavigation());
    }
}
