using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44DiagnosticsTests
{
    [Fact]
    public void RenderableGrayscaleBackgroundReportsLimitedSupport()
    {
        var document = DjvuDocument.Open(Convert.FromHexString(
            "41542654464F524D00000029444A5655494E464F0000000A00040004180064000A01" +
            "424734340000000B000181020004000400FFFF"));

        var frame = document.RenderPage(0);

        Assert.Equal(4, frame.Width);
        Assert.DoesNotContain(document.Diagnostics, notice => notice.Code == "ImageDecodingUnsupported");
        Assert.Contains(document.Diagnostics, notice => notice.Code == "Iw44RenderingLimited");
    }
}
