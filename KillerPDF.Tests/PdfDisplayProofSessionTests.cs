using System.Buffers.Binary;
using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfDisplayProofSessionTests
{
    [Fact]
    public void FittedFrameProofIsPerRequestAndCapturedFramesStayUnchanged()
    {
        WithDocument(path =>
        {
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 8, 8, null);
            AssertRequestIsolation(proof => session.RenderFittedFrame(0, 8, 8, default, proof),
                () => session.RenderFittedFrame(0, 8, 8, default), 8, 8);
        });
    }

    [Fact]
    public void FittedFrameRegionProofIsPerRequestAndCapturedFramesStayUnchanged()
    {
        WithDocument(path =>
        {
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 8, 8, null);
            var region = new VisibleRenderRegion(2, 2, 4, 4, 2, 2, 4, 4);
            AssertRequestIsolation(proof => session.RenderFittedFrameRegion(0, 8, 8, region, default, proof),
                () => session.RenderFittedFrameRegion(0, 8, 8, region, default), 4, 4);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackgroundLeaseProofIsPerRequestAndReturnedSessionStaysNormal(bool regionOnly)
    {
        WithDocument(path =>
        {
            var cache = new PdfBackgroundRenderCache();
            try
            {
                var request = cache.Capture(path, 0, null);
                using (var lease = request.Rent())
                {
                    var region = new VisibleRenderRegion(2, 2, 4, 4, 2, 2, 4, 4);
                    AssertRequestIsolation(proof => regionOnly
                            ? lease.RenderFrameRegion(0, 8, 8, region, default, proof)
                            : lease.RenderFrame(0, 8, 8, default, proof),
                        () => regionOnly ? lease.RenderFrameRegion(0, 8, 8, region, default)
                            : lease.RenderFrame(0, 8, 8, default), regionOnly ? 4 : 8, regionOnly ? 4 : 8);
                }
                using var reused = request.Rent();
                Assert.All(reused.RenderFrame(0, 8, 8, default).Pixels.ToArray(),
                    channel => Assert.Equal(1f, channel));
            }
            finally { cache.Clear(); }
        });
    }

    private static void AssertRequestIsolation(Func<PdfDocumentProofSettings, PdfRenderedFrame> renderProof,
        Func<PdfRenderedFrame> renderNormal, int width, int height)
    {
        var before = renderNormal();
        float[] original = before.Pixels.ToArray();
        var proof = renderProof(new PdfDocumentProofSettings(enabled: true, printerProfile: TintedGrayPrinter(),
            options: new PdfProofOptions { SimulatePaper = true, SimulateBlack = true,
                BlackPointCompensation = false, RenderingIntent = PdfRenderingIntent.RelativeColorimetric }));
        Assert.Equal(width, proof.Width);
        Assert.Equal(height, proof.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, proof.ColorEncoding);
        float[] capturedProof = proof.Pixels.ToArray();
        // Independent paper-white endpoints for the fixture's XYZ media white.
        for (int offset = 0; offset < capturedProof.Length; offset += 4)
        {
            Assert.InRange(capturedProof[offset], .70841859f, .70842259f);
            Assert.InRange(capturedProof[offset + 1], .96490530f, .96490930f);
            Assert.InRange(capturedProof[offset + 2], .83549785f, .83550185f);
            Assert.Equal(1f, capturedProof[offset + 3]);
        }
        var after = renderNormal();
        Assert.Equal(original, after.Pixels.ToArray());
        Assert.Equal(original, before.Pixels.ToArray());
        Assert.Equal(capturedProof, proof.Pixels.ToArray());
        Assert.All(original, channel => Assert.Equal(1f, channel));
        Assert.Throws<NotSupportedException>(() => renderProof(new PdfDocumentProofSettings(enabled: true)));
        Assert.Equal(original, renderNormal().Pixels.ToArray());
    }

    private static void WithDocument(Action<string> test)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(8, 8).Build());
            test(path);
        }
        finally { PdfPageRenderSession.ForgetDocument(path); File.Delete(path); }
    }

    private static PdfColorProfile TintedGrayPrinter()
    {
        byte[] data = new byte[192];
        Write32(data, 0, data.Length);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 156); Write32(data, 140, 16);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        Write32(data, 148, 172); Write32(data, 152, 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        Write32(data, 164, 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(168), 8192);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(170), 65535);
        "XYZ "u8.CopyTo(data.AsSpan(172));
        Write32(data, 180, 52429); Write32(data, 184, 58982); Write32(data, 188, 45875);
        return PdfColorProfile.Load(data);
    }

    private static void Write32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
