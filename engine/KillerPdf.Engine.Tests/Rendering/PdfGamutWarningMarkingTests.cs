using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfGamutWarningMarkingTests
{
    [Fact]
    public void MarkingPaintsOutOfGamutPixelsFlatNeutralGrayAndLeavesTheRestAsProofed()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("0 g 0 0 1 1 re f")).Build()));
        var proofed = renderer.RenderFrame(0, Options(warnings: true));

        var marked = proofed.WithGamutWarningsMarked();

        // Black lies below the fixture printer's black (Y=8192/65535), so pixel 0 is out of gamut;
        // the white page at pixel 1 is inside. The mark is sRGB 128/128/128, opaque.
        Assert.Equal(PdfGamutClassification.Outside, proofed.PrinterGamutWarnings!.GetClassification(0, 0));
        byte[] bgra = marked.ToSrgbBgra32();
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, bgra[..4]);
        Assert.Equal(proofed.ToSrgbBgra32()[4..], bgra[4..]);
        Assert.NotEqual(new byte[] { 128, 128, 128, 255 }, proofed.ToSrgbBgra32()[..4]);
    }

    [Fact]
    public void MarkingWithoutAWarningMaskReturnsTheSameFrame()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("0 g 0 0 1 1 re f")).Build()));
        var proofed = renderer.RenderFrame(0, Options(warnings: false));

        Assert.Same(proofed, proofed.WithGamutWarningsMarked());
    }

    private static PdfRenderOptions Options(bool warnings) => new(2, 1)
    {
        Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: GrayPrinter(), gamutWarnings: warnings,
            options: new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true })
    };

    private static PdfColorProfile GrayPrinter()
    {
        byte[] data = new byte[192];
        Write32(0, data.Length);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(136, 156); Write32(140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(148, 172); Write32(152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        Write32(164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), 65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(180, 63190); Write32(184, 65536); Write32(188, 54061);
        return PdfColorProfile.Load(data);
        void Write32(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }
}
