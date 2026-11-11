using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A proof of a page whose overprint is set only inside a form, or in a shading pattern's own graphics state,
// is routed to the composed-ink proof like any other overprint page, rather than refused.
public sealed class PdfPrecisionOverprintRoutingTests
{
    private const string Overprint = "<< /OP true /op true /OPM 1 >>";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverprintInAFormThatSharesOrInheritsThePageResourcesGetsAComposedProof(bool formNamesTheResources)
    {
        string formResources = formNamesTheResources ? "/Resources 5 0 R " : "";
        byte[] file = TestIccProfiles.Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 5 0 R /Contents 4 0 R >>",
            TestIccProfiles.Stream("", Encoding.ASCII.GetBytes("q /Fm1 Do Q")),
            $"<< /ExtGState << /GS1 {Overprint} >> /XObject << /Fm1 6 0 R >> >>",
            TestIccProfiles.Stream($"/Type /XObject /Subtype /Form /BBox [0 0 100 100] {formResources}",
                Encoding.ASCII.GetBytes("/GS1 gs 0 0 0 1 k 0 0 100 100 re f"))]);

        Assert.NotNull(Proof(file));
    }

    private static PdfRenderedFrame Proof(byte[] file) => new PdfPageRenderer(PdfDocument.Open(file)).RenderFrame(0,
        new PdfRenderOptions(10, 10)
        {
            Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
                new PdfProofOptions { RenderingIntent = KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric,
                    BlackPointCompensation = false })
        });
}
