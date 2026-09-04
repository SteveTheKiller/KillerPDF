using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfImageTests
{
    [Fact]
    public void FromRgb_CompressesExactPixelBytes()
    {
        byte[] pixels = [255, 0, 0, 0, 255, 0];
        PdfImage image = PdfImage.FromRgb(2, 1, pixels);
        using var compressed = new MemoryStream(image.Data.ToArray());
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);

        Assert.Equal(pixels, decoded.ToArray());
        Assert.Equal(PdfImageColorSpace.Rgb, image.ColorSpace);
    }

    [Fact]
    public void FromGray_CompressesExactPixelBytesAndWritesDeviceGray()
    {
        byte[] pixels = [0, 127, 255];
        PdfImage image = PdfImage.FromGray(3, 1, pixels);

        Assert.Equal(pixels, Decode(image));
        Assert.Equal(PdfImageColorSpace.Gray, image.ColorSpace);
        Assert.Equal("DeviceGray", ImageColorSpace(image));
    }

    [Fact]
    public void FromBitonal_PacksRowsIntoOneBitGrayscaleSamples()
    {
        PdfImage image = PdfImage.FromBitonal(10, 2,
        new byte[]
        {
            255, 0, 255, 0, 255, 0, 255, 0, 255, 0,
            0, 255, 0, 255, 0, 255, 0, 255, 0, 255
        });

        Assert.Equal([0xAA, 0x80, 0x55, 0x40], Decode(image));
        Assert.Equal(1, image.BitsPerComponent);
        Assert.Equal(PdfImageColorSpace.Gray, image.ColorSpace);
        Assert.Equal("DeviceGray", ImageColorSpace(image));
    }

    [Fact]
    public void FromBitonalUsesSmallerGroup4PayloadWithDecodeParameters()
    {
        byte[] pixels = new byte[1728 * 100];
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 1728; x++)
                pixels[y * 1728 + x] = x is < 576 or >= 1152 ? (byte)255 : (byte)0;
        PdfImage image = PdfImage.FromBitonal(1728, 100, pixels);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .DrawImage(image, 0, 0, 100, 100)).Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(document,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xobjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));

        Assert.Equal("CCITTFaxDecode", Assert.IsType<PdfName>(
            stream.Dictionary[Name("Filter")]).ValueAsLatin1());
        PdfDictionary parameters = Assert.IsType<PdfDictionary>(
            stream.Dictionary[Name("DecodeParms")]);
        Assert.Equal(-1, Assert.IsType<PdfInteger>(parameters[Name("K")]).Value);
        Assert.Equal(1728, Assert.IsType<PdfInteger>(parameters[Name("Columns")]).Value);
        Assert.Equal(100, Assert.IsType<PdfInteger>(parameters[Name("Rows")]).Value);
        Assert.False(Assert.IsType<PdfBoolean>(parameters[Name("BlackIs1")]).Value);
        Assert.True(image.Data.Length < 100);
    }

    [Fact]
    public void FromCmyk_CompressesExactPixelBytesAndWritesDeviceCmyk()
    {
        byte[] pixels = [0, 64, 128, 255, 255, 128, 64, 0];
        PdfImage image = PdfImage.FromCmyk(2, 1, pixels);

        Assert.Equal(pixels, Decode(image));
        Assert.Equal(PdfImageColorSpace.Cmyk, image.ColorSpace);
        Assert.Equal("DeviceCMYK", ImageColorSpace(image));
    }

    [Fact]
    public void RawImageFactories_RequireExactPixelLengths()
    {
        Assert.Throws<ArgumentException>(() => PdfImage.FromGray(2, 2, new byte[3]));
        Assert.Throws<ArgumentException>(() => PdfImage.FromBitonal(2, 2, new byte[3]));
        Assert.Throws<ArgumentException>(() => PdfImage.FromCmyk(2, 2, new byte[15]));
        Assert.Throws<ArgumentException>(() => PdfImage.FromGrayAlpha(2, 2, new byte[7]));
        Assert.Throws<ArgumentException>(() => PdfImage.FromCmyka(2, 2, new byte[19]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImage.FromRgb(int.MaxValue, 2, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImage.FromRgba(int.MaxValue, 2, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void FromJpeg_ReadsFrameWithoutRecompressing()
    {
        byte[] jpeg = MinimalJpeg(width: 320, height: 200, components: 3);
        PdfImage image = PdfImage.FromJpeg(jpeg);

        Assert.Equal(320, image.Width);
        Assert.Equal(200, image.Height);
        Assert.Equal(8, image.BitsPerComponent);
        Assert.Equal(PdfImageColorSpace.Rgb, image.ColorSpace);
        Assert.Equal(jpeg, image.Data.ToArray());
    }

    [Fact]
    public void FromRgba_WritesAlphaAsImageSoftMask()
    {
        PdfImage image = PdfImage.FromRgba(1, 1, new byte[] { 10, 20, 30, 64 });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(
            100, 100, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 10, 10)).Build());
        var catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        var pages = ResolveDictionary(document, catalog[Name("Pages")]);
        var page = ResolveDictionary(document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var xobjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var color = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));
        var mask = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(color.Dictionary[Name("SMask")])));

        Assert.Equal("DeviceGray", Assert.IsType<PdfName>(
            mask.Dictionary[Name("ColorSpace")]).ValueAsLatin1());
        using var input = new MemoryStream(mask.EncodedData.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        Assert.Equal(64, zlib.ReadByte());
    }

    [Fact]
    public void FromGrayAlpha_WritesGrayPixelsAndAlphaSoftMask()
    {
        PdfImage image = PdfImage.FromGrayAlpha(2, 1, new byte[] { 10, 64, 200, 192 });
        (PdfStream color, PdfStream mask) = WrittenImageAndMask(image);

        Assert.Equal([10, 200], Decode(color.EncodedData));
        Assert.Equal([64, 192], Decode(mask.EncodedData));
        Assert.Equal("DeviceGray", Assert.IsType<PdfName>(
            color.Dictionary[Name("ColorSpace")]).ValueAsLatin1());
    }

    [Fact]
    public void FromCmyka_WritesCmykPixelsAndAlphaSoftMask()
    {
        PdfImage image = PdfImage.FromCmyka(1, 1, new byte[] { 10, 20, 30, 40, 128 });
        (PdfStream color, PdfStream mask) = WrittenImageAndMask(image);

        Assert.Equal([10, 20, 30, 40], Decode(color.EncodedData));
        Assert.Equal([128], Decode(mask.EncodedData));
        Assert.Equal("DeviceCMYK", Assert.IsType<PdfName>(
            color.Dictionary[Name("ColorSpace")]).ValueAsLatin1());
    }

    [Fact]
    public void DrawImage_CreatesImageXObjectAndPlacementOperators()
    {
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 10, 20, 30 });
        var content = new PdfContentStreamBuilder().DrawImage(image, 10, 20, 30, 40);
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build());
        var catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        var pages = ResolveDictionary(document, catalog[Name("Pages")]);
        var page = ResolveDictionary(document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var xobjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var imageStream = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));
        var contentStream = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(page[Name("Contents")])));

        Assert.Equal(1, Assert.IsType<PdfInteger>(imageStream.Dictionary[Name("Width")]).Value);
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(
            imageStream.Dictionary[Name("Filter")]).ValueAsLatin1());
        Assert.Equal("q\n30 0 0 40 10 20 cm\n/Im1 Do\nQ\n",
            Encoding.ASCII.GetString(contentStream.EncodedData.Span));
    }

    [Fact]
    public void SetPageThumbnail_ReusesAuthoredImageObject()
    {
        PdfImage image = PdfImage.FromRgba(1, 1, new byte[] { 20, 80, 160, 192 });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 20, 20))
            .SetPageThumbnail(0, image)
            .Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xobjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference placed = Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")]);
        PdfIndirectReference thumbnail = Assert.IsType<PdfIndirectReference>(page[Name("Thumb")]);
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(thumbnail));

        Assert.Equal(placed.ObjectNumber, thumbnail.ObjectNumber);
        Assert.True(stream.Dictionary.ContainsKey(Name("SMask")));
    }

    [Fact]
    public void SetPageThumbnail_ValidatesPageAndImage()
    {
        var builder = new PdfDocumentBuilder().AddBlankPage();
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 });

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.SetPageThumbnail(1, image));
        Assert.Throws<ArgumentNullException>(() => builder.SetPageThumbnail(0, null!));
    }

    [Fact]
    public void SetPageThumbnail_RequiresPdf14ForAnAlphaSoftMask()
    {
        PdfImage thumbnail = PdfImage.FromRgba(
            1, 1, new byte[] { 20, 80, 160, 192 });

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfDocumentBuilder(new PdfVersion(1, 3))
                .AddBlankPage()
                .SetPageThumbnail(0, thumbnail)
                .Build());

        Assert.Contains("image soft masks", error.Message);
        Assert.NotEmpty(new PdfDocumentBuilder(new PdfVersion(1, 4))
            .AddBlankPage()
            .SetPageThumbnail(0, thumbnail)
            .Build());
    }

    [Fact]
    public void FromJpeg_RejectsUnsupportedComponentCount()
    {
        Assert.Throws<NotSupportedException>(() =>
            PdfImage.FromJpeg(MinimalJpeg(1, 1, components: 2)));
        byte[] truncatedFrame = MinimalJpeg(1, 1, components: 3);
        truncatedFrame[5]--;
        Assert.Throws<FormatException>(() => PdfImage.FromJpeg(truncatedFrame));
        byte[] missingEnd = MinimalJpeg(1, 1, components: 3)[..^2];
        Assert.Throws<FormatException>(() => PdfImage.FromJpeg(missingEnd));
        byte[] twelveBit = MinimalJpeg(1, 1, components: 3);
        twelveBit[6] = 12;
        Assert.Throws<NotSupportedException>(() => PdfImage.FromJpeg(twelveBit));
        byte[] undefinedScanComponent = MinimalJpeg(1, 1, components: 3);
        undefinedScanComponent[26] = 99;
        Assert.Throws<FormatException>(() => PdfImage.FromJpeg(undefinedScanComponent));
        byte[] losslessProcess = MinimalJpeg(1, 1, components: 3);
        losslessProcess[3] = 0xC3;
        Assert.Throws<NotSupportedException>(() => PdfImage.FromJpeg(losslessProcess));
    }

    private static byte[] MinimalJpeg(int width, int height, int components)
    {
        int frameLength = 8 + components * 3;
        var bytes = new List<byte>
        {
            0xFF, 0xD8,
            0xFF, 0xC0, (byte)(frameLength >> 8), (byte)frameLength,
            0x08,
            (byte)(height >> 8), (byte)height,
            (byte)(width >> 8), (byte)width,
            (byte)components
        };
        for (int component = 0; component < components; component++)
        {
            bytes.Add((byte)(component + 1));
            bytes.Add(0x11);
            bytes.Add(0);
        }
        int scanLength = 6 + components * 2;
        bytes.AddRange([0xFF, 0xDA, (byte)(scanLength >> 8), (byte)scanLength,
            (byte)components]);
        for (int component = 0; component < components; component++)
        {
            bytes.Add((byte)(component + 1));
            bytes.Add(0);
        }
        bytes.AddRange([0, 63, 0, 0]);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    private static byte[] Decode(PdfImage image)
    {
        using var input = new MemoryStream(image.Data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        return decoded.ToArray();
    }

    private static byte[] Decode(ReadOnlyMemory<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        return decoded.ToArray();
    }

    private static (PdfStream Color, PdfStream Mask) WrittenImageAndMask(PdfImage image)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(
            10, 10, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 10, 10)).Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(
            document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xobjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfStream color = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));
        PdfStream mask = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(color.Dictionary[Name("SMask")])));
        return (color, mask);
    }

    private static string ImageColorSpace(PdfImage image)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(
            10, 10, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 10, 10)).Build());
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(
            document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xobjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfStream stream = Assert.IsType<PdfStream>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));
        return Assert.IsType<PdfName>(stream.Dictionary[Name("ColorSpace")]).ValueAsLatin1();
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
