using System.Buffers.Binary;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Relative colorimetric maps media white to the reference white (ICC.1 relative colorimetric), so
// an unprinted page shows display white when the proof does not simulate paper, and the paper's
// own color, the absolute colorimetric value of no ink, when it does. Printer tables only
// approximate those endpoints: Windows' RSWOP.icm sends PCS white to 0.6% cyan, 1.7% magenta and
// 2.0% yellow and reads no ink back as Lab (100, 1, 1). The printer below is that error made
// large: its reverse table sends PCS white to 2% black and, in the relative case, its forward
// table reads no ink as XYZ (0.95, 0.98, 0.79).
public sealed class PdfProofPaperWhiteTests
{
    [Fact]
    public void ADetailRegionWhoseProofExceedsStorageIsARefusalWhileThePlainRegionStillFits()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(PdfDjvuScanResolutionTests.HalfBlackPage(16, 16, 300, 1));
        var plain = new PdfRenderOptions(128, 128, maximumPixelBytes: 150_000);
        Assert.Equal(64, snapshot.RenderPageRegion(0, plain, 0, 0, 64, 64).Width);
        var proof = plain with
        {
            Proof = new PdfDocumentProofSettings(true, MisalignedPrinter(alignedForwardWhite: false),
                new PdfProofOptions { BlackPointCompensation = false })
        };

        var failure = Assert.Throws<NotSupportedException>(() => snapshot.RenderPageRegion(0, proof, 0, 0, 64, 64));

        Assert.Contains("proof", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorrectedDisplayWhiteIsNotMarkedOutsideThePrinterGamut()
    {
        var frame = new PdfPageRenderer(PdfDocument.Open(WhitePage())).RenderFrame(0, new PdfRenderOptions(2, 2)
        {
            Proof = new PdfDocumentProofSettings(true, MisalignedPrinter(alignedForwardWhite: false),
                new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true }, gamutWarnings: true)
        });

        Assert.Equal(0, frame.PrinterGamutWarnings!.WarningPixelCount);
        Assert.Equal(0, frame.PrinterGamutWarnings.BoundaryPixelCount);
        Assert.Equal(PdfGamutClassification.Inside, frame.PrinterGamutWarnings.GetClassification(0, 0));
        Assert.Equal(frame.ToSrgbBgra32(), frame.WithGamutWarningsMarked().ToSrgbBgra32());
    }

    [Fact]
    public void WhiteCorrectionRemovesSmallCyanMagentaYellowAndBlackTableErrors()
    {
        var frame = new PdfPageRenderer(PdfDocument.Open(WhitePage())).RenderFrame(0, new PdfRenderOptions(2, 2)
        {
            Proof = new PdfDocumentProofSettings(true, MisalignedPrinter(false, coloredReverseWhite: true),
                new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true }, gamutWarnings: true)
        });

        for (int channel = 0; channel < 3; channel++) Assert.InRange(frame.Pixels[channel], .99999f, 1.00001f);
        Assert.Equal(0, frame.PrinterGamutWarnings!.WarningPixelCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaperSimulationStillRefusesATableWhoseWhiteDisagreesWithItsDeclaredPaper(bool simulateBlack)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(WhitePage()));
        var failure = Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 2)
        {
            Proof = new PdfDocumentProofSettings(true, MisalignedPrinter(alignedForwardWhite: false),
                new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = simulateBlack })
        }));

        Assert.Contains("white endpoint", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AnUnprintedPageProofsAsDisplayWhiteWithoutPaperSimulation(bool blackPointCompensation, bool simulateBlack)
    {
        var frame = new PdfPageRenderer(PdfDocument.Open(WhitePage())).RenderFrame(0, new PdfRenderOptions(2, 2)
        {
            Proof = new PdfDocumentProofSettings(true, MisalignedPrinter(alignedForwardWhite: false),
                new PdfProofOptions { BlackPointCompensation = blackPointCompensation, SimulateBlack = simulateBlack })
        });

        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], 1 - 1e-6f, 1 + 1e-6f);
    }

    // The paper is 0.8 of D50 white, the same chromaticity as the reference: Bradford adaptation
    // takes it to 0.8 of D65 white, linear sRGB (0.8, 0.8, 0.8). ICC s15Fixed16 and u16 encodings
    // of the media white and table entries keep it within 2e-4. The table must agree with the
    // declared paper; the separate reliability test rejects a disagreeing absolute endpoint.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void AnUnprintedPageProofsAsThePaperColorWithPaperSimulation(bool simulateBlack, bool alignedForwardWhite)
    {
        var frame = new PdfPageRenderer(PdfDocument.Open(WhitePage())).RenderFrame(0, new PdfRenderOptions(2, 2)
        {
            Proof = new PdfDocumentProofSettings(true, MisalignedPrinter(alignedForwardWhite),
                new PdfProofOptions { BlackPointCompensation = false, SimulatePaper = true, SimulateBlack = simulateBlack })
        });

        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], .8f - 2e-4f, .8f + 2e-4f);
    }

    // DjVu and EPUB pages take the same proof chain.
    [Fact]
    public void AnUnprintedDjvuAreaProofsAsDisplayWhite()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(PdfDjvuScanResolutionTests.HalfBlackPage(16, 16, 300, 1));
        var frame = snapshot.RenderPage(0, new PdfRenderOptions(16, 16)
        {
            Proof = new PdfDocumentProofSettings(true, MisalignedPrinter(alignedForwardWhite: false),
                new PdfProofOptions { BlackPointCompensation = false })
        });

        int white = -1;
        for (int offset = 0; offset < frame.Pixels.Length; offset += 4)
            if (frame.Pixels[offset + 1] > .9f) { white = offset; break; }
        Assert.True(white >= 0);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[white + channel], 1 - 1e-6f, 1 + 1e-6f);
    }

    private static byte[] WhitePage() => new PdfDocumentBuilder().AddPage(10, 10,
        new PdfContentStreamBuilder().SetFillRgb(1, 1, 1).Rectangle(0, 0, 10, 10).Fill()).Build();

    // A CMYK output profile with XYZ connection and three-point lut16 tables that depend on black only.
    // Forward: no ink reads (0.95, 0.98, 0.79), or D50 white when aligned; half black 0.5 of D50 white,
    // full black 0.1. Reverse: Y 0 is full black, Y 1 (PCS white) is 2% black, Y 2 is no ink.
    // Media white is 0.8 of D50 white. The perceptual tables repeat the colorimetric ones.
    internal static PdfColorProfile MisalignedPrinter(bool alignedForwardWhite, bool coloredReverseWhite = false)
    {
        ushort[] paper = alignedForwardWhite ? [31595, 32768, 27030] : [31130, 32113, 25887];
        byte[] forward = Table(4, 3, cell => coloredReverseWhite
            ? [Sample(paper[0], cell / 27, cell % 3), Sample(paper[1], cell / 9 % 3, cell % 3),
                Sample(paper[2], cell / 3 % 3, cell % 3)]
            : (cell % 3) switch
        {
            0 => paper,
            1 => [15798, 16384, 13515],
            _ => [3160, 3277, 2703]
        });
        byte[] reverse = Table(3, 4, cell => [(ushort)(coloredReverseWhite ? 164 : 0),
            (ushort)(coloredReverseWhite ? 328 : 0), (ushort)(coloredReverseWhite ? 492 : 0),
            ((cell / 3) % 3) switch { 0 => (ushort)65535, 1 => (ushort)1311, _ => (ushort)0 }]);
        byte[] whitePoint = new byte[20];
        "XYZ "u8.CopyTo(whitePoint);
        BinaryPrimitives.WriteInt32BigEndian(whitePoint.AsSpan(8), 50551);
        BinaryPrimitives.WriteInt32BigEndian(whitePoint.AsSpan(12), 52429);
        BinaryPrimitives.WriteInt32BigEndian(whitePoint.AsSpan(16), 43248);
        (string Name, byte[] Data)[] tags =
            [("A2B1", forward), ("B2A1", reverse), ("A2B0", forward), ("B2A0", reverse), ("wtpt", whitePoint)];
        int offset = 132 + 12 * tags.Length;
        byte[] data = new byte[offset + tags.Sum(tag => (tag.Data.Length + 3) & ~3)];
        Write32(data, 0, data.Length);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "CMYK"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, tags.Length);
        for (int index = 0; index < tags.Length; index++)
        {
            int entry = 132 + 12 * index;
            System.Text.Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, tags[index].Data.Length);
            tags[index].Data.CopyTo(data, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return PdfColorProfile.Load(data);

        static ushort Sample(ushort white, int colorCell, int blackCell) =>
            (ushort)Math.Round(white * (1 - .05 * colorCell - .4 * blackCell));
    }

    private static byte[] Table(int inputs, int outputs, Func<int, ushort[]> vertex)
    {
        int cells = (int)Math.Pow(3, inputs);
        byte[] data = new byte[52 + 4 * inputs + 2 * cells * outputs + 4 * outputs];
        "mft2"u8.CopyTo(data);
        data[8] = (byte)inputs; data[9] = (byte)outputs; data[10] = 3;
        for (int axis = 0; axis < 3; axis++) Write32(data, 12 + axis * 16, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), 2);
        int offset = 52;
        void Append(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
            offset += 2;
        }
        for (int channel = 0; channel < inputs; channel++) { Append(0); Append(65535); }
        for (int cell = 0; cell < cells; cell++)
            foreach (ushort value in vertex(cell)) Append(value);
        for (int channel = 0; channel < outputs; channel++) { Append(0); Append(65535); }
        return data;
    }

    private static void Write32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
