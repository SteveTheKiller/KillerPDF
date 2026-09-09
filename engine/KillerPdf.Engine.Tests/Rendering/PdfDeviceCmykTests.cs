using System.Buffers.Binary;
using System.Security.Cryptography;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfDeviceCmykTests
{
    // Interior swatches rendered by the retained PDFium application at native size.
    [Theory]
    [InlineData(0, 0, 0, 0, 255, 255, 255)]
    [InlineData(255, 0, 0, 0, 0, 174, 239)]
    [InlineData(0, 255, 0, 0, 237, 2, 140)]
    [InlineData(0, 0, 255, 0, 255, 241, 1)]
    [InlineData(0, 0, 0, 255, 35, 31, 32)]
    [InlineData(255, 255, 255, 255, 0, 0, 0)]
    [InlineData(192, 128, 64, 128, 42, 69, 93)]
    public void ProcessInksMatchLegacyDisplayForPathsAndImages(
        byte c, byte m, byte y, byte k, byte red, byte green, byte blue)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillCmyk(c / 255d, m / 255d, y / 255d, k / 255d)
            .Rectangle(0, 0, 1, 1).Fill()
            .DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { c, m, y, k }), 1, 0, 1, 1);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
        var page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 1));
        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] { blue, green, red, 255, blue, green, red, 255 }, page.Pixels.ToArray());
    }

    [Fact]
    public void FourDimensionalGridMatchesRetainedLegacySwatches()
    {
        byte[] values = [0, 32, 64, 96, 128, 160, 192, 224, 255];
        byte[] colors = new byte[6561 * 3];
        int offset = 0;
        foreach (byte c in values)
            foreach (byte m in values)
                foreach (byte y in values)
                    foreach (byte k in values)
                    {
                        uint rgb = PdfDeviceCmyk.ToRgb(c | (uint)m << 8 | (uint)y << 16 | (uint)k << 24);
                        colors[offset++] = (byte)(rgb >> 16);
                        colors[offset++] = (byte)(rgb >> 8);
                        colors[offset++] = (byte)rgb;
                    }
        Assert.Equal("ECF0A9B57277A62BB4B58D98EB68FADEACEE2436C523CE8D693D8B3DA1EFDF2C",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(colors)));
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

    [Fact]
    public void InverseMatchesScalarReferenceForEveryRgbColor()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = stackalloc byte[256 * 4];
        for (int red = 0; red < 256; red++)
            for (int green = 0; green < 256; green++)
            {
                for (int blue = 0; blue < 256; blue++)
                    BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(blue * 4, 4),
                        PdfDeviceCmyk.FromRgb((byte)red, (byte)green, (byte)blue));
                hash.AppendData(row);
            }
        // Retained scalar output for all 16,777,216 RGB inputs, packed as little-endian inks.
        Assert.Equal("4ED2006AF8C38E589C435F156B3E6A79C7EE8482E063674943190C55CDEC2A17",
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal static byte[] RenderInk(byte c, byte m, byte y = 0, byte k = 0)
    {
        var content = new PdfContentStreamBuilder().SetFillCmyk(c / 255d, m / 255d, y / 255d, k / 255d)
            .Rectangle(0, 0, 1, 1).Fill();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, content).Build());
        return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(1, 1)).Pixels.ToArray();
    }
}
