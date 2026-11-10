using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

// Gamut checks against a portable CMYK press profile whose gamut is known by construction.
public sealed class PdfPrinterGamutCheckCmykTests
{
    [Theory]
    [InlineData(.3, .2, .4, 0)]
    [InlineData(.1, .6, .2, .3)]
    [InlineData(.5, .4, .3, .1)]
    public void ColorsThePressPrintsAreNotWarned(double c, double m, double y, double k)
    {
        var check = new PdfPrinterGamutCheck(PdfColorProfile.Load(SyntheticCmykPrinter.Create()));

        var result = check.EvaluateXyz(SyntheticCmykPrinter.Printed(c, m, y, k));

        Assert.Equal(PdfGamutClassification.Inside, result.Classification);
    }

    [Theory]
    [InlineData(.3, .2, .4, 0)]
    [InlineData(.1, .6, .2, .3)]
    [InlineData(.5, .4, .3, .1)]
    public void ImpreciseReverseTablesDoNotWarnColorsThePressPrints(double c, double m, double y, double k)
    {
        // An 8% cyan error in the reverse tables moves every printed color more than 2 units.
        var check = new PdfPrinterGamutCheck(PdfColorProfile.Load(SyntheticCmykPrinter.Create(reverseCyanOffset: .08)));

        var result = check.EvaluateXyz(SyntheticCmykPrinter.Printed(c, m, y, k));

        Assert.True(result.DeltaE76 > 2.05, "The fixture's tables must be imprecise.");
        Assert.Equal(PdfGamutClassification.Inside, result.Classification);
    }

    [Theory]
    [InlineData(true, PdfGamutClassification.Inside)]
    [InlineData(false, PdfGamutClassification.Outside)]
    public void BlackTextFollowsTheProofsBlackPointCompensation(bool compensate, PdfGamutClassification expected)
    {
        // Black lies below the press's 4% paper-black floor (about L*18). Compensation maps
        // black onto that floor, which the press prints; without it black cannot be reached.
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("0 g 0 0 1 1 re f")).Build()));
        var options = new PdfRenderOptions(2, 1) { Proof = Proof(compensate) };

        var frame = renderer.RenderFrame(0, options);

        Assert.Equal(expected, frame.PrinterGamutWarnings!.GetClassification(0, 0));
        Assert.Equal(PdfGamutClassification.Inside, frame.PrinterGamutWarnings.GetClassification(1, 0));
    }

    [Theory]
    [InlineData(true, PdfGamutClassification.Inside)]
    [InlineData(false, PdfGamutClassification.Outside)]
    public void BlackEpubPagesFollowTheProofsBlackPointCompensation(bool compensate, PdfGamutClassification expected)
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        byte[] font = new byte[resource.Length];
        resource.ReadExactly(font);
        var snapshot = PdfReadSnapshot.OpenEpub(BlackBook(), new EpubLayoutOptions(80, 48, TrueTypeFont.Load(font))
        {
            FontSize = 12, Margin = 0
        });

        var frame = snapshot.RenderPage(0, new PdfRenderOptions(8, 8) { Proof = Proof(compensate) });

        Assert.Equal(expected, frame.PrinterGamutWarnings!.GetClassification(1, 1));
    }

    [Fact]
    public void CompensatedCheckKeepsBlackInside()
    {
        var printer = PdfColorProfile.Load(SyntheticCmykPrinter.Create());

        var compensated = new PdfPrinterGamutCheck(printer, blackPointCompensation: true).EvaluateXyz([0, 0, 0]);
        var plain = new PdfPrinterGamutCheck(printer, blackPointCompensation: false).EvaluateXyz([0, 0, 0]);

        Assert.Equal(PdfGamutClassification.Inside, compensated.Classification);
        Assert.Equal(PdfGamutClassification.Outside, plain.Classification);
    }

    private static PdfDocumentProofSettings Proof(bool compensate) => new(enabled: true,
        printerProfile: PdfColorProfile.Load(SyntheticCmykPrinter.Create()), gamutWarnings: true,
        options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric,
            BlackPointCompensation = compensate });

    private static byte[] BlackBook()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='book.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("book.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>urn:test:gamut-black</dc:identifier><dc:title>Black</dc:title><dc:language>en</dc:language></metadata><manifest><item id='chapter' href='text.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='chapter'/></spine></package>");
            Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Black</title><style>body{margin:0;background-color:#000}</style></head><body><p>A</p></body></html>");
            void Add(string name, string content, CompressionLevel level = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(name, level).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.08)]
    public void SaturatedRgbGreenIsWarned(double reverseCyanOffset)
    {
        var check = new PdfPrinterGamutCheck(PdfColorProfile.Load(SyntheticCmykPrinter.Create(reverseCyanOffset)));

        // sRGB 0/255/0 in D50 XYZ: the Bradford-adapted sRGB green primary.
        var result = check.EvaluateXyz([.3850649, .7168786, .0971045]);

        Assert.Equal(PdfGamutClassification.Outside, result.Classification);
    }
}
