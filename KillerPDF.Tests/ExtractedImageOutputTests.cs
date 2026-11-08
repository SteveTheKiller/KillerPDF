using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ExtractedImageOutputTests
{
    // Two by two red, lower-right pixel outside the selection: a transparent hole.
    private static ImageAnnotation ExtractedRedWithHole(double size)
    {
        var frame = new PdfRenderedFrame(2, 2, [1,0,0,1, 1,0,0,1, 1,0,0,1, 1,0,0,1], PdfFrameColorEncoding.SrgbD65);
        var selection = PdfRasterSelection.SelectRectangle(2, 2, 0, 0, 2, 2)
            .Subtract(PdfRasterSelection.SelectRectangle(2, 2, 1, 1, 1, 1));
        return new ImageAnnotation
        {
            PageIndex = 0, Position = new Point(0, 0), Scale = 1, SourceWidth = size, SourceHeight = size,
            ImageData = Convert.ToBase64String(PdfRasterExtraction.Extract(frame, selection).Image.ToPdfBytes())
        };
    }

    [Theory]
    [InlineData(false)] // the normal saved copy
    [InlineData(true)]  // the flattened copy that printing and image export render
    public void AnExtractedImageKeepsItsColorPlacementAndHoleWhenBurnedForSaveFlattenPrintAndExport(bool forRasterization)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-extract-burn-{Guid.NewGuid():N}.pdf");
        try
        {
            // A black 20 point page, so the hole shows black and the selected pixels show red.
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(20, 20,
                new PdfContentStreamBuilder().SetFillGray(0).Rectangle(0, 0, 20, 20).Fill()).Build());
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [ExtractedRedWithHole(20)] },
                new Dictionary<int, (int w, int h)> { [0] = (20, 20) }, forRasterization: forRasterization);

            var printed = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(path))).Render(0,
                new PdfRenderOptions(20, 20) { ForPrinting = true });
            byte[] At(int x, int y) => printed.Pixels.Slice((y * 20 + x) * 4, 4).ToArray();
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(5, 5));   // upper-left: selected red
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(15, 5));  // upper-right: selected red
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, At(5, 15));  // lower-left: selected red
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, At(15, 15));   // lower-right: the hole shows the page
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ExtractionReadsTheUnproofedPageWhateverProofTheViewShows()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-extract-proof-{Guid.NewGuid():N}.pdf");
        try
        {
            // Saturated RGB green, which a CMYK proof visibly changes.
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(72, 72,
                new PdfContentStreamBuilder().SetFillRgb(0, 1, 0).Rectangle(0, 0, 72, 72).Fill()).Build());
            PdfPageInformation page = PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)))[0];
            var (_, first) = PageExtractionSource.Render(path, null, 0, page, 0);
            var (_, second) = PageExtractionSource.Render(path, null, 0, page, 0);
            Assert.Equal(first, second);

            // The plain engine render at the same size is the reference: 150 dpi of one inch is 150 pixels.
            using var session = PdfPageRenderSession.OpenEngineFirst(path, PageExtractionSource.Dpi / 72, null);
            Assert.Equal(session.RenderBasePage(0).Pixels.ToArray(), first);
            // A proofed view of the same page differs, so equality above means no proof reached extraction.
            // The tinted-paper printer fixture the display proof tests use; simulated paper changes every pixel.
            var printer = (PdfColorProfile)typeof(PdfDisplayProofSessionTests).GetMethod("TintedGrayPrinter",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;
            var proof = new PdfDocumentProofSettings(enabled: true, printerProfile: printer,
                options: new PdfProofOptions { SimulatePaper = true, SimulateBlack = true,
                    BlackPointCompensation = false, RenderingIntent = PdfRenderingIntent.RelativeColorimetric });
            Assert.NotEqual(session.RenderPreview(0, proof: proof).Pixels.ToArray(), first);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SavingAnExtractedImageOnASignedPdfAppendsAndKeepsTheSignatureValid()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Extract Image test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] signed = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(20, 20).Build()),
            content =>
            {
                var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
                cms.ComputeSignature(new CmsSigner(certificate) { IncludeOption = X509IncludeOption.EndCertOnly });
                return cms.Encode();
            }, new PdfSignatureOptions { ReservedSignatureSize = 8192 });
        var model = ExtractedRedWithHole(20);
        var content = new PdfContentStreamBuilder().DrawImage(
            PdfAnnotationImageSource.ReadPdfAsset(Convert.FromBase64String(model.ImageData)), 0, 0, 20, 20);
        byte[] saved = PdfOverlayAnnotations.AppendPageBatch(PdfDocument.Open(signed), 0, 20, 20, 0, [model], content, null);

        Assert.True(saved.AsSpan(0, signed.Length).SequenceEqual(signed));
        var document = PdfDocument.Open(saved);
        var signature = Assert.Single(PdfSignatureReader.Read(document), item => item.IsSigned);
        Assert.True(PdfSignatureVerifier.VerifyIntegrity(document, signature).IsCryptographicallyValid);
        Assert.IsType<ImageAnnotation>(Assert.Single(PdfOverlayAnnotations.Read(document)).Annotation);
    }
}
