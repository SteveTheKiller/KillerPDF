using System.Buffers.Binary;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44ProgressiveTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RejectsTruncatedContinuationHeaders(int payloadLength)
    {
        var input = ProgressiveImage()[..(64 + payloadLength + (payloadLength & 1))];
        if ((payloadLength & 1) != 0) input[^1] = 0;
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), input.Length - 12);
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(60), payloadLength);
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        Assert.Contains("Truncated IW44 continuation header", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectsTotalProgressiveBudgetsBeforeImageAllocation(int budget)
    {
        var original = ProgressiveImage();
        byte[] input;
        if (budget == 0)
        {
            input = new byte[56 + 16 * 12];
            original.AsSpan(0, 56).CopyTo(input);
            input[43] = 255;
            for (var serial = 1; serial <= 16; serial++)
            {
                var start = 56 + (serial - 1) * 12;
                original.AsSpan(56, 12).CopyTo(input.AsSpan(start));
                input[start + 8] = (byte)serial;
                input[start + 9] = 255;
            }
        }
        else
        {
            input = original;
            ushort dimension = budget == 1 ? (ushort)320 : (ushort)2800;
            BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(24), dimension);
            BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(26), dimension);
            BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(46), dimension);
            BinaryPrimitives.WriteUInt16BigEndian(input.AsSpan(48), dimension);
            input[43] = budget == 1 ? (byte)255 : (byte)1;
            input[65] = input[77] = budget == 1 ? (byte)255 : (byte)0;
        }
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), input.Length - 12);
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("combined storage or coefficient-work limit", error.Message);
        Assert.InRange(allocated, 0, 1024 * 1024 - 1);
    }

    [Fact]
    public void HonorsCancellationBeforeRenderingProgressiveChunks()
    {
        var document = DjvuDocument.Open(ProgressiveImage());
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
    public void RejectsDuplicateMissingOrReorderedSerials(int variant)
    {
        var original = ProgressiveImage();
        var input = variant switch
        {
            1 => original[..56].Concat(original[68..]).ToArray(),
            2 => original[..56].Concat(original[68..]).Append((byte)0).Concat(original[56..68]).ToArray(),
            _ => original
        };
        if (variant == 0) input[64] = 0;
        if (variant == 3) input[42] = 1;
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), input.Length - 12);
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        Assert.Contains("serial", error.Message);
    }

    [Fact]
    public void RendersAllPixelsAfterTwoChunksAndFortyOneSlices()
    {
        AssertPrefixPixels(68, "QUxWYGt1f4mUoK26xtHb4+fk3tbOx8C6tbW1tbW1tbVBTFZga3V/iZSgrbrG0dvj5+Te1s7HwLq1tbW1tbW1tUFMVmBrdX+JlKCtusbR2+Pn5N7WzsfAurW1tbW1tbW1QUxWYGt1f4mUoK26xtHb4+fk3tbOx8C6tbW1tbW1tbVBTFZga3V/iZSgrbrG0dvj5+Te1s7HwLq1tbW1tbW1tUFMVmBrdX+JlKCtusbR2+Pn5N7WzsfAurW1tbW1tbW1P0pUX2p0fomUoa67x9Pd5enm4NnQycK8t7e3t7e3t7dBTFZga3V/iZSgrbrG0dvj5+Te1s7HwLq1tbW1tbW1tVBYYWpyeoOLlJ6ptL3Hz9XY1c/Hv7ixq6emp6enp6enZGpwdnyCiI6UnKOrsrm+wsTAurOrpJ2XkpKSkpKSkpJ6fYCEh4qNkJSYnaGmqayurqqknZWOiIF9fH19fX19fZGSkpKTk5OTlJWXmJmZmpmXkoyFfndwamVlZWVlZWVlrKmmpKGem5iWlJGPjYqHhIB6c2xlXlZQS0pLS0tLS0vEv7mzrqiinZeSjIeBfHZwamNcVU5GPjcyMTIyMjIyMtjQyMC4sKifl4+HfnZuZl5WT0hBOjIqIx4dHh4eHh4e4djOxLuxp56UioB2bGJYT0dAOjQuJyAaFRUVFRUVFRXUy8K4r6adlIqAdmxiWVBHQDs3NDEtKSUiIiIiIiIiIry0rKSdlY2FfXVsY1pSS0Q/PTw8PDw7Ojo6Ojo6Ojo6n5mTjoiCfHZwaWJbVE1IQ0FBREhMT1JVV1dXV1dXV1eAfHh1cW1qZmJdWFNPSkdFRUlPVl5lbHJ3d3d3d3d3d2BeXVxaWVhXVVJPTUpIR0hLUltmcXyGkJeYl5eXl5eXQkNERUZHSElKSklISElKTVJcaXaEkqCttre2tra2trYoKy4xNTg8P0JERUdJTE9UXGh3h5eouMfS1NLS0tLS0hccISYrMTY7QENHSk5TWF9odYWWp7nL2+bo5ubm5ubmGyAmLDI4PUNJTVJWW2BmbXaDkaCwwNDf6evp6enp6ekoLjQ6QEZMUlddYmdscnh/h5GdqbXDz9vk5eTk5OTk5DpARkxRV11jaW90en+Fi5GYoKixucPM1Nrb2tra2traTVNYXmRqcHV7gYeNk5mfpKqvtLi9wsjN0NHQ0NDQ0NBbYWdtc3l+hIuRmJ6kq7G2ur2/wcLFx8nKysrKysrKymhudHqAho2TmaCnr7W8wsfLzMvKyMfGxsXFxcXFxcXFdXuCiI6Vm6GosLi/x87U2dva19LOycbCwL/AwMDAwMCCiY+WnaOpsLe/yNDY3+Xq7Oji29PMxb+6urq6urq6ug==", "BA5C842C4BA54CBD59C7E88D1BDC89155296EAD20E05863B3488F67831AEFCDB");
    }

    [Fact]
    public void RendersAllPixelsAfterTheFirstThirtyOneSlices()
    {
        AssertPrefixPixels(56, "PUdRXGZweoWPm6eyvsnT2+Lk5OPi4uLi4uLi4uLi4uI9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4j1HUVxmcHqFj5unsr7J09vi5OTj4uLi4uLi4uLi4uLiPUdRXGZweoWPm6eyvsnT2+Lk5OPi4uLi4uLi4uLi4uI9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4j1HUVxmcHqFj5unsr7J09vi5OTj4uLi4uLi4uLi4uLiOkVQWmVveoSPm6izv8rV3uTn5+bk5OTk5OTk5OTk5OQ9R1FcZnB6hY+bp7K+ydPb4uTk4+Li4uLi4uLi4uLi4k1VXmZudn6Hj5mirLW9xc3S09PT0tLS0tLS0tLS0tLSY2ludHl/hIqPlpyiqK6zuLu9vby7u7u7u7u7u7u7u7t7fYCDhYiKjY+SlZibnaCio6SkpKOjo6Ojo6Ojo6Ojo5WUk5OSkZGQj4+OjYyLi4qKioqKioqKioqKioqKioqKsq6qpqGdmZWRjIeDfnp2cm9ubm9vb29vb29vb29vb2/Nxr63sKihmpKKgXlwaWFbV1VVVldXV1dXV1dXV1dXV+PZz8W7saeckoZ7b2RZUEdBPj4/QUFBQUFBQUFBQUFB7eHVyr6zp5uPgnVoW05DOTIvLzAyMjIyMjIyMjIyMjLe08i9sqidkod7bmJWS0A3MS4uLzExMTExMTExMTExMcK6saifl46GfXNpX1VMRDw3NTU2Nzc3Nzc3Nzc3Nzc3opyWkIqEfnhxa2RdVlBKRUFAQEBBQUFBQUFBQUFBQUF+e3h1cm9saWZjX1xZVlNQT05OTk9PT09PT09PT09PT1paWltbW1tbW1xcXFxdXV1dXV1dXV1dXV1dXV1dXV1dODw/QkVJTE9SVlpeYWVoam1tbW1tbG1tbW1tbW1tbW0bISctMzk/RkxTWmFnbnR5fX5+fn19fX19fX19fX19fQkRGSEqMjpCS1RdZ3B4gIeNjo6OjYyNjY2NjY2NjY2NDRYeJzA4QUpTXWdxeoSMlJmbm5qZmZmZmZmZmZmZmZkcJS02PkdPWGBqdH2HkJifpKampaSkpKSkpKSkpKSkpDE5QEhQWGBocHmCi5ScpKqvsbGwr6+vr6+vr6+vr6+vRk5VXGNqcnmAiJGZoaivtbq7vLu6urq6urq6urq6urpXXmRrcnmAh46WnqWttLvBxcfHxsXFxcXFxcXFxcXFxWVsc3qAh42Um6OqsrnAx8zR0tLR0dHR0dHR0dHR0dHRdXuCiI+Vm6KosLe+xczT2Nzd3d3c3Nzc3Nzc3Nzc3NyEi5GXnaOpr7a9xMvS2N7j5+np6Ofn5+fn5+fn5+fn5w==", "F6E0AB6ED376F7780839496BCE687F5FA98CCD8CD18892713A6BB7920EFC9442");
    }

    private static void AssertPrefixPixels(int length, string oracle, string oracleHash)
    {
        var input = ProgressiveImage()[..length];
        BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(8), input.Length - 12);
        var expected = Convert.FromBase64String(oracle);
        Assert.Equal(oracleHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)));
        var frame = DjvuDocument.Open(input).RenderPage(0);
        Assert.Equal(32, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(4096, frame.Pixels.Length);
        for (var pixel = 0; pixel < expected.Length; pixel++)
        {
            var linear = (float)Math.Pow(expected[pixel] / 255.0, 2.2);
            for (var channel = 0; channel < 3; channel++)
                Assert.Equal(linear, frame.Pixels[pixel * 4 + channel]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void RendersAllPixelsAcrossThreeProgressiveBackgroundChunks()
    {
        var input = ProgressiveImage();
        var expected = Convert.FromBase64String(FinalGray);
        Assert.Equal("CCE2C2C9FBA279962EA0D432B6F94C097EB14DCEF8BE4883E8D5045B8AC13153",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)));
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var frame = document.RenderPage(0);
        Assert.Equal(32, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(4096, frame.Pixels.Length);
        // Frozen public grayscale bytes are independent of this decoder.
        // Linear expectations apply the declared INFO gamma of 2.2.
        for (var pixel = 0; pixel < expected.Length; pixel++)
        {
            var linear = (float)Math.Pow(expected[pixel] / 255.0, 2.2);
            Assert.Equal(linear, frame.Pixels[pixel * 4]);
            Assert.Equal(linear, frame.Pixels[pixel * 4 + 1]);
            Assert.Equal(linear, frame.Pixels[pixel * 4 + 2]);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    private static byte[] ProgressiveImage()
    {
        var input = Convert.FromBase64String(EncodedImage);
        Assert.Equal("CE518E90B8FCCC5E4DF128EAB1D8D6451E9378766146608E2383E74E255AFB93",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)));
        return input;
    }

    private const string EncodedImage = "QVQmVEZPUk0AAAHlREpWVUlORk8AAAAKACAAIBgAZAAWAUJHNDQAAAANAB+BAgAgACAArrNCvwBCRzQ0AAAABAEK31dCRzQ0AAABpQI7/ZTSbGTEJAYYAwIjW+/LbF4K2Cta9tnv7cH6Tmq97SMYVirU/6m2DLwtDUiBJa5abUe6/WMujcU/Lh2Rtt3/pvnyi8csS6D37A908IzsPLN41F1qSMoavpIj8PO4u5tVHTc15OrAvPWS1MvbGJQ+wQ7VMtBqDPtyOjW0Fp6hkgypCB1kVnlk6Fuv9jwQTrCc6OdI8qoDUq3BdBZGIfa9FVD/35dK0UvrBxDJD6GLV0llycy0IdtOfY6/Isbv6skRpPUUXXzhOQl+D4JY3HhmaXh5xFPunFEtRODTayl8J7BpaC5rdz4oNmYJQA+pvfJ8eUBU4aueczmUqT28ABnSOVsD3xHOuf5GcAADfAl7deq0K5j7Pbg33RXsGd14KljZ5O1Ky71CIvTtIMu5RUveyLstDE5LkXmVqI4RfT7VVYlSZGLizUEQzpHTilDXK5dFkuhmS6yTxU8AtHtTxtcL3M9T2qWq2c5UJjRSpImQe7q5a7KygGVapsGzF1YrgIk7YmuZPlw2TyJPeZTKzQGZgh6Fx0I5UIDWmd9qN2XKnj51V2I=";
    private const string FinalGray = "AAgSGURMTFY7P0xNdHd1hXSGoKjX7v3/8v3///////8JEx0kT1dXYUZKV1h/goCQf5Grs+L5///9//////9GSBYeJi5ZY2RtUVNdYYmOjpuJmrK97P//////////TFZaIykvN2NvcnhbXGRqkpqbppOiucf2/////////1lfYmhTVVZiSVFfYIaLipd/go2SwdHc9OTy////UVRU////R1pfY2hUWmZtjpWXoI+QmaPM3OX05f///1NaZmb/T1lfam90e1xgbHOgqKiylpSfq9js+P/y////V2htdVdaZWpyeH6HbXB7gaqysryjoq663PL///z///9nbnp8YGJwc1hibXKboqKulZego8zP3NzF3Oj3Ki1IWD9DSlR+jI6PZm52gaWtr7aioqmt1OXr89Tc5PQnOEhLNjhDSHJ7gIBqc3yDsby+xaSosbfn7PL94d/x/h8xQD4oKjY7bHZ8fHiAiI21wsbMr7rGwfT9AAHt9Pn7LTE3PCIlMTVcZm9vq62wu6CkrL7h7vT62OLm7RsmJSUDBwsNNj08SC43QEC0uLzDr7K4vurz/QXi5vL4IScoJAkBAQYoLy81Iy44OMHHzNO1tsPL9P8MEfP+/wIsLC4jBAAAACQrKzQXICkpxs3V3MfI09YBBxMd/QQLETo8Mi8MBQYAISgqNRwjKSmttr/G9fn9Bubu9P8tMz1IJx8fGTo5LzISFSImT1RaWr3FzdH/BAoM8vcEDjhDSU0xKSksSktFRi8xPEVna29vyc3R3AkQFhX/CQ0URVVVWTUyODlgZWFnSElTWoeOlJTP1+DoEBYjJg8SGh9OT1deQUVPTnF4eYNqbHV5oqu0tAMIDBf6/wYTOj1KUTY2R0dsdnmEaW16f6mztb6gqa+wDRMZJQYNGRlERk9VQUZQVXWBiJOEiZWgxdXb3MDKz9IXHSQqDRQgIE9XXWVGSFNZh5WbrJadrLjr+///3+3w9SEoMDocJC0vVF9kbVJTXWWRn6e7q7bH1f//////////BhAaHkZSU1w/RVNVe4GBkXyGmaXY6/b/9fv//////z8UGyEqTVpeY05SXGKCio2YipOjs+D3/////////0RMUhohJy9eaWtzVVZgZpOcnKiPl6m48P////////9GTFNXKTA3PmdvcHtjZGxznaSlsZukt8X4/////////01VXF5bYGRuUVRfZI+VlKCHipWbyNjh9eP3/f87QEhS//9BQ2RsdXheX2lzmKGjqZSWoKrS5fL+7vv//0dRVWJCRlBQb3d+hGdqdXylrq+3naCrst3v+v/2////VFxga0xUW1t6gYeQcHSBhrK7u8Wmqra66fn///////9iZ2t1VWJnZw==";
}
