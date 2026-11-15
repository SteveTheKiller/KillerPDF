using System.Windows;
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
        Assert.True(worker.Join(TimeSpan.FromSeconds(60)), $"ParsePageRange(\"{input}\", {pageCount}) did not return");
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

    [Fact]
    public void FullCanvasOnAnOffsetBoxMapsToTheBoxInUserSpace()
    {
        // Box [100 100 400 500]: Left 100, Bottom 100, 300 x 400. The whole canvas is the whole box.
        var (x, y, w, h) = CropMath.ToCropBox(new Rect(0, 0, 300, 400), 300, 400, 300, 400, 0, 100, 100, 300, 400);
        Assert.Equal((100.0, 100.0, 300.0, 400.0), (x, y, w, h));
    }

    [Fact]
    public void DrawnRectOnAnOffsetBoxAddsTheBoxOrigin()
    {
        // Canvas 600 x 800 shows the 300 x 400 box at 2x. Canvas x 100..300, y 200..600 is box x 50..150,
        // and from the top edge y 100..300, so bottom-up y = 400-300 .. 400-100 = 100..300.
        var (x, y, w, h) = CropMath.ToCropBox(new Rect(100, 200, 200, 400), 300, 400, 600, 800, 0, 100, 100, 300, 400);
        Assert.Equal((150.0, 200.0, 100.0, 200.0), (x, y, w, h));
    }

    [Fact]
    public void OtherPagesScaleThenTakeTheirOwnOffset()
    {
        // Reference page 300 x 400 at the origin; a second page 600 x 800 with its box at (10, 20).
        // The reference rect (75..225, 100..300) is scaled by 2 to 150..450, 200..600, then offset.
        var (x, y, w, h) = CropMath.ToCropBox(new Rect(75, 100, 150, 200), 300, 400, 300, 400, 0, 10, 20, 600, 800);
        Assert.Equal((160.0, 220.0, 300.0, 400.0), (x, y, w, h));
    }

    [Fact]
    public void ClampsToTheOffsetBoxNotToTheOrigin()
    {
        // A rect hanging past the canvas is clipped by CanvasToPage to the box, then offset.
        var (x, y, w, h) = CropMath.ToCropBox(new Rect(-50, -50, 500, 500), 300, 400, 300, 400, 0, 100, 100, 300, 400);
        Assert.Equal((100.0, 100.0, 300.0, 400.0), (x, y, w, h));
    }

    [Fact]
    public void RotatedPageOffsetsAfterTheRotationMapping()
    {
        // Rotation 90: canvas is 400 x 300. Canvas rect x 0..200, y 0..150 maps to page
        // x = cy*pW/cH = 0..150 (pW 300, cH 300), y = cx*pH/cW = 0..200. Box origin (100, 100) is added.
        var (x, y, w, h) = CropMath.ToCropBox(new Rect(0, 0, 200, 150), 300, 400, 400, 300, 90, 100, 100, 300, 400);
        Assert.Equal((100.0, 100.0, 150.0, 200.0), (x, y, w, h));
    }

    [Fact]
    public void TinyRectIsGrownToOnePoint()
    {
        var (x, y, w, h) = CropMath.ToCropBox(new Rect(0, 0, 0, 0), 300, 400, 300, 400, 0, 100, 100, 300, 400);
        Assert.Equal(1.0, w, 6);
        Assert.Equal(1.0, h, 6);
        Assert.Equal(100.0, x, 6);
    }

    [Fact]
    public void AllChecksEveryPageEvenWithARangeTyped()
    {
        Assert.Equal([0, 1, 2, 3, 4], CropMath.SelectPages(true, "1-2", 5, 3)!);
    }

    [Fact]
    public void TypedRangeBeatsTheBoxPage()
    {
        Assert.Equal([0, 1, 2], CropMath.SelectPages(false, "1-3", 5, 4)!);
    }

    [Fact]
    public void BadRangeSelectsNothing()
    {
        Assert.Null(CropMath.SelectPages(false, "x", 5, 4));
    }

    [Fact]
    public void NoAllAndNoRangeCropsOnlyThePageHoldingTheBox()
    {
        Assert.Equal([6], CropMath.SelectPages(false, "  ", 10, CropMath.BoxPage(6, 2))!);
        Assert.Equal([2], CropMath.SelectPages(false, null, 10, CropMath.BoxPage(-1, 2))!);
    }

    [Fact]
    public void BoxPageIsTheDrawnPageNotTheScrolledToPage()
    {
        Assert.Equal(6, CropMath.BoxPage(6, 2));
        Assert.Equal(0, CropMath.BoxPage(0, 2));
        Assert.Equal(2, CropMath.BoxPage(-1, 2));
    }
}
