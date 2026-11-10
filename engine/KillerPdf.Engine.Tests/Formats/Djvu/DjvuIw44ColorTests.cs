using System.Security.Cryptography;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuIw44ColorTests
{
    [Fact]
    public void QualifiedOwnedRedFieldRendersOpaqueColorAtNaturalDimensions()
    {
        byte[] source = OwnedRedField(), original = source.ToArray();
        Assert.Equal("52162E0DE10790736423A65169800DFF1ACCE298A4B8CEA43472876E6A1F0096",
            Convert.ToHexString(SHA256.HashData(source)));
        var document = DjvuDocument.Open(source);
        var page = Assert.Single(document.Pages);
        Assert.Equal(32, page.Width);
        Assert.Equal(32, page.Height);
        Assert.Equal(DjvuOrientation.Upright, page.Orientation);
        try
        {
            var frame = document.RenderPage(0);
            Assert.Equal(32, frame.Width);
            Assert.Equal(32, frame.Height);
            Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
            // The owned literal red input has native display RGB [255,1,1] at every pixel.
            // These are initial color-capability bounds, not an exact linear-color oracle.
            for (int pixel = 0; pixel < 1024; pixel++)
            {
                Assert.InRange(frame.Pixels[pixel * 4], .99f, 1f);
                Assert.InRange(frame.Pixels[pixel * 4 + 1], 0f, .01f);
                Assert.InRange(frame.Pixels[pixel * 4 + 2], 0f, .01f);
                Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
            }
        }
        finally
        {
            Assert.Equal(original, source);
        }
    }

    // Header byte 50 is the chroma byte: 0 codes chrominance at half resolution with no delay. DjVuLibre 3.5.29 ddjvu
    // renders this file, and the 31 x 32 one below, as RGB [255,1,1] at every pixel. PDF conversion stays grayscale only.
    [Fact]
    public void HalfResolutionChromaRendersNativelyAndConversionKeepsItsRefusal()
    {
        byte[] source = OwnedRedField(), original;
        source[50] = 0;
        original = source.ToArray();
        var document = DjvuDocument.Open(source);

        AssertOwnedRed(document.RenderPage(0), 32);
        var conversion = Assert.Throws<NotSupportedException>(() => document.ConvertToPdf());

        Assert.Equal("Color IW44 decoding is not supported.", conversion.Message);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(44, 2)]
    [InlineData(45, 3)]
    [InlineData(47, 64)]
    public void UnqualifiedHeadersRemainRefused(int offset, byte value)
    {
        byte[] source = OwnedRedField();
        source[offset] = value;
        byte[] original = source.ToArray();
        var document = DjvuDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
        Assert.Equal(original, source);
    }

    [Fact]
    public void OddColorDimensionsRenderLikeTheReferenceDecoder()
    {
        byte[] source = OwnedRedField();
        source[25] = source[47] = 31;
        byte[] original = source.ToArray();
        var document = DjvuDocument.Open(source);
        AssertOwnedRed(document.RenderPage(0), 31);
        Assert.Equal(original, source);
    }

    private static void AssertOwnedRed(PdfRenderedFrame frame, int width)
    {
        Assert.Equal(width, frame.Width);
        Assert.Equal(32, frame.Height);
        float[] expected = [1f, (float)Math.Pow(1 / 255d, 2.2), (float)Math.Pow(1 / 255d, 2.2), 1f];
        for (int pixel = 0; pixel < width * 32; pixel++)
            for (int channel = 0; channel < 4; channel++)
                Assert.Equal(expected[channel], frame.Pixels[pixel * 4 + channel]);
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
    public void TruncatedInitialHeadersRemainMalformed(int payloadLength)
    {
        byte[] source = OwnedRedField()[..(42 + payloadLength)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(8), source.Length - 12);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(38), payloadLength);
        byte[] original = source.ToArray();
        var document = DjvuDocument.Open(source);
        Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        Assert.Equal(original, source);
    }

    // A float frame render holds three 16-bit coefficient sets and a signed plane (7 bytes per pixel) plus the
    // 32 bytes per pixel of the frame and its copy: 2800 x 2800 needs 306 MB and 2880 x 2880 323 MB, over 256 MiB.
    [Theory]
    [InlineData(2800)]
    [InlineData(2880)]
    public void CombinedColorWorkAndStorageAreRefusedBeforeLargeAllocations(ushort size)
    {
        byte[] source = OwnedRedField();
        foreach (int offset in new[] { 24, 26, 46, 48 })
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(offset), size);
        byte[] original = source.ToArray();
        var document = DjvuDocument.Open(source);
        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0));
        Assert.Contains("combined storage or coefficient-work limit", error.Message);
        Assert.Equal(original, source);
    }

    [Fact]
    public void CancellationAndPdfConversionRemainIndependentControls()
    {
        byte[] source = OwnedRedField(), original = source.ToArray();
        var document = DjvuDocument.Open(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => document.RenderPage(0, cancellation.Token));
        var conversion = Assert.Throws<NotSupportedException>(() => document.ConvertToPdf());
        Assert.Equal("Color IW44 decoding is not supported.", conversion.Message);
        Assert.Equal(original, source);
    }

    [Fact]
    public void OtherLayersRemainRefused()
    {
        byte[] source = WithTrailingChunk("ANTa"), original = source.ToArray();
        var document = DjvuDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => document.RenderPage(0));
        Assert.Equal(original, source);
    }

    // A second BG44 chunk with serial 1 and no slices adds nothing; ddjvu renders the file red like the one-chunk field.
    [Fact]
    public void AContinuationChunkWithoutSlicesRendersLikeTheReferenceDecoder()
    {
        byte[] source = WithTrailingChunk("BG44"), original = source.ToArray();
        AssertOwnedRed(DjvuDocument.Open(source).RenderPage(0), 32);
        Assert.Equal(original, source);
    }

    private static byte[] WithTrailingChunk(string chunkId)
    {
        byte[] source = new byte[76];
        OwnedRedField().CopyTo(source, 0);
        System.Text.Encoding.ASCII.GetBytes(chunkId).CopyTo(source, 66);
        source[73] = 2;
        source[74] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(source.AsSpan(8), source.Length - 12);
        return source;
    }

    // Owned one-slice 32 by 32 color backgrounds. Each component codes at most its DC
    // coefficient, so every plane is constant: 1.375 * 0x4000 = 22528 rounds to 352 and
    // clips to 127, its negative clips to -128, and an uncoded plane stays 0. DjVuLibre
    // 3.5.29 ddjvu renders the listed RGB at every pixel of each file.
    [Theory]
    // (y, cb, cr) = (-128, 127, -128): t1 = 31, t2 = -192, t3 = -31,
    // R = 0 - 192 clips to 0, G = -31 + 96 = 65, B = -31 + 254 = 223.
    [InlineData("41542654464F524D0000002A444A5655494E464F0000000A00200020180064001601424734340000000B000101020020002080046400", 0, 65, 223)]
    // (y, cb, cr) = (0, 127, 0): t1 = 31, t2 = 0, t3 = 97, R = 128, G = 97, B = 351 clips to 255.
    [InlineData("41542654464F524D00000028444A5655494E464F0000000A00200020180064001601424734340000000A0001010200200020809C", 128, 97, 255)]
    // (y, cb, cr) = (-128, 0, 127): t1 = 0, t2 = 190, t3 = 0, R = 190, G = -95 clips to 0, B = 0.
    [InlineData("41542654464F524D00000028444A5655494E464F0000000A00200020180064001601424734340000000A00010102002000208013", 190, 0, 0)]
    public void ColorConversionFloorsEachPartialTermLikeTheReferenceDecoder(string hex, int red, int green, int blue)
    {
        byte[] source = Convert.FromHexString(hex), original = source.ToArray();
        var frame = DjvuDocument.Open(source).RenderPage(0);
        Assert.Equal(32, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        float[] expected = [(float)Math.Pow(red / 255d, 2.2), (float)Math.Pow(green / 255d, 2.2), (float)Math.Pow(blue / 255d, 2.2), 1f];
        for (int pixel = 0; pixel < 1024; pixel++)
            for (int channel = 0; channel < 4; channel++)
                Assert.Equal(expected[channel], frame.Pixels[pixel * 4 + channel]);
        Assert.Equal(original, source);
    }

    // Independently encoded with c44 -slice 100 -crcbfull -gamma 2.2 -dpi 100 from owned
    // literal inputs. DjVuLibre 3.5.29 rendered the frozen RGBA with display gamma 2.2
    // before any KillerPDF rendering; the stripe has left red and right blue halves.
    [Theory]
    [InlineData("green-32", 32, "QVQmVEZPUk0AAAA1REpWVUlORk8AAAAKACAAIBgAZAAWAUJHNDQAAAAXAGQBAgAgACCA8uE+0daVsf2FzdBKLqY=",
        "5693ECA47E26262C4356DA0BD9AB6C203DB4FAB97D1FEAE417A18AF37F52981B",
        "eNrtwgEJAAAAAqD+n64fIZoGAAAAODfMbfhq", "56419A079049271B37EEA3515C12BBAFB46578E6AC4DB012C61395EF8B2E5A68")]
    [InlineData("blue-32", 32, "QVQmVEZPUk0AAAA1REpWVUlORk8AAAAKACAAIBgAZAAWAUJHNDQAAAAXAGQBAgAgACCA7q8NZ5yf9AlZc7aR2u8=",
        "9C5C1384C727238C7F613C4CBDBB7D81FBB8738A9EA3C1C4ACDA721A9F936976",
        "eNrtw7EJAAAIA7CC/5/rXP+QBJLZRlVVVX39AM8R3Go=", "83BBB4E820F25AECF1A1CC7AD1B1039D32475F8DC2C1553CDDEFC63FFAA52B40")]
    [InlineData("white-32", 32, "QVQmVEZPUk0AAAAxREpWVUlORk8AAAAKACAAIBgAZAAWAUJHNDQAAAATAGQBAgAgACCA8B3qDh/RxB15WA==",
        "89844BF03CF8B1E14566F1C470543575DA1B11A2F564C015D0F98942F2E1844A",
        "eNrtwQENAAAAwqD+qW8PBxQAAADwboFh8OI=", "F47A8EC3E9AFF2318D896942282AD4FE37D6391C82914F54A5DA8A37DE1300C6")]
    [InlineData("red-blue-stripe-64x32", 64,
        "QVQmVEZPUk0AAADdREpWVUlORk8AAAAKAEAAIBgAZAAWAUJHNDQAAAC/AGQBAgBAACCA+PAdixZm0utwTU5PvH3yfT//sycxE9QDXrVD3gzb8MF5VEx9phfhWSOZX0qV21tUXvNxdi4VdIpdIpcJFK67uyefeBmE9A5/paf1qVoz7eC5lGK4jyd6P9z2k83pAmhtRDeBvjctF2wUo3Q86Bc5Fi3uNTl8rStaB7ayf8GZQXjgojw5hVZgOAUJkVsqQzox4AfxdO/kbAXcZnhTkbMM3KrPYFcUdIpe4DgJvF4w3Ra49f9gpX8=",
        "B340F30699C8149CF8E324857D4C2AE8506126BE23B2640213D5046F62651ADE",
        "eNrtz8sKwjAQRuFJU2zrrb7/83lFUREF2yq/kwriogvXchaHCSFM+JSZFELfM4ufHiFX1zf6Kh8svVdI5ZKl3vu6kOme9kb/I+V/pbs2FmotqrFKVt5k8SIrfJZXWeXn6uydZOOjz4NsspdNd7LZVjbfyOq1t/KWssVA9e8JP378+PHjx48fP378+PHjx48fP378+PHjx48fP378+PHj/wP/C0Jg0SI=",
        "2773F1C97C3D31ADC3D2852432B50F38F8047B194E62D1ABA29E9BA03C8AD8B3")]
    public void FullChromaBackgroundsMatchFrozenNativePixels(string name, int width, string encoded, string encodedHash, string packedNative, string nativeHash)
    {
        byte[] source = Convert.FromBase64String(encoded), original = source.ToArray();
        Assert.Equal(encodedHash, Convert.ToHexString(SHA256.HashData(source)));
        using var packed = new MemoryStream(Convert.FromBase64String(packedNative));
        using var inflater = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionMode.Decompress);
        using var unpacked = new MemoryStream();
        inflater.CopyTo(unpacked);
        byte[] native = unpacked.ToArray();
        Assert.Equal(nativeHash, Convert.ToHexString(SHA256.HashData(native)));
        Assert.Equal(width * 32 * 4, native.Length);
        var frame = DjvuDocument.Open(source).RenderPage(0);
        Assert.Equal(width, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        int differing = 0;
        for (int sample = 0; sample < native.Length; sample++)
        {
            float expected = sample % 4 == 3 ? 1f : (float)Math.Pow(native[sample] / 255d, 2.2);
            if (frame.Pixels[sample] != expected) differing++;
        }
        Assert.True(differing == 0, $"{name}: {differing} of {native.Length} native channels differ.");
        Assert.Equal(original, source);
    }

    private static byte[] OwnedRedField() => Convert.FromHexString(
        "41542654464F524D00000035444A5655494E464F0000000A00200020180064001601" +
        "4247343400000017006401020020002080F5D01728527BAFFF24FD12108B7F");
}
