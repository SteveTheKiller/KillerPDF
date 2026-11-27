using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// BF-B-004: Insert Pages lists a tab that has not loaded without a page count and fills the count in when a
/// background read finishes, so building the list never parses a document on the window's thread.
/// </summary>
public sealed class InsertPagesLateCountTests
{
    [Fact]
    public void ALateCountFillsAnUnreadSourceOnce()
    {
        var counts = new InsertPagesCounts([4, null, null]);
        Assert.Null(counts[1]);
        Assert.True(counts.TryFill(1, 12));
        Assert.Equal(12, counts[1]);
        Assert.False(counts.TryFill(1, 99));
        Assert.Equal(12, counts[1]);
        Assert.Null(counts[2]);
    }

    [Fact]
    public void ACountNeverReplacesAKnownOneOrLandsOutsideTheList()
    {
        var counts = new InsertPagesCounts([4, null]);
        Assert.False(counts.TryFill(0, 9));
        Assert.Equal(4, counts[0]);
        Assert.False(counts.TryFill(2, 3));
        Assert.False(counts.TryFill(-1, 3));
        Assert.False(counts.TryFill(1, 0));
        Assert.Null(counts[1]);
    }

    [Fact]
    public void DescribeNoLongerParsesADeferredTabOnTheWindowThread()
    {
        string insert = ShellSource.Read("Shell", "InsertPages.cs");
        string describe = ShellSource.Between(insert, "private static InsertPagesSource Describe(", "tab.Title, path, count);");
        Assert.DoesNotContain("ReadPageInformation", describe);
        Assert.DoesNotContain("PdfEngineIntegration", describe);
        string flow = ShellSource.Between(insert, "private async Task InsertPagesAt(", "private async Task<SavedCopy?> CopyPagesFromTab(");
        // The read runs in the background with no password, so a protected file stays unread, and lands on the dialog.
        Assert.Contains("Task.Run(", flow);
        Assert.Contains("PdfEngineIntegration.ReadPageInformation(deferred, null).Count", flow);
        Assert.Contains("dialog.SetPageCount(", flow);
        Assert.True(flow.IndexOf("Task.Run(", StringComparison.Ordinal) < flow.IndexOf("dialog.ShowDialog()", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDialogTakesALateCountIntoItsRowSpanAndPlan()
    {
        string dialog = ShellSource.Read("Controls", "InsertPagesDialog.cs");
        string late = ShellSource.Between(dialog, "internal void SetPageCount(", "private static TextBlock Label(");
        Assert.Contains("if (_closed) return;", late);
        Assert.Contains("_counts.TryFill(", late);
        Assert.Contains("SpanWholeSource();", late);
        Assert.Contains("Replan();", late);
        // Only the span shown while no range is picked is rewritten; a typed range is left as typed.
        string span = ShellSource.Between(dialog, "private void SpanWholeSource()", "private void Follow(");
        Assert.Contains("if (_range.IsChecked == true", span);
        Assert.Contains("_counts[index]", span);
    }
}
