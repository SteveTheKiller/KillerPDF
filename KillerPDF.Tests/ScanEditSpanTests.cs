using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ScanEditSpanTests
{
    private static readonly string[] Line = ["Invoice", "total", "due", "today"];

    [Fact]
    public void OneChangedWordIsTheWholeEdit()
    {
        Assert.Equal(new ScanEditSpan(1, 1, "sum"), ScanEditSpan.Diff(Line, "Invoice sum due today"));
    }

    [Fact]
    public void FirstAndLastWordsCanChange()
    {
        Assert.Equal(new ScanEditSpan(0, 3, "Bill total due now"), ScanEditSpan.Diff(Line, "Bill total due now"));
    }

    [Fact]
    public void NothingChangedIsNoEdit()
    {
        Assert.Null(ScanEditSpan.Diff(Line, "Invoice  total due today"));
    }

    [Fact]
    public void DeletingAWordKeepsItsPlace()
    {
        Assert.Equal(new ScanEditSpan(2, 2, ""), ScanEditSpan.Diff(Line, "Invoice total today"));
    }

    [Fact]
    public void AddingAWordCoversTheNeighborItFollows()
    {
        Assert.Equal(new ScanEditSpan(1, 1, "total now"), ScanEditSpan.Diff(Line, "Invoice total now due today"));
    }

    [Fact]
    public void AddingAtTheStartKeepsTheFirstWordItPrecedes()
    {
        Assert.Equal(new ScanEditSpan(0, 0, "New Invoice"), ScanEditSpan.Diff(Line, "New Invoice total due today"));
    }

    [Fact]
    public void AddingAtTheStartOfAOneWordLineKeepsThatWord()
    {
        Assert.Equal(new ScanEditSpan(0, 0, "New Invoice"), ScanEditSpan.Diff(["Invoice"], "New Invoice"));
    }

    [Fact]
    public void AddingAtTheEndCoversTheLastWord()
    {
        Assert.Equal(new ScanEditSpan(3, 3, "today please"), ScanEditSpan.Diff(Line, "Invoice total due today please"));
    }

    [Fact]
    public void DeletingTheFirstAndLastWordsKeepsTheirPlace()
    {
        Assert.Equal(new ScanEditSpan(0, 0, ""), ScanEditSpan.Diff(Line, "total due today"));
        Assert.Equal(new ScanEditSpan(3, 3, ""), ScanEditSpan.Diff(Line, "Invoice total due"));
    }

    [Fact]
    public void ALineWithNoWordsHasNothingToEdit()
    {
        Assert.Null(ScanEditSpan.Diff([], "New"));
        Assert.Null(ScanEditSpan.Diff([], ""));
    }
}
