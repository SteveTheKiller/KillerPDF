using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfLargeTransformTests
{
    [Theory]
    [InlineData("340290000000000000000000000000000000000.0")]
    [InlineData("340390000000000000000000000000000000000.0")]
    public void OppositeLargeTranslationsPreserveVisibleContent(string translation)
    {
        const string paint = "1 0 0 rg 2 2 16 16 re f";
        PdfRenderedPage expected = Render(paint);
        PdfRenderedPage actual = Render($"1 0 0 1 {translation} 0 cm "
            + $"1 0 0 1 -{translation} 0 cm {paint}");

        Assert.Empty(actual.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, actual.Pixels.Slice((10 * 20 + 10) * 4, 4).ToArray());
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(string content)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 20, Encoding.ASCII.GetBytes(content)).Build());
        return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(20, 20));
    }
}
