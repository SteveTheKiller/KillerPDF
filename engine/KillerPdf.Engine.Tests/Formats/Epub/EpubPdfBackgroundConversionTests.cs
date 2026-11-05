using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubPdfBackgroundConversionTests(ITestOutputHelper output)
{
    [Fact]
    public void ConversionPreservesCanvasBehindRgbaImageSelectableTextAndNativeSource()
    {
        // Frozen 32x16 RGBA PNG: opaque red, half-alpha green, transparent blue, opaque black.
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAACAAAAAQCAYAAAB3AH1ZAAAALklEQVR42mP4z8DwnxIMhA0U4lEHjDpg1AED7ACQGZRhig0YdcCoA0YdMLAOAAB/i72fxwsyHAAAAABJRU5ErkJggg==");
        Assert.Equal("BDE34EA44321DC1EAD9BF994DD6BD4E9E85B46509B2ED22FE5EA7B84FE74AF44",
            Convert.ToHexString(SHA256.HashData(png)));
        byte[] source = Book(png);
        byte[] original = source.ToArray();
        string sourceHash = Convert.ToHexString(SHA256.HashData(source));
        output.WriteLine("EPUB fixture SHA256: " + sourceHash);
        output.WriteLine("PNG fixture SHA256: " + Convert.ToHexString(SHA256.HashData(png)));
        string enginePath = typeof(EpubDocument).Assembly.Location;
        output.WriteLine("Loaded engine: " + enginePath);
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(enginePath))));
        using Stream fontStream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        fontStream.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        var snapshot = PdfReadSnapshot.OpenEpub(source,
            new EpubLayoutOptions(320, 160, font) { FontSize = 16, Margin = 16 });
        Guid identity = snapshot.LayoutIdentity;
        var geometry = snapshot.GetPageGeometry(0);
        float[] nativePixels = snapshot.RenderPage(0).Pixels.ToArray();
        Assert.Equal(new float[] { 0, 0, 1, 1 }, nativePixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, nativePixels.AsSpan((20 * 320 + 20) * 4, 4).ToArray());
        var epub = EpubDocument.Open(source);
        byte[] chapter = epub.ReadResource("EPUB/text.xhtml");

        byte[] converted = epub.ConvertToPdf(new EpubPdfConversionOptions(font));

        var pdf = PdfDocument.Open(converted);
        var paper = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.InRange(paper.PhysicalWidth, 595.2745905511812, 595.2765905511812);
        Assert.InRange(paper.PhysicalHeight, 841.8887637795276, 841.8907637795276);
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var page = reader.Read(0);
        Assert.Equal("ALPHA BRAVO", string.Concat(page.Letters.Select(letter => letter.Value)));
        var image = Assert.Single(page.Images);
        Assert.Equal(32, image.PixelWidth);
        Assert.Equal(16, image.PixelHeight);
        Assert.InRange(image.BoundingBox.Left, 42.51868503937008, 42.52068503937008);
        Assert.InRange(image.BoundingBox.Right, 66.51868503937008, 66.52068503937008);
        Assert.InRange(image.BoundingBox.Bottom, 787.3690787401575, 787.3710787401575);
        Assert.InRange(image.BoundingBox.Top, 799.3690787401575, 799.3710787401575);
        Assert.All(page.Letters.Select(letter => letter.BoundingBox).Append(image.BoundingBox), bounds =>
        {
            Assert.InRange(bounds.Left, 42.51868503937008, 552.7569055118111);
            Assert.InRange(bounds.Right, 42.51868503937008, 552.7569055118111);
            Assert.InRange(bounds.Bottom, 42.51868503937008, 799.3710787401575);
            Assert.InRange(bounds.Top, 42.51868503937008, 799.3710787401575);
        });
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(794, 1123));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        Pixel(0, 0, [255, 0, 0, 255]);
        Pixel(793, 1122, [255, 0, 0, 255]);
        Pixel(400, 500, [255, 0, 0, 255]);
        Pixel(62, 61, [0, 0, 255, 255]);
        Pixel(80, 61, [127, 128, 0, 255]);
        Pixel(62, 69, [255, 0, 0, 255]);
        Pixel(80, 69, [0, 0, 0, 255]);
        Pixel(55, 61, [255, 0, 0, 255]);
        Pixel(90, 61, [255, 0, 0, 255]);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index =>
            pixels[index * 4] == 255 && pixels[index * 4 + 1] == 255
            && pixels[index * 4 + 2] == 255 && pixels[index * 4 + 3] == 255);
        Assert.Equal(original, source);
        Assert.Equal(chapter, epub.ReadResource("EPUB/text.xhtml"));
        Assert.Equal(png, epub.ReadResource("EPUB/check.png"));
        Assert.Equal(PdfDocumentFormat.Epub, snapshot.Format);
        Assert.Equal(sourceHash, snapshot.SourceSha256);
        Assert.Equal(identity, snapshot.LayoutIdentity);
        Assert.Same(geometry, snapshot.GetPageGeometry(0));
        Assert.Equal(nativePixels, snapshot.RenderPage(0).Pixels.ToArray());

        void Pixel(int x, int y, byte[] expected) =>
            Assert.Equal(expected, pixels.AsSpan((y * 794 + x) * 4, 4).ToArray());
    }

    private static byte[] Book(byte[] png)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", Encoding.UTF8.GetBytes("application/epub+zip"));
            Add("META-INF/container.xml", Encoding.UTF8.GetBytes("""
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """));
            Add("EPUB/book.opf", Encoding.UTF8.GetBytes("""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:background-conversion</dc:identifier><dc:title>Background conversion</dc:title><dc:language>en</dc:language></metadata><manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="image" href="check.png" media-type="image/png"/></manifest><spine><itemref idref="chapter"/></spine></package>
                """));
            Add("EPUB/text.xhtml", Encoding.UTF8.GetBytes("""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Background</title></head><body style="background-color:#0000ff;color:#ffffff"><p><img src="check.png" alt=""/></p><p>ALPHA BRAVO</p></body></html>
                """));
            Add("EPUB/check.png", png);
            void Add(string path, byte[] data)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return buffer.ToArray();
    }
}
