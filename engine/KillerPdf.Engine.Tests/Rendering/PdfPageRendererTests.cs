using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererTests
{
    [Fact]
    public void Render_BlankPageProducesOpaqueWhiteBgra()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(10, 20).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 4, includeAnnotations: false, includeFormFields: false));

        Assert.Equal(2, page.Width);
        Assert.Equal(4, page.Height);
        Assert.Equal(32, page.Pixels.Length);
        Assert.All(page.Pixels.ToArray().Chunk(4), pixel =>
            Assert.Equal([255, 255, 255, 255], pixel));
        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_TransformsAndFillsRgbRectangle()
    {
        byte[] content = "q 1 0 0 1 2 3 cm 1 0 0 rg 1 1 2 2 re f Q"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 3, 4));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 0));
    }

    [Fact]
    public void OptionsRejectUnboundedPixelBuffers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfRenderOptions(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfRenderOptions(PdfRenderOptions.MaximumDimension + 1, 1));
        Assert.Throws<ArgumentException>(() => new PdfRenderOptions(20_000, 20_000));
    }

    [Fact]
    public void Render_FillsGeneralPathAndStrokesCurve()
    {
        byte[] content = "0 1 0 rg 1 1 m 8 1 l 4 8 l h f 0 0 1 RG 1 w 1 9 m 3 5 7 5 9 9 c S"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(page, 4, 7));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 1, 1));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 9));
    }

    [Fact]
    public void Render_DecodesAndTransformsRgbImageXObject()
    {
        PdfImage image = PdfImage.FromRgb(2, 1, new byte[]
        {
            255, 0, 0,
            0, 255, 0
        });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(image, 2, 3, 6, 2))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 3, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 7, 6));
        Assert.DoesNotContain("Image rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_DecodesBaselineJpegImageXObjects()
    {
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDwyiiivw8/0oP/2Q==");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromJpeg(jpeg), 2, 3, 6, 2)).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] pixel = Pixel(page, 3, 6);
        Assert.InRange(pixel[2], 235, 245);
        Assert.InRange(pixel[1], 15, 25);
        Assert.InRange(pixel[0], 5, 15);
        Assert.Equal(255, pixel[3]);
        Assert.DoesNotContain("The image compression filter is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_CompositesImageSoftMasks()
    {
        PdfImage image = PdfImage.FromRgba(2, 1, new byte[]
        {
            255, 0, 0, 128,
            0, 255, 0, 0
        });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(image, 2, 3, 6, 2))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 128], Pixel(page, 3, 6));
        Assert.Equal([255, 255, 255, 0], Pixel(page, 7, 6));
        Assert.DoesNotContain("The image soft mask is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_DecodesCcittFaxImageXObjects()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(8, 1, new byte[8]), 0, 0, 8, 1))
            .Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter",
            Name("CCITTFaxDecode"), [0x89, 0xC0]);
        PdfDocument bitDepth = AddImageDictionaryEntry(filtered, "BitsPerComponent",
            new PdfInteger(1));
        PdfDocument document = AddImageDictionaryEntry(bitDepth, "DecodeParms",
            new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(
                Name("Columns"), new PdfInteger(8))]));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(page, 3, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 6, 0));
        Assert.DoesNotContain("The image compression filter is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_AppliesImageDecodeArrays()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 10, 20, 30 }), 2, 3, 6, 2))
            .Build());
        var decode = new PdfArray(Enumerable.Range(0, 3).SelectMany(_ =>
            new PdfObject[] { new PdfInteger(1), new PdfInteger(0) }));
        PdfDocument document = AddImageDictionaryEntry(source, "Decode", decode);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([225, 235, 245, 255], Pixel(rendered, 3, 6));
    }

    [Fact]
    public void Render_AppliesImageColorKeyMasks()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(2, 1, new byte[] { 255, 0, 0, 0, 255, 0 }), 2, 3, 6, 2))
            .Build());
        var mask = new PdfArray(new PdfObject[]
        {
            new PdfInteger(250), new PdfInteger(255),
            new PdfInteger(0), new PdfInteger(5),
            new PdfInteger(0), new PdfInteger(5)
        });
        PdfDocument document = AddImageDictionaryEntry(source, "Mask", mask);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 0], Pixel(rendered, 3, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 7, 6));
    }

    [Fact]
    public void Render_ResolvesIndexedImageColorSpaces()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 1 }), 2, 3, 6, 2))
            .Build());
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Indexed"), Name("DeviceRGB"), new PdfInteger(1),
            new PdfString(new byte[] { 255, 0, 0, 0, 0, 255 }, PdfStringForm.Hexadecimal)
        });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 3, 6));
        Assert.Equal([255, 0, 0, 255], Pixel(rendered, 7, 6));
    }

    [Fact]
    public void Render_ResolvesNamedImageColorSpaceResources()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 2, 3, 6, 2))
            .Build());
        PdfDocument named = AddImageDictionaryEntry(source, "ColorSpace", Name("ImageRgb"));
        PdfDocument document = AddPageColorSpaceResource(
            named, "ImageRgb", Name("DeviceRGB"));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_UsesIccImageAlternateColorSpaces()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 0, 255, 0 }), 2, 3, 6, 2))
            .Build());
        var profile = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(3)),
            new KeyValuePair<PdfName, PdfObject>(Name("Alternate"), Name("ImageRgb"))]), []);
        PdfDocument profiled = AddIccImageColorSpace(source, profile);
        PdfDocument document = AddPageColorSpaceResource(
            profiled, "ImageRgb", Name("DeviceRGB"));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsCalGrayImagesToSrgb()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(1, 1, new byte[] { 128 }), 2, 3, 6, 2))
            .Build());
        var parameters = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("WhitePoint"),
                Reals(0.95047, 1, 1.08883))]);
        var colorSpace = new PdfArray(new PdfObject[] { Name("CalGray"), parameters });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([188, 188, 188, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsCalRgbImagesToSrgb()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 128, 0, 0 }), 2, 3, 6, 2))
            .Build());
        var parameters = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("WhitePoint"),
                Reals(0.95047, 1, 1.08883)),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), Reals(
                0.4124564, 0.2126729, 0.0193339,
                0.3575761, 0.7151522, 0.119192,
                0.1804375, 0.072175, 0.9503041))]);
        var colorSpace = new PdfArray(new PdfObject[] { Name("CalRGB"), parameters });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 188, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsLabImagesUsingDefaultDecodeRanges()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(1, 1, new byte[] { 128, 128, 128 }), 2, 3, 6, 2))
            .Build());
        var parameters = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("WhitePoint"),
                Reals(0.95047, 1, 1.08883))]);
        var colorSpace = new PdfArray(new PdfObject[] { Name("Lab"), parameters });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] pixel = Pixel(rendered, 3, 6);
        Assert.All(pixel[..3], channel => Assert.InRange(channel, (byte)115, (byte)125));
        Assert.Equal(255, pixel[3]);
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesSeparationTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(1, 1, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Separation"), Name("SpotRed"), Name("DeviceRGB"), function
        });
        PdfDocument document = AddImageDictionaryEntry(source, "ColorSpace", colorSpace);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 3, 6));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 7, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsAxialShadings()
    {
        var shading = new PdfAxialGradient(0, 0, 10, 0,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(1, 1, 1))
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([13, 13, 13, 255], Pixel(rendered, 0, 5));
        Assert.Equal([242, 242, 242, 255], Pixel(rendered, 9, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsMultiStopAxialShadings()
    {
        var shading = new PdfAxialGradient(0, 0, 10, 0,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(0.5, new PdfRgbColor(1, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(1, 1, 1))
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 230, 255], Pixel(rendered, 4, 5));
        Assert.Equal([26, 26, 255, 255], Pixel(rendered, 5, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ClipsAxialShadingsToTheirBounds()
    {
        var shading = new PdfAxialGradient(0, 0, 10, 0,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(0, 0, 0))
        ], bounds: new PdfShadingBounds(2, 2, 8, 8));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 5));
        Assert.Equal([0, 0, 0, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 8, 5));
    }

    [Fact]
    public void Render_PaintsRadialShadings()
    {
        var shading = new PdfRadialGradient(5, 5, 0, 5, 5, 5,
        [
            new PdfGradientStop(0, new PdfRgbColor(0, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(1, 1, 1))
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().PaintShading(shading)).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([36, 36, 36, 255], Pixel(rendered, 5, 5));
        Assert.Equal([180, 180, 180, 255], Pixel(rendered, 8, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesSampledSeparationTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(new PdfObject[] { new PdfInteger(2) })),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(8))]),
            new byte[] { 255, 255, 255, 255, 0, 0 });
        PdfDocument document = AddSampledSeparationColorSpace(source, function);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 3, 6));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 7, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesMultidimensionalDeviceNTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(new PdfObject[] { new PdfInteger(2), new PdfInteger(2) })),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(8))]),
            new byte[]
            {
                255, 255, 255, 255, 0, 0,
                0, 255, 0, 0, 0, 0
            });
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255, 0, 255, 0, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ExecutesType3GlyphProgramsAndAdvancesText()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("AA")
            .EndText();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());
        PdfDocument document = AddType3TriangleFont(source);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 15, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_FillsEmbeddedTrueTypeGlyphContours()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(font, 10)
            .SetCharacterSpacing(4)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowUnicodeText("AA")
            .EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 15, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_FillsEmbeddedCffCubicGlyphContours()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("A")
            .EndText();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());
        PdfDocument document = AddCffCurveFont(source);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_FillsAndStrokesPathsAndSupportsCurveShorthands()
    {
        byte[] content = "1 0 0 rg 0 0 1 RG 1 w 2 2 4 4 re B 1 8 m 3 6 5 8 v 5 8 m 7 6 9 8 y S"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 4, 6));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 2, 7));
        Assert.NotEqual([255, 255, 255, 255], Pixel(page, 3, 3));
        Assert.NotEqual([255, 255, 255, 255], Pixel(page, 7, 3));
    }

    [Fact]
    public void Render_UsesCropOriginAndClockwisePageRotation()
    {
        byte[] content = "1 0 0 rg 10 5 5 5 re f"u8.ToArray();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 15, content).Build());
        PdfDocument document = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetCropBox(0, 10, 5, 10, 10)
            .SetRotation(0, 90)
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 2));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 7, 7));
    }

    [Fact]
    public void Render_IntersectsNestedGraphicsStateClippingPaths()
    {
        byte[] content = "2 2 6 6 re W n q 4 0 4 10 re W* n 1 0 0 rg 0 0 10 10 re f Q 0 0 1 rg 0 0 2 2 re f"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 8, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 8));
    }

    [Fact]
    public void Render_DecodesInlineRgbImages()
    {
        byte[] prefix = Encoding.ASCII.GetBytes(
            "q 4 0 0 2 2 3 cm BI /W 2 /H 1 /BPC 8 /CS /RGB ID ");
        byte[] suffix = Encoding.ASCII.GetBytes(" EI Q");
        byte[] content = [.. prefix, 255, 0, 0, 0, 255, 0, .. suffix];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 6));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 5, 6));
        Assert.DoesNotContain("Inline-image rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_DistinguishesNonzeroAndEvenOddCompoundFills()
    {
        byte[] content = "1 0 0 rg 1 1 8 8 re 3 3 4 4 re f 0 0 1 rg 11 1 8 8 re 13 3 4 4 re f*"u8.ToArray();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 15, 5));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 12, 5));
    }

    [Fact]
    public void Render_CompositesGraphicsStateOpacityOverTheBackground()
    {
        var content = new PdfContentStreamBuilder()
            .SetOpacity(0.5)
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 10, 10)
            .Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage opaque = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage transparent = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 255, 255], Pixel(opaque, 5, 5));
        Assert.Equal([0, 0, 255, 128], Pixel(transparent, 5, 5));
    }

    [Theory]
    [InlineData(PdfBlendMode.Multiply, 0, 0, 0)]
    [InlineData(PdfBlendMode.Screen, 255, 0, 255)]
    [InlineData(PdfBlendMode.Darken, 0, 0, 0)]
    [InlineData(PdfBlendMode.Lighten, 255, 0, 255)]
    [InlineData(PdfBlendMode.Difference, 255, 0, 255)]
    [InlineData(PdfBlendMode.Exclusion, 255, 0, 255)]
    public void Render_CompositesSeparableBlendModes(
        PdfBlendMode blendMode, byte blue, byte green, byte red)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0, 0, 1).Rectangle(0, 0, 10, 10).Fill()
            .SetBlendMode(blendMode)
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 10, 10).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([blue, green, red, (byte)255], Pixel(rendered, 5, 5));
        Assert.DoesNotContain("Transparency blend-mode rendering is not implemented.",
            rendered.Diagnostics);
    }

    [Theory]
    [InlineData(PdfBlendMode.Overlay)]
    [InlineData(PdfBlendMode.ColorDodge)]
    [InlineData(PdfBlendMode.ColorBurn)]
    [InlineData(PdfBlendMode.HardLight)]
    [InlineData(PdfBlendMode.SoftLight)]
    public void Render_AcceptsRemainingSeparableBlendModes(PdfBlendMode blendMode)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0.2, 0.4, 0.6).Rectangle(0, 0, 10, 10).Fill()
            .SetBlendMode(blendMode)
            .SetFillRgb(0.8, 0.3, 0.1).Rectangle(0, 0, 10, 10).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain("Transparency blend-mode rendering is not implemented.",
            rendered.Diagnostics);
        Assert.Equal(255, Pixel(rendered, 5, 5)[3]);
    }

    [Theory]
    [InlineData(PdfBlendMode.Hue, 102)]
    [InlineData(PdfBlendMode.Saturation, 102)]
    [InlineData(PdfBlendMode.Color, 102)]
    [InlineData(PdfBlendMode.Luminosity, 204)]
    public void Render_CompositesNonseparableBlendModes(
        PdfBlendMode blendMode, byte expected)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0.4, 0.4, 0.4).Rectangle(0, 0, 10, 10).Fill()
            .SetBlendMode(blendMode)
            .SetFillRgb(0.8, 0.8, 0.8).Rectangle(0, 0, 10, 10).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([expected, expected, expected, (byte)255], Pixel(rendered, 5, 5));
        Assert.DoesNotContain("Transparency blend-mode rendering is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_ExpandsNestedFormsWithScopedResourcesMatricesAndBounds()
    {
        var inner = new PdfFormXObject(4, 4, new PdfContentStreamBuilder()
            .SetOpacity(0.5)
            .SetFillRgb(1, 0, 0)
            .Rectangle(-2, -2, 8, 8)
            .Fill());
        var outer = new PdfFormXObject(8, 8,
            new PdfContentStreamBuilder().DrawForm(inner, 2, 2));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawForm(outer, 1, 1))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 255, 255], Pixel(page, 4, 4));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 2, 4));
        Assert.DoesNotContain("Form XObject rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_SeparatesWidgetAppearanceInclusionFromPageContent()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(20, 20)
            .AddCheckBox(0, "approved", 5, 5, 10, 10, isChecked: true)
            .Build());
        var renderer = new PdfPageRenderer(document);

        PdfRenderedPage hidden = renderer.Render(0,
            new PdfRenderOptions(20, 20, includeAnnotations: true, includeFormFields: false));
        PdfRenderedPage shown = renderer.Render(0,
            new PdfRenderOptions(20, 20, includeAnnotations: false, includeFormFields: true));

        Assert.Equal([255, 255, 255, 255], Pixel(hidden, 5, 14));
        Assert.NotEqual([255, 255, 255, 255], Pixel(shown, 5, 14));
        Assert.DoesNotContain("Form-field rendering is not implemented.", shown.Diagnostics);
    }

    [Fact]
    public void Render_PaintsInlineStencilMasksWithTheCurrentFill()
    {
        byte[] prefix = Encoding.ASCII.GetBytes(
            "0 0 1 rg q 4 0 0 2 2 3 cm BI /W 2 /H 1 /IM true ID ");
        byte[] suffix = Encoding.ASCII.GetBytes(" EI Q");
        byte[] content = [.. prefix, 0b0100_0000, .. suffix];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 0, 255], Pixel(page, 2, 6));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 5, 6));
        Assert.DoesNotContain("Masked-image rendering is not implemented.", page.Diagnostics);
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(value)));

    private static PdfDocument AddImageDictionaryEntry(
        PdfDocument source, string name, PdfObject value, byte[]? encodedData = null)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(reference));
        PdfName entryName = Name(name);
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(entryName)).Append(
            new KeyValuePair<PdfName, PdfObject>(entryName, value)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber,
                new PdfStream(dictionary, encodedData ?? image.EncodedData.ToArray())).Build());
    }

    private static PdfDocument AddPageColorSpaceResource(
        PdfDocument source, string name, PdfObject value)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var colorSpaces = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name(name), value)]);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("ColorSpace")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpaces)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDocument AddIccImageColorSpace(PdfDocument source, PdfStream profile)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference imageReference = Assert.IsType<PdfIndirectReference>(
            xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(imageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference profileReference = update.AddObject(profile);
        var colorSpace = new PdfArray(new PdfObject[] { Name("ICCBased"), profileReference });
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("ColorSpace")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, image.EncodedData.Span)).Build());
    }

    private static PdfDocument AddSampledSeparationColorSpace(
        PdfDocument source, PdfStream function)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference imageReference = Assert.IsType<PdfIndirectReference>(
            xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(imageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference functionReference = update.AddObject(function);
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("Separation"), Name("SpotRed"), Name("DeviceRGB"), functionReference
        });
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("ColorSpace")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, image.EncodedData.Span)).Build());
    }

    private static PdfDocument AddDeviceNColorSpace(
        PdfDocument source, PdfStream function, byte[] imageSamples)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfIndirectReference imageReference = Assert.IsType<PdfIndirectReference>(
            xObjects[Name("Im1")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(imageReference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference functionReference = update.AddObject(function);
        var colorSpace = new PdfArray(new PdfObject[]
        {
            Name("DeviceN"),
            new PdfArray(new PdfObject[] { Name("SpotRed"), Name("SpotGreen") }),
            Name("DeviceRGB"), functionReference
        });
        var dictionary = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("ColorSpace"))
                && !entry.Key.Equals(Name("Filter"))
                && !entry.Key.Equals(Name("DecodeParms")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), colorSpace)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, imageSamples)).Build());
    }

    private static PdfDocument AddType3TriangleFont(PdfDocument source)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        KeyValuePair<PdfName, PdfObject> fontEntry = Assert.Single(fonts);
        PdfIndirectReference fontReference = Assert.IsType<PdfIndirectReference>(fontEntry.Value);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference glyphReference = update.AddObject(new PdfStream(
            new PdfDictionary([]), "0 0 m 1000 0 l 500 1000 l h f"u8));
        var font = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("Font")),
            new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("Type3")),
            new KeyValuePair<PdfName, PdfObject>(Name("FontBBox"), Reals(0, 0, 1000, 1000)),
            new KeyValuePair<PdfName, PdfObject>(Name("FontMatrix"),
                Reals(0.001, 0, 0, 0.001, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("CharProcs"),
                new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(Name("A"), glyphReference)])),
            new KeyValuePair<PdfName, PdfObject>(Name("Encoding"),
                new PdfDictionary([
                    new KeyValuePair<PdfName, PdfObject>(Name("Differences"),
                        new PdfArray(new PdfObject[] { new PdfInteger(65), Name("A") }))])),
            new KeyValuePair<PdfName, PdfObject>(Name("FirstChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("LastChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("Widths"), Reals(1000)),
            new KeyValuePair<PdfName, PdfObject>(Name("Resources"), new PdfDictionary([]))]);
        return PdfDocument.Open(update.ReplaceObject(fontReference.ObjectNumber, font).Build());
    }

    private static PdfDocument AddCffCurveFont(PdfDocument source)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        PdfIndirectReference fontReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(fonts).Value);
        byte[] program = [.. PdfCffGlyphReaderTests.Numbers(0, 0), 21,
            .. PdfCffGlyphReaderTests.Numbers(0, 1000, 1000, 0, 0, -1000), 8, 14];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference fileReference = update.AddObject(new PdfStream(
            new PdfDictionary([new KeyValuePair<PdfName, PdfObject>(
                Name("Subtype"), Name("Type1C"))]), PdfCffGlyphReaderTests.Build(program)));
        var descriptor = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("FontDescriptor")),
            new KeyValuePair<PdfName, PdfObject>(Name("FontName"), Name("KillerCff")),
            new KeyValuePair<PdfName, PdfObject>(Name("FontFile3"), fileReference)]);
        var font = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("Font")),
            new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("Type1")),
            new KeyValuePair<PdfName, PdfObject>(Name("BaseFont"), Name("KillerCff")),
            new KeyValuePair<PdfName, PdfObject>(Name("FirstChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("LastChar"), new PdfInteger(65)),
            new KeyValuePair<PdfName, PdfObject>(Name("Widths"), Reals(1000)),
            new KeyValuePair<PdfName, PdfObject>(Name("FontDescriptor"), descriptor)]);
        return PdfDocument.Open(update.ReplaceObject(fontReference.ObjectNumber, font).Build());
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfArray Reals(params double[] values) =>
        new(values.Select(value => (PdfObject)new PdfReal(value)));
}
