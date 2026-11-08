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
        AssertPrefixPixels(68, "QUxWYGp0foiToKy5xNDZ4eXj3NXMxb+4tLO0tLS0tLRBTFZganR+iJOgrLnE0Nnh5ePc1czFv7i0s7S0tLS0tEFMVmBqdH6Ik6CsucTQ2eHl49zVzMW/uLSztLS0tLS0QUxWYGp0foiToKy5xNDZ4eXj3NXMxb+4tLO0tLS0tLRBTFZganR+iJOgrLnE0Nnh5ePc1czFv7i0s7S0tLS0tEFMVmBqdH6Ik6CsucTQ2eHl49zVzMW/uLSztLS0tLS0P0pUX2lzfoiToK26xtHb4+fl39fPx8G6trW2tra2trZBTFZganR+iJOgrLnE0Nnh5ePc1czFv7i0s7S0tLS0tFBYYWlyeoKKk56osrzGzdTX083GvrewqqWkpaWlpaWlZGpwdnyBh42Tm6Kqsbe9wcK/uLGpopyVkZCRkZGRkZF6fYCDhomMkJOXnKCkqKutrKiim5SNhoB7e3t7e3t7e5GSkpKSkpOTk5SWl5eYmJeVkIqEfHVvaGRjZGRkZGRkrKmmo6Cdm5iVk5COi4mGgn54cmtkXFVOSUhJSUlJSUnEv7mzraiinJaRi4WAenRuaGFaVExEPTYwMDAwMDAwMNjQyMC3r6eflo6FfXVsZFxUTUY/ODApIhwcHBwcHBwc4djOxLqxp52TiX91amBXTkU+ODIsJR8YFBMUFBQUFBTUy8K4r6ack4qAdmxiWE9GPzo2MzAsKCQhISEhISEhIby0rKSdlY2FfXRsY1pSSkQ/PDw8PDw7Ojo6Ojo6Ojo6n5mUjoiCfHZwaWJbVE5IREFCRUhMT1JVV1hXV1dXV1eAfHh1cW5rZ2NeWVRQTEhGR0tRWF9mbXR4eXh4eHh4eGBfXVxbWllYVlRRTkxKSkpNVF5oc36JkpmamZmZmZmZQkNERkdISktMS0tLS0tNUFVfbHmHlaOwubq5ubm5ubkoKy8yNjk9QURGR0lMT1NYYGx7ipusvMvW19bW1tbW1hcdIicsMjc9QUVJTVFWXGJseYmaq73P3+rs6urq6urqGyEnLTM5P0VKT1RZXmNqcXqHlaSzxNTi7e7t7e3t7e0oLjQ6QUdNU1leZGlvdHuCipSgrLjF0t7n6Ofn5+fn5zpARkxSWF5kanB1e4GHjZOaoqqzu8XO1t3d3d3d3d3dTVNZXmRqcHZ8goiOlJqgpauwtbm+xMnO0dLR0dHR0dFbYWdtc3l/hYuRmJ6lq7G2u77AwcPFx8nLy8vLy8vLy2hudHqAhoyTmaCnrrW8wsfLy8vJyMfGxsXFxcXFxcXFdXuCiI6Um6Gor7e/xs3T2NrZ1tHNycXBv7+/v7+/v7+CiY+WnKOpr7a+x8/X3uTp6ufh2dHKxL25uLm5ubm5uQ==", "7761F46AEE176D73AD374761789D98FB87351FB6DED3247714D109827A6A4322");
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
        Assert.Equal("24256ED5B656C763B7029EC64EC7A0885F076E2E8282740B84E2B7F41890FD8F",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)));
        var document = DjvuDocument.Open(input);
        Assert.Single(document.Pages);
        var frame = document.RenderPage(0);
        Assert.Equal(32, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(4096, frame.Pixels.Length);
        // DjVuLibre 3.5.29 ddjvu -format=pgm bytes are independent of this decoder.
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
    private const string FinalGray = "AAgSGERLS1U6PkpMcnVzg3GEnqbU6/r/8Pv///////8JEx0jT1ZWYEVJVVd9gH6OfI+psd/2///6//////9DRhYeJi1YYmNsT1JcYIeMjJmHl7C76f//////////SlRYIykvN2JucXdaWmNokZiZpJGgt8T0/////////1ddYGZTVVZiSVBeX4WKiJZ9gIuQv8/Z8eLw////T1JR////RVpfY2hUWWVtjZSVno2Ol6HJ2ePx4////1FYY2T/TVZdam90e1xfbHKfp6exlJKdqdbq9f/w////VGZqc1RYY2hyeH2HbG96gKmxsbuhoKy42vD///r///9la3h6Xl9ucVhibHGboaGtlJafocrN2tnD2uX1KCtGVj1BR1J8ioyNZm52gKStrrWhoair0uTp8dLa4vIlNkZJNDZARnB5fn5qc3uDsLy+xKOnsLbm6vD7393v/B0vPjwmKDQ5anR6eniAiIy1wcXLrrnFwPP7AADr8vf6Ky81OiAjLzNaZG1tq62vu6Cjq73g7fP41+Hk6xkkIyMBBgkLNDs6Riw1Pj60uLzDr7G4veny/APh5PH3HyUmIgcAAAQmLS0zISw2NsHHzNO0tcLK8/8LEPH8/wAqKiwhAwAAACIpKTIWHicnxs3U3MfI09UBBhIc/AMJEDg6MC0KAwQAHyYoMxshJyettr/F9fn9BuXt9P8sMjxHJh4eGDk4LjERFCElTlNZWb3FzdH/BAoM8vcEDjdDSU0wKSksSktFRi8xPEVna29vyc3R3QkQFhX/CQ0VRVZVWTYzOTphZWJoSUpUWoiOlZXP1+DoERYjJxASGyBPUFlfQ0ZRUHJ5eoVsbnd7o622tgMIDBf6/wcUOz5MUjg4SUlveXuGa3B8gay2t8Ciq7GyDRMaJgcOGhtFSFFXREhTWXmEi5eIjZijyNje38TN0tUXHiQqDhUhIlFZX2hJTFddi5ifsJmgsLzu////4/H0+SEpMDodJi8xVmFmcFVXYWiVo6u/r7rL2f//////////BhAbH0dUVV5AR1VXfoSFlICKnKnc7/n/+f7//////0MUGyEqTlpfZU9TXmSEjI+ajZWltuP6/////////0dPVRohKC9eaWt0VldhaJSdnqqRmKq68v////////9HTlRZKTA4PmdvcHtjZG1znaWlsZylt8X4/////////05VXF9bYGRuUVRfZI+VlKCHipWbyNjh9eP2/f87QEdR//9BQmRsdHdeX2lyl6GiqZOVn6nR5PH97fv//0ZQVGFBRU9Pb3Z+g2dpdHukra62nJ6psNvt+P/0////U1peakpSWlp6gIePb3OAhLG6usOkp7S45vb///z///9fZWhyU19kZA==";
}
