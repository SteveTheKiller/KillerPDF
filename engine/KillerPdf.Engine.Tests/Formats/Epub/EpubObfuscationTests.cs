using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubObfuscationTests
{
    private const string Algorithm = "http://www.idpf.org/2008/embedding";

    [Fact]
    public void OpensIdpfFontUsingUniqueIdentifierAndLeavesBytesAfter1040Unchanged()
    {
        // SHA-1("abc") is the published FIPS example. These encoded bytes use that
        // literal digest with a TrueType header followed by zero padding.
        var encoded = Convert.FromHexString("A9983E364707817ABA3E25717850C26C9CD0D89D" +
            string.Concat(Enumerable.Repeat("A9993E364706816ABA3E25717850C26C9CD0D89D", 51)) + "1234");
        var expected = new byte[1042];
        Convert.FromHexString("0001000000010010000000000000000000000000").CopyTo(expected, 0);
        expected[1040] = 0x12;
        expected[1041] = 0x34;

        var book = EpubDocument.Open(Book(encoded));

        Assert.Equal(expected, book.ReadResource("EPUB/fonts/book.ttf"));
        var independentCopy = book.ReadResource("EPUB/fonts/book.ttf");
        independentCopy[0] = 0xff;
        Assert.Equal(expected, book.ReadResource("EPUB/fonts/book.ttf"));
    }

    [Theory]
    [InlineData("font/ttf", "2.0")]
    [InlineData("font/otf", "3.0")]
    [InlineData("font/woff", "3.0")]
    [InlineData("font/woff2", "3.0")]
    [InlineData("application/font-sfnt", "2.0")]
    [InlineData("application/font-woff", "3.0")]
    [InlineData("application/vnd.ms-opentype", "2.0")]
    public void RestoresShortFontsForCoreFontMediaTypes(string mediaType, string version)
    {
        var book = EpubDocument.Open(Book(Convert.FromHexString("A9983E364707817ABA3E25717850C26C9CD0D89D"),
            mediaType: mediaType, version: version));
        Assert.Equal(Convert.FromHexString("0001000000010010000000000000000000000000"),
            book.ReadResource("EPUB/fonts/book.ttf"));
    }

    [Fact]
    public void PreservesNonXmlWhitespaceInObfuscationIdentifier()
    {
        // Independently calculated with Python hashlib for UTF-8 "abc\u00a0".
        var book = EpubDocument.Open(Book(Convert.FromHexString("9098DE2B3E91A0AC984C20886E265F72744670A4"),
            identifier: "abc&#xA0;"));
        Assert.Equal(new byte[20], book.ReadResource("EPUB/fonts/book.ttf"));
    }

    [Theory]
    [InlineData("EPUB/fonts/%62ook.ttf")]
    [InlineData("EPUB/./fonts/book.ttf")]
    public void ResolvesFontReferencesFromContainerRoot(string uri)
    {
        var book = EpubDocument.Open(Book(Convert.FromHexString("A999"), Encryption(uri)));
        Assert.Equal(new byte[2], book.ReadResource("EPUB/fonts/book.ttf"));
    }

    [Theory]
    [InlineData("../EPUB/fonts/book.ttf")]
    [InlineData("%2e%2e/EPUB/fonts/book.ttf")]
    [InlineData("/EPUB/fonts/book.ttf")]
    [InlineData("https://example.invalid/font.ttf")]
    [InlineData("EPUB\\fonts\\book.ttf")]
    [InlineData("EPUB/fonts/book.ttf#fragment")]
    [InlineData("EPUB/fonts/book.ttf?query")]
    [InlineData("EPUB/fonts/%2562ook.ttf")]
    [InlineData("EPUB/fonts/missing.ttf")]
    [InlineData("fonts/book.ttf")]
    [InlineData("EPUB/chapter.xhtml")]
    [InlineData("EPUB/book.opf")]
    public void RejectsUnsafeMissingOrNonFontTargets(string uri)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book([0], Encryption(uri))));
    }

    [Theory]
    [InlineData("http://www.w3.org/2001/04/xmlenc#aes128-cbc")]
    [InlineData("http://ns.adobe.com/pdf/enc#RC")]
    [InlineData("")]
    public void RejectsUnsupportedEncryptionAlgorithms(string algorithm)
    {
        var exception = Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book([0], Encryption(algorithm: algorithm))));
        Assert.Equal("Encrypted EPUB publications are unsupported.", exception.Message);
    }

    [Theory]
    [InlineData("application/xhtml+xml")]
    [InlineData("text/css")]
    [InlineData("application/octet-stream")]
    [InlineData("application/x-font-ttf")]
    public void RejectsTargetsWithoutCoreFontManifestType(string mediaType)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book([0], mediaType: mediaType)));
    }

    [Theory]
    [InlineData("<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container'/>")]
    [InlineData("<encryption/>")]
    [InlineData("<encryption")]
    [InlineData("<!DOCTYPE encryption SYSTEM 'https://example.invalid/encryption.dtd'><encryption/>")]
    [InlineData("<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><EncryptedData/></encryption>")]
    public void RejectsMalformedEncryptionDocuments(string encryption)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book([0], encryption)));
    }

    [Theory]
    [InlineData("<enc:KeyInfo/>", "</enc:EncryptedData>")]
    [InlineData("<enc:EncryptedKey/>", "</encryption>")]
    [InlineData("<enc:Transforms/>", "</enc:CipherData>")]
    public void RejectsAdditionalEncryptionOperations(string additional, string before)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book([0],
            Encryption().Replace(before, additional + before))));
    }

    [Fact]
    public void RejectsDuplicateObfuscationOfSameResource()
    {
        var entry = """
            <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/><enc:CipherData><enc:CipherReference URI="EPUB/fonts/%62ook.ttf"/></enc:CipherData></enc:EncryptedData>
            """;
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book([0],
            Encryption().Replace("</encryption>", entry + "</encryption>"))));
    }

    private static byte[] Book(byte[] font, string? encryption = null, string identifier = " a&#x9;b&#xD;c&#xA; ",
        string mediaType = "font/ttf", string version = "3.0")
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
        {
            Add("mimetype", Encoding.UTF8.GetBytes("application/epub+zip"));
            Add("META-INF/container.xml", Encoding.UTF8.GetBytes("""
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """));
            Add("EPUB/book.opf", Encoding.UTF8.GetBytes($"""
                <package xmlns="http://www.idpf.org/2007/opf" version="{version}" unique-identifier="chosen">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="other">wrong</dc:identifier><dc:identifier id="chosen">{identifier}</dc:identifier></metadata>
                  <manifest><item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml"/><item id="font" href="fonts/book.ttf" media-type="{mediaType}"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """));
            Add("EPUB/chapter.xhtml", Encoding.UTF8.GetBytes("<html xmlns='http://www.w3.org/1999/xhtml'><body>Reading</body></html>"));
            Add("EPUB/fonts/book.ttf", font);
            Add("META-INF/encryption.xml", Encoding.UTF8.GetBytes(encryption ?? Encryption()));

            void Add(string name, byte[] bytes)
            {
                using var output = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                output.Write(bytes);
            }
        }
        return data.ToArray();
    }

    private static string Encryption(string uri = "EPUB/fonts/book.ttf", string algorithm = Algorithm) => $"""
        <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
          <enc:EncryptedData><enc:EncryptionMethod Algorithm="{algorithm}"/><enc:CipherData><enc:CipherReference URI="{uri}"/></enc:CipherData></enc:EncryptedData>
        </encryption>
        """;
}
