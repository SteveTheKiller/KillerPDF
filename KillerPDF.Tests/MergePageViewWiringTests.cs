using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// #495: the Merge dialog's page view is offered only where the merge builds a new file, because only that path can
// take a page order. Each caller either passes the order on or leaves the page view out. The window itself is
// checked on a UI VM.
public sealed class MergePageViewWiringTests
{
    private static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        int next = Regex.Match(source[(start + signature.Length)..], @"\n        (private|internal|public) ").Index;
        return next > 0 ? source.Substring(start, signature.Length + next) : source[start..];
    }

    [Fact]
    public void MergePdfsOffersPagesAndMergesInThatOrder()
    {
        string merge = Method(Source("Shell", "FileOperations.cs"), "private void Merge_Click(");
        Assert.Contains("new MergeFilesDialog(this, initial, labels: labels, offerPages: true)", merge, StringComparison.Ordinal);
        Assert.Contains("OpenMerged([.. dlg.Paths], batch, skippedPictures, dlg.PageOrder);", merge, StringComparison.Ordinal);
    }

    [Fact]
    public void ADropOfSeveralPdfsOffersPagesAndMergesInThatOrder()
    {
        string source = Source("Shell", "ImportAndZip.cs");
        Assert.Contains("offerOpenSeparately: true,\n                        offerPages: true);", source.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("OpenMerged([.. kept.Select(file => file.Path)], batch, pages: dlg.PageOrder);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageCardMergeInsertsWholeFilesSoItOffersNoPageView()
    {
        string insert = Method(Source("Shell", "FileOperations.cs"), "private async Task MergeAtIndex(");
        Assert.Contains("new MergeFilesDialog(this);", insert, StringComparison.Ordinal);
        Assert.DoesNotContain("offerPages", insert, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCombinedFileTakesThePageOrderInsideTheCancellableBuild()
    {
        string source = Source("Shell", "ImportAndZip.cs");
        Assert.Contains("await Task.Run(() => BuildCombinedPdf(found, ct, pages));", source, StringComparison.Ordinal);
        string build = Method(source, "private static (string? Path, int LeftOut) BuildCombinedPdf(");
        Assert.Contains("out var merged, ct);", build, StringComparison.Ordinal);
        Assert.Contains("MergePageArrangement.Plan(pages, merged)", build, StringComparison.Ordinal);
        Assert.Contains("MergePageArrangement.Apply(outPath, plan);", build, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDialogStopsItsThumbnailsBeforeTheSnapshotGoes()
    {
        string dialog = Source("Controls", "MergeFilesDialog.cs");
        Assert.Contains("Closed += (_, _) => StopLoading();", dialog, StringComparison.Ordinal);
        Assert.Contains("_loader.Wait(", dialog, StringComparison.Ordinal);
        // The loader never waits on the UI thread, which is what makes that wait safe.
        Assert.DoesNotContain("Dispatcher.Invoke(", dialog, StringComparison.Ordinal);
        // Without lazy cancellation the chain's tail is cancelled at once and the wait returns while a load still runs.
        Assert.Contains("TaskContinuationOptions.LazyCancellation", dialog, StringComparison.Ordinal);
    }

    // A page count arriving mid-drag opens a whole file up and shifts the places, so the drop looks the pages up again.
    [Fact]
    public void ADropMovesThePagesTheDragPickedUpNotThePlacesTheyHad()
    {
        string dialog = Source("Controls", "MergeFilesDialog.cs");
        string drag = Method(dialog, "private void StartPageDrag(");
        Assert.Contains("_draggedPages = [.. moving.Select(index => _arrangement.Pages[index])];", drag, StringComparison.Ordinal);
        string drop = Method(dialog, "private void OnDrop(");
        Assert.Contains("int[] pages = _arrangement.IndicesOf(_draggedPages);", drop, StringComparison.Ordinal);
        Assert.DoesNotContain("GetData(PageFormat) is int[]", drop, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTakenOutAndAddedBackLoadsItsPagesAgain()
    {
        string sync = Method(Source("Controls", "MergeFilesDialog.cs"), "private void SyncPages(");
        Assert.Contains("_queued.Remove(gone);", sync, StringComparison.Ordinal);
    }
}
