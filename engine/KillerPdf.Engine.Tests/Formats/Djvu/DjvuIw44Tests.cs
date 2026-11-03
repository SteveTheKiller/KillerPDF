using System.Buffers.Binary;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44Tests
{
    [Fact]
    public void RejectsCombinedWorkingStorageBeforeAllocatingImageBuffers()
    {
        var input = OneSliceImage();
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(24), 2800);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(26), 2800);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(46), 2800);
        BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(48), 2800);
        var document = DjvuDocument.Open(input);
        var page = Assert.Single(document.Pages);
        Assert.Equal(2800, page.Width);
        Assert.Equal(2800, page.Height);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("combined storage", error.Message);
        Assert.InRange(allocated, 0, 1024 * 1024 - 1);
    }

    [Fact]
    public void HonorsCancellationBeforeRenderingIw44()
    {
        var document = DjvuDocument.Open(OneSliceImage());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(() => document.RenderPage(0, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RejectsTruncatedInitialHeadersInsideValidContainers(int payloadLength)
    {
        var input = new byte[42 + payloadLength + (payloadLength & 1)];
        OneSliceImage().AsSpan(0, 42 + payloadLength).CopyTo(input);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), input.Length - 12);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(38), payloadLength);
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        Assert.Contains("Truncated IW44 initial chunk", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RejectsDuplicateOrProgressiveBackgroundChunks(byte secondSerial)
    {
        var original = OneSliceImage();
        var input = new byte[74];
        original.CopyTo(input, 0);
        original.AsSpan(34, 19).CopyTo(input.AsSpan(54));
        input[62] = secondSerial;
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), input.Length - 12);
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var error = Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
        Assert.Contains("Progressive IW44 background chunks", error.Message);
    }

    [Fact]
    public void RejectsColorIw44Explicitly()
    {
        var input = OneSliceImage();
        input[44] = 1;
        var document = DjvuDocument.Open(input);
        var error = Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
        Assert.Contains("Color IW44", error.Message);
    }

    public static TheoryData<byte, float[]> OrientedRampPixels => new()
    {
        { 1, [0f, 0.117647059f, 0.254901961f, 0.376470588f,
            0.498039216f, 0.635294118f, 0.772549020f, 0.894117647f,
            1f, 0.886274510f, 0.760784314f, 0.6f,
            0.498039216f, 0.376470588f, 0.254901961f, 0.125490203f] },
        { 2, [0.125490203f, 0.254901961f, 0.376470588f, 0.498039216f,
            0.6f, 0.760784314f, 0.886274510f, 1f,
            0.894117647f, 0.772549020f, 0.635294118f, 0.498039216f,
            0.376470588f, 0.254901961f, 0.117647059f, 0f] },
        { 5, [0.498039216f, 1f, 0.498039216f, 0f,
            0.376470588f, 0.886274510f, 0.635294118f, 0.117647059f,
            0.254901961f, 0.760784314f, 0.772549020f, 0.254901961f,
            0.125490203f, 0.6f, 0.894117647f, 0.376470588f] },
        { 6, [0.376470588f, 0.894117647f, 0.6f, 0.125490203f,
            0.254901961f, 0.772549020f, 0.760784314f, 0.254901961f,
            0.117647059f, 0.635294118f, 0.886274510f, 0.376470588f,
            0f, 0.498039216f, 1f, 0.498039216f] }
    };

    [Theory]
    [MemberData(nameof(OrientedRampPixels))]
    public void RendersAllRampPixelsInTheDeclaredOrientation(byte orientation, float[] expected)
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000039444A5655494E464F0000000A00040004180064001601" +
            "424734340000001B006481020004000400FFD5A537F9348F11CFC657F6ACD695FCA23C");
        Assert.Equal("2BB5BDAFDD29B7D9D16540DDE65ACAB060C4A7D5E77173D6CD8415911368CD34",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        input[32] = 10;
        input[33] = orientation;
        var frame = DjvuDocument.Open(input).RenderPage(0);
        Assert.Equal(4, frame.Width);
        Assert.Equal(4, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        AssertPixels(frame, expected);
    }

    [Fact]
    public void RejectsZeroGammaExplicitly()
    {
        var input = OneSliceImage();
        input[32] = 0;
        var document = DjvuDocument.Open(input);
        var error = Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
        Assert.Contains("positive INFO gamma", error.Message);
    }

    [Theory]
    [InlineData(10, 0.49803922f)]
    [InlineData(18, 0.28515065f)]
    [InlineData(22, 0.2157644f)]
    [InlineData(28, 0.1420162f)]
    public void AppliesDeclaredGammaToTheNormativeConstant(byte gamma, float expected)
    {
        var input = OneSliceImage();
        input[32] = gamma;
        var frame = DjvuDocument.Open(input).RenderPage(0);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(4, frame.Width);
        Assert.Equal(4, frame.Height);
        AssertPixels(frame, Enumerable.Repeat(expected, 16).ToArray());
    }

    private static byte[] OneSliceImage() => Convert.FromHexString(
        "41542654464F524D00000029444A5655494E464F0000000A00040004180064000A01" +
        "424734340000000B000181020004000400FFFF");

    private static void AssertPixels(PdfRenderedFrame frame, float[] expected)
    {
        Assert.Equal(expected.Length * 4, frame.Pixels.Length);
        for (var pixel = 0; pixel < expected.Length; pixel++)
        {
            Assert.Equal(expected[pixel], frame.Pixels[pixel * 4]);
            Assert.Equal(expected[pixel], frame.Pixels[pixel * 4 + 1]);
            Assert.Equal(expected[pixel], frame.Pixels[pixel * 4 + 2]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void RendersAllOwnedRampPixelsWithTheDeclaredGamma()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000039444A5655494E464F0000000A00040004180064001601" +
            "424734340000001B006481020004000400FFD5A537F9348F11CFC657F6ACD695FCA23C");
        Assert.Equal("2BB5BDAFDD29B7D9D16540DDE65ACAB060C4A7D5E77173D6CD8415911368CD34",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        float[] expected = [0f, 0.0090214919f, 0.0494334576f, 0.116575776f,
            0.2157644f, 0.368590922f, 0.566809735f, 0.781750678f,
            1f, 0.766743617f, 0.547993502f, 0.325036963f,
            0.2157644f, 0.116575776f, 0.0494334576f, 0.0103978023f];
        var frame = DjvuDocument.Open(input).RenderPage(0);
        Assert.Equal(4, frame.Width);
        Assert.Equal(4, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(64, frame.Pixels.Length);
        for (var pixel = 0; pixel < expected.Length; pixel++)
        {
            Assert.Equal(expected[pixel], frame.Pixels[pixel * 4]);
            Assert.Equal(expected[pixel], frame.Pixels[pixel * 4 + 1]);
            Assert.Equal(expected[pixel], frame.Pixels[pixel * 4 + 2]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void RendersTheNormativeOneSliceGrayscaleImageAsLinearPixels()
    {
        var input = Convert.FromHexString(
            "41542654464F524D00000029444A5655494E464F0000000A00040004180064000A01" +
            "424734340000000B000181020004000400FFFF");
        Assert.Equal("6B95409CE949820FF3D485A46C0B96601F35B26B95F8E59CDB86E486BFBC3371",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        var document = DjvuDocument.Open(input);
        var page = Assert.Single(document.Pages);
        Assert.Equal(4, page.Width);
        Assert.Equal(4, page.Height);

        var frame = document.RenderPage(0);
        Assert.Equal(4, frame.Width);
        Assert.Equal(4, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(64, frame.Pixels.Length);
        for (var pixel = 0; pixel < 16; pixel++)
        {
            Assert.Equal(0.49803922f, frame.Pixels[pixel * 4]);
            Assert.Equal(0.49803922f, frame.Pixels[pixel * 4 + 1]);
            Assert.Equal(0.49803922f, frame.Pixels[pixel * 4 + 2]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }
}
