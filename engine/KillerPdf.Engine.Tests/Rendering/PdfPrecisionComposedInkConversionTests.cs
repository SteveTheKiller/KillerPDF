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

    // Pixel i holds ink (i * 7919) mod distinctInks, so every ink repeats, spread over the page,
    // and 7919 is prime to 50,000 and 200,000, so the first distinctInks pixels already hold every ink.
    private static byte[] PictureFile(int distinctInks)
    {
        var samples = new byte[Side * Side * 4];
        for (int pixel = 0; pixel < Side * Side; pixel++)
        {
            int ink = (int)((long)pixel * 7919 % distinctInks);
            samples[pixel * 4] = (byte)ink;
            samples[pixel * 4 + 1] = (byte)(ink >> 8);
            samples[pixel * 4 + 2] = (byte)(ink >> 16);
        }
        return TestIccProfiles.Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Side} {Side}] /Resources << /ExtGState << /O << /op true /OP true >> >> /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>",
            TestIccProfiles.Stream("", Encoding.ASCII.GetBytes($"/O gs q {Side} 0 0 {Side} 0 0 cm /Im Do Q")),
            TestIccProfiles.Stream($"/Type /XObject /Subtype /Image /Width {Side} /Height {Side} /ColorSpace /DeviceCMYK /BitsPerComponent 8", samples)]);
    }

    private static (long Conversions, float[] Pixels) RenderPicture(int parallelism, int distinctInks = DistinctInks)
    {
        long conversions = 0;
        var renderer = new PdfPageRenderer(PdfDocument.Open(PictureFile(distinctInks)))
        {
            ComposedInkConversionObserver = () => Interlocked.Increment(ref conversions)
        };
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(Side, Side)
        {
            MaximumParallelism = parallelism,
            Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
                new PdfProofOptions { RenderingIntent = KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric,
                    BlackPointCompensation = false })
        });
        return (conversions, frame.Pixels.ToArray());
    }

    [Fact]
    public void EachDistinctInkOfAPictureIsConvertedOnce()
    {
        // 50,000 distinct inks fit the cache, and 50,000 is prime to 7919.
        var (conversions, _) = RenderPicture(1, 50_000);

        Assert.InRange(conversions, 1, 50_000);
    }

    // The converted-ink cache holds at most 65,536 entries. Each of the 200,000 inks appears five times, far
    // apart, so an ink the cache cannot hold is converted at every appearance: at least 65,536 + 5 * (200,000 -
    // 65,536) conversions. A cache that holds more than the cap converts fewer. Row workers share the cache,
    // so the cap holds under parallel rows too, and the pixels do not depend on the thread count.
    [Fact]
    public void ConvertedInkCacheStaysWithinItsEntryCap()
    {
        const int cap = 1 << 16;
        const long minimum = cap + 5L * (DistinctInks - cap);

        var (serialConversions, serialPixels) = RenderPicture(1);
        var (parallelConversions, parallelPixels) = RenderPicture(4);

        Assert.True(serialConversions >= minimum, $"serial conversions {serialConversions} < {minimum}");
        Assert.True(parallelConversions >= minimum, $"parallel conversions {parallelConversions} < {minimum}");
        Assert.True(serialPixels.AsSpan().SequenceEqual(parallelPixels), "parallel rows changed the pixels");
    }
}
