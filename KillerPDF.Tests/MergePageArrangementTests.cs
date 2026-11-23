using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #495: the Merge dialog's page view lists every page of the files to merge, so pages can be put in any order or left
// out before merging. The order is kept per file and mapped onto what the merge actually took, so a file the merge
// leaves out moves nobody else's pages.
public sealed class MergePageArrangementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "merge-pages-" + Guid.NewGuid().ToString("N"));

    public MergePageArrangementTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MergePage P(string path, int page) => new(path, page);

    [Fact]
    public void ANewArrangementIsEveryPageInFileOrder()
    {
        var pages = MergePageArrangement.For([("a", 2), ("b", 1)]);
        Assert.Equal([P("a", 0), P("a", 1), P("b", 0)], pages.Pages);
        Assert.True(pages.IsPlainOrder(["a", "b"]));
    }

    [Fact]
    public void AFileWhosePagesCannotBeReadIsOneWholeFileEntry()
    {
        var pages = MergePageArrangement.For([("locked", null), ("b", 1)]);
        Assert.Equal([P("locked", MergePage.WholeFile), P("b", 0)], pages.Pages);
        Assert.True(pages.IsPlainOrder(["locked", "b"]));
    }

    [Fact]
    public void MovingAndRemovingPagesIsNoLongerThePlainOrder()
    {
        var moved = MergePageArrangement.For([("a", 2), ("b", 2)]);
        Assert.Equal(0, moved.MoveTo([2], 0));
        Assert.Equal([P("b", 0), P("a", 0), P("a", 1), P("b", 1)], moved.Pages);
        Assert.False(moved.IsPlainOrder(["a", "b"]));

        var removed = MergePageArrangement.For([("a", 2), ("b", 2)]);
        removed.RemoveAt([1]);
        Assert.False(removed.IsPlainOrder(["a", "b"]));
    }

    [Fact]
    public void ABlockOfPagesMovesTogetherAndStepsOnePlaceAtATime()
    {
        var pages = MergePageArrangement.For([("a", 5)]);
        // Pages 1 and 3 go in front of page 0, in their own order.
        Assert.Equal(0, pages.MoveTo([3, 1], 0));
        Assert.Equal([1, 3, 0, 2, 4], pages.Pages.Select(p => p.Page));
        // Forward one place, then to the end and no further.
        Assert.Equal(1, pages.Step([0, 1], 1));
        Assert.Equal([0, 1, 3, 2, 4], pages.Pages.Select(p => p.Page));
        Assert.Equal(3, pages.MoveTo([1, 2], 5));
        Assert.Equal(3, pages.Step([3, 4], 1));
        Assert.Equal([0, 2, 4, 1, 3], pages.Pages.Select(p => p.Page));
        // Back one place, and at the front it stays.
        Assert.Equal(0, pages.Step([1], -1));
        Assert.Equal(0, pages.Step([0], -1));
        Assert.Equal([2, 0, 4, 1, 3], pages.Pages.Select(p => p.Page));
    }

    [Fact]
    public void FileEditsAppendDropAndRegroupPages()
    {
        var pages = MergePageArrangement.For([("a", 2), ("b", 2)]);
        pages.MoveTo([3], 0);                       // b1, a0, a1, b0
        pages.AddFile("c", 1);
        pages.AddFile("A", 2);                      // already listed, in any case
        Assert.Equal([P("b", 1), P("a", 0), P("a", 1), P("b", 0), P("c", 0)], pages.Pages);

        pages.FollowFileOrder(["c", "b", "a"]);     // grouped by file, each file's own order kept
        Assert.Equal([P("c", 0), P("b", 1), P("b", 0), P("a", 0), P("a", 1)], pages.Pages);

        pages.RemoveFile("B");
        Assert.Equal([P("c", 0), P("a", 0), P("a", 1)], pages.Pages);
    }

    [Fact]
    public void APageCountThatArrivesLaterFillsTheFilesPlace()
    {
        // b's count is still loading, so it stands as one whole-file entry until it arrives.
        var pages = MergePageArrangement.For([("a", 1), ("b", null), ("c", 1)]);
        pages.MoveTo([2], 0);                       // c0, a0, b
        pages.ExpandFile("B", 2);
        Assert.Equal([P("c", 0), P("a", 0), P("b", 0), P("b", 1)], pages.Pages);
        Assert.True(pages.IsPlainOrder(["c", "a", "b"]));

        // A count for a file that is gone or already known changes nothing.
        pages.ExpandFile("gone", 3);
        pages.ExpandFile("a", 5);
        Assert.Equal(4, pages.Pages.Count);
    }

    [Fact]
    public void ThePlanRemovesTheLeftOutPagesAndOrdersTheRest()
    {
        // a has pages 0-1 and b pages 2-4 in the combined file. Keep b1, a0, b0.
        var plan = MergePageArrangement.Plan([P("b", 1), P("a", 0), P("b", 0)], [("a", 2), ("b", 3)])!;
        Assert.Equal([1, 4], plan.Removed);
        // Left after removal: a0 b0 b1 as 0 1 2.
        Assert.Equal([2, 0, 1], plan.Order);
    }

    [Fact]
    public void AFileTheMergeLeftOutMovesNobodyElsesPages()
    {
        // b was left out, so c's pages start right after a's.
        var plan = MergePageArrangement.Plan([P("c", 0), P("b", 0), P("a", 0)], [("a", 1), ("c", 1)])!;
        Assert.Empty(plan.Removed);
        Assert.Equal([1, 0], plan.Order);
    }

    [Fact]
    public void TheDialogsFullPathsMatchTheMergesOwnSpelling()
    {
        string root = Path.GetTempPath();
        string listed = Path.Combine(root, "x", "a.pdf");
        string given = Path.Combine(root, "x", ".", "A.PDF");
        var plan = MergePageArrangement.Plan([P(listed, 1), P(listed, 0)], [(given, 2)])!;
        Assert.Equal([1, 0], plan.Order);
    }

    [Fact]
    public void AWholeFileEntryTakesEveryPageTheMergePutIn()
    {
        var plan = MergePageArrangement.Plan([P("b", 0), P("locked", MergePage.WholeFile)], [("locked", 2), ("b", 1)])!;
        Assert.Empty(plan.Removed);
        Assert.Equal([2, 0, 1], plan.Order);
    }

    [Fact]
    public void ThePlainOrderNeedsNoReorderAndNoPagesMeansNoPlan()
    {
        var plan = MergePageArrangement.Plan([P("a", 0), P("a", 1)], [("a", 2)])!;
        Assert.Empty(plan.Removed);
        Assert.Null(plan.Order);
        Assert.Null(MergePageArrangement.Plan([P("gone", 0)], [("a", 2)]));
    }

    [Fact]
    public void TheMergeReportsThePagesEachFileTookAndThePlanMakesThatOrder()
    {
        string a = Blank("a.pdf", 200, 300, 210, 310);   // two pages
        string b = Blank("b.pdf", 400, 500);
        string bad = Path.Combine(_root, "bad.pdf");
        File.WriteAllText(bad, "not a pdf");
        string c = Blank("c.pdf", 600, 700);

        byte[] bytes = PdfEngineIntegration.MergeReadableFiles([a, bad, b, c], out var leftOut, out var merged,
            CancellationToken.None);
        Assert.Equal([bad], leftOut);
        Assert.Equal([(a, 2), (b, 1), (c, 1)], merged);

        string output = Path.Combine(_root, "out.pdf");
        File.WriteAllBytes(output, bytes);
        var plan = MergePageArrangement.Plan([P(c, 0), P(bad, 0), P(a, 1), P(b, 0)], merged)!;
        MergePageArrangement.Apply(output, plan);

        var widths = PdfEngineIntegration.ReadPageInformation(output, null).Select(page => (int)Math.Round(page.Width));
        Assert.Equal([600, 210, 400], widths);
    }

    // A PDF with one blank page per width and height pair.
    private string Blank(string name, params double[] sizes)
    {
        var parts = new List<string>();
        for (int index = 0; index < sizes.Length; index += 2)
        {
            string part = Path.Combine(_root, name + "." + index + ".part.pdf");
            File.WriteAllBytes(part, PdfEngineIntegration.CreateBlankDocument(sizes[index], sizes[index + 1]));
            parts.Add(part);
        }
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, parts.Count == 1 ? File.ReadAllBytes(parts[0]) : PdfEngineIntegration.MergeReadableFiles(parts));
        return path;
    }
}
