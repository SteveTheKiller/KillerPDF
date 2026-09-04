using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageContentReaderTests
{
    [Fact]
    public void ResolvesInheritedResourcesAndCropOrigin()
    {
        var page = Read("BT /F1 12 Tf 30 60 Td (Hello world) Tj ET", crop: "/CropBox [10 20 210 320]");
        Assert.Equal(200, page.Width);
        Assert.Equal(300, page.Height);
        Assert.Equal("Hello world", page.Text);
        Assert.Equal(20, page.Letters[0].StartBaseLine.X);
        Assert.Equal(40, page.Letters[0].StartBaseLine.Y);
        Assert.Equal("Helvetica", page.Letters[0].FontName);
        Assert.All(page.Letters, l => Assert.Equal(12, l.PointSize));
        var run = Assert.Single(page.TextRuns);
        Assert.Equal("Hello world", run.Text);
        Assert.Equal("Helvetica", run.FontName);
        Assert.Equal(PdfWritingDirection.LeftToRight, run.WritingDirection);
        Assert.Equal("Hello world", Assert.Single(page.Lines).Text);
        Assert.Contains(page.Instructions, instruction => instruction.Operator == "Tj");
    }

    [Fact]
    public void FormResourcesAndMatrixDoNotLeakIntoFollowingPageText()
    {
        string form = "BT /F1 10 Tf 1 2 Td (B) Tj ET";
        var page = Read("q 2 0 0 2 20 30 cm /Form Do Q BT /F1 12 Tf (A) Tj ET",
            extraResources: "/XObject << /Form 6 0 R >>",
            extras: [$"<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [1 0 0 1 3 4] /Resources << /Font << /F1 7 0 R >> >> /Length {form.Length} >>\nstream\n{form}\nendstream",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"]);
        Assert.Equal(["B", "A"], page.Letters.Select(l => l.Value));
        Assert.Equal("Courier", page.Letters[0].FontName);
        Assert.Equal(28, page.Letters[0].StartBaseLine.X);
        Assert.Equal(42, page.Letters[0].StartBaseLine.Y);
        Assert.Equal(20, page.Letters[0].PointSize);
        Assert.Equal("Helvetica", page.Letters[1].FontName);
        Assert.Equal(0, page.Letters[1].StartBaseLine.X);
    }

    [Fact]
    public void RecordsInlineAndExternalImagesWithTransformsAndClip()
    {
        var page = Read("q 10 20 30 40 re W n 100 0 0 100 0 0 cm /Im Do Q " +
            "q 5 0 0 6 70 80 cm BI /W 1 /H 1 /BPC 8 /CS /Local ID abc EI Q",
            extraResources: "/XObject << /Im 6 0 R >> /ColorSpace << /Local /DeviceRGB >>",
            extras: ["<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length 3 >>\nstream\nabc\nendstream"]);
        Assert.Equal(2, page.Images.Count);
        Assert.Equal(new PdfContentBounds(10, 20, 40, 60), page.Images[0].BoundingBox);
        Assert.Equal("Im", page.Images[0].ResourceName);
        Assert.False(page.Images[0].IsInline);
        Assert.Equal(1, page.Images[0].PixelWidth);
        Assert.Equal(1, page.Images[0].PixelHeight);
        Assert.Equal(100, page.Images[0].RenderedWidth);
        Assert.Equal(100, page.Images[0].RenderedHeight);
        Assert.Equal(0.72, page.Images[0].HorizontalDpi);
        Assert.Equal(0.72, page.Images[0].VerticalDpi);
        Assert.Equal(new PdfContentBounds(70, 80, 75, 86), page.Images[1].BoundingBox);
        Assert.Null(page.Images[1].ResourceName);
        Assert.True(page.Images[1].IsInline);
        Assert.Equal(1, page.Images[1].PixelWidth);
        Assert.Equal(1, page.Images[1].PixelHeight);
        Assert.Equal(5, page.Images[1].RenderedWidth);
        Assert.Equal(6, page.Images[1].RenderedHeight);
        Assert.Equal(14.4, page.Images[1].HorizontalDpi);
        Assert.Equal(12, page.Images[1].VerticalDpi);
        Assert.Equal(2, page.Instructions.Count(instruction => instruction.Operator == "BI" || instruction.Operator == "Do"));
        PdfExtractedPath clippingPath = Assert.Single(page.Paths);
        Assert.True(clippingPath.IsClippingPath);
        Assert.Equal("n", clippingPath.PaintOperator);
        Assert.Equal(new PdfContentBounds(10, 20, 40, 60), clippingPath.BoundingBox);
        Assert.Equal("re", Assert.Single(clippingPath.Segments).Operator);
    }

    [Fact]
    public void ExtractsTransformedVectorPathSegmentsInPaintingOrder()
    {
        PdfPageContent page = Read("q 2 0 0 3 10 20 cm 1 2 m 4 5 l 6 7 8 9 10 11 c h S Q");

        PdfExtractedPath path = Assert.Single(page.Paths);
        Assert.False(path.IsClippingPath);
        Assert.Equal("S", path.PaintOperator);
        Assert.Equal(new PdfContentBounds(12, 26, 30, 53), path.BoundingBox);
        Assert.Equal(["m", "l", "c", "h"], path.Segments.Select(segment => segment.Operator));
        Assert.Equal(new PdfPoint(12, 26), path.Segments[0].Points[0]);
        Assert.Equal(new PdfPoint(30, 53), path.Segments[2].Points[2]);
    }

    [Fact]
    public void AppliesActualTextOnceAcrossNestedMarkedContent()
    {
        var page = Read("BT /F1 10 Tf /Span << /ActualText (replacement) >> BDC " +
            "(A) Tj /Span << /ActualText (inner) >> BDC (B) Tj EMC (C) Tj EMC (D) Tj ET");
        Assert.Equal(["replacement", "D"], page.Letters.Select(l => l.Value));
        Assert.True(page.Letters[0].BoundingBox.Width > page.Letters[1].BoundingBox.Width);
    }

    [Fact]
    public void ExposesNestedMarkedContentPropertiesAndInstructionRanges()
    {
        PdfPageContent page = Read("/Document << /MCID 4 >> BDC "
            + "/OC /LayerOne BDC BT /F1 10 Tf (A) Tj ET EMC EMC /Artifact BMC EMC",
            extraResources: "/Properties << /LayerOne << /Type /OCG >> >>");

        Assert.Collection(page.MarkedContent,
            outer =>
            {
                Assert.Equal("Document", outer.Tag);
                Assert.Equal(0, outer.Depth);
                Assert.Equal(4, outer.MarkedContentId);
                Assert.True(outer.StartInstructionIndex < outer.EndInstructionIndex);
            },
            optional =>
            {
                Assert.Equal("OC", optional.Tag);
                Assert.Equal("LayerOne", optional.PropertyName);
                Assert.Equal(1, optional.Depth);
                Assert.True(optional.IsOptionalContent);
            },
            artifact =>
            {
                Assert.True(artifact.IsArtifact);
                Assert.Equal(0, artifact.Depth);
            });
    }

    [Fact]
    public void OuterActualTextPreservesGeometryWhenInnerReplacementIsEmpty()
    {
        var page = Read("/Span << /ActualText (outer) >> BDC /Span << /ActualText () >> BDC BT /F1 12 Tf (A) Tj ET EMC EMC");
        Assert.Equal("outer", Assert.Single(page.Letters).Value);
        Assert.True(page.Letters[0].BoundingBox.Width > 0);
    }

    [Fact]
    public void EmptyClipSuppressesImageAndOverflowingTransformsAreRejected()
    {
        Assert.Empty(Read("W n BI /W 1 /H 1 /BPC 8 /CS /RGB ID abc EI").Images);
        Assert.Throws<FormatException>(() => Read(string.Concat(Enumerable.Repeat("1000000000 0 0 1000000000 0 0 cm ", 35))));
    }

    [Fact]
    public void ReportsRecoverableGraphicsStateDamageWithoutDroppingText()
    {
        var page = Read("q /Missing gs BT /F1 12 Tf (text) Tj ET");
        Assert.Equal("text", page.Text);
        Assert.Equal(2, page.Diagnostics.Count);
    }

    [Fact]
    public void AppliesVerticalAdvancesOriginsAndTjAdjustments()
    {
        var page = Read("BT /F2 10 Tf 50 Tz 100 200 Td [<0041> 100 <0041>] TJ ET",
            extraResources: "/Font << /F1 4 0 R /F2 6 0 R >>",
            extras: ["<< /Type /Font /Subtype /Type0 /BaseFont /Vertical /Encoding /Identity-V /DescendantFonts [7 0 R] /ToUnicode 8 0 R >>",
                "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Vertical /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 /DW2 [880 -1000] >>",
                Stream("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfchar <0041> <0041> endbfchar")]);
        Assert.Equal(2, page.Letters.Count);
        Assert.Equal(200, page.Letters[0].StartBaseLine.Y);
        Assert.Equal(190, page.Letters[0].EndBaseLine.Y);
        Assert.Equal(189, page.Letters[1].StartBaseLine.Y);
        Assert.Equal(100, page.Letters[1].StartBaseLine.X);
        Assert.All(page.Letters, letter => Assert.Equal(PdfWritingDirection.TopToBottom, letter.WritingDirection));
        Assert.Equal(PdfWritingDirection.TopToBottom, Assert.Single(page.TextRuns).WritingDirection);
        Assert.Single(page.Lines);
    }

    [Fact]
    public void RejectsCyclicFormsAndHonorsCancellation()
    {
        Assert.Throws<FormatException>(() => Read("/Loop Do", extraResources: "/XObject << /Loop 6 0 R >>",
            extras: ["<< /Subtype /Form /BBox [0 0 20 20] /Length 8 >>\nstream\n/Loop Do\nendstream"]));
        var document = Document("BT /F1 12 Tf (A) Tj ET", "", "", []);
        Assert.Throws<OperationCanceledException>(() => new PdfPageContentReader(document).Read(0, new CancellationToken(true)));
    }

    [Fact]
    public void RawInstructionsRoundTripUnknownOperatorsAndInlineImages()
    {
        PdfDocument document = Document("1 2 FutureOp BI /W 1 /H 1 /BPC 8 /CS /RGB ID abc EI", "", "", []);
        var reader = new PdfPageContentReader(document);
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions = reader.ReadInstructions(0);

        byte[] rewritten = KillerPdf.Engine.Parsing.PdfContentStreamWriter.Write(instructions);
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> reopened =
            KillerPdf.Engine.Parsing.PdfContentStreamReader.Read(rewritten);

        Assert.Equal(["FutureOp", "BI"], reopened.Select(item => item.Operator));
        Assert.Equal("abc"u8.ToArray(), reopened[1].InlineImageData?.ToArray());
    }

    [Fact]
    public void RecordsShadingResourcesAndActiveClipBounds()
    {
        PdfPageContent page = Read(
            "10 20 30 40 re W n /Shade sh",
            extraResources: "/Shading << /Shade 6 0 R >>",
            extras:
            [
                "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] " +
                "/Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 0] " +
                "/C1 [1 1 1] /N 1 >> /Extend [true true] >>"
            ]);

        PdfExtractedShading shading = Assert.Single(page.Shadings);
        Assert.Equal("Shade", shading.ResourceName);
        Assert.Equal(2, shading.ShadingType);
        Assert.Equal(new PdfContentBounds(10, 20, 40, 60), shading.BoundingBox);
    }

    private static PdfPageContent Read(string content, string crop = "", string extraResources = "", string[]? extras = null) =>
        new PdfPageContentReader(Document(content, crop, extraResources, extras ?? [])).Read(0);

    private static string Stream(string content) => $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static PdfDocument Document(string content, string crop, string extraResources, string[] extras)
    {
        string fonts = extraResources.Contains("/Font") ? "" : "/Font << /F1 4 0 R >>";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] {crop} /Resources << {fonts} {extraResources} >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", Stream(content), .. extras];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
