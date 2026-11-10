using System.Diagnostics;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[CollectionDefinition(nameof(GamutWarningCostCollection), DisableParallelization = true)]
public sealed class GamutWarningCostCollection;

[Collection(nameof(GamutWarningCostCollection))]
public sealed class PdfGamutWarningCostTests
{
    [Fact]
    public void WarningsOnAPhotoCostLittleMoreThanTheProofAlone()
    {
        // A photo-like page: every pixel of the 512x512 picture has its own color, so no
        // classification can be reused from a neighbor.
        const int size = 512;
        var pixels = new byte[size * size * 3];
        for (int y = 0, i = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                pixels[i++] = (byte)(x * 256 / size);
                pixels[i++] = (byte)(y * 256 / size);
                pixels[i++] = (byte)((x * 7 + y * 13) % 256);
            }
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(size, size,
            new PdfContentStreamBuilder().DrawImage(PdfImage.FromRgb(size, size, pixels), 0, 0, size, size)).Build()));
        var press = PdfColorProfile.Load(SyntheticCmykPrinter.Create());
        PdfRenderOptions Options(bool warnings) => new(size, size)
        {
            Proof = new PdfDocumentProofSettings(enabled: true, printerProfile: press, gamutWarnings: warnings,
                options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric })
        };
        double Time(bool warnings)
        {
            var timer = Stopwatch.StartNew();
            renderer.RenderFrame(0, Options(warnings)).WithGamutWarningsMarked();
            return timer.Elapsed.TotalMilliseconds;
        }
        Time(false);
        Time(true);
        var off = new List<double>();
        var on = new List<double>();
        for (int run = 0; run < 5; run++)
        {
            off.Add(Time(false));
            on.Add(Time(true));
        }
        off.Sort();
        on.Sort();

        // Classifying each pixel by its own printer round trips cost more than twice the proof.
        Assert.True(on[2] < off[2] * 1.5, $"Warnings on took {on[2]:F0} ms against {off[2]:F0} ms off.");
    }
}
