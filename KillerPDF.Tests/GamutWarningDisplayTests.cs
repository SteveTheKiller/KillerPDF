using System.IO;
using System.IO.Compression;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using Xunit;

namespace KillerPDF.Tests;

// Gamut warnings in the viewer's frames against a portable CMYK press profile with a known gamut.
public sealed class GamutWarningDisplayTests
{
    private static readonly PdfColorProfile Press = PdfColorProfile.Load(
        KillerPdf.Engine.Tests.ColorManagement.SyntheticCmykPrinter.Create());

    private static readonly byte[] WarningGray = [128, 128, 128, 255];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewerPagesMarkOutOfGamutGreenGrayAndKeepAPrintableSwatchProofed(bool regionOnly)
    {
        WithSwatches(path =>
        {
            var cache = new PdfBackgroundRenderCache();
            try
            {
                using var lease = cache.Capture(path, 0, null).Rent();
                var region = new VisibleRenderRegion(0, 0, 8, 4, 0, 0, 8, 4);
                PdfRenderedFramePixels Render(bool warnings) => new(regionOnly
                    ? lease.RenderFrameRegion(0, 8, 8, region, default, Proof(warnings))
                    : lease.RenderFrame(0, 8, 8, default, Proof(warnings)));

                var on = Render(warnings: true);
                var off = Render(warnings: false);

                // Left half saturated green, right half a warm tone the press prints.
                Assert.Equal(WarningGray, on.At(1, 1));
                Assert.NotEqual(WarningGray, off.At(1, 1));
                Assert.Equal(off.At(6, 1), on.At(6, 1));
                Assert.NotEqual(WarningGray, on.At(6, 1));
            }
            finally { cache.Clear(); }
        });
    }

    [Fact]
    public void WarningsOffLeavesTheProofedPageIdenticalToPlainProofing()
    {
        WithSwatches(path =>
        {
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 8, 8, null);
            var plain = new KillerPdf.Engine.Rendering.PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path)))
                .RenderFrame(0, new KillerPdf.Engine.Rendering.PdfRenderOptions(8, 4) { Proof = Proof(warnings: false) });

            var off = session.RenderFittedFrame(0, 8, 8, default, Proof(warnings: false));

            Assert.Equal(plain.Pixels.ToArray(), off.Pixels.ToArray());
            Assert.Null(off.PrinterGamutWarnings);
        });
    }

    [Fact]
    public void PageThumbnailsMarkOutOfGamutGreen()
    {
        WithSwatches(path =>
        {
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 8, 8, null);

            var page = session.RenderPreview(0, proof: Proof(warnings: true));

            Assert.Equal(8, page.Width);
            Assert.Equal(WarningGray, page.Pixels.AsSpan((1 * page.Width + 1) * 4, 4).ToArray());
            Assert.NotEqual(WarningGray, page.Pixels.AsSpan((1 * page.Width + 6) * 4, 4).ToArray());
        });
    }

    [Fact]
    public void ProofedEpubPagesMarkOutOfGamutGreen()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")
            ?? throw new InvalidOperationException("The portable font fixture is missing.");
        byte[] fontBytes = new byte[checked((int)resource.Length)];
        resource.ReadExactly(fontBytes);
        var snapshot = PdfReadSnapshot.OpenEpub(GreenBook(),
            new EpubLayoutOptions(80, 48, TrueTypeFont.Load(fontBytes)) { FontSize = 12, Margin = 0 });
        using var revision = new PdfCapturedReadRevision(snapshot);

        var on = new PdfRenderedFramePixels(NativeFrameRendering.Render(revision, 0, 40, 40, default, Proof(warnings: true)));
        var off = new PdfRenderedFramePixels(NativeFrameRendering.Render(revision, 0, 40, 40, default, Proof(warnings: false)));

        Assert.Equal(WarningGray, on.At(2, 20));
        Assert.NotEqual(WarningGray, off.At(2, 20));
    }

    private sealed class PdfRenderedFramePixels(KillerPdf.Engine.Rendering.PdfRenderedFrame frame)
    {
        private readonly byte[] _bgra = frame.ToSrgbBgra32();
        internal byte[] At(int x, int y) => _bgra.AsSpan((y * frame.Width + x) * 4, 4).ToArray();
    }

    private static PdfDocumentProofSettings Proof(bool warnings) => new(enabled: true, printerProfile: Press,
        gamutWarnings: warnings, options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric,
            BlackPointCompensation = false, SimulatePaper = false, SimulateBlack = false });

    private static void WithSwatches(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            // sRGB 200/150/120 on the right is a warm tone well inside the press gamut.
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(8, 4, System.Text.Encoding.ASCII.GetBytes(
                "0 1 0 rg 0 0 4 4 re f .78431 .58824 .47059 rg 4 0 4 4 re f")).Build());
            test(path);
        }
        finally { PdfPageRenderSession.ForgetDocument(path); File.Delete(path); }
    }

    private static byte[] GreenBook()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:test:gamut-green</dc:identifier><dc:title>Green</dc:title><dc:language>en</dc:language></metadata><manifest><item id='chapter' href='text.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='chapter'/></spine></package>");
            Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Green</title><style>body{margin:0;background-color:#00ff00}</style></head><body><p>A</p></body></html>");
            void Add(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
            {
                var entry = zip.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
