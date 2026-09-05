using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using System.IO.Compression;
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
    public async Task Render_HonorsCancellationDuringRasterization()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, "0 0 100 100 re f"u8.ToArray()).Build());
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(4096, 4096,
                    includeAnnotations: false, includeFormFields: false),
                cancellation.Token)));
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
    public void Render_AcceptsEmptyPathPaintingOperatorsAsNoOps()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, "S s B B* b b*"u8.ToArray()).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Empty(page.Diagnostics);
    }

    [Fact]
    public void Render_PreservesNonzeroAndEvenOddFillRules()
    {
        const string paths = "1 1 m 9 1 l 9 9 l 1 9 l h "
            + "3 3 m 7 3 l 7 7 l 3 7 l h ";
        PdfRenderedPage nonzero = Render(paths + "f");
        PdfRenderedPage evenOdd = Render(paths + "f*");

        Assert.Equal([0, 0, 0, 255], Pixel(nonzero, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(evenOdd, 5, 5));
        Assert.Equal([0, 0, 0, 255], Pixel(evenOdd, 2, 5));

        static PdfRenderedPage Render(string content)
        {
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(10, 10, Encoding.ASCII.GetBytes(content)).Build());
            return new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(10, 10,
                    includeAnnotations: false, includeFormFields: false));
        }
    }

    [Fact]
    public void Render_AppliesLineDashPatternPhaseAndTransform()
    {
        PdfDocument patternDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, "0 w [1 3] 0 d 0 0.5 m 8 0.5 l S"u8.ToArray()).Build());
        PdfDocument phaseDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, "0 w [1 3] 2 d 0 0.5 m 8 0.5 l S"u8.ToArray()).Build());
        PdfDocument negativePhaseDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1, "0 w [1 3] -2 d 0 0.5 m 8 0.5 l S"u8.ToArray()).Build());
        PdfDocument transformDocument = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 1,
                "2 0 0 1 0 0 cm 0 w [1 1] 0 d 0 0.5 m 4 0.5 l S"u8.ToArray()).Build());

        PdfRenderedPage pattern = new PdfPageRenderer(patternDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage phase = new PdfPageRenderer(phaseDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage negativePhase = new PdfPageRenderer(negativePhaseDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage transformed = new PdfPageRenderer(transformDocument).Render(
            0, new PdfRenderOptions(8, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(pattern, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(pattern, 2, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(pattern, 4, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(phase, 0, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(phase, 2, 0));
        Assert.Equal(phase.Pixels.ToArray(), negativePhase.Pixels.ToArray());
        Assert.Equal([255, 255, 255, 255], Pixel(transformed, 3, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(transformed, 4, 0));
    }

    [Fact]
    public void Render_TransformsStrokeWidthWithTheCurrentMatrix()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "q 0.1 0 0 0.1 0 0 cm 50 w 0 50 m 100 50 l S Q"))
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 5, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(page, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 5, 9));
    }

    [Fact]
    public void Render_AppliesButtRoundAndProjectingLineCaps()
    {
        PdfRenderedPage butt = RenderCap(PdfLineCap.Butt);
        PdfRenderedPage round = RenderCap(PdfLineCap.Round);
        PdfRenderedPage square = RenderCap(PdfLineCap.ProjectingSquare);

        Assert.Equal([255, 255, 255, 255], Pixel(butt, 0, 1));
        Assert.Equal([0, 0, 0, 255], Pixel(round, 0, 1));
        Assert.Equal([255, 255, 255, 255], Pixel(round, 0, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(square, 0, 0));

        static PdfRenderedPage RenderCap(PdfLineCap cap)
        {
            var content = new PdfContentStreamBuilder()
                .SetLineWidth(2).SetLineCap(cap)
                .MoveTo(1.5, 1.5).LineTo(3.5, 1.5).Stroke();
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(5, 3, content).Build());
            return new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(5, 3,
                    includeAnnotations: false, includeFormFields: false));
        }
    }

    [Fact]
    public void Render_AppliesMiterRoundAndBevelLineJoins()
    {
        PdfRenderedPage miter = RenderJoin(PdfLineJoin.Miter, 10);
        PdfRenderedPage limitedMiter = RenderJoin(PdfLineJoin.Miter, 1);
        PdfRenderedPage round = RenderJoin(PdfLineJoin.Round, 10);
        PdfRenderedPage bevel = RenderJoin(PdfLineJoin.Bevel, 10);

        Assert.Equal([0, 0, 0, 255], Pixel(miter, 9, 1));
        Assert.Equal([255, 255, 255, 255], Pixel(limitedMiter, 9, 1));
        Assert.Equal([0, 0, 0, 255], Pixel(round, 9, 2));
        Assert.Equal([255, 255, 255, 255], Pixel(bevel, 9, 2));

        static PdfRenderedPage RenderJoin(PdfLineJoin join, double limit)
        {
            var content = new PdfContentStreamBuilder()
                .SetLineWidth(4).SetLineJoin(join).SetMiterLimit(limit)
                .MoveTo(4, 4).LineTo(10, 16).LineTo(16, 4).Stroke();
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(20, 20, content).Build());
            return new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(20, 20,
                    includeAnnotations: false, includeFormFields: false));
        }
    }

    [Fact]
    public void Render_ReportsUnsupportedOperatorsOutsideCompatibilitySections()
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "/Perceptual ri 1 i /Tag MP 1 2 d0 1 2 3 4 5 6 d1 "
            + "BX UnsupportedInsideCompatibility EX UnsupportedOutsideCompatibility");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain(page.Diagnostics,
            diagnostic => diagnostic.Contains("UnsupportedInsideCompatibility",
                StringComparison.Ordinal));
        Assert.Contains("Rendering operator UnsupportedOutsideCompatibility is not implemented.",
            page.Diagnostics);
    }

    [Fact]
    public void Render_IgnoresClosePathWithoutAnActiveSubpath()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, "h"u8.ToArray()).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain("Rendering operator h is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_PaintsNamedAndExplicitNonPatternColorSpaces()
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "/CS1 cs 1 0 0 sc 0 0 1 1 re f "
            + "/DeviceCMYK cs 1 0 0 0 scn 1 0 1 1 re f "
            + "/CS1 CS 0 0 1 SCN 0 w 0 1.5 m 2 1.5 l S");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 2, content).Build());
        PdfDocument document = AddPageColorSpaceResource(source, "CS1", Name("DeviceRGB"));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 2, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 1));
        Assert.Equal([255, 255, 0, 255], Pixel(page, 1, 1));
    }

    [Fact]
    public void Render_HonorsDefaultOptionalContentVisibility()
    {
        var hidden = new PdfOptionalContentGroup("Hidden", initiallyVisible: false);
        var visible = new PdfOptionalContentGroup("Visible", initiallyVisible: true);
        var content = new PdfContentStreamBuilder()
            .BeginOptionalContent(hidden)
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill()
            .EndMarkedContent()
            .BeginOptionalContent(visible)
            .SetFillRgb(0, 1, 0).Rectangle(1, 0, 1, 1).Fill()
            .EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 1, 0));
    }

    [Fact]
    public void Render_EvaluatesOptionalContentMembershipPoliciesAndExpressions()
    {
        PdfRenderedPage hiddenByPolicy = new PdfPageRenderer(
            OptionalContentMembershipDocument("/OCGs [5 0 R 6 0 R] /P /AllOn")).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));
        PdfRenderedPage visibleByExpression = new PdfPageRenderer(
            OptionalContentMembershipDocument("/VE [/Or 5 0 R 6 0 R]")).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(hiddenByPolicy, 0, 0));
        Assert.Equal([0, 0, 0, 255], Pixel(visibleByExpression, 0, 0));
    }

    [Fact]
    public void Render_HonorsOptionalContentAttachedToXObjects()
    {
        PdfRenderedPage page = new PdfPageRenderer(OptionalContentXObjectDocument()).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
    }

    [Fact]
    public void Render_HonorsOptionalContentAttachedToAnnotations()
    {
        PdfRenderedPage visible = new PdfPageRenderer(
            OptionalContentAnnotationDocument(hidden: false)).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: true, includeFormFields: false));
        PdfRenderedPage hidden = new PdfPageRenderer(
            OptionalContentAnnotationDocument(hidden: true)).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: true, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(visible, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(hidden, 0, 0));
    }

    [Fact]
    public void Render_FillsWithColoredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetFillPattern(pattern).Rectangle(0, 0, 4, 1).Fill())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_FillsWithUncoloredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .Rectangle(0, 0, 1, 1).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetFillPattern(pattern, new PdfRgbColor(0, 1, 0))
                .Rectangle(0, 0, 4, 1).Fill())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_StrokesWithColoredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 1, 1).Fill());
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetStrokePattern(pattern).SetLineWidth(1)
                .MoveTo(0, 0.5).LineTo(4, 0.5).Stroke())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([0, 0, 255, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
    }

    [Fact]
    public void Render_StrokesWithUncoloredTilingPatterns()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder()
            .Rectangle(0, 0, 1, 1).Fill(),
            paintType: PdfTilingPatternPaintType.Uncolored);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 1, new PdfContentStreamBuilder()
                .SetStrokePattern(pattern, new PdfRgbColor(0, 0, 1)).SetLineWidth(1)
                .MoveTo(0, 0.5).LineTo(4, 0.5).Stroke())
            .Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(4, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.Equal([255, 0, 0, 255], Pixel(page, 2, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 3, 0));
        Assert.DoesNotContain("Tiling-pattern rendering is not implemented.", page.Diagnostics);
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

    [Theory]
    [InlineData(2, new byte[] { 0x30 })]
    [InlineData(4, new byte[] { 0x0F })]
    [InlineData(16, new byte[] { 0x00, 0x00, 0xFF, 0xFF })]
    public void Render_DecodesPackedImageSampleDepths(int bits, byte[] samples)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 0, 0, 2, 1))
            .Build());
        PdfDocument document = AddImageDictionaryEntry(source, "BitsPerComponent",
            new PdfInteger(bits), Compress(samples));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 255], Pixel(page, 1, 0));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            page.Diagnostics);
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

    [Theory]
    [InlineData(2, new byte[] { 0x30 })]
    [InlineData(4, new byte[] { 0x0F })]
    [InlineData(16, new byte[] { 0x00, 0x00, 0xFF, 0xFF })]
    public void Render_CompositesPackedImageSoftMasks(int bits, byte[] samples)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgba(2, 1, new byte[]
                {
                    255, 0, 0, 255,
                    0, 255, 0, 255
                }), 0, 0, 2, 1))
            .Build());
        PdfDocument document = ReplaceImageSoftMask(source, bits, Compress(samples));

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 0], Pixel(page, 0, 0));
        Assert.Equal([0, 255, 0, 255], Pixel(page, 1, 0));
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
    public void Render_AppliesExplicitImageMaskStreams()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromRgb(2, 1, new byte[]
                {
                    255, 0, 0,
                    0, 255, 0
                }), 0, 0, 2, 1))
            .Build());
        PdfDocument document = AddExplicitImageMask(source, [0b0100_0000]);

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(2, 1, transparentBackground: true,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(page, 0, 0));
        Assert.Equal([255, 255, 255, 0], Pixel(page, 1, 0));
        Assert.DoesNotContain("Masked-image rendering is not implemented.", page.Diagnostics);
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
    public void Render_PaintsAndClipsShadingPatterns()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "/Pattern cs /P1 scn 0 0 5 10 re f /Pattern CS /P1 SCN 1 w 7 1 2 8 re S"))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))]);
        PdfDocument document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 2, 0));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 13, 255], Pixel(rendered, 2, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 5, 9));
        Assert.Equal([0, 0, 140, 255], Pixel(rendered, 7, 1));
        Assert.DoesNotContain("Pattern rendering is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_ResolvesNamedPatternColorSpaces()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "/CS1 cs /P1 scn 0 0 5 10 re f /CS2 CS /P1 SCN 1 w 7 1 2 8 re S"))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))]);
        PdfDocument document = AddShadingPatternResource(source, shading, Reals(1, 0, 0, 1, 0, 0));
        document = AddPageColorSpaceResources(document,
            new KeyValuePair<PdfName, PdfObject>(Name("CS1"),
                new PdfArray(new PdfObject[] { Name("Pattern") })),
            new KeyValuePair<PdfName, PdfObject>(Name("CS2"),
                new PdfArray(new PdfObject[] { Name("Pattern"), Name("DeviceRGB") })));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 13, 255], Pixel(rendered, 0, 5));
        Assert.Equal([0, 0, 191, 255], Pixel(rendered, 7, 1));
        Assert.DoesNotContain("Pattern rendering is not implemented.", rendered.Diagnostics);
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
    public void Render_AcceptsRepeatedStitchingFunctionBoundaries()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        PdfDictionary Segment(double red) => new([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(red, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(red, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(3)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Functions"),
                new PdfArray(new PdfObject[] { Segment(0), Segment(0.5), Segment(1) })),
            new KeyValuePair<PdfName, PdfObject>(Name("Bounds"), Reals(0.5, 0.5)),
            new KeyValuePair<PdfName, PdfObject>(Name("Encode"), Reals(0, 1, 0, 1, 0, 1))]);
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))]), []);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(rendered, 4, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
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
    public void Render_PaintsFunctionShadingsWithTheirMatrixAndDomain()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            Encoding.ASCII.GetBytes("{ pop dup dup }") );
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(1)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), Reals(5, 0, 0, 5, 2, 3)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function)]);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 5));
        Assert.Equal([25, 25, 25, 255], Pixel(rendered, 2, 6));
        Assert.Equal([230, 230, 230, 255], Pixel(rendered, 6, 6));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsTensorPatchShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        byte[] points =
        [
            0, 0, 85, 0, 170, 0, 255, 0,
            255, 85, 255, 170, 255, 255, 170, 255,
            85, 255, 0, 255, 0, 170, 0, 85,
            85, 85, 170, 85, 170, 170, 85, 170
        ];
        byte[] colors = Enumerable.Repeat(new byte[] { 255, 0, 0 }, 4)
            .SelectMany(value => value).ToArray();
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(7)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[] { 0 }.Concat(points).Concat(colors).ToArray());
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.DoesNotContain("The shading type or function is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsCoonsPatchShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        byte[] boundary =
        [
            0, 0, 85, 0, 170, 0, 255, 0,
            255, 85, 255, 170, 255, 255, 170, 255,
            85, 255, 0, 255, 0, 170, 0, 85
        ];
        byte[] colors = Enumerable.Repeat(new byte[] { 0, 255, 0 }, 4)
            .SelectMany(value => value).ToArray();
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(6)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[] { 0 }.Concat(boundary).Concat(colors).ToArray());
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ContinuesTensorPatchMeshesAcrossSharedEdges()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        byte[] firstPoints =
        [
            0, 0, 43, 0, 85, 0, 128, 0,
            128, 43, 128, 85, 128, 128, 85, 128,
            43, 128, 0, 128, 0, 85, 0, 43,
            43, 43, 85, 43, 85, 85, 43, 85
        ];
        byte[] secondPoints =
        [
            170, 128, 213, 128, 255, 128, 255, 85,
            255, 43, 255, 0, 213, 0, 170, 0,
            170, 43, 213, 43, 213, 85, 170, 85
        ];
        byte[] firstColors = Enumerable.Repeat(new byte[] { 255, 0, 0 }, 4)
            .SelectMany(value => value).ToArray();
        byte[] secondColors = [0, 0, 255, 0, 0, 255];
        byte[] data = new byte[] { 0 }.Concat(firstPoints).Concat(firstColors)
            .Concat(new byte[] { 1 }).Concat(secondPoints).Concat(secondColors).ToArray();
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(7)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]), data);
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] continuedPatch = Pixel(rendered, 9, 7);
        Assert.True(continuedPatch[0] > 200 && continuedPatch[2] < 50);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsFreeFormGouraudMeshShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[]
            {
                0, 0, 0, 255, 0, 0,
                0, 255, 0, 0, 255, 0,
                0, 0, 255, 0, 0, 255
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([64, 64, 128, 255], Pixel(rendered, 2, 7));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 8, 1));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ContinuesFreeFormGouraudMeshesAcrossEitherAvailableEdge()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[]
            {
                0, 0, 255, 255, 0, 0,
                0, 0, 0, 0, 255, 0,
                0, 128, 0, 0, 0, 255,
                1, 128, 255, 255, 255, 255,
                2, 255, 255, 255, 0, 0
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.NotEqual([255, 255, 255, 255], Pixel(rendered, 4, 5));
        Assert.NotEqual([255, 255, 255, 255], Pixel(rendered, 8, 1));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_EvaluatesGouraudMeshFunctionsAfterInterpolation()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(0, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 1, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(2))]);
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerFlag"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), function),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"), Reals(0, 10, 0, 10, 0, 1))]),
            new byte[]
            {
                0, 0, 0, 0,
                0, 255, 0, 255,
                0, 0, 255, 0
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] pixel = Pixel(rendered, 2, 7);
        Assert.InRange(pixel[0], 10, 30);
        Assert.Equal(pixel[0], pixel[1]);
        Assert.Equal(pixel[0], pixel[2]);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsLatticeGouraudMeshShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var shading = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(5)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerCoordinate"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerComponent"), new PdfInteger(8)),
            new KeyValuePair<PdfName, PdfObject>(Name("VerticesPerRow"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Decode"),
                Reals(0, 10, 0, 10, 0, 1, 0, 1, 0, 1))]),
            new byte[]
            {
                0, 0, 255, 0, 0,
                255, 0, 0, 255, 0,
                0, 255, 0, 0, 255,
                255, 255, 255, 255, 255
            });
        PdfDocument document = AddShadingResource(source, shading);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        byte[] lowerLeft = Pixel(rendered, 0, 9);
        byte[] upperRight = Pixel(rendered, 9, 0);
        Assert.True(lowerLeft[2] > lowerLeft[1] && lowerLeft[2] > lowerLeft[0]);
        Assert.True(upperRight[0] > 200 && upperRight[1] > 200 && upperRight[2] > 200);
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_PaintsThirtyTwoBitSampledShadings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes("/Sh1 sh")).Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Size"),
                new PdfArray(new PdfObject[] { new PdfInteger(2) })),
            new KeyValuePair<PdfName, PdfObject>(Name("BitsPerSample"), new PdfInteger(32))]),
            new byte[12].Concat(Enumerable.Repeat(byte.MaxValue, 12)).ToArray());
        PdfDocument document = AddSampledAxialShadingResource(source, function);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([13, 13, 13, 255], Pixel(rendered, 0, 5));
        Assert.Equal([242, 242, 242, 255], Pixel(rendered, 9, 5));
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
    public void Render_AppliesCalculatorDeviceNTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ 1 index 1 cvr exch sub 3 1 roll 1 exch sub 3 1 roll }"u8);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 0, 255, 255, 0, 255, 0, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesCalculatorCopyOperator()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ 2 copy }"u8);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255, 255, 0, 255, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesCalculatorLogicalAndBitwiseOperators()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ pop pop true false or false xor false ne { 1 } { 0 } ifelse "u8.ToArray()
                .Concat("true dup and false exch pop not 5 3 and 1 eq and "u8.ToArray())
                .Concat("{ 1 } { 0 } ifelse 1 3 bitshift 8 eq not not "u8.ToArray())
                .Concat("{ 1 } { 0 } ifelse }"u8.ToArray())
                .ToArray());
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 3, 6));
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesNestedCalculatorConditionalsAndAtan()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ atan 360 div dup 0 lt { pop 0 } { dup 1 gt { pop 1 } if } ifelse dup dup }"u8);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 255, 0, 0, 255 });

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([64, 64, 64, 255, 0, 0, 0, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesCalculatorSeparationTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 255 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(4)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1))]),
            "{ dup 1 exch sub 0 }"u8);
        PdfDocument document = AddSampledSeparationColorSpace(source, function);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 255, 0, 255, 0, 0, 255, 255],
            Pixel(rendered, 3, 6).Concat(Pixel(rendered, 7, 6)).ToArray());
        Assert.DoesNotContain("The image color space or sample depth is not implemented.",
            rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesSingleChannelExponentialDeviceNTintTransforms()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder().DrawImage(
                PdfImage.FromGray(2, 1, new byte[] { 0, 0 }), 2, 3, 6, 2))
            .Build());
        var function = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("FunctionType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Domain"), Reals(0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("Range"), Reals(0, 1, 0, 1, 0, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C0"), Reals(1, 1, 1)),
            new KeyValuePair<PdfName, PdfObject>(Name("C1"), Reals(1, 0, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("N"), new PdfInteger(1))]);
        PdfDocument document = AddDeviceNColorSpace(source, function,
            new byte[] { 0, 255 }, componentCount: 1);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([255, 255, 255, 255, 0, 0, 255, 255],
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

    [Theory]
    [InlineData(PdfStandardFont.Helvetica)]
    [InlineData(PdfStandardFont.HelveticaBold)]
    [InlineData(PdfStandardFont.HelveticaOblique)]
    [InlineData(PdfStandardFont.HelveticaBoldOblique)]
    [InlineData(PdfStandardFont.TimesRoman)]
    [InlineData(PdfStandardFont.TimesBold)]
    [InlineData(PdfStandardFont.TimesItalic)]
    [InlineData(PdfStandardFont.TimesBoldItalic)]
    [InlineData(PdfStandardFont.Courier)]
    [InlineData(PdfStandardFont.CourierBold)]
    [InlineData(PdfStandardFont.CourierOblique)]
    [InlineData(PdfStandardFont.CourierBoldOblique)]
    public void Render_UsesBundledOutlinesForOrdinaryStandardFonts(
        PdfStandardFont standardFont)
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(standardFont, 10)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowLatin1Text("A A")
            .EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Contains(Enumerable.Range(0, rendered.Width * rendered.Height)
            .Select(index => rendered.Pixels.Span.Slice(index * 4, 4).ToArray()),
            pixel => pixel.SequenceEqual(new byte[] { 0, 0, 255, 255 }));
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain(rendered.Diagnostics,
            diagnostic => diagnostic.StartsWith("Text outlines for font ",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Render_AcceptsNegativeFontSizes()
    {
        var content = new PdfContentStreamBuilder()
            .BeginText()
            .SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(1, 0, 0, 1, 10, 10)
            .ShowLatin1Text("A")
            .EndText();
        byte[] source = new PdfDocumentBuilder().AddPage(20, 20, content).Build();
        string pdf = Encoding.Latin1.GetString(source)
            .Replace(" 10 Tf", " -1 Tf", StringComparison.Ordinal);
        PdfDocument document = PdfDocument.Open(Encoding.Latin1.GetBytes(pdf));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 20,
                includeAnnotations: false, includeFormFields: false));

        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesEmbeddedGlyphsToTheClippingPathAtEndText()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .BeginText()
            .SetFont(font, 10)
            .SetTextRenderingMode(PdfTextRenderingMode.Clip)
            .SetTextMatrix(1, 0, 0, 1, 0, 0)
            .ShowUnicodeText("A")
            .EndText()
            .SetFillRgb(1, 0, 0)
            .Rectangle(0, 0, 10, 10)
            .Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 1));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
        Assert.DoesNotContain("A text glyph outline is not implemented.", rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesVerticalOriginsAdvancesAndPositionAdjustments()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .BeginText()
            .SetFont(font, 10)
            .SetTextMatrix(1, 0, 0, 1, 5, 20)
            .ShowPositionedUnicodeText(["A", "A"], [100])
            .EndText();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 20, content).Build());
        PdfDocument document = MakeEmbeddedFontVertical(source);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 20, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 5));
        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 5, 15));
        Assert.Equal([255, 255, 255, 255], Pixel(rendered, 1, 10));
        Assert.DoesNotContain("Text rendering is not implemented.", rendered.Diagnostics);
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

    [Fact]
    public void Render_CompositesOverlappingStrokeSegmentsOnce()
    {
        var content = new PdfContentStreamBuilder()
            .SetOpacity(0.5).SetLineWidth(2)
            .MoveTo(1, 1).LineTo(5, 5).LineTo(1, 5).LineTo(5, 1).Stroke();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(6, 6, content).Build());

        PdfRenderedPage page = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(6, 6,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([128, 128, 128, 255], Pixel(page, 3, 3));
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
    public void Render_AppliesAlphaSoftMaskBackdropAndTransferFunction()
    {
        PdfDocument document = AddGraphicsSoftMask(
            "Alpha", 0.5, 1, 0.5, "0 0 5 10 re f");

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 255, 255], Pixel(rendered, 2, 5));
        Assert.Equal([127, 127, 255, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_AppliesLuminositySoftMaskBackdropAndTransferFunction()
    {
        PdfDocument document = AddGraphicsSoftMask(
            "Luminosity", 0.25, 0.75, 1, "0 g 0 0 5 10 re f");

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([191, 191, 255, 255], Pixel(rendered, 2, 5));
        Assert.Equal([64, 64, 255, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_ConvertsRgbSoftMaskBackdropToLuminosity()
    {
        PdfDocument document = AddGraphicsSoftMask(
            "Luminosity", 0, 1, 0, "0 g 0 0 5 10 re f",
            "DeviceRGB", [1, 0, 0]);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10,
                includeAnnotations: false, includeFormFields: false));

        Assert.Equal([178, 178, 255, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
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

    [Theory]
    [InlineData(2,
        "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAnmpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACMAAf+T34AQDF/fgBAJP9+AGAWxf8+0CAsX/9k=",
        200, 0, 0)]
    [InlineData(1,
        "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAoGpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACUAAf+T34AgC7KKf9+AGAWi3d+AEAk/z7QICxf/2Q==",
        0, 255, 0)]
    public void Render_UsesJpeg2000EmbeddedAlpha(
        int maskMode, string encoded, byte blue, byte green, byte red)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, new PdfContentStreamBuilder()
                .SetFillGray(0.5).Rectangle(0, 0, 20, 10).Fill()
                .DrawImage(PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 20, 10))
            .Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));
        PdfDocument document = AddImageDictionaryEntry(
            filtered, "SMaskInData", new PdfInteger(maskMode));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([64, 64, 192, 255], Pixel(rendered, 2, 5));
        Assert.Equal([blue, green, red, (byte)255], Pixel(rendered, 15, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_InfersJpeg2000ColorSpaceWithEmbeddedAlpha()
    {
        const string encoded =
            "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAABPanAyaAAAABZpaGRyAAAAAQAAAAIABAcHAAAAAAAPY29scgEAAAAAABAAAAAiY2RlZgAEAAAAAAABAAEAAAACAAIAAAADAAMAAQAAAAAAnWpwMmP/T/9RADIAAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAQHAQEHAQEHAQEHAQH/UgAMAAAAAQAABAQAAf9cAARAQP9kACUAAUNyZWF0ZWQgYnkgT3BlbkpQRUcgdmVyc2lvbiAyLjUuNP+QAAoAAAAAACIAAf+T34AQDF/fgBAMX8+0CAGv34AQDF//2Q==";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .SetFillGray(0.5).Rectangle(0, 0, 10, 10).Fill()
                .DrawImage(PdfImage.FromRgb(2, 1, new byte[6]), 0, 0, 10, 10))
            .Build());
        PdfDocument filtered = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));
        PdfDocument masked = AddImageDictionaryEntry(
            filtered, "SMaskInData", new PdfInteger(2));
        PdfDocument withMatte = AddImageDictionaryEntry(
            masked, "Matte", Reals(0, 0, 1));
        PdfDocument document = RemoveImageDictionaryEntry(withMatte, "ColorSpace");

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([192, 192, 192, 255], Pixel(rendered, 2, 5));
        Assert.Equal([128, 128, 128, 255], Pixel(rendered, 7, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    [Fact]
    public void Render_DecodesJpeg2000AtTheRequiredDisplayResolution()
    {
        const string encoded =
            "AAAADGpQICANCocKAAAAHGZ0eXBqcDIgAAAAAGpwMiBqcHhianB4IAAAAB5ycmVxAfj4AAUAAYAABUAADCAAEhAANwgAAAAAAFhqcDJoAAAAFmloZHIAAADsAAAA7AABBwcBAAAAAA9jb2xyAQIBAAAADAAAABNwY2xyAAEEBwcHBwD//wAAAAAYY21hcAAAAQAAAAEBAAABAgAAAQMAAAC7anAyY/9P/1EAKQAAAAAA7AAAAOwAAAAAAAAAAAAAAOwAAADsAAAAAAAAAAAAAQcBAf9SAAwAAQABAAUDAwAB/1wAE0BASEhQSEhQSEhQSEhQSEhQ/5AACgAAAAAAFgAG/5PfgCgRUFSjb/+QAAoAAAAAAA8BBv+TgP+QAAoAAAAAAA8CBv+TgP+QAAoAAAAAAA8DBv+TgP+QAAoAAAAAAA8EBv+TgP+QAAoAAAAAAA8FBv+TgP/Z";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, new PdfContentStreamBuilder()
                .DrawImage(PdfImage.FromGray(236, 236, new byte[236 * 236]),
                    0, 0, 10, 10))
            .Build());
        PdfDocument document = AddImageDictionaryEntry(source, "Filter", Name("JPXDecode"),
            Convert.FromBase64String(encoded));

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal([0, 0, 0, 255], Pixel(rendered, 5, 5));
        Assert.Empty(rendered.Diagnostics);
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(source);
        return output.ToArray();
    }

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

    private static PdfDocument RemoveImageDictionaryEntry(PdfDocument source, string name)
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
        var dictionary = new PdfDictionary(
            image.Dictionary.Where(entry => !entry.Key.Equals(entryName)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(reference.ObjectNumber,
                new PdfStream(dictionary, image.EncodedData.ToArray())).Build());
    }

    private static PdfDocument ReplaceImageSoftMask(
        PdfDocument source, int bits, byte[] encodedData)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary xObjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        PdfStream image = Assert.IsType<PdfStream>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(xObjects[Name("Im1")])));
        PdfIndirectReference maskReference = Assert.IsType<PdfIndirectReference>(
            image.Dictionary[Name("SMask")]);
        PdfStream mask = Assert.IsType<PdfStream>(source.Resolve(maskReference));
        var dictionary = new PdfDictionary(mask.Dictionary
            .Where(entry => !entry.Key.Equals(Name("BitsPerComponent")))
            .Append(new KeyValuePair<PdfName, PdfObject>(
                Name("BitsPerComponent"), new PdfInteger(bits))));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(maskReference.ObjectNumber, new PdfStream(dictionary, encodedData))
            .Build());
    }

    private static PdfDocument AddExplicitImageMask(PdfDocument source, byte[] samples)
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
        PdfIndirectReference maskReference = update.AddObject(new PdfStream(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("XObject")),
            new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("Image")),
            new KeyValuePair<PdfName, PdfObject>(Name("Width"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Height"), new PdfInteger(1)),
            new KeyValuePair<PdfName, PdfObject>(Name("ImageMask"), new PdfBoolean(true))
        ]), samples));
        var dictionary = new PdfDictionary(image.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Mask"), maskReference)));
        return PdfDocument.Open(update.ReplaceObject(imageReference.ObjectNumber,
            new PdfStream(dictionary, image.EncodedData.Span)).Build());
    }

    private static PdfDocument AddPageColorSpaceResource(
        PdfDocument source, string name, PdfObject value)
        => AddPageColorSpaceResources(source,
            new KeyValuePair<PdfName, PdfObject>(Name(name), value));

    private static PdfDocument AddGraphicsSoftMask(string subtype,
        double transferStart, double transferEnd, double backdrop, string maskContent,
        string groupColorSpace = "DeviceGray", double[]? backdropComponents = null)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, Encoding.ASCII.GetBytes(
                "q /Mask gs 1 0 0 rg 0 0 10 10 re f Q"))
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference groupReference = update.AddObject(new PdfStream(
            new PdfDictionary([
                Entry("Type", Name("XObject")),
                Entry("Subtype", Name("Form")),
                Entry("BBox", Reals(0, 0, 10, 10)),
                Entry("Resources", new PdfDictionary([])),
                Entry("Group", new PdfDictionary([
                    Entry("Type", Name("Group")),
                    Entry("S", Name("Transparency")),
                    Entry("CS", Name(groupColorSpace))
                ]))
            ]), Encoding.ASCII.GetBytes(maskContent)));
        var transfer = new PdfDictionary([
            Entry("FunctionType", new PdfInteger(2)),
            Entry("Domain", Reals(0, 1)),
            Entry("Range", Reals(0, 1)),
            Entry("N", new PdfInteger(1)),
            Entry("C0", Reals(transferStart)),
            Entry("C1", Reals(transferEnd))
        ]);
        var softMask = new PdfDictionary([
            Entry("S", Name(subtype)),
            Entry("G", groupReference),
            Entry("BC", Reals(backdropComponents ?? [backdrop])),
            Entry("TR", transfer)
        ]);
        var states = new PdfDictionary([Entry("Mask",
            new PdfDictionary([Entry("SMask", softMask)]))]);
        var updatedResources = new PdfDictionary(resources.Append(
            Entry("ExtGState", states)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", updatedResources)));
        return PdfDocument.Open(update.ReplaceObject(
            pageReference.ObjectNumber, updatedPage).Build());

        static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) =>
            new(Name(name), value);
    }

    private static PdfDocument AddPageColorSpaceResources(PdfDocument source,
        params KeyValuePair<PdfName, PdfObject>[] values)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var colorSpaces = new PdfDictionary(values);
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
        PdfDocument source, PdfObject function)
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
        PdfDocument source, PdfObject function, byte[] imageSamples, int componentCount = 2)
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
            new PdfArray(Enumerable.Range(0, componentCount)
                .Select(index => (PdfObject)Name($"Spot{index + 1}"))),
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

    private static PdfDocument AddShadingResource(PdfDocument source, PdfObject shading)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject preparedShading = shading;
        if (shading is PdfDictionary dictionary
            && dictionary.TryGetValue(Name("Function"), out PdfObject? functionValue)
            && functionValue is PdfStream function)
        {
            PdfIndirectReference functionReference = update.AddObject(function);
            preparedShading = new PdfDictionary(dictionary
                .Where(entry => !entry.Key.Equals(Name("Function")))
                .Append(new KeyValuePair<PdfName, PdfObject>(
                    Name("Function"), functionReference)));
        }
        PdfIndirectReference shadingReference = update.AddObject(preparedShading);
        var shadings = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Sh1"), shadingReference)
        ]);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("Shading")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Shading"), shadings)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        return PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDocument AddSampledAxialShadingResource(
        PdfDocument source, PdfStream function)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference functionReference = update.AddObject(function);
        var shading = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("ShadingType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), Name("DeviceRGB")),
            new KeyValuePair<PdfName, PdfObject>(Name("Coords"), Reals(0, 0, 10, 0)),
            new KeyValuePair<PdfName, PdfObject>(Name("Function"), functionReference),
            new KeyValuePair<PdfName, PdfObject>(Name("Extend"),
                new PdfArray(new PdfObject[] { new PdfBoolean(true), new PdfBoolean(true) }))
        ]);
        PdfIndirectReference shadingReference = update.AddObject(shading);
        var shadings = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Sh1"), shadingReference)
        ]);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("Shading")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Shading"), shadings)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        return PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDocument AddShadingPatternResource(
        PdfDocument source, PdfDictionary shading, PdfArray matrix)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var pattern = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("Pattern")),
            new KeyValuePair<PdfName, PdfObject>(Name("PatternType"), new PdfInteger(2)),
            new KeyValuePair<PdfName, PdfObject>(Name("Shading"), shading),
            new KeyValuePair<PdfName, PdfObject>(Name("Matrix"), matrix)]);
        var patterns = new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(Name("P1"), pattern)]);
        var updatedResources = new PdfDictionary(resources
            .Where(entry => !entry.Key.Equals(Name("Pattern")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Pattern"), patterns)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Resources"), updatedResources)));
        var update = new PdfIncrementalUpdateBuilder(source);
        return PdfDocument.Open(update.ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
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

    private static PdfDocument MakeEmbeddedFontVertical(PdfDocument source)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfDictionary page = ResolveDictionary(source,
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[Name("Font")]);
        PdfIndirectReference fontReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(fonts).Value);
        PdfDictionary font = Assert.IsType<PdfDictionary>(source.Resolve(fontReference));
        PdfArray descendants = Assert.IsType<PdfArray>(font[Name("DescendantFonts")]);
        PdfIndirectReference descendantReference = Assert.IsType<PdfIndirectReference>(
            Assert.Single(descendants));
        PdfDictionary descendant = Assert.IsType<PdfDictionary>(
            source.Resolve(descendantReference));
        var verticalFont = new PdfDictionary(font
            .Where(entry => !entry.Key.Equals(Name("Encoding")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Encoding"), Name("Identity-V"))));
        var verticalDescendant = new PdfDictionary(descendant
            .Where(entry => !entry.Key.Equals(Name("DW2")) && !entry.Key.Equals(Name("W2")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("DW2"), Reals(1000, -1000)))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("W2"),
                new PdfArray(new PdfObject[]
                {
                    new PdfInteger(1), Reals(-1000, 500, 1000)
                }))));
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(fontReference.ObjectNumber, verticalFont);
        update.ReplaceObject(descendantReference.ObjectNumber, verticalDescendant);
        return PdfDocument.Open(update.Build());
    }

    private static PdfDocument OptionalContentMembershipDocument(string membershipEntries)
    {
        const string content = "/OC /LayerSet BDC 0 0 2 1 re f EMC";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /OFF /ON [5 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Properties << /LayerSet 7 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /OCG /Name (Visible) >>",
            "<< /Type /OCG /Name (Hidden) >>",
            $"<< /Type /OCMD {membershipEntries} >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfDocument OptionalContentXObjectDocument()
    {
        const string pageContent = "/Fm Do";
        const string formContent = "0 0 2 1 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] /D << /BaseState /OFF >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /XObject << /Fm 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(pageContent)} >>\nstream\n{pageContent}\nendstream",
            "<< /Type /OCG /Name (Hidden) >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 2 1] /OC 5 0 R /Length {Encoding.ASCII.GetByteCount(formContent)} >>\nstream\n{formContent}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfDocument OptionalContentAnnotationDocument(bool hidden)
    {
        const string appearanceContent = "0 0 2 1 re f";
        string optionalContent = hidden ? " /OC 5 0 R" : string.Empty;
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R] /D << /BaseState /OFF >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 2 1] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [6 0 R] >>",
            "<< >>",
            "<< /Type /OCG /Name (Hidden) >>",
            $"<< /Type /Annot /Subtype /Square /Rect [0 0 2 1] /AP << /N 7 0 R >>{optionalContent} >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 2 1] /Length {Encoding.ASCII.GetByteCount(appearanceContent)} >>\nstream\n{appearanceContent}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfArray Reals(params double[] values) =>
        new(values.Select(value => (PdfObject)new PdfReal(value)));
}
