using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

// A page-size photo is compressed row by row: every row goes through the adaptive PNG predictor straight into one Flate
// stream, so the only large buffers are the pixels and the compressed image. The written image must decode to the
// exact pixels, with predictor 15 over three colors, whatever the content (noise included, where whole-image Flate
// without prediction would otherwise be kept).
public sealed class PdfStreamedRgbImageTests
{
    [Theory]
    [InlineData("noise")]
    [InlineData("gradient")]
    public void AStreamedRgbImageDecodesToItsExactPixelsThroughThePngPredictor(string content)
    {
        const int width = 97, height = 41;
        var pixels = new byte[width * height * 3];
        var random = new Random(7);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                    pixels[(y * width + x) * 3 + c] = content == "noise"
                        ? (byte)random.Next(256) : (byte)(x * 2 + y * c);

        PdfImage image = PdfImage.FromRgbRows(width, height, pixels);

        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 100, 100)).Build());
        var stream = ImageStream(document);
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(stream.Dictionary[Name("Filter")]).ValueAsLatin1());
        var parameters = Assert.IsType<PdfDictionary>(stream.Dictionary[Name("DecodeParms")]);
        Assert.Equal(15, Assert.IsType<PdfInteger>(parameters[Name("Predictor")]).Value);
        Assert.Equal(3, Assert.IsType<PdfInteger>(parameters[Name("Colors")]).Value);
        Assert.Equal(width, Assert.IsType<PdfInteger>(parameters[Name("Columns")]).Value);
        Assert.Equal(pixels, PdfStreamDecoder.Decode(stream, document.Resolve, pixels.Length));
    }

    private static PdfStream ImageStream(PdfDocument document)
    {
        PdfDictionary Dictionary(PdfObject? value) => Assert.IsType<PdfDictionary>(
            value is PdfIndirectReference reference ? document.Resolve(reference) : value);
        var catalog = Dictionary(document.Trailer[Name("Root")]);
        var pages = Dictionary(catalog[Name("Pages")]);
        var page = Dictionary(Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var xobjects = Dictionary(Dictionary(page[Name("Resources")])[Name("XObject")]);
        return Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
