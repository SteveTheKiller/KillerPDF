using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageContentReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendersContentBeyondTheFormerPageLimitAcrossStreamBoundaries(bool recovery)
    {
        using var encoded = new MemoryStream();
        using (var compressor = new System.IO.Compression.ZLibStream(encoded,
            System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] block = new byte[1024 * 1024];
            Array.Fill(block, (byte)' ');
            "q Q\n"u8.CopyTo(block);
            for (int index = 0; index < 65; index++) compressor.Write(block);
            compressor.Write("1 0"u8);
        }
        string data = Encoding.Latin1.GetString(encoded.ToArray());
        string first = $"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n{data}\nendstream";
        var document = Document("", "", "", [first, Stream("0 rg 0 0 300 400 re f")], "[6 0 R 7 0 R]");
        if (recovery) document = PdfDocument.OpenWithCompatibilityRecovery(document.Source);
        var options = new KillerPdf.Engine.Rendering.PdfRenderOptions(30, 40);
        var actual = new KillerPdf.Engine.Rendering.PdfPageRenderer(document).Render(0, options);
        var expected = new KillerPdf.Engine.Rendering.PdfPageRenderer(
            Document("1 0 0 rg 0 0 300 400 re f", "", "", [])).Render(0, options);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Fact]
    public void StreamingContentReportsAnUndecodableStream()
    {
        var document = Document("", "", "", ["<< /Length 3 /Filter /FlateDecode >>\nstream\nabc\nendstream"], "6 0 R");
        document = PdfDocument.OpenWithCompatibilityRecovery(document.Source);
        var diagnostics = new HashSet<string>();
        Assert.Empty(new PdfPageContentReader(document).EnumerateInstructions(0, default, diagnostics));
        Assert.Contains(diagnostics, message => message.StartsWith("Page content was truncated during streaming:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryPreservesPrefixBeforeOversizedJbig2Content(bool oversizedFirst)
    {
        // Page information alone declares a 1.25 GB bitmap. No bitmap should be allocated.
        string encoded = Encoding.Latin1.GetString(Convert.FromHexString(
            "0000000030000100000013000186A0000186A00000000000000000010000"));
        string bomb = $"<< /Length {encoded.Length} /Filter /JBIG2Decode >>\nstream\n{encoded}\nendstream";
        PdfDocument strict = Document("1 0 0 rg 0 0 20 20 re f BT /F1 12 Tf (Visible) Tj ET",
            "", "", [bomb, Stream("0 0 1 rg 30 0 20 20 re f")],
            oversizedFirst ? "[6 0 R 5 0 R 7 0 R]" : "[5 0 R 6 0 R 7 0 R]");
        var options = new KillerPdf.Engine.Rendering.PdfRenderOptions(300, 400);
        var failure = Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() =>
            new PdfPageContentReader(strict).Read(0));
        Assert.Contains("safety limit", failure.Message, StringComparison.Ordinal);
        Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() =>
            new KillerPdf.Engine.Rendering.PdfPageRenderer(strict).Render(0, options));
        PdfDocument recovery = PdfDocument.OpenWithCompatibilityRecovery(strict.Source);
        var oversized = Assert.IsType<KillerPdf.Engine.Objects.PdfStream>(recovery.Resolve(6));
        Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() => recovery.DecodeStream(oversized));
        var reader = new PdfPageContentReader(recovery);
        // The raw instruction API used by editing paths must still reject incomplete content.
        Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() => reader.ReadInstructions(0));
        var renderer = new KillerPdf.Engine.Rendering.PdfPageRenderer(recovery);
        if (oversizedFirst)
        {
            Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() => reader.Read(0));
            Assert.Throws<KillerPdf.Engine.Filters.PdfFilterException>(() => renderer.Render(0, options));
            return;
        }
        PdfPageContent content = reader.Read(0);
        Assert.Equal("Visible", content.Text);
        Assert.Contains("Page content was truncated because a stream could not be decoded.", content.Diagnostics);
        foreach (int width in new[] { 300, 150 })
        {
            var rendered = renderer.Render(0, new KillerPdf.Engine.Rendering.PdfRenderOptions(width, 400));
            Assert.Contains("Page content was truncated because a stream could not be decoded.", rendered.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 255, 255 },
                rendered.Pixels.Slice((390 * width + width / 30) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 255, 255, 255, 255 },
                rendered.Pixels.Slice((390 * width + width * 2 / 15) * 4, 4).ToArray());
        }
        Assert.Throws<OperationCanceledException>(() => reader.Read(0, new CancellationToken(true)));
    }

    [Theory]
    [InlineData("/Height", "1")]
    [InlineData("1", "true")]
    [InlineData("null", "1")]
    public void RecoveryPreservesContentAroundImagesWithNonnumericDimensions(string width, string height)
    {
        PdfDocument strict = Document(
            "1 0 0 rg 0 0 20 20 re f /Im Do " +
            "0 0 1 rg 30 0 20 20 re f BT /F1 12 Tf 10 60 Td (Visible) Tj ET",
            "", "/XObject << /Im 6 0 R >>",
            [$"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} " +
                "/BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>\nstream\nx\nendstream"]);
        Assert.Throws<FormatException>(() => new PdfPageContentReader(strict).Read(0));
        var options = new KillerPdf.Engine.Rendering.PdfRenderOptions(300, 400);
        Assert.Throws<FormatException>(() => new KillerPdf.Engine.Rendering.PdfPageRenderer(strict).Render(0, options));
        PdfDocument recovery = PdfDocument.OpenWithCompatibilityRecovery(strict.Source);
        PdfPageContent content = new PdfPageContentReader(recovery).Read(0);
        Assert.Equal("Visible", content.Text);
        Assert.Contains("An image has invalid pixel dimensions.", content.Diagnostics);
        var rendered = new KillerPdf.Engine.Rendering.PdfPageRenderer(recovery).Render(0, options);
        Assert.Contains("An image with invalid pixel dimensions was skipped.", rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((390 * 300 + 10) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rendered.Pixels.Slice((390 * 300 + 40) * 4, 4).ToArray());
    }

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

    [Theory]
    [InlineData("/Loop Do", 16)]
    [InlineData("/Loop Do /Loop Do", 65)]
    public void RecoversCyclicFormsWithinBoundsAndContinuesPageContent(string recursiveCalls,
        int expectedCopies)
    {
        string formContent = "1 0 0 rg 50 100 20 20 re f BT /F1 12 Tf (A) Tj ET " + recursiveCalls;
        PdfDocument source = Document(
            "/Loop Do BT /F1 12 Tf (END) Tj ET 0 0 1 rg 200 200 40 40 re f", "",
            "/XObject << /Loop 6 0 R >>",
            [$"<< /Subtype /Form /BBox [0 0 300 400] /Length {formContent.Length} >>\nstream\n{formContent}\nendstream"]);
        Assert.Throws<FormatException>(() => new PdfPageContentReader(source).Read(0));
        Assert.Throws<FormatException>(() => new KillerPdf.Engine.Rendering.PdfPageRenderer(source)
            .Render(0, new KillerPdf.Engine.Rendering.PdfRenderOptions(30, 40)));
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source.Source);

        PdfPageContent extracted = new PdfPageContentReader(document).Read(0);
        Assert.EndsWith("END", extracted.Text.Trim());
        Assert.Equal(expectedCopies, extracted.Letters.Count(letter => letter.Value == "A"));
        Assert.Contains("Cyclic Form XObject expansion limit reached.", extracted.Diagnostics);
        var rendered = new KillerPdf.Engine.Rendering.PdfPageRenderer(document)
            .Render(0, new KillerPdf.Engine.Rendering.PdfRenderOptions(30, 40));
        Assert.Contains("Cyclic Form XObject expansion limit reached.", rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 },
            rendered.Pixels.Span.Slice((29 * 30 + 6) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 },
            rendered.Pixels.Span.Slice((18 * 30 + 22) * 4, 4).ToArray());
        Assert.Throws<OperationCanceledException>(() => new PdfPageContentReader(document)
            .Read(0, new CancellationToken(true)));
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

    private static PdfDocument Document(string content, string crop, string extraResources, string[] extras,
        string contents = "5 0 R")
    {
        string fonts = extraResources.Contains("/Font") ? "" : "/Font << /F1 4 0 R >>";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] {crop} /Resources << {fonts} {extraResources} >> >>",
            $"<< /Type /Page /Parent 2 0 R /Contents {contents} >>",
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
