using CoreJ2K;
using CoreJ2K.Configuration;
using CoreJ2K.Util;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfGhostscriptJpxCompatibilityTests
{
    // Synthetic 16 x 16 grayscale inputs compressed with OpenJPEG 2.5.4 using
    // -n 3 -I -r 4. Ghostscript 10.08.0 confirmed the output indices and colors.
    private const string IndexRoundsUp =
        "/0//UQApAAAAAAAQAAAAEAAAAAAAAAAAAAAAEAAAABAAAAAAAAAAAAABBwEB/1IADAAAAAEAAgQEAAD/XAARQl9SUAVQBVBHV9NX01di/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAAALAAB/5PHiBDgzGr/N5iGkcGAJFROo4SfAKBeUpcPXACZKjn/2Q==";
    private const string IndexRoundsDown =
        "/0//UQApAAAAAAAQAAAAEAAAAAAAAAAAAAAAEAAAABAAAAAAAAAAAAABBwEB/1IADAAAAAEAAgQEAAD/XAARQl9SUAVQBVBHV9NX01di/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAAAKQAB/5PHxCgSWtb1f7yjQojHwijDAB8EJKNpxGBXg2P/2Q==";

    [Theory]
    [InlineData(IndexRoundsUp, 12, 13, 34)]
    [InlineData(IndexRoundsDown, 8, 7, 156)]
    public void Render_UsesGhostscriptConfirmedIndicesForLossyJpx(
        string codestream, int x, int y, int expectedIndex)
    {
        const int size = 16;
        byte[] palette = new byte[256 * 3];
        for (int index = 0; index < 256; index++)
        {
            ReadOnlySpan<byte> color = index % 2 == 0 ? [0, 90, 255] : [255, 0, 30];
            color.CopyTo(palette.AsSpan(index * 3));
        }

        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(size, size, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromRgb(size, size, new byte[size * size * 3]), 0, 0, size, size)).Build());
        PdfIndirectReference reference = FirstImage(document);
        var image = Assert.IsType<PdfStream>(document.Resolve(reference));
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("Filter")) && !entry.Key.Equals(Name("ColorSpace")))
            .Append(new(Name("Filter"), Name("JPXDecode")))
            .Append(new(Name("ColorSpace"), new PdfArray([
                Name("Indexed"), Name("DeviceRGB"), new PdfInteger(255),
                new PdfString(palette, PdfStringForm.Hexadecimal)]))));
        document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber,
                new PdfStream(dictionary, Convert.FromBase64String(codestream))).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(size, size, includeAnnotations: false, includeFormFields: false));

        Assert.Empty(rendered.Diagnostics);
        int pixel = y * size + x;
        Assert.Equal(new byte[] { 255, 90, 0, 255 }, rendered.Pixels.Span.Slice(pixel * 4, 4).ToArray());
        byte[] indices = PdfStreamDecoder.Decode(Assert.IsType<PdfStream>(document.Resolve(reference)));
        Assert.Equal((byte)expectedIndex, indices[pixel]);
    }

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
        PdfIndirectReference reference = FirstImage(document);
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

    private static PdfIndirectReference FirstImage(PdfDocument document)
    {
        PdfDictionary catalog = Dictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = Dictionary(document, catalog[Name("Pages")]);
        PdfDictionary page = Dictionary(document, Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var images = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        return Assert.IsType<PdfIndirectReference>(images[Name("Im1")]);
    }

    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));

}
