using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfNativeReadSnapshotTests
{
    [Fact]
    public void NativeEpubSnapshotsRetainReadStateWithoutSharingLogicalOwners()
    {
        byte[] firstBytes = Book();
        byte[] secondBytes = (byte[])firstBytes.Clone();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(16, 14, font) {
            FontSize = 10, Margin = 2, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001) };
        var first = PdfReadSnapshot.OpenEpub(firstBytes, options);
        var second = PdfReadSnapshot.OpenEpub(secondBytes, options);
        var firstOwner = new PdfLogicalDocument(first);
        var secondOwner = new PdfLogicalDocument(second);
        Array.Clear(firstBytes);
        Array.Clear(secondBytes);

        Assert.Equal(PdfDocumentFormat.Epub, first.Format);
        Assert.Equal(PdfDocumentFormat.Epub, second.Format);
        Assert.Matches("^[0-9A-Fa-f]{64}$", first.SourceSha256);
        Assert.Equal(first.SourceSha256, second.SourceSha256);
        Assert.NotEqual(firstOwner.Id, secondOwner.Id);
        Assert.NotEqual(Guid.Empty, firstOwner.Id);
        Assert.Same(first, firstOwner.ReadSnapshot);
        Assert.Same(second, secondOwner.ReadSnapshot);
        Assert.NotEqual(Guid.Empty, first.LayoutIdentity);
        Assert.NotEqual(first.LayoutIdentity, second.LayoutIdentity);
        foreach (var snapshot in new[] { first, second })
        {
            Assert.Equal(1, snapshot.PageCount);
            var geometry = snapshot.GetPageGeometry(0);
            Assert.Equal(16, geometry.Width);
            Assert.Equal(14, geometry.Height);
            Assert.Equal(96, geometry.DpiX);
            Assert.Equal(96, geometry.DpiY);
            Assert.Equal(0, geometry.IntrinsicRotation);
            Assert.Equal(PdfReadPageUnit.CssPixel, geometry.Unit);
            Assert.Equal(12, geometry.WidthPoints);
            Assert.Equal(10.5, geometry.HeightPoints);
            var frame = snapshot.RenderPage(0);
            Assert.Equal(16, frame.Width);
            Assert.Equal(14, frame.Height);
            Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(frame.Pixels[(5 * 16 + 7) * 4 + channel], 0.21413370f, 0.21413373f);
            Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((5 * 16 + 14) * 4, 4).ToArray());
            for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4)
                Assert.Equal(1f, frame.Pixels[alpha]);
        }
    }

    [Fact]
    public void NativeReadRejectsInvalidPagesAndPropagatesCancellation()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 };
        var snapshot = PdfReadSnapshot.OpenEpub(Book(), options);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPageGeometry(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPageGeometry(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfReadSnapshot.OpenEpub(Book(), options, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => snapshot.RenderPage(0, cancellation.Token));
        Assert.Throws<ArgumentNullException>(() => new PdfLogicalDocument(null!));
    }

    [Fact]
    public void ReflowChangesLayoutIdentityWhileSourceAndExistingGeometryStayFixed()
    {
        byte[] bytes = Book();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 };
        var original = PdfReadSnapshot.OpenEpub(bytes, options);
        options = options with { PageWidth = 20 };
        var reflowed = PdfReadSnapshot.OpenEpub(bytes, options);
        Assert.Equal(original.SourceSha256, reflowed.SourceSha256);
        Assert.NotEqual(original.LayoutIdentity, reflowed.LayoutIdentity);
        Assert.Equal(16, original.GetPageGeometry(0).Width);
        Assert.Equal(20, reflowed.GetPageGeometry(0).Width);
        Assert.Equal(16, original.RenderPage(0).Width);
        var firstOwner = new PdfLogicalDocument(original);
        var secondOwner = new PdfLogicalDocument(original);
        Assert.NotEqual(firstOwner.Id, secondOwner.Id);
        Assert.Same(firstOwner.ReadSnapshot, secondOwner.ReadSnapshot);
    }

    [Fact]
    public void NativeReadPreservesTheEpubInputSizeLimit()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 };
        byte[] oversized = new byte[65 * 1024 * 1024];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => PdfReadSnapshot.OpenEpub(oversized, options));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("EPUB exceeds the input size limit.", error.Message);
        Assert.True(allocated < 1024 * 1024, $"Rejected input allocated {allocated} bytes.");
    }

    [Fact]
    public void SourceIdentityDistinguishesBytesWithTheSameVisibleText()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(16, 14, font) { FontSize = 10, Margin = 2 };
        var compact = PdfReadSnapshot.OpenEpub(Book(), options);
        var spaced = PdfReadSnapshot.OpenEpub(Book(" A "), options);
        Assert.NotEqual(compact.SourceSha256, spaced.SourceSha256);
        Assert.Equal(compact.RenderPage(0).Pixels.ToArray(), spaced.RenderPage(0).Pixels.ToArray());
    }

    private static byte[] Book(string text = "A")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:native-read</dc:identifier><dc:title>Native read</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Native read</title></head><body><p>{text}</p></body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Native read</a></li></ol></nav></body></html>
                """);
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
