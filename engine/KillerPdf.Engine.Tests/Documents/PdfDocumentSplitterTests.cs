using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentSplitterTests
{
    [Fact]
    public void PlanDividesByPageCountAndNumbersPartsWithPadding()
    {
        PdfDocument document = Pages(11);

        PdfSplitPlan plan = PdfDocumentSplitter.Plan(document,
            new PdfSplitOptions { Mode = PdfSplitMode.PageCount, PagesPerPart = 4 }, "invoice");

        Assert.Equal(3, plan.Parts.Count);
        Assert.Equal(["invoice-1", "invoice-2", "invoice-3"],
            plan.Parts.Select(part => part.Name));
        Assert.Equal([0, 1, 2, 3], plan.Parts[0].SourcePageIndices);
        Assert.Equal([8, 9, 10], plan.Parts[2].SourcePageIndices);
    }

    [Fact]
    public void PlanDividesIntoEvenPartsAndHonoursTheNameTemplate()
    {
        PdfDocument document = Pages(10);

        PdfSplitPlan plan = PdfDocumentSplitter.Plan(document, new PdfSplitOptions
        {
            Mode = PdfSplitMode.PartCount,
            PartCount = 3,
            NameTemplate = "{name} pages {first}-{last}"
        }, "report");

        Assert.Equal(["report pages 1-4", "report pages 5-7", "report pages 8-10"],
            plan.Parts.Select(part => part.Name));
        Assert.Equal(4, plan.Parts[0].SourcePageIndices.Count);
        Assert.Equal(3, plan.Parts[1].SourcePageIndices.Count);
    }

    [Fact]
    public void PlanDividesBySuppliedRangesAndRejectsInvalidOnes()
    {
        PdfDocument document = Pages(6);

        PdfSplitPlan plan = PdfDocumentSplitter.Plan(document, new PdfSplitOptions
        {
            Mode = PdfSplitMode.PageRanges,
            Ranges = [new PdfPageRange(0, 1), new PdfPageRange(4, 5)]
        });

        Assert.Equal(2, plan.Parts.Count);
        Assert.Equal([4, 5], plan.Parts[1].SourcePageIndices);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfDocumentSplitter.Plan(document,
            new PdfSplitOptions
            {
                Mode = PdfSplitMode.PageRanges,
                Ranges = [new PdfPageRange(3, 1)]
            }));
        Assert.Throws<ArgumentException>(() => PdfDocumentSplitter.Plan(document,
            new PdfSplitOptions { Mode = PdfSplitMode.MaximumBytes }));
    }

    [Fact]
    public void PlanStartsAPartAtEachBookmarkOfTheSelectedLevel()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBlankPage().AddBlankPage().AddBlankPage()
            .AddBookmark("Front matter", 0)
            .AddBookmark("Chapter one", 1)
            .AddBookmark("Section", 2, 1)
            .AddBookmark("Chapter two", 3)
            .Build());

        PdfSplitPlan plan = PdfDocumentSplitter.Plan(document, new PdfSplitOptions
        {
            Mode = PdfSplitMode.BookmarkLevel,
            BookmarkLevel = 1,
            NameTemplate = "{title}"
        }, "book");

        Assert.Equal(["Front matter", "Chapter one", "Chapter two"],
            plan.Parts.Select(part => part.Name));
        Assert.Equal([1, 2], plan.Parts[1].SourcePageIndices);
        Assert.Equal([3, 4], plan.Parts[2].SourcePageIndices);
        Assert.Equal("Chapter two", plan.Parts[2].Title);
    }

    [Fact]
    public void SplitBuildsEachPartWithOnlyItsOwnPages()
    {
        PdfDocument document = Pages(5);

        IReadOnlyList<PdfSplitOutput> outputs = PdfDocumentSplitter.Split(document,
            new PdfSplitOptions { Mode = PdfSplitMode.PageCount, PagesPerPart = 2 }, "scan");

        Assert.Equal(3, outputs.Count);
        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(outputs[0].Document)).Count);
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(outputs[2].Document)));
        Assert.Equal("scan-3", outputs[2].Name);
    }

    [Fact]
    public void ExtractKeepsTheSelectedPagesAndRejectsRepeatedOrMissingOnes()
    {
        PdfDocument document = Pages(4);

        PdfDocument extracted = PdfDocument.Open(PdfDocumentSplitter.Extract(document, [1, 3]));

        Assert.Equal(2, PdfPageInformation.Read(extracted).Count);
        Assert.Throws<ArgumentException>(() => PdfDocumentSplitter.Extract(document, [1, 1]));
        Assert.Throws<ArgumentException>(() => PdfDocumentSplitter.Extract(document, []));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfDocumentSplitter.Extract(document, [9]));
    }

    [Fact]
    public void SplitBySizeStartsANewPartBeforeTheBudgetIsExceeded()
    {
        PdfDocument document = Pages(6);
        long single = PdfDocumentSplitter.Extract(document, [0]).LongLength;

        IReadOnlyList<PdfSplitOutput> outputs = PdfDocumentSplitter.Split(document,
            new PdfSplitOptions { Mode = PdfSplitMode.MaximumBytes, MaximumBytes = single },
            "big");

        Assert.All(outputs, output => Assert.Single(output.SourcePageIndices));
        Assert.Equal(6, outputs.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfDocumentSplitter.Split(document,
            new PdfSplitOptions { Mode = PdfSplitMode.MaximumBytes, MaximumBytes = 0 }));
    }

    [Fact]
    public void SplitDropsTheContentTheRemovedPagesLeaveBehind()
    {
        var builder = new PdfDocumentBuilder();
        for (int index = 0; index < 8; index++) builder.AddBlankPage();
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(builder.Build()));
        for (int index = 0; index < 8; index++)
            editor.SetPageContent(index, PageContent(index));
        byte[] sourceBytes = editor.Build();
        PdfDocument document = PdfDocument.Open(sourceBytes);

        byte[] compacted = PdfDocumentSplitter.Extract(document, [0]);
        byte[] verbatim = PdfDocumentSplitter.Extract(document, [0], compact: false);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(compacted)));
        Assert.True(compacted.LongLength * 2 < verbatim.LongLength,
            $"compacted {compacted.LongLength} is not much smaller than verbatim {verbatim.LongLength} "
            + $"(source {sourceBytes.LongLength})");
    }

    [Fact]
    public void ResourceDeduplicationHandlesStreamResources()
    {
        // An image XObject in page resources is a stream, which has no direct serialization.
        // Rewriting such a document used to fail instead of pruning anything.
        PdfImage image = PdfImage.FromGray(4, 4, Gray());
        var builder = new PdfDocumentBuilder();
        for (int index = 0; index < 4; index++)
            builder.AddPage(200, 200,
                new PdfContentStreamBuilder().DrawImage(image, 0, 0, 100, 100));
        PdfDocument document = PdfDocument.Open(builder.Build());

        byte[] part = PdfDocumentSplitter.Extract(document, [0]);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(part)));
    }

    private static byte[] Gray()
    {
        byte[] pixels = new byte[16];
        for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index * 16);
        return pixels;
    }

    private static byte[] PageContent(int pageIndex)
    {
        var builder = new StringBuilder(40000);
        for (int line = 0; line < 2000; line++)
            builder.Append(CultureInfo.InvariantCulture,
                $"{pageIndex}.{line} {line % 500} m {line % 400} {line % 300} l S\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static PdfDocument Pages(int count)
    {
        var builder = new PdfDocumentBuilder();
        for (int index = 0; index < count; index++) builder.AddBlankPage();
        return PdfDocument.Open(builder.Build());
    }
}
