using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The one-step Delete Pages dialog plans from the range the user typed: which pages go, grouped into runs for the
// thumbnail strip, how many stay, and whether the document keeps at least one page.
public sealed class DeletePagesPlanTests
{
    [Fact]
    public void RunsOfNeighbouringPagesBecomeOneGroup()
    {
        var plan = DeletePagesPlan.From([1, 6, 14, 39, 40, 41, 599], pageCount: 900);

        Assert.Equal([(1, 1), (6, 6), (14, 14), (39, 41), (599, 599)], plan.Groups);
        Assert.Equal(7, plan.Pages.Count);
        Assert.Equal(893, plan.Remaining);
        Assert.True(plan.KeepsAPage);
    }

    [Fact]
    public void PagesAreSortedAndCountedOnce()
    {
        var plan = DeletePagesPlan.From([4, 2, 3, 2, 4], pageCount: 6);

        Assert.Equal([2, 3, 4], plan.Pages);
        Assert.Equal([(2, 4)], plan.Groups);
        Assert.Equal(3, plan.Remaining);
    }

    [Fact]
    public void DeletingEveryPageIsRefused()
    {
        var plan = DeletePagesPlan.From([0, 1, 2], pageCount: 3);

        Assert.False(plan.KeepsAPage);
        Assert.Equal(0, plan.Remaining);
    }

    [Fact]
    public void PagesOutsideTheDocumentAreDropped()
    {
        var plan = DeletePagesPlan.From([-1, 0, 5, 9], pageCount: 6);

        Assert.Equal([0, 5], plan.Pages);
        Assert.Equal([(0, 0), (5, 5)], plan.Groups);
    }

    [Fact]
    public void DeleteIsOneDialogWithNoYesNoBoxAfterIt()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "Shell", "PageOperations.cs"));
        int start = source.IndexOf("private void Delete_Click", StringComparison.Ordinal);
        string delete = source[start..source.IndexOf("private string InitialPageRange", start, StringComparison.Ordinal)];

        Assert.Contains("new DeletePagesDialog(", delete);
        Assert.DoesNotContain("MessageBoxButton.YesNo", delete);
        Assert.DoesNotContain("PromptPageOperationRange", delete);
    }

    [Fact]
    public void AnEmptyRangePlansNothing()
    {
        var plan = DeletePagesPlan.From([], pageCount: 6);

        Assert.Empty(plan.Pages);
        Assert.Empty(plan.Groups);
        Assert.False(plan.HasPages);
    }
}
