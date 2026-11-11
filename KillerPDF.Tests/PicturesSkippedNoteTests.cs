using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// Round 2, app BF-004 and BF-005. Every output that burns pictures says how many it left out, and a failed save does
// not lend its count to the next one.
public sealed class PicturesSkippedNoteTests
{
    [Fact]
    public void TheNoteNamesTheCountAndIsAbsentWhenNothingWasSkipped()
    {
        Assert.Equal("Saved. Skipped pictures: 1.", PicturesSkippedNote.Append("Saved.", 1, "Skipped pictures: {0}."));
        Assert.Equal("Saved.", PicturesSkippedNote.Append("Saved.", 0, "Skipped pictures: {0}."));
    }

    // The count a burn leaves behind is what the output paths read right after it.
    [Fact]
    public void ABurnLeavesTheCountOfPicturesItCouldNotReadForTheCallerToReport()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-skipped-note-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
            var picture = new ImageAnnotation
            {
                PageIndex = 0, Position = new Point(10, 10), Scale = 1, SourceWidth = 50, SourceHeight = 50,
                ImageData = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.7 not a picture"))
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [picture] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 200) }, flattenEditing: true);
            Assert.Equal("Copy saved. Left out: 1.",
                PicturesSkippedNote.Append("Copy saved.", PdfEngineBurn.SkippedPictures, "Left out: {0}."));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // SOURCE PINS: the save flow lives in the window, which this project does not compile. Window check on SN02:
    // 1. Place a picture copied from a PDF that cannot be read and make the destination read-only. 2. Save: it
    // fails. 3. Delete the picture and make the destination writable. 4. Save: the status has no skipped-picture
    // note. 5. Repeat with the picture in place for Save a Copy, Save Flattened, Reduce Size and Divide Spreads:
    // each status ends with the skipped-picture note.
    [Fact]
    public void EveryOutputThatBurnsPicturesReportsTheSkippedCount()
    {
        string files = Source("Shell", "FileOperations.cs");
        string reduce = Source("Shell", "ReduceSize.cs");
        string divide = Source("Shell", "DivideSpreads.cs");

        Assert.Contains("skippedPictures = PdfEngineBurn.SkippedPictures;", reduce);
        Assert.Contains("return new SavedCopy(copy, skippedPictures);", reduce);
        Assert.Contains("PicturesSkippedNote.Append(string.Format(Loc(\"Str_Reduce_Done\")", reduce);
        Assert.Contains("skippedPictures = PdfEngineBurn.SkippedPictures;", divide);
        Assert.Contains("PicturesSkippedNote.Append(", divide);
        Assert.Equal(2, Count(files, "skippedPictures, Loc(\"Str_Save_PicturesSkipped\")")
            + Count(files, "flattenSkippedPictures, Loc(\"Str_Save_PicturesSkipped\")"));
    }

    [Fact]
    public void AFailedSaveOrSaveAsClearsTheCountSoTheNextSaveDoesNotReportIt()
    {
        string files = Source("Shell", "FileOperations.cs");

        Assert.Equal(2, Count(files, "_lastSaveSkippedPictures = 0;   // a failed save must not lend its count to the next one"));
        // Also cleared before each save decides whether to burn, so a save that burns nothing starts from none.
        Assert.Equal(5, Count(files, "_lastSaveSkippedPictures = 0;"));   // two starts, two failures, ReportSaved
    }

    // Print burns on a worker and SkippedPictures is thread-static, so the count is read inside the burn delegate and
    // appended to the status after the await.
    [Fact]
    public void PrintReadsTheCountOnTheWorkerAndAppendsTheNote()
    {
        string file = File.ReadAllText(Path.Combine(ShellFolder(), "FileOperations.cs"));
        string print = file[file.IndexOf("private async void RunPrintFlow", StringComparison.Ordinal)..];
        int burn = print.IndexOf("PdfEngineBurn.Burn(path, annotsSnap", StringComparison.Ordinal);
        int read = print.IndexOf("printSkipped = PdfEngineBurn.SkippedPictures;", StringComparison.Ordinal);
        int ready = print.IndexOf("PrintCopy.Ready(printPath, burn", StringComparison.Ordinal);
        Assert.True(burn > 0 && read > burn && read < ready, "the count is read inside the burn delegate");
        Assert.Contains("Services.PicturesSkippedNote.Append(", print);
        Assert.Contains("printSkipped, Loc(\"Str_Save_PicturesSkipped\")", print);
    }

    [Fact]
    public void TheNoteSummaryListsTheOutputsExactly()
    {
        string summary = File.ReadAllText(Path.Combine(ShellFolder(), "..", "Services", "PicturesSkippedNote.cs"));
        summary = summary[..summary.IndexOf("internal static class PicturesSkippedNote", StringComparison.Ordinal)];
        foreach (string word in new[] { "Print", "OCR", "Save As", "exports", "macros", "recovery dialog" })
            Assert.Contains(word, summary);
        Assert.DoesNotContain("only read the page", summary);
        Assert.DoesNotContain("reports it to the log", summary);
    }

    private static string ShellFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "Shell");
    }
}
