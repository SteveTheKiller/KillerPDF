using System.IO.Compression;
using System.Security.Cryptography;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44StepBoundaryTests
{
    [Fact]
    public void CoarseStepAcrossPartialBlocksMatchesFrozenNativePixels()
    {
        // Independently encoded 73x37 step, 31 grayscale slices, INFO gamma 2.2.
        // Frozen DjVuLibre 3.5.29 public PGM output is a compatibility oracle.
        byte[] input = Convert.FromBase64String("QVQmVEZPUk0AAAAuREpWVUlORk8AAAAKAEkAJRgAZAAWAUJHNDQAAAAQAB+BAgBJACUA4TG/LSWXPw==");
        Assert.Equal("A1645F4C78F97DCB05FFC1EAAC56E449EC30622F2BBA8CCAD1104F0CC864E75C",
            Convert.ToHexString(SHA256.HashData(input)));
        using var packed = new MemoryStream(Convert.FromBase64String(
            "eNoTEhYRE5eQkpaVU1BUUlFVU9cAAU1NLW0dXX1DEwtbF9/wxJzyxt4Zi9duP3jq4vWbN29cu3Lx3OmTJ44dPXLo4AEYEBo1aNSgUYNGDRo1aNSgUYNGDRo1aNQgEg0CAKxCs3Y="));
        using var decoder = new ZLibStream(packed, CompressionMode.Decompress);
        using var samples = new MemoryStream();
        decoder.CopyTo(samples);
        byte[] expected = samples.ToArray();
        Assert.Equal(2701, expected.Length);
        Assert.Equal("0A1E5E10F57A95C80D6FFCD245C7AE9CF9B7E8D22A8403B7A463FBD07F85212C",
            Convert.ToHexString(SHA256.HashData(expected)));
        var frame = DjvuDocument.Open(input).RenderPage(0);
        Assert.Equal(73, frame.Width);
        Assert.Equal(37, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        var differences = new List<int>();
        for (int pixel = 0; pixel < expected.Length; pixel++)
        {
            float linear = (float)Math.Pow(expected[pixel] / 255d, 2.2);
            if (frame.Pixels[pixel * 4] != linear || frame.Pixels[pixel * 4 + 1] != linear
                || frame.Pixels[pixel * 4 + 2] != linear || frame.Pixels[pixel * 4 + 3] != 1f)
                differences.Add(pixel);
        }
        Assert.True(differences.Count == 0, $"Differing native compatibility pixels: {differences.Count}/2701");
    }
}
