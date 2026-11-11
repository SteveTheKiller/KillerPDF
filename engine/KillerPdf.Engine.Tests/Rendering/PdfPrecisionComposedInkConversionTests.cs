using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// The composed ink of a picture page holds many distinct inks. Each distinct ink goes through the profile
// once, however the repeats are spread across the page.
public sealed class PdfPrecisionComposedInkConversionTests
{
    private const int Side = 1000;
    private const int DistinctInks = 200_000;

    [Fact]
    public void EachDistinctInkOfAPictureIsConvertedOnce()
    {
        // Pixel i holds ink (i * 7919) mod 200,000, so every ink appears five times, spread over the page,
        // and 7919 is prime to 200,000, so the first 200,000 pixels already hold every ink.
        var samples = new byte[Side * Side * 4];
        for (int pixel = 0; pixel < Side * Side; pixel++)
        {
            int ink = (int)((long)pixel * 7919 % DistinctInks);
            samples[pixel * 4] = (byte)ink;
            samples[pixel * 4 + 1] = (byte)(ink >> 8);
            samples[pixel * 4 + 2] = (byte)(ink >> 16);
        }
        byte[] file = TestIccProfiles.Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Side} {Side}] /Resources << /ExtGState << /O << /op true /OP true >> >> /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>",
            TestIccProfiles.Stream("", Encoding.ASCII.GetBytes($"/O gs q {Side} 0 0 {Side} 0 0 cm /Im Do Q")),
            TestIccProfiles.Stream($"/Type /XObject /Subtype /Image /Width {Side} /Height {Side} /ColorSpace /DeviceCMYK /BitsPerComponent 8", samples)]);
        long conversions = 0;
        var renderer = new PdfPageRenderer(PdfDocument.Open(file))
        {
            ComposedInkConversionObserver = () => Interlocked.Increment(ref conversions)
        };

        renderer.RenderFrame(0, new PdfRenderOptions(Side, Side)
        {
            Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
                new PdfProofOptions { RenderingIntent = KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric,
                    BlackPointCompensation = false })
        });

        Assert.InRange(conversions, 1, DistinctInks);
    }
}
