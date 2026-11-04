using CoreJ2K;
using CoreJ2K.Configuration;
using CoreJ2K.Util;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfGhostscriptJpxCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_UsesSixteenBitJpxSamplesRegardlessOfDictionaryDepth(bool filterArray)
    {
        // Lossless endpoint samples specify a red pixel followed by a green pixel.
        int[][] samples = [[32767, -32768], [-32768, 32767], [-32768, -32768]];
        var source = new InterleavedImageSource(2, 1, 3, 16, new bool[3], samples);
        var parameters = new J2KEncoderConfiguration().WithLossless().ToParameterList();
        parameters["Wlev"] = "0";
        byte[] encoded = J2kImage.ToBytes(source, parameters);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 20, 10)).Build());
        PdfDictionary catalog = Dictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = Dictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = Dictionary(document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var images = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        var reference = Assert.IsType<PdfIndirectReference>(images[Name("Im1")]);
        var image = Assert.IsType<PdfStream>(document.Resolve(reference));
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("Filter")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Filter"),
                filterArray ? new PdfArray([Name("JPXDecode")]) : Name("JPXDecode"))));
        document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber, new PdfStream(dictionary, encoded)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Span.Slice((5 * 20 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, rendered.Pixels.Span.Slice((5 * 20 + 17) * 4, 4).ToArray());
        Assert.Empty(rendered.Diagnostics);
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));

    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));
}
