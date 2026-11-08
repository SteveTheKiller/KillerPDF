using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfDeviceCmykTests
{
    // Untagged process inks display through the built-in CGATS TR 001 working profile. Expected
    // colors are LittleCMS 2 (LibreOffice's lcms2.dll) evaluating the same bundled profile to its
    // built-in sRGB, perceptual intent, no optimization; the engine agrees within one code.
    [Theory]
    [InlineData(0, 0, 0, 0, 255, 255, 255)]
    [InlineData(255, 0, 0, 0, 0, 174, 239)]
    [InlineData(0, 255, 0, 0, 236, 0, 140)]
    [InlineData(0, 0, 255, 0, 255, 242, 0)]
    [InlineData(0, 0, 0, 255, 35, 31, 32)]
    [InlineData(255, 255, 255, 255, 0, 0, 0)]
    [InlineData(192, 128, 64, 128, 41, 69, 93)]
    [InlineData(255, 255, 0, 0, 46, 48, 146)]
    public void ProcessInksShowTheWorkingProfileForPathsAndImages(
        byte c, byte m, byte y, byte k, byte red, byte green, byte blue)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillCmyk(c / 255d, m / 255d, y / 255d, k / 255d)
            .Rectangle(0, 0, 1, 1).Fill()
            .DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { c, m, y, k }), 1, 0, 1, 1);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
        var page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 1));
        Assert.Empty(page.Diagnostics);
        byte[] expected = [blue, green, red, 255, blue, green, red, 255];
        byte[] actual = page.Pixels.ToArray();
        for (int channel = 0; channel < expected.Length; channel++)
            Assert.InRange(actual[channel], expected[channel] - 1, expected[channel] + 1);
    }

    // Every surface shows the managed (float) result: the byte path converts each of the 9^4
    // grid inks exactly as a managed frame of the same page does.
    [Fact]
    public void FourDimensionalGridMatchesTheManagedWorkingProfileResult()
    {
        byte[] values = [0, 32, 64, 96, 128, 160, 192, 224, 255];
        const int side = 81;
        var content = new StringBuilder();
        int index = 0;
        foreach (byte c in values)
            foreach (byte m in values)
                foreach (byte y in values)
                    foreach (byte k in values)
                    {
                        content.Append(string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} k {4} {5} 1 1 re f\n",
                            c / 255d, m / 255d, y / 255d, k / 255d, index % side, side - 1 - index / side));
                        index++;
                    }
        var renderer = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(side, side, Encoding.ASCII.GetBytes(content.ToString())).Build()));

        byte[] managed = renderer.RenderFrame(0, new PdfRenderOptions(side, side)).ToSrgbBgra32();
        byte[] bytes = renderer.Render(0, new PdfRenderOptions(side, side)).Pixels.ToArray();

        int mismatches = 0;
        for (int channel = 0; channel < bytes.Length; channel++)
            if (Math.Abs(bytes[channel] - managed[channel]) > 1) mismatches++;
        Assert.Equal(0, mismatches);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(32, 32, 32)]
    [InlineData(64, 64, 64)]
    [InlineData(128, 128, 128)]
    [InlineData(192, 192, 192)]
    [InlineData(240, 240, 240)]
    [InlineData(255, 255, 255)]
    [InlineData(128, 96, 64)]
    [InlineData(64, 96, 128)]
    public void InGamutRgbPaintsAvoidWashedOutRoundTrips(byte red, byte green, byte blue)
    {
        uint displayed = PdfDeviceCmyk.ToRgb(PdfDeviceCmyk.FromRgb(red, green, blue));
        Assert.InRange(Math.Abs((byte)(displayed >> 16) - red), 0, 4);
        Assert.InRange(Math.Abs((byte)(displayed >> 8) - green), 0, 4);
        Assert.InRange(Math.Abs((byte)displayed - blue), 0, 4);
    }

    internal static byte[] RenderInk(byte c, byte m, byte y = 0, byte k = 0)
    {
        var content = new PdfContentStreamBuilder().SetFillCmyk(c / 255d, m / 255d, y / 255d, k / 255d)
            .Rectangle(0, 0, 1, 1).Fill();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, content).Build());
        return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Pixels.ToArray();
    }
}
