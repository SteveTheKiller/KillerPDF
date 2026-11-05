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

public sealed class EpubPdfConversionTests(ITestOutputHelper output)
{
    [Fact]
    public void ConversionPreservesExplicitRedSelectableTextAndNativeSource()
    {
        byte[] source = Book("color:#ff0000");
        byte[] original = source.ToArray();
        string sourceHash = Convert.ToHexString(SHA256.HashData(source));
        output.WriteLine("Red EPUB fixture SHA256: " + sourceHash);
        using Stream fontStream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        fontStream.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        var snapshot = PdfReadSnapshot.OpenEpub(source,
            new EpubLayoutOptions(320, 160, font) { FontSize = 16, Margin = 16 });
        Guid layoutIdentity = snapshot.LayoutIdentity;
        var geometry = snapshot.GetPageGeometry(0);
        float[] nativePixels = snapshot.RenderPage(0).Pixels.ToArray();
        Assert.Contains(Enumerable.Range(0, nativePixels.Length / 4), index =>
            nativePixels[index * 4] == 1 && nativePixels[index * 4 + 1] == 0
            && nativePixels[index * 4 + 2] == 0 && nativePixels[index * 4 + 3] == 1);
        var epub = EpubDocument.Open(source);
        byte[] chapter = epub.ReadResource("EPUB/text.xhtml");

        byte[] converted = epub.ConvertToPdf(new EpubPdfConversionOptions(font));

        var pdf = PdfDocument.Open(converted);
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("ALPHA BRAVO", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(794, 1123));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixels.AsSpan(0, 4).ToArray());
        int red = 0, white = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.Equal(255, pixels[offset + 2]);
            Assert.Equal(pixels[offset], pixels[offset + 1]);
            Assert.Equal(255, pixels[offset + 3]);
            if (pixels[offset] == 0) red++;
            if (pixels[offset] == 255) white++;
        }
        Assert.True(red > 0);
        Assert.True(white > pixels.Length / 8);
        Assert.Equal(original, source);
        Assert.Equal(chapter, epub.ReadResource("EPUB/text.xhtml"));
        Assert.Equal(PdfDocumentFormat.Epub, snapshot.Format);
        Assert.Equal(sourceHash, snapshot.SourceSha256);
        Assert.Equal(layoutIdentity, snapshot.LayoutIdentity);
        Assert.Same(geometry, snapshot.GetPageGeometry(0));
        Assert.Equal(nativePixels, snapshot.RenderPage(0).Pixels.ToArray());
    }

    [Fact]
    public void ConversionUsesA4MarginsAndKeepsSelectableTextAndNativeSource()
    {
        byte[] source = Book();
        byte[] original = source.ToArray();
        string sourceHash = Convert.ToHexString(SHA256.HashData(source));
        output.WriteLine("EPUB fixture SHA256: " + sourceHash);
        using Stream fontStream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var fontBytes = new MemoryStream();
        fontStream.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        var snapshot = PdfReadSnapshot.OpenEpub(source,
            new EpubLayoutOptions(320, 160, font) { FontSize = 16, Margin = 16 });
        Guid layoutIdentity = snapshot.LayoutIdentity;
        var geometry = snapshot.GetPageGeometry(0);
        float[] nativePixels = snapshot.RenderPage(0).Pixels.ToArray();
        var epub = EpubDocument.Open(source);
        byte[] chapter = epub.ReadResource("EPUB/text.xhtml");

        byte[] converted = epub.ConvertToPdf(new EpubPdfConversionOptions(font));

        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(converted.AsSpan(0, 8)));
        var pdf = PdfDocument.Open(converted);
        var paper = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.InRange(paper.PhysicalWidth, 595.2745905511812, 595.2765905511812);
        Assert.InRange(paper.PhysicalHeight, 841.8887637795276, 841.8907637795276);
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var page = reader.Read(0);
        Assert.Equal("ALPHA BRAVO", string.Concat(page.Letters.Select(letter => letter.Value)));
        Assert.All(page.Letters, letter =>
        {
            Assert.InRange(letter.BoundingBox.Left, 42.51868503937008, 552.7569055118111);
            Assert.InRange(letter.BoundingBox.Right, 42.51868503937008, 552.7569055118111);
            Assert.InRange(letter.BoundingBox.Bottom, 42.51868503937008, 799.3710787401575);
            Assert.InRange(letter.BoundingBox.Top, 42.51868503937008, 799.3710787401575);
        });
        Assert.Equal(original, source);
        Assert.Equal(chapter, epub.ReadResource("EPUB/text.xhtml"));
        Assert.Equal(PdfDocumentFormat.Epub, snapshot.Format);
        Assert.Equal(sourceHash, snapshot.SourceSha256);
        Assert.Equal(layoutIdentity, snapshot.LayoutIdentity);
        Assert.Same(geometry, snapshot.GetPageGeometry(0));
        Assert.Equal(nativePixels, snapshot.RenderPage(0).Pixels.ToArray());
    }

    private static byte[] Book(string style = "")
    {
        string styleAttribute = style.Length == 0 ? string.Empty : " style=\"" + style + "\"";
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:epub-conversion</dc:identifier><dc:title>Conversion</dc:title><dc:language>en</dc:language></metadata><manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="chapter"/></spine></package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Conversion</title></head><body><p{styleAttribute}>ALPHA BRAVO</p></body></html>
                """);
            void Add(string path, string text, CompressionLevel compression = CompressionLevel.Optimal)
            {
                var entry = zip.CreateEntry(path, compression);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }
}
