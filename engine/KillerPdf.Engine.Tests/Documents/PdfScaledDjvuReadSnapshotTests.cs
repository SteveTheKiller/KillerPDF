using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfScaledDjvuReadSnapshotTests
{
    [Fact]
    public void EnlargesOrientedFrameInLinearLightAndPreservesIdentity()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page());
        var geometry = snapshot.GetPageGeometry(0);
        var identity = snapshot.LayoutIdentity;
        var source = snapshot.SourceSha256;
        var frame = snapshot.RenderPage(0, new PdfRenderOptions(4, 6, maximumPixelBytes: 960));
        Assert.Equal(4, frame.Width);
        Assert.Equal(6, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(1f, frame.Pixels[0]);
        Assert.Equal(.9375f, frame.Pixels[(1 * 4 + 1) * 4]);
        Assert.Equal(.4375f, frame.Pixels[(2 * 4 + 2) * 4]);
        Assert.Equal(.25f, frame.Pixels[(3 * 4 + 2) * 4]);
        Assert.Equal(0f, frame.Pixels[(5 * 4 + 3) * 4]);
        for (int i = 0; i < frame.Pixels.Length; i += 4)
        {
            Assert.Equal(frame.Pixels[i], frame.Pixels[i + 1]);
            Assert.Equal(frame.Pixels[i], frame.Pixels[i + 2]);
            Assert.Equal(1f, frame.Pixels[i + 3]);
        }
        Assert.Same(geometry, snapshot.GetPageGeometry(0));
        Assert.Equal(identity, snapshot.LayoutIdentity);
        Assert.Equal(source, snapshot.SourceSha256);
    }

    [Fact]
    public void ReducesAndScalesMixedAxesWithoutChangingEarlierFrames()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page());
        var natural = snapshot.RenderPage(0);
        var before = natural.Pixels.ToArray();
        var reduced = snapshot.RenderPage(0, new PdfRenderOptions(1, 1));
        Assert.InRange(reduced.Pixels[0], 2f / 3 - 1e-7f, 2f / 3 + 1e-7f);
        var mixed = snapshot.RenderPage(0, new PdfRenderOptions(1, 6));
        Assert.Equal(1f, mixed.Pixels[0]);
        Assert.Equal(.875f, mixed.Pixels[4]);
        Assert.Equal(.625f, mixed.Pixels[8]);
        Assert.Equal(.5f, mixed.Pixels[20]);
        Assert.Equal(before, natural.Pixels.ToArray());
    }

    [Theory]
    [InlineData((byte)1, 3, 2, 1f, 0f, 1f, 1f)]
    [InlineData((byte)2, 3, 2, 1f, 1f, 0f, 1f)]
    [InlineData((byte)5, 2, 3, 1f, 1f, 1f, 0f)]
    [InlineData((byte)6, 2, 3, 0f, 1f, 1f, 1f)]
    public void ScalesEachIntrinsicOrientationOnce(byte orientation, int width, int height,
        float topLeft, float topRight, float bottomLeft, float bottomRight)
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page(3, 2, orientation));
        var natural = snapshot.RenderPage(0);
        var same = snapshot.RenderPage(0, new PdfRenderOptions(width, height));
        Assert.Equal(natural.Pixels.ToArray(), same.Pixels.ToArray());
        var scaled = snapshot.RenderPage(0, new PdfRenderOptions(width * 2, height * 2));
        Assert.Equal(topLeft, scaled.Pixels[0]);
        Assert.Equal(topRight, scaled.Pixels[(width * 2 - 1) * 4]);
        Assert.Equal(bottomLeft, scaled.Pixels[((height * 2 - 1) * width * 2) * 4]);
        Assert.Equal(bottomRight, scaled.Pixels[(width * height * 4 - 1) * 4]);
    }

    [Fact]
    public void ChecksBudgetAndWorkBeforeDecoding()
    {
        var bytes = Page();
        bytes[42] = 0;
        var malformed = PdfReadSnapshot.OpenDjvu(bytes);
        Assert.Throws<ArgumentException>(() => malformed.RenderPage(0, new PdfRenderOptions(4, 6, maximumPixelBytes: 959)));
        Assert.Throws<InvalidDataException>(() => malformed.RenderPage(0, new PdfRenderOptions(4, 6, maximumPixelBytes: 960)));
        Assert.Throws<OperationCanceledException>(() => malformed.RenderPage(-1, null!, new CancellationToken(true)));
        Assert.Throws<ArgumentNullException>(() => malformed.RenderPage(-1, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => malformed.RenderPage(-1, new PdfRenderOptions(4, 6)));
        Assert.Throws<NotSupportedException>(() => malformed.RenderPage(0, new PdfRenderOptions(4, 6) { SeparationPlate = "Black" }));
        var extreme = Page(1, 32768, 1);
        var tall = PdfReadSnapshot.OpenDjvu(extreme);
        var workError = Assert.Throws<InvalidDataException>(() => tall.RenderPage(0, new PdfRenderOptions(32768, 1)));
        Assert.Contains("weighted work limit", workError.Message);
    }

    private static byte[] Page(int width = 3, int height = 2, byte orientation = 5) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 26, 0, 0x2c, 1, 22, orientation]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }
}
