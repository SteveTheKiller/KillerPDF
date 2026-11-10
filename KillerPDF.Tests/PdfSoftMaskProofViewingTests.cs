using System.Buffers.Binary;
using System.IO;
using KillerPDF.Presentation;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPDF.Tests;

// Soft proofing draws pages with soft-masked artwork instead of falling back to the plain render.
public sealed class PdfSoftMaskProofViewingTests
{
    [Fact]
    public void ASoftMaskedPageIsProofedWithoutTheProofUnavailableNote()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            var mask = new PdfFormXObject(8, 8, new PdfContentStreamBuilder().SetFillGray(.6).Rectangle(0, 0, 4, 8).Fill(),
                isolatedTransparencyGroup: true, transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Gray);
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(8, 8, new PdfContentStreamBuilder()
                .SetGraphicsState(new PdfGraphicsState(softMask: new PdfSoftMask(mask, PdfSoftMaskSubtype.Luminosity)))
                .SetFillRgb(.2, .4, .6).Rectangle(0, 0, 8, 8).Fill()).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 8, 8, null);
            var proof = new PdfDocumentProofSettings(enabled: true, printerProfile: TintedGrayPrinter());

            var result = PdfViewingFrameConversion.Render(
                requested => session.RenderFittedFrame(0, 8, 8, default, requested),
                () => session.RenderFittedPage(0, 8, 8), proof, monitor: null, default);

            Assert.False(result.ProofUnavailable);
            Assert.Null(result.Compatibility);
            Assert.Null(result.DisplayFallbackOperation);
        }
        finally { PdfPageRenderSession.ForgetDocument(path); File.Delete(path); }
    }

    // The tinted gray printer of PdfDisplayProofSessionTests: a gray output profile whose media white
    // is tinted, so a proofed page differs visibly from the plain one.
    private static PdfColorProfile TintedGrayPrinter()
    {
        byte[] data = new byte[192];
        Write32(data, 0, data.Length);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 156); Write32(data, 140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(data, 148, 172); Write32(data, 152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        Write32(data, 164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), 65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(data, 180, 52429); Write32(data, 184, 58982); Write32(data, 188, 45875);
        return PdfColorProfile.Load(data);
    }

    private static void Write32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
