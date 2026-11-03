using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed partial class EpubDocumentTests
{
    // W3C EPUB 3.3 sections 4.2.6.3.1, 5.6 and 5.7 define these independent fixtures.
    private const string Package = """
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>Reading order</dc:title><dc:identifier id="id">urn:test:book</dc:identifier><dc:language>ar</dc:language></metadata>
          <manifest><item id="second" href="two.xhtml" media-type="application/xhtml+xml"/><item id="first" href="one.xhtml" media-type="application/xhtml+xml"/></manifest>
          <spine page-progression-direction="rtl"><itemref idref="first"/><itemref idref="second"/></spine>
        </package>
        """;

    [Fact]
    public void OpensPackageAndPreservesSpineOrderAndRtl()
    {
        var book = EpubDocument.Open(Book());
        Assert.Equal("Reading order", book.Metadata.Title);
        Assert.Equal("rtl", book.PageProgressionDirection);
        Assert.Equal(new[] { "EPUB/one.xhtml", "EPUB/two.xhtml" }, book.Spine.Select(x => x.Item.Href));
    }

    [Theory]
    [InlineData("../outside.xhtml")]
    [InlineData("/absolute.xhtml")]
    [InlineData("EPUB\\outside.xhtml")]
    [InlineData("EPUB/%2e%2e/outside.xhtml")]
    public void RejectsUnsafeArchivePaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(Package, (path, "unsafe"))));
    }

    [Theory]
    [InlineData("EPUB/one.xhtml")]
    [InlineData("epub/ONE.xhtml")]
    public void RejectsAmbiguousDuplicateResources(string path)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(Package, (path, "duplicate"))));
    }

    [Fact]
    public void RejectsEncryptedPublication()
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(Package,
            ("META-INF/encryption.xml", "<encryption xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"/>"))));
    }

    [Theory]
    [InlineData("<!DOCTYPE package [<!ENTITY title 'expanded'>]>")]
    [InlineData("<!DOCTYPE package SYSTEM 'https://example.invalid/epub.dtd'>")]
    public void RejectsDtdAndEntitiesAsInvalidPublication(string declaration)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(declaration + Package)));
    }

    [Fact]
    public void RejectsOversizedExpandedResource()
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(Package,
            ("EPUB/large.bin", new string('x', 8 * 1024 * 1024 + 1)))));
    }

    [Fact]
    public void RejectsExcessiveTotalExpandedSize()
    {
        var content = new string('x', 8 * 1024 * 1024);
        var entries = Enumerable.Range(0, 9).Select(i => ($"EPUB/large{i}.bin", content)).ToArray();
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(Package, entries)));
    }

    [Fact]
    public void RejectsExcessiveResourceCount()
    {
        var entries = Enumerable.Range(0, 4096).Select(i => ($"EPUB/{i}.txt", "x")).ToArray();
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(Package, entries)));
    }

    [Fact]
    public void RejectsOversizedInputBeforeOpeningArchive()
    {
        var book = Book();
        var oversized = new byte[64 * 1024 * 1024 + 1];
        book.CopyTo(oversized, 0);
        var exception = Assert.Throws<InvalidDataException>(() => EpubDocument.Open(oversized));
        Assert.Equal("EPUB exceeds the input size limit.", exception.Message);
    }

    [Theory]
    [InlineData("idref=\"first\"", "idref=\"missing\"")]
    [InlineData("id=\"second\"", "id=\"first\"")]
    [InlineData("href=\"one.xhtml\"", "href=\"missing.xhtml\"")]
    [InlineData("href=\"one.xhtml\"", "href=\"https://example.invalid/book.xhtml\"")]
    [InlineData("href=\"one.xhtml\"", "href=\"../../outside.xhtml\"")]
    [InlineData("version=\"3.0\"", "version=\"9.0\"")]
    public void RejectsInvalidPackageReferences(string oldValue, string newValue)
    {
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(Package.Replace(oldValue, newValue))));
    }

    [Theory]
    [InlineData("mimetype", "text/plain")]
    [InlineData("mimetype", "application/epub+zip ")]
    [InlineData("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles/></container>")]
    [InlineData("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='../book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>")]
    public void RejectsInvalidContainer(string path, string content)
    {
        using var data = new MemoryStream();
        data.Write(Book());
        using (var zip = new ZipArchive(data, ZipArchiveMode.Update, true))
        {
            zip.GetEntry(path)!.Delete();
            using var stream = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(data.ToArray()));
    }

    [Fact]
    public void UsesPackageUniqueIdentifierReference()
    {
        var package = Package.Replace("<dc:identifier id=\"id\">", "<dc:identifier id=\"other\">wrong</dc:identifier><dc:identifier id=\"id\">");
        Assert.Equal("urn:test:book", EpubDocument.Open(Book(package)).Metadata.Identifier);
    }

    [Fact]
    public void PreservesEpub2ManifestAndNonlinearSpine()
    {
        var package = Package.Replace("version=\"3.0\"", "version=\"2.0\"")
            .Replace("<itemref idref=\"second\"/>", "<itemref idref=\"second\" linear=\"no\"/>");
        var book = EpubDocument.Open(Book(package));
        Assert.Equal("2.0", book.Version);
        Assert.False(book.Spine[1].IsLinear);
        Assert.Equal("reflowable", book.Layout);
    }

    [Fact]
    public void PreservesFixedLayoutAndNavigationDeclaration()
    {
        var package = Package.Replace("</metadata>", "<meta property=\"rendition:layout\">pre-paginated</meta></metadata>")
            .Replace("id=\"second\" href", "id=\"second\" properties=\"nav\" href")
            .Replace("<itemref idref=\"first\"/>", "<itemref idref=\"first\" properties=\"rendition:page-spread-right\"/>");
        var book = EpubDocument.Open(Book(package));
        Assert.Equal("pre-paginated", book.Layout);
        Assert.Equal("nav", book.Manifest[0].Properties);
        Assert.Equal("rendition:page-spread-right", book.Spine[0].Properties);
    }

    [Fact]
    public void ResourceReadsAreIsolatedFromCallerMutations()
    {
        var source = Book();
        var book = EpubDocument.Open(source);
        Array.Clear(source);
        var resource = book.ReadResource("EPUB/one.xhtml");
        Array.Clear(resource);
        Assert.Equal("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>One</body></html>",
            Encoding.UTF8.GetString(book.ReadResource("EPUB/one.xhtml")));
        Assert.Throws<NotSupportedException>(() => ((IList<EpubManifestItem>)book.Manifest).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<EpubSpineItem>)book.Spine).Clear());
    }

    [Fact]
    public void HonorsCancellationBeforeOpening()
    {
        Assert.Throws<OperationCanceledException>(() => EpubDocument.Open(Book(), new CancellationToken(true)));
    }

    [Fact]
    public void ResolvesInternalRelativeReferences()
    {
        var package = Package.Replace("href=\"one.xhtml\"", "href=\"../EPUB/one%2Exhtml\"");
        Assert.Equal("EPUB/one.xhtml", EpubDocument.Open(Book(package)).Spine[0].Item.Href);
    }

    [Fact]
    public void RejectsZipEncryptionFlag()
    {
        var bytes = Book();
        bytes[6] |= 1;
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(bytes));
    }

    private static byte[] Book(string package = Package, params (string Path, string Content)[] extra)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", package);
            Add("EPUB/one.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>One</body></html>");
            Add("EPUB/two.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>Two</body></html>");
            foreach (var entry in extra) Add(entry.Path, entry.Content);

            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
