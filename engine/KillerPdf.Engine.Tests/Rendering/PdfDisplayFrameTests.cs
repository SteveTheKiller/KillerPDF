using System.Buffers.Binary;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfDisplayFrameTests
{
    [Fact]
    public void MonitorConversionHonorsCancellationWithoutChangingTheFrame()
    {
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, .5f], PdfFrameColorEncoding.LinearSrgbD65);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            frame.ToDisplayBgra32(PdfColorProfile.Load(IdentityXyzMonitor()), cancellation.Token));
        Assert.Equal(1f, frame.Pixels[0]);
        Assert.Equal(.5f, frame.Pixels[3]);
    }

    [Theory]
    [InlineData(PdfFrameColorEncoding.SrgbD65, .5f)]
    [InlineData(PdfFrameColorEncoding.LinearSrgbD65, .21404114f)]
    public void EncodedAndLinearGrayProduceTheSameDeviceBytes(PdfFrameColorEncoding encoding, float value)
    {
        var frame = new PdfRenderedFrame(1, 1, [value, value, value, .5f], encoding);
        // IEC sRGB transfer and D50 white give PCS approximately .20638,.21404,.17663.
        // See https://registry.color.org/rgb-registry/files/sRGB.pdf.
        Assert.Equal(new byte[] { 45, 55, 53, 128 }, frame.ToDisplayBgra32(PdfColorProfile.Load(IdentityXyzMonitor())));
    }

    [Fact]
    public void MonitorConversionEnforcesInputAndOutputStorageBudget()
    {
        var monitor = PdfColorProfile.Load(IdentityXyzMonitor());
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);
        Assert.Throws<ArgumentException>(() => frame.ToDisplayBgra32(monitor, maximumCombinedPixelBytes: 19));
        Assert.Equal(new byte[] { 4, 57, 111, 255 }, frame.ToDisplayBgra32(monitor, maximumCombinedPixelBytes: 20));
    }

    [Fact]
    public void DisplayBytesDiagnoseAGrayMonitorBeforeConvertingPixels()
    {
        byte[] bytes = IdentityXyzMonitor()[..156];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 156);
        "GRAY"u8.CopyTo(bytes.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 1);
        "kTRC"u8.CopyTo(bytes.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(136), 144);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(140), 12);
        bytes.AsSpan(144).Clear();
        "curv"u8.CopyTo(bytes.AsSpan(144));
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);

        Assert.Throws<NotSupportedException>(() => frame.ToDisplayBgra32(PdfColorProfile.Load(bytes)));
    }

    [Theory]
    [InlineData("prtr")]
    [InlineData("scnr")]
    [InlineData("spac")]
    public void DeviceOutputRejectsProfilesThatDoNotDescribeAMonitor(string profileClass)
    {
        byte[] bytes = IdentityXyzMonitor();
        System.Text.Encoding.ASCII.GetBytes(profileClass).CopyTo(bytes, 12);
        var profile = PdfColorProfile.Load(bytes);
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, 1f], PdfFrameColorEncoding.LinearSrgbD65);

        Assert.Throws<NotSupportedException>(() => frame.ToDisplayBgra32(profile));
    }

    [Fact]
    public void LinearFrameUsesMonitorDeviceChannelsAtFinalByteBoundary()
    {
        var monitor = PdfColorProfile.Load(IdentityXyzMonitor());
        var frame = new PdfRenderedFrame(1, 1, [1f, 0f, 0f, .5f], PdfFrameColorEncoding.LinearSrgbD65);

        // D50-adapted sRGB red has XYZ .4360747, .2225045, .0139322.
        // This synthetic monitor's identity matrix and curves map PCS directly to RGB.
        Assert.Equal(new byte[] { 4, 57, 111, 128 }, frame.ToDisplayBgra32(monitor));
        Assert.Equal(new byte[] { 0, 0, 255, 128 }, frame.ToSrgbBgra32());
        Assert.Equal(1f, frame.Pixels[0]);
        Assert.Equal(.5f, frame.Pixels[3]);
    }

    [Fact]
    public void MonitorConversionRunsOncePerDistinctColorEvenWhenAlphaChangesAndColorsRepeat()
    {
        // Rows of paper white and ink alternate with varying alpha: only the two colors need converting.
        var pixels = new float[400 * 4];
        for (int pixel = 0; pixel < 400; pixel++)
        {
            bool ink = pixel / 10 % 2 == 1 || pixel % 2 == 1 && pixel >= 200;
            float value = ink ? .25f : 1f;
            pixels[pixel * 4] = pixels[pixel * 4 + 1] = pixels[pixel * 4 + 2] = value;
            pixels[pixel * 4 + 3] = pixel % 3 == 0 ? .5f : 1f;
        }
        var frame = new PdfRenderedFrame(400, 1, pixels, PdfFrameColorEncoding.LinearSrgbD65);
        var monitor = PdfColorProfile.Load(IdentityXyzMonitor());

        byte[] bytes = frame.ToDisplayBgra32(monitor, CancellationToken.None, PdfRenderedFrame.MaximumPixelBytes,
            out long conversions);

        Assert.Equal(2, conversions);
        for (int pixel = 0; pixel < 400; pixel++)
        {
            var single = new PdfRenderedFrame(1, 1, pixels[(pixel * 4)..(pixel * 4 + 4)], PdfFrameColorEncoding.LinearSrgbD65);
            Assert.Equal(single.ToDisplayBgra32(monitor), bytes[(pixel * 4)..(pixel * 4 + 4)]);
        }
    }

    private static byte[] IdentityXyzMonitor()
    {
        var data = new byte[300];
        BinaryPrimitives.WriteUInt32BigEndian(data, 300);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        "RGB "u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), 6);
        string[] signatures = ["rTRC", "gTRC", "bTRC", "rXYZ", "gXYZ", "bXYZ"];
        for (int index = 0; index < signatures.Length; index++)
        {
            int entry = 132 + index * 12;
            System.Text.Encoding.ASCII.GetBytes(signatures[index]).CopyTo(data, entry);
            int offset = index < 3 ? 204 : 216 + (index - 3) * 20;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 8), index < 3 ? 12u : 20u);
            if (index < 3) "curv"u8.CopyTo(data.AsSpan(offset));
            else
            {
                "XYZ "u8.CopyTo(data.AsSpan(offset));
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + 8 + (index - 3) * 4), 65536);
            }
        }
        return data;
    }
}
