using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuGrayscalePdfConversionTests
{
    [Fact]
    public void ConversionPreservesOriginalConstantSamplesAndDeclaredGamma()
    {
        byte[] source = Constant();
        byte[] original = source.ToArray();
        var pdf = PdfDocument.Open(DjvuDocument.Open(source).ConvertToPdf());
        AssertImage(pdf, Enumerable.Repeat((byte)127, 16).ToArray(), 1);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(10, .49803922f)]
    [InlineData(18, .28515065f)]
    [InlineData(22, .2157644f)]
    [InlineData(28, .1420162f)]
    public void ConversionKeepsGammaAsCalibrationRatherThanQuantizingSamples(byte gamma, float linear)
    {
        var source = Constant();
        source[32] = gamma;
        var original = source.ToArray();
        var pdf = PdfDocument.Open(DjvuDocument.Open(source).ConvertToPdf());
        AssertImage(pdf, Enumerable.Repeat((byte)127, 16).ToArray(), gamma / 10d);
        AssertLinear(pdf, 4, 4, Enumerable.Repeat(linear, 16).ToArray());
        Assert.Equal(original, source);
    }

    // DjVuLibre 3.5.29 ddjvu -format=pgm output for the ramp with each orientation flag.
    [Theory]
    [InlineData((byte)1, new byte[] { 0,31,66,97,127,163,198,229,255,225,193,152,127,95,64,31 })]
    [InlineData((byte)2, new byte[] { 31,64,95,127,152,193,225,255,229,198,163,127,97,66,31,0 })]
    [InlineData((byte)5, new byte[] { 127,255,127,0,95,225,163,31,64,193,198,66,31,152,229,97 })]
    [InlineData((byte)6, new byte[] { 97,229,152,31,66,198,193,64,31,163,225,95,0,127,255,127 })]
    public void ConversionOrientsTheOriginalRampSamplesExactly(byte orientation, byte[] expected)
    {
        var source = Ramp();
        source[33] = orientation;
        var original = source.ToArray();
        var pdf = PdfDocument.Open(DjvuDocument.Open(source).ConvertToPdf());
        AssertImage(pdf, expected, 2.2);
        Assert.Equal(original, source);
    }

    [Fact]
    public void ConvertedRampRendersIndependentLiteralLinearLevels()
    {
        var pdf = PdfDocument.Open(DjvuDocument.Open(Ramp()).ConvertToPdf());
        // DjVuLibre 3.5.29 ddjvu samples with the declared gamma 2.2 applied.
        AssertLinear(pdf, 4, 4, [0f, .009696329f, .05112205f, .119264014f,
            .2157644f, .37361503f, .57315886f, .78931373f, 1f, .7592996f,
            .54179835f, .32038155f, .2157644f, .113920934f, .047775753f, .009696329f]);
    }

    [Theory]
    [InlineData((byte)1, 65, 37, 0, 0, 1, .5)]
    [InlineData((byte)2, 65, 37, 31.5, 18, 32.5, 18.5)]
    [InlineData((byte)5, 37, 65, 0, 31.5, .5, 32.5)]
    [InlineData((byte)6, 37, 65, 18, 0, 18.5, 1)]
    public void RectangularConversionPreservesPhysicalSizeAndLocatedText(byte orientation, int width,
        int height, double left, double bottom, double right, double top)
    {
        var source = Rectangle(orientation, LocatedText());
        var original = source.ToArray();
        var native = DjvuDocument.Open(source);
        var pdf = PdfDocument.Open(native.ConvertToPdf());
        AssertImage(pdf, Enumerable.Repeat((byte)127, 65 * 37).ToArray(), 1);
        var geometry = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal(width / 2d, geometry.PhysicalWidth, 6);
        Assert.Equal(height / 2d, geometry.PhysicalHeight, 6);
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(left, letter.BoundingBox.Left, 6);
        Assert.Equal(bottom, letter.BoundingBox.Bottom, 6);
        Assert.Equal(right, letter.BoundingBox.Right, 6);
        Assert.Equal(top, letter.BoundingBox.Top, 6);
        AssertLinear(pdf, width, height, Enumerable.Repeat(.49803922f, width * height).ToArray());
        Assert.Equal("A", native.ReadText(0));
        Assert.Equal(original, source);
    }

    // DjVuInfo::decode clamps the stored gamma to 0.3 through 5.0, and DjVuLibre
    // 3.5.29 ddjvu renders a zero gamma byte like 3 and a 255 byte like 50.
    [Theory]
    [InlineData(0, 0.3)]
    [InlineData(3, 0.3)]
    [InlineData(51, 5.0)]
    [InlineData(255, 5.0)]
    public void ConversionClampsDeclaredGammaLikeTheReferenceDecoder(byte gamma, double expected)
    {
        var source = Constant();
        source[32] = gamma;
        var pdf = PdfDocument.Open(DjvuDocument.Open(source).ConvertToPdf());
        AssertImage(pdf, Enumerable.Repeat((byte)127, 16).ToArray(), expected);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("color")]
    [InlineData("layer")]
    [InlineData("continuation")]
    [InlineData("unreadable-text")]
    [InlineData("unlocated-text")]
    [InlineData("frame-limit")]
    [InlineData("storage-limit")]
    public void ConversionRetainsNativeRefusalsAndSource(string scenario)
    {
        var source = Constant();
        switch (scenario)
        {
            case "version": source[28] = 27; break;
            case "color": source[44] = 1; break;
            case "layer": source = Append(source, Chunk("BGjp", [0])); break;
            case "continuation": source = Append(source, Chunk("BG44", [2, 0])); break;
            case "unreadable-text": source = Append(source, Chunk("TXTz", [0])); break;
            case "unlocated-text": source = Append(source, Chunk("TXTa", [0,0,1,(byte)'A',1])); break;
            case "frame-limit": SetDimensions(source, 4000, 4000); break;
            case "storage-limit": SetDimensions(source, 2800, 2800); break;
        }
        var original = source.ToArray();
        var native = DjvuDocument.Open(source);
        if (scenario is "unreadable-text" or "unlocated-text")
        {
            // Text that cannot be read or placed costs only the page's text: the gray page converts, with a notice.
            var notices = new List<DjvuDiagnostic>();
            Assert.Single(PdfPageInformation.Read(PdfDocument.Open(native.ConvertToPdf(notices))));
            Assert.Equal("TextNotConverted", Assert.Single(notices).Code);
        }
        else if (scenario == "color")
        {
            // Native rendering decodes color IW44; PDF conversion stays grayscale only.
            Assert.Equal(4, native.RenderPage(0).Width);
            Assert.Equal("Color IW44 decoding is not supported.", Assert.Throws<NotSupportedException>(() => native.ConvertToPdf()).Message);
        }
        else
        {
            var rendering = Record.Exception(() => native.RenderPage(0));
            Assert.NotNull(rendering);
            var conversion = Record.Exception(() => native.ConvertToPdf());
            Assert.NotNull(conversion);
            Assert.Equal(rendering.GetType(), conversion.GetType());
            Assert.Equal(rendering.Message, conversion.Message);
        }
        Assert.Equal(original, source);
    }

    [Fact]
    public void CancellationPrecedesPageIndexValidationAndConversionWork()
    {
        var native = DjvuDocument.Open(Constant());
        var token = new CancellationToken(true);
        Assert.Equal(token, Assert.Throws<OperationCanceledException>(() => native.RenderPage(-1, token)).CancellationToken);
        Assert.Equal(token, Assert.Throws<OperationCanceledException>(() => native.ConvertToPdf(token)).CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedBundleKeepsOrderTextAndNestedBookmarks(bool grayFirst)
    {
        byte[] gray = Rectangle(1, LocatedText())[4..];
        byte[] bitonal = Chunk("FORM", [.. "DJVU"u8,
            .. Chunk("INFO", [0,3,0,2,26,0,144,0,22,1]),
            .. Chunk("Smmr", [.. "MMR"u8,0,0,3,0,2,0x23,0xe1,0x80,0x08,0,0x80]), .. LocatedText()]);
        byte[] first = grayFirst ? gray : bitonal;
        byte[] second = grayFirst ? bitonal : gray;
        var nav = Chunk("NAVM", Convert.FromHexString("FFFFDCFE710ED97FCE8AD681247C75F5957907266F39911034415499EB17C37B"));
        byte[] directory = [0x81,0,2,0,0,0,0,0,0,0,0];
        int offset = 16 + Chunk("DIRM", directory).Length + nav.Length;
        BinaryPrimitives.WriteInt32BigEndian(directory.AsSpan(3), offset);
        BinaryPrimitives.WriteInt32BigEndian(directory.AsSpan(7), offset + first.Length);
        byte[] source = [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVM"u8, .. Chunk("DIRM", directory), .. nav, .. first, .. second])];
        var original = source.ToArray();
        var native = DjvuDocument.Open(source);
        var pdf = PdfDocument.Open(native.ConvertToPdf());
        var geometry = PdfPageInformation.Read(pdf);
        Assert.Equal(grayFirst ? 32.5 : 1.5, geometry[0].PhysicalWidth, 6);
        Assert.Equal(grayFirst ? 1.5 : 32.5, geometry[1].PhysicalWidth, 6);
        Assert.Equal(2, geometry.Count);
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal("A", reader.Read(0).Text);
        Assert.Equal("A", reader.Read(1).Text);
        var catalog = Resolve(pdf, pdf.Trailer[Name("Root")]);
        var outlines = Resolve(pdf, catalog[Name("Outlines")]);
        var root = Resolve(pdf, outlines[Name("First")]);
        var child = Resolve(pdf, root[Name("First")]);
        var pages = Assert.IsType<PdfArray>(Resolve(pdf, catalog[Name("Pages")])[Name("Kids")]);
        Assert.Equal("R\u00e9sum\u00e9", Title(root));
        Assert.Equal("\u05e4\u05e8\u05e7", Title(child));
        foreach (var bookmark in new[] { root, child })
            Assert.Equal(Assert.IsType<PdfIndirectReference>(pages[0]).ObjectNumber,
                Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(bookmark[Name("Dest")])[0]).ObjectNumber);
        int grayIndex = grayFirst ? 0 : 1;
        AssertImage(pdf, Enumerable.Repeat((byte)127,65*37).ToArray(),1,grayIndex);
        var bitonalFrame = new PdfPageRenderer(pdf).Render(1-grayIndex,new PdfRenderOptions(3,2));
        Assert.Empty(bitonalFrame.Diagnostics);
        Assert.Equal(new byte[] {255,0,0,255,255,255}, bitonalFrame.Pixels.ToArray().Where((_,index)=>index%4==0));
        Assert.Equal(original, source);
    }

    private static string Title(PdfDictionary dictionary) => Encoding.BigEndianUnicode.GetString(
        Assert.IsType<PdfString>(dictionary[Name("Title")]).Bytes.Span[2..]);

    private static void AssertLinear(PdfDocument pdf, int width, int height, float[] expected)
    {
        var frame = new PdfPageRenderer(pdf).RenderFrame(0, new PdfRenderOptions(width, height));
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(expected.Length * 4, frame.Pixels.Length);
        for (int pixel = 0; pixel < expected.Length; pixel++)
        {
            for (int component = 0; component < 3; component++)
                Assert.InRange(frame.Pixels[pixel * 4 + component], expected[pixel] - .000001f, expected[pixel] + .000001f);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    private static byte[] Rectangle(byte orientation, params byte[][] extra)
    {
        var source = Constant();
        SetDimensions(source, 65, 37);
        source[30] = 144;
        source[33] = orientation;
        return Append(source, extra.SelectMany(chunk => chunk).ToArray());
    }

    private static void SetDimensions(byte[] source, ushort width, ushort height)
    {
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(24), width);
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(26), height);
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(46), width);
        BinaryPrimitives.WriteUInt16BigEndian(source.AsSpan(48), height);
    }

    private static byte[] LocatedText()
    {
        byte[] zone = [6,0x80,0,0x80,0,0x80,2,0x80,1,0x80,0,0,0,1,0,0,0];
        return Chunk("TXTa", [0,0,1,(byte)'A',1, .. zone]);
    }

    private static byte[] Append(byte[] source, byte[] chunk)
    {
        byte[] result = [.. source, .. (source.Length % 2 == 1 ? new byte[1] : []), .. chunk];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(8), result.Length - 12);
        return result;
    }

    private static byte[] Chunk(string id, byte[] payload)
    {
        var result = new byte[8 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes(id).CopyTo(result, 0);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(4), payload.Length);
        payload.CopyTo(result,8);
        return result;
    }

    private static byte[] Ramp() => Convert.FromHexString(
        "41542654464F524D00000039444A5655494E464F0000000A00040004180064001601" +
        "424734340000001B006481020004000400FFD5A537F9348F11CFC657F6ACD695FCA23C");

    private static byte[] Constant() => Convert.FromHexString(
        "41542654464F524D00000029444A5655494E464F0000000A00040004180064000A01" +
        "424734340000000B000181020004000400FFFF");

    private static void AssertImage(PdfDocument pdf, byte[] samples, double gamma, int pageIndex = 0)
    {
        var catalog = Resolve(pdf, pdf.Trailer[Name("Root")]);
        var pages = Resolve(pdf, catalog[Name("Pages")]);
        var page = Resolve(pdf, Assert.IsType<PdfArray>(pages[Name("Kids")])[pageIndex]);
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var objects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var stream = Assert.IsType<PdfStream>(pdf.Resolve(Assert.IsType<PdfIndirectReference>(objects[Name("Im1")])));
        Assert.Equal(samples, PdfStreamDecoder.Decode(stream, pdf.Resolve, samples.Length));
        Assert.Equal(8, Number(stream.Dictionary[Name("BitsPerComponent")]));
        var colorSpace = Assert.IsType<PdfArray>(stream.Dictionary[Name("ColorSpace")]);
        Assert.Equal("CalGray", Assert.IsType<PdfName>(colorSpace[0]).ValueAsLatin1());
        var calibration = Assert.IsType<PdfDictionary>(colorSpace[1]);
        Assert.Equal(new[] { 0.95047, 1, 1.08883 }, Assert.IsType<PdfArray>(calibration[Name("WhitePoint")]).Select(Number));
        Assert.Equal(new[] { 0d, 0, 0 }, Assert.IsType<PdfArray>(calibration[Name("BlackPoint")]).Select(Number));
        Assert.Equal(gamma, Number(calibration[Name("Gamma")]));
    }

    private static PdfDictionary Resolve(PdfDocument pdf, PdfObject value) =>
        Assert.IsType<PdfDictionary>(pdf.Resolve(Assert.IsType<PdfIndirectReference>(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new Xunit.Sdk.XunitException("Expected number")
    };
}
