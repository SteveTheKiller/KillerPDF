using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Insert Pages (issue 529): the page card inserts pages from another open tab, a range of them, before or after a
/// page of the open document. The plan's numbers, the real extract and insert the window runs, and the wiring that
/// keeps the disk merge and the source tab's live state.
/// </summary>
public sealed class InsertPagesTests
{
    [Theory]
    [InlineData(1, true, 5, 0)]
    [InlineData(1, false, 5, 1)]
    [InlineData(3, true, 5, 2)]
    [InlineData(5, false, 5, 5)]
    public void ThePositionIsWherePagesGoInBeforeOrAfterThePage(int page, bool before, int pageCount, int expected)
        => Assert.Equal(expected, InsertPagesPlan.Position(page, before, pageCount));

    [Theory]
    [InlineData(0, false, 5)]
    [InlineData(6, true, 5)]
    [InlineData(-2, false, 5)]
    public void APageOutsideTheDocumentHasNoPosition(int page, bool before, int pageCount)
        => Assert.Null(InsertPagesPlan.Position(page, before, pageCount));

    [Fact]
    public void ARangeReadsEitherWayRoundAndStaysInsideAKnownSource()
    {
        Assert.Equal((2, 4), InsertPagesPlan.Span(4, 2, 6));
        Assert.Equal((1, 6), InsertPagesPlan.Span(1, 6, 6));
        Assert.Null(InsertPagesPlan.Span(2, 7, 6));
        Assert.Null(InsertPagesPlan.Span(0, 3, 6));
    }

    [Fact]
    public void AnUnreadSourceTakesAnyRangeStartingAtPageOne()
    {
        // A tab that has not loaded yet, or a protected one, shows no page count; the range is checked once it opens.
        Assert.Equal((3, 900), InsertPagesPlan.Span(900, 3, null));
        Assert.Null(InsertPagesPlan.Span(0, 3, null));
    }

    [Fact]
    public void TheSourcePagesAreZeroBasedAndRecheckedAgainstTheOpenedCount()
    {
        Assert.Equal([0, 1, 2, 3], InsertPagesPlan.SourcePages(true, 0, 0, 4));
        Assert.Equal([1, 2], InsertPagesPlan.SourcePages(false, 2, 3, 4));
        Assert.Null(InsertPagesPlan.SourcePages(false, 3, 9, 4));
        Assert.Null(InsertPagesPlan.SourcePages(true, 0, 0, 0));
    }

    // The window's own steps, minus the window: pages 2 to 3 of the source, turned, extracted from its saved copy, then
    // inserted before page 2 of the target. Pages are told apart by width.
    [Fact]
    public void ExtractedPagesGoInAtThePositionWithTheirTurns()
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-insertpages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string target = Path.Combine(folder, "target.pdf");
            string source = Path.Combine(folder, "source.pdf");
            string pages = Path.Combine(folder, "pages.pdf");
            File.WriteAllBytes(target, new PdfDocumentBuilder().AddBlankPage(100, 700).AddBlankPage(110, 700).Build());
            File.WriteAllBytes(source, new PdfDocumentBuilder()
                .AddBlankPage(200, 700).AddBlankPage(210, 700).AddBlankPage(220, 700).AddBlankPage(230, 700).Build());

            IReadOnlyList<int> chosen = InsertPagesPlan.SourcePages(false, 2, 3, 4)!;
            var turns = new Dictionary<int, int> { [0] = 0, [1] = 90, [2] = 0, [3] = 0 };
            PdfEngineIntegration.ExtractPages(source, pages, chosen, turns);
            var extracted = PdfEngineIntegration.ReadPageInformation(pages, null);
            var import = new PdfEngineIntegration.ImportedDocument(pages, [.. extracted.Select(p => p.Rotation)], "source.pdf");
            Assert.Equal([90, 0], import.PageRotations);
            int at = InsertPagesPlan.Position(2, true, 2)!.Value;
            PdfEngineIntegration.InsertDocuments(target, [import], at);
            // The file's pages stand upright and the window's rotation map carries the turn, as a merge does.
            var shown = new Dictionary<int, int> { [0] = 0, [1] = 0 };
            PdfEngineIntegration.RemapRotationsAfterDocumentInsertion(shown, [import], at);

            var result = PdfEngineIntegration.ReadPageInformation(target, null);
            Assert.Equal([100d, 210d, 220d, 110d], result.Select(p => p.Width));
            Assert.Equal([0, 90, 0, 0], Enumerable.Range(0, 4).Select(index => shown[index]));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ThePageCardOffersInsertPagesInPlaceOfMerge()
    {
        string card = ShellSource.Read("Shell", "PageCard.cs");
        Assert.Contains("Loc(\"Str_Ctx_InsertPages\"), null, async () => await InsertPagesAt(page)", card);
        Assert.DoesNotContain("MergeAtIndex", card);
    }

    [Fact]
    public void InsertPagesKeepsTheDiskMergeAndTheSharedInsert()
    {
        string insert = ShellSource.Read("Shell", "InsertPages.cs");
        string flow = ShellSource.Between(insert, "private async Task InsertPagesAt(", "private async Task<SavedCopy?> CopyPagesFromTab(");
        // No other tab: the tile is the merge it replaced. Files from disk: the same merge, at the chosen place.
        Assert.Contains("if (sources.Count == 0) { await MergeAtIndex(page + 1); return; }", flow);
        Assert.Contains("if (dialog.FromDisk) { await MergeAtIndex(dialog.InsertAt); return; }", flow);
        Assert.Matches(@"if \(_doc is null \|\| !isTargetCurrent\(\)\)\s*\{\s*SetStatus\(Loc\(""Str_Merge_TargetChanged""\)\);\s*return;\s*\}", flow);
        Assert.Contains("InsertImportedDocuments([import], dialog.InsertAt);", flow);
        // Pictures the source's saved copy left out are named in the status, as Extract Pages does.
        Assert.Contains("SetStatus(copied.Note(string.Format(Loc(\"Str_InsertPages_Done\"), pages.Count, shown.Title), Loc(\"Str_Save_PicturesSkipped\")));", flow);
        Assert.Contains("InsertImportedDocuments(imports, insertionIndex);",
            ShellSource.Between(ShellSource.Read("Shell", "FileOperations.cs"), "private async Task MergeAtIndex(", "private void InsertImportedDocuments("));
    }

    // The source's pages come from its saved copy as it stands (markup, form values and turns included), under its own
    // protection, and both panes go back to the tabs they showed.
    [Fact]
    public void TheSourceTabIsReadAsItStandsAndEveryPaneGoesBack()
    {
        string copy = ShellSource.Between(ShellSource.Read("Shell", "InsertPages.cs"), "private async Task<SavedCopy?> CopyPagesFromTab(", "private static InsertPagesSource Describe(");
        Assert.Contains("if (!EnsureMayWrite(PdfOutput.Printable))", copy);
        Assert.Contains("CommitActiveTextBox();", copy);
        Assert.Contains("WriteSavedCopyToTemp(throwaway: true)", copy);
        int restore = copy.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(restore > 0);
        string back = copy[restore..];
        Assert.Contains("sourceViewer.SwitchToTabExt(sourceWasShowing);", back);
        Assert.Contains("FocusPane(home);", back);
        Assert.Contains("home.SwitchToTabExt(homeTab);", back);
        // A canceled owner password or a tab still opening says so after the home tab is back, not under the source.
        int switchedHome = back.IndexOf("home.SwitchToTabExt(homeTab);", StringComparison.Ordinal);
        Assert.True(back.IndexOf("SetStatus(reason);", StringComparison.Ordinal) > switchedHome);
        Assert.Contains("reason = string.Format(Loc(\"Str_InsertPages_StillOpening\"), title);", copy);
    }
}
