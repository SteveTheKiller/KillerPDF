using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

public sealed class CropMathTests
{
    // Runs the parse on a background thread so a regression to an endless loop fails the test
    // instead of hanging the run.
    private static int[]? ParseWithin(string input, int pageCount)
    {
        int[]? result = null;
        var worker = new Thread(() => result = CropMath.ParsePageRange(input, pageCount)) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)), $"ParsePageRange(\"{input}\", {pageCount}) did not return");
        return result;
    }

    [Fact]
    public void RangeEndingAtIntMaxReturnsAtOnceWithTheDocumentsPages()
    {
        Assert.Equal([0, 1, 2], ParseWithin("1-2147483647", 3)!);
    }

    [Fact]
    public void RangeWithHugeEndIsClampedToPageCount()
    {
        Assert.Equal([1, 2], ParseWithin("2-999999999", 3)!);
    }

    [Fact]
    public void ListsAndRangesMergeInPageOrder()
    {
        Assert.Equal([0, 1, 4, 6, 7], ParseWithin("7-8, 1-2,5", 8)!);
    }

    [Fact]
    public void ReversedOrOutOfRangeOrBadInputReturnsNull()
    {
        Assert.Null(ParseWithin("5-1", 3));
        Assert.Null(ParseWithin("9", 3));
        Assert.Null(ParseWithin("a-b", 3));
        Assert.Null(ParseWithin("", 3));
    }
}
