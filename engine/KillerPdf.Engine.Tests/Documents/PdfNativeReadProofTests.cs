using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfNativeReadProofTests
{
    [Fact]
    public void EpubSnapshotProofSimulatesPrinterBlackWithoutChangingTheSource()
    {
        byte[] bytes = Book();
        var snapshot = PdfReadSnapshot.OpenEpub(bytes, LayoutOptions());
        var original = snapshot.RenderPage(0);
        var frame = snapshot.RenderPage(0, Options(16, 14));

        // The gray fixture has encoded black endpoint 8192/65535, independent of source RGB.
        AssertLuminance(frame, (5 * 16 + 7) * 4, .125001907377737087);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(1f, frame.Pixels[(5 * 16 + 7) * 4 + 3]);
        Assert.Equal(original.Pixels.ToArray(), snapshot.RenderPage(0).Pixels.ToArray());
        Assert.Equal(bytes, Book());
    }

    [Fact]
    public void BitonalDjvuProofChangesBlackAndPreservesSnapshotIdentityAndOriginalPixels()
    {
        byte[] bytes = Djvu();
        byte[] saved = (byte[])bytes.Clone();
        var snapshot = PdfReadSnapshot.OpenDjvu(bytes);
        string identity = snapshot.SourceSha256;
        var original = snapshot.RenderPage(0);
        var frame = snapshot.RenderPage(0, Options(3, 1));

        AssertLuminance(frame, 0, 1);
        AssertLuminance(frame, 4, .125001907377737087);
        AssertLuminance(frame, 8, .125001907377737087);
        Assert.Equal(original.Pixels.ToArray(), snapshot.RenderPage(0).Pixels.ToArray());
        Assert.Equal(identity, snapshot.SourceSha256);
        Assert.Equal(saved, bytes);
    }

    // The viewer proofs within 256 MiB. A 600 dpi letter page's bits are 4.2 MB, not a 538 MB float frame.
    [Fact]
    public void A600DpiScanPageDrawsAProofedThumbnailInsideTheProofBudget()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(PdfDjvuScanResolutionTests.HalfBlackPage(5100, 6600, 600, 1));
        var frame = snapshot.RenderPage(0, Options(396, 512) with { });
        var budgeted = snapshot.RenderPage(0, new PdfRenderOptions(396, 512, maximumPixelBytes: 256L << 20)
            { Proof = Options(1, 1).Proof });
        Assert.Equal(frame.Pixels.ToArray(), budgeted.Pixels.ToArray());
        // Worked ICC endpoints: black becomes the curve's black fraction 8192/65535, white stays white.
        AssertLuminance(budgeted, 0, .125001907377737087);
        AssertLuminance(budgeted, 395 * 4, 1);
    }

    [Theory]
    [InlineData(false, false, 0, 0, 0, 1, 1, 1)]
    [InlineData(false, true, .125001907377737087, .125001907377737087, .125001907377737087, 1, 1, 1)]
    [InlineData(true, false, 0, 0, 0, .7084205909186849, .9649073041724270, .8354998544865850)]
    [InlineData(true, true, .08855392509049922, .1206152534642637, .10443907542464492,
        .7084205909186849, .9649073041724270, .8354998544865850)]
    public void DirectEpubLayoutKeepsPaperAndBlackSimulationIndependent(bool paper, bool black,
        double br, double bg, double bb, double wr, double wg, double wb)
    {
        var layout = EpubDocument.Open(Book()).CreateLayout(LayoutOptions());
        var options = Options(16, 14) with { Proof = new PdfDocumentProofSettings(enabled: true,
            printerProfile: Printer(tintedPaper: true), options: new PdfProofOptions {
                BlackPointCompensation = false, SimulatePaper = paper, SimulateBlack = black }) };
        var frame = layout.RenderPage(0, options);

        // Worked ICC endpoints: tinted white and its curve black fraction 8192/65535.
        AssertRgb(frame, (5 * 16 + 7) * 4, br, bg, bb);
        AssertRgb(frame, 0, wr, wg, wb);
        Assert.Equal(layout.RenderPage(0).Pixels.ToArray(), layout.RenderPage(0, new PdfRenderOptions(16, 14)).Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, .5)]
    [InlineData(true, .562500953688868543)]
    public void EpubProofUsesSourceCompensationBeforePrinterConversion(bool compensate, double expected)
    {
        var layout = LayoutOptions() with { TextColor = new PdfRgbColor(.7353569830524495,
            .7353569830524495, .7353569830524495) };
        var snapshot = PdfReadSnapshot.OpenEpub(Book(), layout);
        var options = Options(16, 14) with { Proof = new PdfDocumentProofSettings(enabled: true,
            printerProfile: Printer(), options: new PdfProofOptions {
                BlackPointCompensation = compensate, SimulateBlack = true }) };

        var frame = snapshot.RenderPage(0, options);

        // Encoded gray gives linear 1/2. Source compensation maps Y to p+(1-p)*Y, p=8192/65535.
        AssertLuminance(frame, (5 * 16 + 7) * 4, expected);
    }

    [Theory]
    [InlineData(false, 16, 14, 11200)]
    // The 3 x 1 DjVu mask is kept as one byte and a two-entry float table (9 bytes) beside six proofed pixels at
    // 48 bytes and a 2-byte warning class each: 9 + 6 x 50 = 309.
    [InlineData(true, 6, 1, 309)]
    public void NativeProofBoundsCombinedStorageAndReturnsIndependentWarningMasks(
        bool djvu, int width, int height, long exactBytes)
    {
        var snapshot = djvu ? PdfReadSnapshot.OpenDjvu(Djvu()) : PdfReadSnapshot.OpenEpub(Book(), LayoutOptions());
        var printer = Printer();
        var proof = new PdfDocumentProofSettings(enabled: true, printerProfile: printer, gamutWarnings: true,
            options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true });
        PdfRenderOptions Request(long budget) => new(width, height, maximumPixelBytes: budget) { Proof = proof };

        Assert.Throws<ArgumentException>(() => snapshot.RenderPage(0, Request(exactBytes - 1)));
        var frame = snapshot.RenderPage(0, Request(exactBytes));
        var mask = Assert.IsType<PdfGamutWarningMask>(frame.PrinterGamutWarnings);
        Assert.Equal(width, mask.Width);
        Assert.Equal(height, mask.Height);
        Assert.Equal(printer.ContentSha256, mask.PrinterProfileSha256);
        Assert.Equal(PdfGamutClassification.Inside, mask.GetClassification(0, 0));
        Assert.Equal(PdfGamutClassification.Outside, mask.GetClassification(djvu ? 5 : 7, djvu ? 0 : 5));
        Assert.True(mask.WarningPixelCount > 0);
        var withoutWarnings = snapshot.RenderPage(0, new PdfRenderOptions(width, height) { Proof =
            new PdfDocumentProofSettings(enabled: true, printerProfile: printer, options: proof.Options) });
        Assert.Equal(withoutWarnings.Pixels.ToArray(), frame.Pixels.ToArray());
        Assert.Null(withoutWarnings.PrinterGamutWarnings);
        Assert.Throws<OperationCanceledException>(() => snapshot.RenderPage(0, Request(exactBytes), new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledNativeProofPreservesDefaultPixelsAndEnabledUnavailableRequestsFail(bool djvu)
    {
        var snapshot = djvu ? PdfReadSnapshot.OpenDjvu(Djvu()) : PdfReadSnapshot.OpenEpub(Book(), LayoutOptions());
        int width = djvu ? 3 : 16, height = djvu ? 1 : 14;
        var normal = new PdfRenderOptions(width, height);
        var disabled = normal with { Proof = new PdfDocumentProofSettings(printerProfile: Printer(profileClass: "scnr"),
            gamutWarnings: true, options: new PdfProofOptions { RenderingIntent = (PdfRenderingIntent)99, SimulatePaper = true }) };
        Assert.Equal(snapshot.RenderPage(0).Pixels.ToArray(), snapshot.RenderPage(0, normal).Pixels.ToArray());
        Assert.Equal(snapshot.RenderPage(0, normal).Pixels.ToArray(), snapshot.RenderPage(0, disabled).Pixels.ToArray());
        Assert.Null(snapshot.RenderPage(0, disabled).PrinterGamutWarnings);
        foreach (var request in new[] {
            normal with { Proof = new PdfDocumentProofSettings(enabled: true) },
            normal with { Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(profileClass: "scnr")) },
            normal with { Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
                options: new PdfProofOptions { RenderingIntent = (PdfRenderingIntent)99 }) },
            normal with { ForPrinting = true, Proof = Options(width, height).Proof },
            normal with { Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(mediaWhite: false),
                options: new PdfProofOptions { SimulatePaper = true }) },
            normal with { Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(mediaWhite: false),
                options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.AbsoluteColorimetric }) } })
        {
            var error = Assert.Throws<NotSupportedException>(() => snapshot.RenderPage(0, request));
            Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsoluteEpubProofUsesMediaWhiteAndIgnoresSourceCompensation(bool compensate)
    {
        var layout = LayoutOptions() with { TextColor = new PdfRgbColor(.7353569830524495,
            .7353569830524495, .7353569830524495) };
        var snapshot = PdfReadSnapshot.OpenEpub(Book(), layout);
        var request = Options(16, 14) with { Proof = new PdfDocumentProofSettings(enabled: true,
            printerProfile: Printer(tintedPaper: true), options: new PdfProofOptions {
                RenderingIntent = PdfRenderingIntent.AbsoluteColorimetric,
                BlackPointCompensation = compensate, SimulateBlack = true }) };

        var frame = snapshot.RenderPage(0, request);

        // Absolute source Y=1/2 divided by media-white Y=58982/65536.
        AssertRgb(frame, (5 * 16 + 7) * 4, .5555593231833441, .5555593231833441, .5555593231833441);
    }

    [Fact]
    public void NativeAndPdfProofsRefuseTheSamePaperWhiteForEveryPaperSimulation()
    {
        // The kTRC curve ends at 60000/65535, so the forward white is about 8 percent below the
        // declared media white, beyond the 0.001 endpoint tolerance.
        var printer = Printer(whiteEnd: 60000);
        var settings = new PdfDocumentProofSettings(enabled: true, printerProfile: printer,
            options: new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = true });
        var pdf = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(1, 1, "0.5 g 0 0 1 1 re f"u8.ToArray()).Build()));
        var layout = EpubDocument.Open(Book()).CreateLayout(LayoutOptions());

        var pdfError = Assert.Throws<NotSupportedException>(() =>
            pdf.RenderFrame(0, new PdfRenderOptions(1, 1) { Proof = settings }));
        var nativeError = Assert.Throws<NotSupportedException>(() =>
            layout.RenderPage(0, new PdfRenderOptions(16, 14) { Proof = settings }));

        Assert.Equal(pdfError.Message, nativeError.Message);
    }

    private static void AssertRgb(PdfRenderedFrame frame, int offset, double red, double green, double blue)
    {
        Assert.InRange(frame.Pixels[offset], red - .00003, red + .00003);
        Assert.InRange(frame.Pixels[offset + 1], green - .00003, green + .00003);
        Assert.InRange(frame.Pixels[offset + 2], blue - .00003, blue + .00003);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    private static byte[] Djvu()
    {
        byte[] info = Chunk("INFO", [0, 3, 0, 1, 26, 0, 0x2c, 1, 22, 1]);
        byte[] mask = Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 1, 0x23, 0xe0, 0x02, 0, 0x20]);
        return [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8, .. info, .. mask])];
        static byte[] Chunk(string name, byte[] payload) => [.. Encoding.ASCII.GetBytes(name),
            (byte)(payload.Length >> 24), (byte)(payload.Length >> 16), (byte)(payload.Length >> 8),
            (byte)payload.Length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }

    private static void AssertLuminance(PdfRenderedFrame frame, int offset, double expected)
    {
        double actual = .2126 * frame.Pixels[offset] + .7152 * frame.Pixels[offset + 1]
            + .0722 * frame.Pixels[offset + 2];
        Assert.InRange(actual, expected - .00003, expected + .00003);
    }

    private static EpubLayoutOptions LayoutOptions() => new(16, 14,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true)))
        { FontSize = 10, Margin = 2 };

    private static PdfRenderOptions Options(int width, int height) => new(width, height)
    {
        Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: Printer(),
            options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
    };

    private static PdfColorProfile Printer(bool tintedPaper = false, string profileClass = "prtr", bool mediaWhite = true,
        ushort whiteEnd = 65535)
    {
        byte[] data = new byte[192];
        Write32(0, data.Length); data[8] = 4;
        Encoding.ASCII.GetBytes(profileClass).CopyTo(data, 12); "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20)); "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(128, 2); "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(136, 156); Write32(140, 16);
        Encoding.ASCII.GetBytes(mediaWhite ? "wtpt" : "xxxx").CopyTo(data, 144);
        Write32(148, 172); Write32(152, 20); "curv"u8.CopyTo(data.AsSpan(156));
        Write32(164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), whiteEnd);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(180, tintedPaper ? 52429 : 63190);
        Write32(184, tintedPaper ? 58982 : 65536);
        Write32(188, tintedPaper ? 45875 : 54061);
        return PdfColorProfile.Load(data);
        void Write32(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }

    private static byte[] Book()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:native-proof</dc:identifier><dc:title>Proof</dc:title><dc:language>en</dc:language></metadata><manifest><item id='chapter' href='text.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='chapter'/></spine></package>");
            Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><body><p>A</p></body></html>");
            void Add(string path, string content)
            {
                var entry = zip.CreateEntry(path, path == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
