using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A paint the high-precision path cannot draw is refused with NotSupportedException, which callers
// catch to fall back to the byte render. Row-parallel painting must keep that contract.
public sealed class PdfPrecisionParallelRefusalTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void AnUnsupportedBlendOverALargeShadingIsRefusedTheSameWayAtAnyParallelism(int parallelism)
    {
        byte[] pdf = TestIccProfiles.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1200 1200] /Resources << /ExtGState << /GS0 << /BM /Overlay >> >> "
                + "/Shading << /Sh0 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1200 0] "
                + "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >> >> /Contents 4 0 R >>",
            TestIccProfiles.Stream("", Encoding.ASCII.GetBytes("/GS0 gs /Sh0 sh")));
        var renderer = new PdfPageRenderer(PdfDocument.Open(pdf));
        var options = new PdfRenderOptions(1200, 1200) { MaximumParallelism = parallelism };
        Exception error = Record.Exception(() => renderer.RenderFrame(0, options));
        Assert.IsType<NotSupportedException>(error);
    }
}
