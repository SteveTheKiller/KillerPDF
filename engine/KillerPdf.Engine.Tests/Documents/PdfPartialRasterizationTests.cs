using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPartialRasterizationTests
{
    [Fact]
    public void PlanCalculatesPixelsAndSelectsOnlyIntersectingContent()
    {
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 20, 40, 60 });
        var content = new PdfContentStreamBuilder()
            .Rectangle(10, 10, 20, 20).Stroke()
            .DrawImage(image, 50, 50, 20, 20);
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build());
        PdfPageContent page = new PdfPageContentReader(document).Read(0);

        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 30, 30), 144);

        Assert.Equal(50, plan.PixelWidth);
        Assert.Equal(50, plan.PixelHeight);
        Assert.Single(plan.Paths);
        Assert.Empty(plan.Images);
        Assert.Empty(plan.TextRuns);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPartialRasterization.Plan(
                page, new PdfContentBounds(-1, 0, 10, 10), 144));
    }

    [Fact]
    public void PlanIncludesOnlyShadingsThatIntersectTheRegion()
    {
        var shading = new PdfAxialGradient(0, 0, 100, 0, [
            new PdfGradientStop(0, new PdfRgbColor(1, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(0, 0, 1))]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .SaveState().Rectangle(10, 10, 20, 20).Clip().EndPath()
                .PaintShading(shading).RestoreState())
            .Build());
        PdfPageContent page = new PdfPageContentReader(document).Read(0);

        PdfPartialRasterizationPlan inside = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(15, 15, 25, 25), 144);
        PdfPartialRasterizationPlan outside = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(50, 50, 60, 60), 144);

        Assert.Single(inside.Shadings);
        Assert.Empty(outside.Shadings);
    }

    // A rasterized region is an image: the text it covered can no longer be found, selected or copied. (#369)
    [Fact]
    public void ApplyRemovesTextInsideTheRegionOnAnUntaggedPage()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(200, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(20, 40).ShowLatin1Text("inside").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(120, 40).ShowLatin1Text("outside").EndText())
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(10, 30, 100, 60), 72);
        Assert.NotEmpty(plan.TextRuns);

        byte[] changed = PdfPartialRasterization.Apply(document, 0, plan,
            PdfImage.FromRgb(plan.PixelWidth, plan.PixelHeight, new byte[plan.PixelWidth * plan.PixelHeight * 3]));

        string[] words = [.. new PdfPageContentReader(PdfDocument.Open(changed)).Read(0).GetWords().Select(word => word.Text)];
        Assert.DoesNotContain("inside", words);
        Assert.Contains("outside", words);
    }

    // On a tagged page the covered words leave the page text but stay readable to assistive technology,
    // as the alternate description of a Figure that holds the image. (#369)
    [Fact]
    public void ApplyOnATaggedPageMovesTheCoveredWordsIntoAFigureDescription()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Tagged", Language = "en-US" })
            .AddPage(200, 100, new PdfContentStreamBuilder()
                .BeginMarkedContent(PdfStructureType.Paragraph, 0)
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(20, 40).ShowLatin1Text("inside").EndText()
                .EndMarkedContent()
                .BeginMarkedContent(PdfStructureType.Paragraph, 1)
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(120, 40).ShowLatin1Text("outside").EndText()
                .EndMarkedContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Paragraph, 0, 0, 1)
            .AddStructureElement(PdfStructureType.Paragraph, 0, 1, 1)
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(10, 30, 100, 60), 72);

        PdfDocument changed = PdfDocument.Open(PdfPartialRasterization.Apply(document, 0, plan,
            PdfImage.FromRgb(plan.PixelWidth, plan.PixelHeight, new byte[plan.PixelWidth * plan.PixelHeight * 3])));

        string[] words = [.. new PdfPageContentReader(changed).Read(0).GetWords().Select(word => word.Text)];
        Assert.DoesNotContain("inside", words);
        Assert.Contains("outside", words);
        string[] descriptions = [.. FigureDescriptions(changed)];
        Assert.Equal(["inside"], descriptions);
    }

    private static IEnumerable<string> FigureDescriptions(PdfDocument document)
    {
        static PdfName N(string name) => new(System.Text.Encoding.ASCII.GetBytes(name));
        PdfObject Resolve(PdfObject value) => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
        var catalog = (PdfDictionary)Resolve(document.Trailer[N("Root")]);
        var root = (PdfDictionary)Resolve(catalog[N("StructTreeRoot")]);
        var numbers = (PdfArray)((PdfDictionary)Resolve(root[N("ParentTree")]))[N("Nums")];
        for (int index = 1; index < numbers.Count; index += 2)
        {
            PdfObject entry = Resolve(numbers[index]);
            IEnumerable<PdfObject> elements = entry is PdfArray array ? array : [entry];
            foreach (PdfObject value in elements)
                if (Resolve(value) is PdfDictionary element && element.TryGetValue(N("S"), out PdfObject? role)
                    && role is PdfName name && name.Equals(N("Figure"))
                    && element.TryGetValue(N("Alt"), out PdfObject? alt) && alt is PdfString text)
                {
                    byte[] bytes = text.Bytes.ToArray();
                    yield return bytes is [0xFE, 0xFF, ..]
                        ? System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
                        : System.Text.Encoding.Latin1.GetString(bytes);
                }
        }
    }

    [Fact]
    public void ApplyClipsOriginalContentOutsideRegionAndPlacesExactRaster()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .Rectangle(10, 10, 20, 20).Stroke()
                .Rectangle(60, 60, 20, 20).Stroke())
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 30, 30), 72);
        PdfImage raster = PdfImage.FromRgb(25, 25,
            Enumerable.Repeat((byte)180, 25 * 25 * 3).ToArray());

        byte[] changed = PdfPartialRasterization.Apply(document, 0, plan, raster);
        PdfDocument reopened = PdfDocument.Open(changed);
        PdfPageContent result = new PdfPageContentReader(reopened).Read(0);

        Assert.True(changed.AsSpan(0, source.Length).SequenceEqual(source));
        PdfExtractedImage image = Assert.Single(result.Images);
        Assert.Equal(new PdfContentBounds(5, 5, 30, 30), image.BoundingBox);
        IReadOnlyList<PdfContentInstruction> instructions =
            new PdfPageContentReader(reopened).ReadInstructions(0);
        Assert.Contains(instructions, instruction => instruction.Operator == "W*");
        Assert.True(instructions.Count(instruction => instruction.Operator == "re") >= 3);
    }

    [Fact]
    public void ApplyRejectsRasterDimensionsThatDoNotMatchPlan()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            new PdfPageContentReader(document).Read(0),
            new PdfContentBounds(0, 0, 10, 10), 72);

        Assert.Throws<ArgumentException>(() => PdfPartialRasterization.Apply(
            document, 0, plan, PdfImage.FromRgb(9, 10, new byte[9 * 10 * 3])));
        PdfPartialRasterizationPlan forged = plan with { PixelWidth = 9 };
        Assert.Throws<ArgumentException>(() => PdfPartialRasterization.Apply(
            document, 0, forged, PdfImage.FromRgb(9, 10, new byte[9 * 10 * 3])));
    }

    [Fact]
    public void ApplyReplacesMultipleRegionsInOneRevision()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .Rectangle(5, 5, 20, 20).Stroke()
                .Rectangle(60, 60, 20, 20).Stroke())
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan first = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 25, 25), 72);
        PdfPartialRasterizationPlan second = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(60, 60, 80, 80), 72);

        byte[] changed = PdfPartialRasterization.Apply(document,
        [
            new PdfPartialRasterizationReplacement(0, first,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3])),
            new PdfPartialRasterizationReplacement(0, second,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3]))
        ]);
        PdfPageContent result = new PdfPageContentReader(PdfDocument.Open(changed)).Read(0);

        Assert.True(changed.AsSpan(0, source.Length).SequenceEqual(source));
        Assert.Equal([first.Region, second.Region],
            result.Images.Select(image => image.BoundingBox));
    }

    [Fact]
    public void ApplyLandsTheRasterInsideTheCropBoxWhenTheBoxDoesNotStartAtTheOrigin()
    {
        byte[] source = RawPdf("/MediaBox [0 0 612 792] /CropBox [36 36 576 756]",
            "0 1 0 rg 36 36 540 720 re f");
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(0, 0, 20, 20), 72);
        PdfImage red = SolidRaster(plan.PixelWidth, plan.PixelHeight, 255, 0, 0);

        byte[] changed = PdfPartialRasterization.Apply(document, 0, plan, red);
        PdfRenderedPage shown = new PdfPageRenderer(PdfDocument.Open(changed))
            .Render(0, new PdfRenderOptions(540, 720));

        // The region is the crop box's lower left corner, so it is the bottom left of the image.
        Assert.Equal((255, 0, 0), Pixel(shown, 10, 710));
        Assert.Equal((0, 255, 0), Pixel(shown, 530, 710));
        Assert.Equal((0, 255, 0), Pixel(shown, 10, 10));
        Assert.Equal((0, 255, 0), Pixel(shown, 30, 690));
    }

    // Review BF-001: on a tagged page the raster goes in as a described Figure, and it must land where the
    // clip hole is, inside the crop box, not at the page origin.
    [Fact]
    public void ApplyLandsATaggedRasterInsideTheCropBoxWhenTheBoxDoesNotStartAtTheOrigin()
    {
        string content = "0 1 0 rg 36 36 540 720 re f /P <</MCID 0>> BDC BT /F1 12 Tf 0 0 0 rg 46 46 Td (inside) Tj ET EMC";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /CropBox [36 36 576 756] /StructParents 0 /Resources << /Font << /F1 8 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /StructTreeRoot /K 6 0 R /ParentTree 7 0 R >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [6 0 R]] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(System.Text.Encoding.Latin1.GetBytes(pdf.ToString()));
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(page, new PdfContentBounds(0, 0, 60, 30), 72);

        byte[] changed = PdfPartialRasterization.Apply(document, 0, plan,
            SolidRaster(plan.PixelWidth, plan.PixelHeight, 255, 0, 0));
        PdfDocument result = PdfDocument.Open(changed);
        PdfRenderedPage shown = new PdfPageRenderer(result).Render(0, new PdfRenderOptions(540, 720));

        Assert.Equal(["inside"], FigureDescriptions(result).ToArray());
        Assert.Equal((255, 0, 0), Pixel(shown, 5, 715));     // the region: the crop box's lower left corner
        Assert.Equal((255, 0, 0), Pixel(shown, 55, 695));
        Assert.Equal((0, 255, 0), Pixel(shown, 65, 715));    // just right of the region
        Assert.Equal((0, 255, 0), Pixel(shown, 5, 685));     // just above it
    }

    [Fact]
    public void ApplyShowsTheRasterWhenThePageContentLeavesAGraphicsStateOpen()
    {
        byte[] source = RawPdf("/MediaBox [0 0 100 100]", "q 0 0 1 rg 0 0 100 100 re f");
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(40, 40, 60, 60), 72);

        byte[] changed = PdfPartialRasterization.Apply(document, 0, plan,
            SolidRaster(plan.PixelWidth, plan.PixelHeight, 255, 0, 0));
        PdfRenderedPage shown = new PdfPageRenderer(PdfDocument.Open(changed))
            .Render(0, new PdfRenderOptions(100, 100));

        Assert.Equal((255, 0, 0), Pixel(shown, 50, 50));
        Assert.Equal((0, 0, 255), Pixel(shown, 10, 10));
        // The fix must leave the saved page content balanced.
        int depth = 0, lowest = 0;
        foreach (PdfContentInstruction instruction in new PdfPageContentReader(PdfDocument.Open(changed)).ReadInstructions(0))
        {
            if (instruction.Operator == "q") depth++;
            else if (instruction.Operator == "Q") lowest = Math.Min(lowest, --depth);
        }
        Assert.Equal(0, depth);
        Assert.Equal(0, lowest);
    }

    [Fact]
    public void ApplyReadsThePageTreeOnceHoweverManyPagesAreReplaced()
    {
        const int pages = 200;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(page => $"{3 + page * 2} 0 R"))}] /Count {pages} >>"
        };
        for (int page = 0; page < pages; page++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents {4 + page * 2} 0 R >>");
            objects.Add("<< /Length 30 >>\nstream\n0 0 1 rg 0 0 100 100 re f   \nendstream");
        }
        PdfDocument document = PdfDocument.Open(Rendering.TestIccProfiles.Pdf([.. objects]));
        var reader = new PdfPageContentReader(document);
        var replacements = new List<PdfPartialRasterizationReplacement>();
        for (int page = 0; page < pages; page++)
        {
            PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
                reader.Read(page), new PdfContentBounds(0, 0, 10, 10), 72);
            replacements.Add(new PdfPartialRasterizationReplacement(page, plan,
                SolidRaster(plan.PixelWidth, plan.PixelHeight, 255, 0, 0)));
        }

        PdfPageTree.ReadCountOnThisThread = 0;
        PdfPartialRasterization.Apply(document, replacements);

        // The incremental editor reads the tree once for each page it changes when it builds, and the rasterization
        // itself adds a handful of reads in all, not one for each page.
        Assert.InRange(PdfPageTree.ReadCountOnThisThread, 1, pages + 5);
    }
    private static PdfImage SolidRaster(int width, int height, byte red, byte green, byte blue)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int index = 0; index < rgb.Length; index += 3)
        {
            rgb[index] = red;
            rgb[index + 1] = green;
            rgb[index + 2] = blue;
        }
        return PdfImage.FromRgb(width, height, rgb);
    }

    private static (byte Red, byte Green, byte Blue) Pixel(PdfRenderedPage page, int x, int y)
    {
        int offset = (y * page.Width + x) * 4;
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static byte[] RawPdf(string pageEntries, string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R {pageEntries} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(pdf.ToString());
    }

    [Fact]
    public void ApplyRejectsOverlappingReplacementRegions()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan first = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 25, 25), 72);
        PdfPartialRasterizationPlan second = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(20, 20, 40, 40), 72);

        Assert.Throws<ArgumentException>(() => PdfPartialRasterization.Apply(document,
        [
            new PdfPartialRasterizationReplacement(0, first,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3])),
            new PdfPartialRasterizationReplacement(0, second,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3]))
        ]));
    }
}
