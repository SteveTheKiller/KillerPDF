using System.IO.Compression;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubPackageDegradeTests
{
    [Fact]
    public void OversizedChapterKeepsItsOutlineTargetAndResolvesToItsDiagnosisPage()
    {
        var source = new Book { Chapters = { Chapter("<p>" + new string('A', 9 * 1024 * 1024) + "</p>"), Chapter("<p>A</p>") } };
        var book = EpubDocument.Open(source.Build());

        string target = Assert.Single(book.ReadNavigation()).Target;

        Assert.Equal("OEBPS/text/c0.xhtml", target);
        var layout = book.CreateLayout(Options());
        Assert.Equal(0, layout.ResolveNavigationTarget(target).PageIndex);
        Assert.Equal(0, layout.ResolveNavigationTarget(target + "#unavailable-anchor").PageIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedSpineChapterKeepsItsPlaceAndObeysTheIsolationPolicy(bool isolate)
    {
        var source = new Book { Chapters = { Chapter("<p>" + new string('A', 9 * 1024 * 1024) + "</p>"), Chapter("<p>A</p>") } };
        var book = EpubDocument.Open(source.Build());
        var options = new EpubLayoutOptions(400, 600, Options().DefaultFont) { IsolateChapterFailures = isolate };
        var diagnostics = new List<EpubChapterDiagnostic>();
        var conversion = new EpubPdfConversionOptions(Options().DefaultFont)
        { IsolateChapterFailures = isolate, DiagnosticReported = diagnostics.Add };
        Assert.Equal(2, book.Spine.Count);
        Assert.Contains("OEBPS/text/c0.xhtml", Assert.Throws<InvalidDataException>(() => book.ReadText(0)).Message);
        if (!isolate)
        {
            Assert.Contains("OEBPS/text/c0.xhtml", Assert.Throws<InvalidDataException>(() => book.CreateLayout(options)).Message);
            Assert.Contains("OEBPS/text/c0.xhtml", Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(conversion)).Message);
            return;
        }

        var layout = book.CreateLayout(options);
        Assert.Equal(2, layout.Pages.Count);
        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal("OEBPS/text/c0.xhtml", diagnostic.Href);
        Assert.Equal(EpubOmissionReason.LimitExceeded, diagnostic.Reason);
        Assert.Equal("A", layout.Pages[1].Text.Trim());
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(conversion)));
        Assert.Equal(2, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
        var reported = Assert.Single(diagnostics);
        Assert.Equal("OEBPS/text/c0.xhtml", reported.Href);
        Assert.Contains(reported.Href, reported.Message);
        Assert.Equal(EpubOmissionReason.LimitExceeded, reported.Reason);
    }

    [Theory]
    [InlineData("META-INF/container.xml")]
    [InlineData("OEBPS/content.opf")]
    public void OversizedRequiredPackageDocumentsStillRefuseOpening(string path)
    {
        byte[] source = new Book { Chapters = { Chapter("<p>A</p>") } }.Build();
        using var data = new MemoryStream();
        data.Write(source);
        using (var zip = new ZipArchive(data, ZipArchiveMode.Update, true))
        {
            zip.GetEntry(path)!.Delete();
            using var entry = zip.CreateEntry(path).Open();
            entry.Write(new byte[9 * 1024 * 1024]);
        }
        Assert.Contains(path, Assert.Throws<InvalidDataException>(() => EpubDocument.Open(data.ToArray())).Message);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("name%25.dat")]
    [InlineData("name:odd.dat")]
    public void UnusableOptionalNamesDoNotPreventOpeningOrConversion(string name)
    {
        var source = new Book
        {
            Chapters = { Chapter("<p>A</p>") },
            ExtraManifest = name == "remote" ? "<item id='remote' href='https://example.invalid/font.ttf' media-type='font/ttf'/>" : ""
        };
        if (name != "remote") source.Resources.Add((name, [1, 2, 3]));

        var book = EpubDocument.Open(source.Build());

        Assert.Single(book.Spine);
        Assert.Empty(book.CreateLayout(Options()).ChapterDiagnostics);
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }

    [Fact]
    public void UnusedOversizedResourceDoesNotPreventOpeningOrConversion()
    {
        var source = new Book { Chapters = { Chapter("<p>A</p>") } };
        source.Resources.Add(("unused.dat", new byte[9 * 1024 * 1024]));

        var book = EpubDocument.Open(source.Build());

        Assert.Single(book.Spine);
        Assert.Empty(book.CreateLayout(Options()).ChapterDiagnostics);
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }

    // ZIP structure: the central record gives the name, the local header offset and the compressed size; the local
    // header gives where the data starts. The compressed bytes become garbage, or the method field becomes 14 in both.
    private static byte[] Damage(byte[] zip, string name, bool methodOnly)
    {
        byte[] result = (byte[])zip.Clone();
        for (int at = 0; at + 46 <= result.Length; at++)
        {
            if (BitConverter.ToUInt32(result, at) != 0x02014b50) continue;
            int nameLength = BitConverter.ToUInt16(result, at + 28);
            if (System.Text.Encoding.UTF8.GetString(result, at + 46, nameLength) != name) continue;
            int local = (int)BitConverter.ToUInt32(result, at + 42);
            int data = local + 30 + BitConverter.ToUInt16(result, local + 26) + BitConverter.ToUInt16(result, local + 28);
            if (methodOnly)
            {
                BitConverter.GetBytes((ushort)14).CopyTo(result, at + 10);
                BitConverter.GetBytes((ushort)14).CopyTo(result, local + 8);
            }
            else Array.Fill(result, (byte)0xFF, data, (int)BitConverter.ToUInt32(result, at + 20));
            return result;
        }
        throw new InvalidOperationException("The ZIP has no entry " + name);
    }

    private static Book BookWithImage()
    {
        var source = new Book
        {
            Chapters = { Chapter("<p>A</p>"), Chapter("<p>B</p>") },
            ExtraManifest = "<item id=\"img\" href=\"images/a.png\" media-type=\"image/png\"/>"
        };
        source.Resources.Add(("images/a.png", Enumerable.Repeat((byte)7, 4000).ToArray()));
        return source;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACorruptImageEntryIsLeftOutWithADiagnosticAndTheBookStillConverts(bool methodOnly)
    {
        byte[] damaged = Damage(BookWithImage().Build(), "OEBPS/images/a.png", methodOnly);

        var book = EpubDocument.Open(damaged);

        var reported = new List<EpubChapterDiagnostic>();
        var pdf = book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { IsolateChapterFailures = true, DiagnosticReported = reported.Add });
        Assert.Equal(2, new PdfPageContentReader(PdfDocument.Open(pdf)).PageCount);
        var diagnostic = Assert.Single(reported, item => item.SpineIndex == -1);
        Assert.Equal("OEBPS/images/a.png", diagnostic.Href);
        Assert.Contains("OEBPS/images/a.png", diagnostic.Message);
    }

    [Fact]
    public void ACorruptChapterEntryBecomesThatChaptersNoticePage()
    {
        var source = new Book { Chapters = { Chapter("<p>A</p>"), Chapter("<p>B</p>") } };

        var book = EpubDocument.Open(Damage(source.Build(), "OEBPS/text/c0.xhtml", false));

        Assert.Equal(2, book.Spine.Count);
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal("OEBPS/text/c0.xhtml", diagnostic.Href);
        Assert.Equal("B", layout.Pages[1].Text.Trim());
    }

    [Theory]
    [InlineData("META-INF/container.xml")]
    [InlineData("OEBPS/content.opf")]
    public void ACorruptRequiredPackageDocumentStillRefusesOpening(string path)
    {
        byte[] source = new Book { Chapters = { Chapter("<p>A</p>") } }.Build();

        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Damage(source, path, false)));
    }

    // A damaged or oversized encryption manifest cannot say which resources are encrypted, so reading every resource as
    // plain text would misreport the book; the book is refused instead.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnreadableEncryptionManifestRefusesOpening(bool oversized)
    {
        string manifest = """
            <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
            <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes256-cbc"/>
            <enc:CipherData><enc:CipherReference URI="OEBPS/text/c0.xhtml"/></enc:CipherData></enc:EncryptedData>
            """ + (oversized ? new string(' ', 9 * 1024 * 1024) : "") + "</encryption>";
        byte[] source = new Book { Encryption = manifest, Chapters = { Chapter("<p>A</p>") } }.Build();
        byte[] archive = oversized ? source : Damage(source, "META-INF/encryption.xml", false);

        var error = Assert.Throws<InvalidDataException>(() => EpubDocument.Open(archive));

        Assert.Contains("META-INF/encryption.xml", error.Message);
    }
}
