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
}
