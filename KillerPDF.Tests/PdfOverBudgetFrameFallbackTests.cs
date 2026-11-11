using System.Buffers.Binary;
using KillerPDF.Presentation;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

// A float render whose soft mask does not fit beside the frame falls back to the plain render the
// way a page the float renderer cannot draw does, instead of failing the page.
public sealed class PdfOverBudgetFrameFallbackTests
{
    // A 40 x 40 float frame reserves 36 bytes a pixel (57,600 bytes). Its full-page mask keeps one
    // byte a pixel and renders through a five-byte-a-pixel tile (9,600 bytes with the kept samples),
    // so 1,000 spare bytes cannot hold it, while the 6,400-byte plain render fits.
    private const long Budget = 57_600 + 1_000;

    [Fact]
    public void AProofWhoseMaskExceedsTheBudgetShowsThePlainPageWithTheProofUnavailableNote()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(MaskedPage()));
        byte[] plain = renderer.Render(0, new PdfRenderOptions(40, 40, maximumPixelBytes: Budget)).Pixels.ToArray();
        var proof = new PdfDocumentProofSettings(enabled: true, printerProfile: TintedGrayPrinter());

        var result = PdfViewingFrameConversion.Render(
            requested => renderer.RenderFrame(0, new PdfRenderOptions(40, 40, maximumPixelBytes: Budget) { Proof = requested }),
            () => new KillerPDF.Services.PdfRenderedPage(40, 40, renderer.Render(0,
                new PdfRenderOptions(40, 40, maximumPixelBytes: Budget)).Pixels.ToArray(),
                KillerPDF.Services.PdfRenderBackend.Engine, null), proof, monitor: null, default);

        Assert.True(result.ProofUnavailable);
        Assert.NotNull(result.Compatibility);
        Assert.Null(result.DisplayFallbackOperation);
        Assert.Equal(plain, result.Pixels);
    }

    // Only refusals fall back: an argument error that is not about what the page needs still fails.
    [Fact]
    public void AnUnrelatedArgumentErrorIsNotTakenForARefusal()
    {
        var proof = new PdfDocumentProofSettings(enabled: true, printerProfile: TintedGrayPrinter());

        Assert.Throws<ArgumentException>(() => PdfViewingFrameConversion.Render(
            _ => throw new ArgumentException("A caller error."), () => throw new InvalidOperationException(),
            proof, monitor: null, default));
    }

    private static byte[] MaskedPage()
    {
        var mask = new PdfFormXObject(10, 10, new PdfContentStreamBuilder().SetFillGray(.6).Rectangle(0, 0, 10, 10).Fill(),
            isolatedTransparencyGroup: true, transparencyGroupColorSpace: PdfTransparencyGroupColorSpace.Gray);
        return new PdfDocumentBuilder().AddPage(10, 10, new PdfContentStreamBuilder()
            .SetFillRgb(.5, .5, .5).Rectangle(0, 0, 10, 10).Fill()
            .SetGraphicsState(new PdfGraphicsState(softMask: new PdfSoftMask(mask, PdfSoftMaskSubtype.Luminosity)))
            .SetFillRgb(.2, .4, .6).Rectangle(0, 0, 10, 10).Fill()).Build();
    }

    // The tinted gray printer of PdfSoftMaskProofViewingTests.
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
