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
    [InlineData("EPUB/book.opf")]
    // An entry that names no usable packaged resource protects nothing the reader opens, so the
    // book opens; nothing is decoded through an unsafe or missing path and the font stays as stored.
    public void UnsafeMissingOrPackageTargetsAreIgnored(string uri)
    {
        var book = EpubDocument.Open(Book([0], Encryption(uri)));
        Assert.Equal(new byte[] { 0 }, book.ReadResource("EPUB/fonts/book.ttf"));
        Assert.Equal("Reading", book.ReadText(0));
    }

    [Fact]
    public void AnEncryptedChapterIsRefusedOnlyWhereItIsRead()
    {
        var book = EpubDocument.Open(Book([0], Encryption("EPUB/chapter.xhtml")));
        Assert.Contains("encrypted", Assert.Throws<NotSupportedException>(() => book.ReadText(0)).Message);
    }

    // Real ciphers and unknown algorithms leave the resource encrypted; it is refused where used, not at Open.
    // Adobe obfuscation needs a UUID identifier, so this font stays scrambled and text uses installed fonts.
    [Theory]
    [InlineData("http://www.w3.org/2001/04/xmlenc#aes128-cbc")]
    [InlineData("http://ns.adobe.com/pdf/enc#RC")]
    [InlineData("")]
    public void UnsupportedEncryptionAlgorithmsLeaveTheResourceUntouchedAndTheBookOpen(string algorithm)
    {
        var book = EpubDocument.Open(Book([0], Encryption(algorithm: algorithm)));
        Assert.Equal(new byte[] { 0 }, book.ReadResource("EPUB/fonts/book.ttf"));
        Assert.Equal("Reading", book.ReadText(0));
    }

    [Fact]
    public void AdobeObfuscationXorsTheFirst1024BytesWithTheUuidIdentifier()
    {
        // urn:uuid:00112233-4455-6677-8899-aabbccddeeff gives the key bytes 00 11 22 ... ff.
        byte[] font = new byte[1026];
        var book = EpubDocument.Open(Book(font, Encryption(algorithm: "http://ns.adobe.com/pdf/enc#RC"),
            identifier: "urn:uuid:00112233-4455-6677-8899-aabbccddeeff"));
        byte[] restored = book.ReadResource("EPUB/fonts/book.ttf");
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33 }, restored[..4]);
        Assert.Equal(0xff, restored[1023]);
        Assert.Equal(new byte[] { 0, 0 }, restored[1024..]);
    }

    [Theory]
    [InlineData("application/xhtml+xml")]
    [InlineData("text/css")]
    [InlineData("application/octet-stream")]
    public void ObfuscatedNonFontTargetsStayEncrypted(string mediaType)
    {
        Assert.Equal(new byte[] { 0 }, EpubDocument.Open(Book([0], mediaType: mediaType)).ReadResource("EPUB/fonts/book.ttf"));
    }

    // Legacy font media types written by older InDesign and Calibre releases are restored like the core types.
    [Theory]
    [InlineData("application/x-font-ttf")]
    [InlineData("application/x-font-otf")]
    [InlineData("font/sfnt")]
    public void LegacyFontMediaTypesAreRestored(string mediaType)
    {
        // SHA-1("abc") begins with A9, the key for the whitespace-stripped identifier "abc".
        Assert.Equal(new byte[] { 0xA9 }, EpubDocument.Open(Book([0], mediaType: mediaType)).ReadResource("EPUB/fonts/book.ttf"));
    }

    [Theory]
    [InlineData("<encryption/>")]
    [InlineData("<encryption")]
    [InlineData("<!DOCTYPE encryption SYSTEM 'https://example.invalid/encryption.dtd'><encryption/>")]
    public void RejectsMalformedEncryptionDocuments(string encryption)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book([0], encryption)));
    }

    [Theory]
    [InlineData("<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container'/>")]
    [InlineData("<encryption xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><EncryptedData/></encryption>")]
    public void EncryptionDocumentsThatProtectNothingOpenTheBook(string encryption)
    {
        Assert.Equal("Reading", EpubDocument.Open(Book([0], encryption)).ReadText(0));
    }

    // Key material or transforms mean a real cipher: the font is left encrypted. An unrelated
    // EncryptedKey elsewhere does not change a plain obfuscation entry.
    [Theory]
    [InlineData("<enc:KeyInfo/>", "</enc:EncryptedData>", 0x00)]
    [InlineData("<enc:EncryptedKey/>", "</encryption>", 0xA9)]
    [InlineData("<enc:Transforms/>", "</enc:CipherReference>", 0x00)]
    public void AdditionalEncryptionOperationsMarkARealCipher(string additional, string before, byte expected)
    {
        string encryption = Encryption().Replace("/></enc:CipherData>", "></enc:CipherReference></enc:CipherData>");
        var book = EpubDocument.Open(Book([0], encryption.Replace(before, additional + before)));
        Assert.Equal(new[] { expected }, book.ReadResource("EPUB/fonts/book.ttf"));
    }

    [Fact]
    public void DuplicateObfuscationOfTheSameResourceIsAppliedOnce()
    {
        var entry = """
            <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.idpf.org/2008/embedding"/><enc:CipherData><enc:CipherReference URI="EPUB/fonts/%62ook.ttf"/></enc:CipherData></enc:EncryptedData>
            """;
        var book = EpubDocument.Open(Book([0], Encryption().Replace("</encryption>", entry + "</encryption>")));
        Assert.Equal(new byte[] { 0xA9 }, book.ReadResource("EPUB/fonts/book.ttf"));
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
