using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDjvuReadSnapshotTests
{
    [Fact]
    public void UnimplementedDjvuLinkRegionsRemainExplicitlyDiagnosed()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page(300, 1));
        Assert.Throws<NotSupportedException>(() => snapshot.ReadLinkRegions(0));
        Assert.Throws<OperationCanceledException>(() => snapshot.ReadLinkRegions(0, new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.ReadLinkRegions(-1));
    }

    [Fact]
    public void DjvuSnapshotRejectsEnabledProofUntilNativeProofRenderingIsSupported()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page(300, 1));

        var error = Assert.Throws<NotSupportedException>(() => snapshot.RenderPage(0,
            new PdfRenderOptions(3, 2) { Proof = new PdfDocumentProofSettings(enabled: true) }));

        Assert.Contains("proof", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeDjvuKeepsNaturalGeometryAndRendersItsIntrinsicRotationOnce()
    {
        byte[] bytes = Page(300, 5);
        var snapshot = PdfReadSnapshot.OpenDjvu(bytes);
        Array.Clear(bytes);
        Assert.Equal(PdfDocumentFormat.Djvu, snapshot.Format);
        Assert.Matches("^[0-9A-Fa-f]{64}$", snapshot.SourceSha256);
        Assert.Equal(1, snapshot.PageCount);
        var geometry = snapshot.GetPageGeometry(0);
        Assert.Equal(3, geometry.Width);
        Assert.Equal(2, geometry.Height);
        Assert.Equal(PdfReadPageUnit.Pixel, geometry.Unit);
        Assert.Equal(300, geometry.DpiX);
        Assert.Equal(300, geometry.DpiY);
        Assert.Equal(90, geometry.IntrinsicRotation);
        Assert.Equal(0.72, geometry.WidthPoints);
        Assert.Equal(0.48, geometry.HeightPoints);
        var frame = snapshot.RenderPage(0);
        Assert.Equal(2, frame.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        float[] expected = [1, 1, 1, 0, 1, 0];
        for (int pixel = 0; pixel < 6; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
                Assert.Equal(expected[pixel], frame.Pixels[pixel * 4 + channel]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void NativeDjvuRejectsInvalidPagesAndHonorsCancellation()
    {
        var snapshot = PdfReadSnapshot.OpenDjvu(Page(300, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPageGeometry(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPageGeometry(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(1));
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => PdfReadSnapshot.OpenDjvu(Page(300, 5), canceled));
        Assert.Throws<OperationCanceledException>(() => snapshot.RenderPage(-1, canceled));
    }

    [Fact]
    public void NativeDjvuRejectsOversizedInputBeforeCopying()
    {
        byte[] oversized = new byte[65 * 1024 * 1024];
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => PdfReadSnapshot.OpenDjvu(oversized));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("DjVu input exceeds the 64 MiB limit.", error.Message);
        Assert.True(allocated < 1024 * 1024, $"Oversized input allocated {allocated} bytes.");
        Assert.Throws<OperationCanceledException>(() => PdfReadSnapshot.OpenDjvu(oversized, new CancellationToken(true)));
    }

    [Fact]
    public void BundledPagesRetainTheirOwnResolutionAndOrientation()
    {
        byte[] first = Page(300, 1)[4..];
        byte[] second = Page(150, 6)[4..];
        byte[] directory = [0x81, 0, 2, 0, 0, 0, 36, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(directory.AsSpan(7), 36 + first.Length);
        byte[] bytes = [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8,
            .. Chunk("DIRM", directory), .. first, .. second])];
        var snapshot = PdfReadSnapshot.OpenDjvu(bytes);
        Assert.Equal(2, snapshot.PageCount);
        var upright = snapshot.GetPageGeometry(0);
        var rotated = snapshot.GetPageGeometry(1);
        Assert.Equal(300, upright.DpiX);
        Assert.Equal(300, upright.DpiY);
        Assert.Equal(0, upright.IntrinsicRotation);
        Assert.Equal(0.72, upright.WidthPoints);
        Assert.Equal(0.48, upright.HeightPoints);
        Assert.Equal(150, rotated.DpiX);
        Assert.Equal(150, rotated.DpiY);
        Assert.Equal(270, rotated.IntrinsicRotation);
        Assert.Equal(3, rotated.Width);
        Assert.Equal(2, rotated.Height);
        Assert.Equal(1.44, rotated.WidthPoints);
        Assert.Equal(0.96, rotated.HeightPoints);
        Assert.Equal(3, snapshot.RenderPage(0).Width);
        var secondFrame = snapshot.RenderPage(1);
        Assert.Equal(2, secondFrame.Width);
        Assert.Equal(3, secondFrame.Height);
        Assert.Equal(new float[] { 0, 1, 0, 1, 1, 1 }, secondFrame.Pixels.ToArray().Where((_, i) => i % 4 == 0));
    }

    [Fact]
    public void NativeDjvuPropagatesMalformedAndUnsupportedContentDiagnoses()
    {
        Assert.Throws<InvalidDataException>(() => PdfReadSnapshot.OpenDjvu(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => PdfReadSnapshot.OpenDjvu(Page(300, 0)));
        byte[] newerVersion = Page(300, 1);
        newerVersion[28] = 99;
        var unsupported = PdfReadSnapshot.OpenDjvu(newerVersion);
        Assert.Throws<NotSupportedException>(() => unsupported.RenderPage(0));
        byte[] malformedMask = Page(300, 1);
        malformedMask[42] = 0;
        var malformed = PdfReadSnapshot.OpenDjvu(malformedMask);
        var error = Assert.Throws<InvalidDataException>(() => malformed.RenderPage(0));
        Assert.Equal("Invalid Smmr MMR header.", error.Message);
    }

    [Fact]
    public void NativeDjvuDoesNotOpenAContainerWithoutLocalPages()
    {
        byte[] indirect = [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", [1, 0, 0])])];
        var error = Assert.Throws<NotSupportedException>(() => PdfReadSnapshot.OpenDjvu(indirect));
        Assert.Equal("The DjVu container has no local pages available for reading.", error.Message);
    }

    private static byte[] Page(int dpi, byte orientation) => [.. "AT&T"u8,
        .. Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0, 3, 0, 2, 26, 0, (byte)dpi, (byte)(dpi >> 8), 22, orientation]),
            .. Chunk("Smmr", [.. "MMR"u8, 0, 0, 3, 0, 2, 0x23, 0xe1, 0x80, 0x08, 0, 0x80])])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        return [.. Encoding.ASCII.GetBytes(id), .. length, .. payload, .. (payload.Length % 2 == 1 ? new byte[1] : [])];
    }
}
