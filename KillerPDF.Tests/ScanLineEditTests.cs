using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ScanLineEditTests
{
    private static readonly string[] Words = ["Invoice", "total", "due", "today"];
    private static readonly Rect[] Boxes =
    [
        new(10, 20, 70, 14), new(90, 20, 50, 14), new(150, 20, 30, 14), new(190, 20, 50, 14)
    ];

    private static double TenPerLetter(string text) => text.Length * 10;

    [Fact]
    public void OneChangedWordCoversOnlyThatWord()
    {
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(Words, Boxes, "Invoice sum due today", TenPerLetter));

        Assert.Equal("sum", plan.Text);
        Assert.Equal(new Rect(90, 20, 50, 14), plan.Covered);
    }

    [Fact]
    public void AnUnchangedLineNeedsNoEdit()
    {
        Assert.Null(ScanLineEdit.Plan(Words, Boxes, "Invoice total due today", TenPerLetter));
    }

    [Fact]
    public void ADeletedWordCoversItWithNoNewText()
    {
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(Words, Boxes, "Invoice total today", TenPerLetter));

        Assert.Equal("", plan.Text);
        Assert.Equal(new Rect(150, 20, 30, 14), plan.Covered);
    }

    [Fact]
    public void AWordTooWideForItsGapTakesOverTheRestOfTheLine()
    {
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(Words, Boxes, "Invoice grandtotal due today", TenPerLetter));

        Assert.Equal("grandtotal due today", plan.Text);
        Assert.Equal(new Rect(90, 20, 150, 14), plan.Covered);
    }

    [Fact]
    public void TheLastWordMayRunPastTheLineEnd()
    {
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(Words, Boxes, "Invoice total due tomorrow-ish", TenPerLetter));

        Assert.Equal("tomorrow-ish", plan.Text);
        Assert.Equal(new Rect(190, 20, 50, 14), plan.Covered);
    }

    [Fact]
    public void ALineBreakReplacesTheWholeLine()
    {
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(Words, Boxes, "Invoice sum\ndue today", TenPerLetter));

        Assert.Equal("Invoice sum\ndue today", plan.Text);
        Assert.Equal(new Rect(10, 20, 230, 14), plan.Covered);
    }

    [Fact]
    public void TheCoverReachesPastTheWordsByAMarginInRoomyGaps()
    {
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(Words, Boxes, "Invoice sum due today", TenPerLetter));

        Assert.Equal(new Rect(87, 17, 56, 20), plan.Cover);
    }

    [Fact]
    public void TheCoverStopsShortOfNeighborsCloserThanTheMargin()
    {
        Rect[] tight = [new(10, 20, 70, 14), new(81, 20, 50, 14), new(132, 20, 30, 14)];

        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(["Invoice", "total", "due"], tight, "Invoice sum due", TenPerLetter));

        Assert.Equal(80, plan.Cover.Left);
        Assert.Equal(132, plan.Cover.Right);
        Assert.True(plan.Cover.Contains(plan.Covered));
    }

    [Fact]
    public void AnEditAtTheLineStartKeepsTheScannedWordItPrecedes()
    {
        ScanLineEditPlan plan = Assert.IsType<ScanLineEditPlan>(
            ScanLineEdit.Plan(Words, Boxes, "New Invoice total due today", text => text.Length * 5));

        Assert.Equal("New Invoice", plan.Text);
        Assert.Equal(new Rect(10, 20, 70, 14), plan.Covered);
    }

    [Fact]
    public void ImageWordsLandOnTheBitmapsPixels()
    {
        var word = ScanLineEdit.ToPixelWord("sum", new Rect(10, 20, 30, 10), 2, 3);

        Assert.Equal(("sum", 20, 60, 80, 90), (word.Text, word.Left, word.Top, word.Right, word.Bottom));
    }

    private static ScanLineFit Fit(double size) =>
        new(System.Windows.Media.Colors.White, System.Windows.Media.Colors.Black, "Arial", size, false, 10, [10.0]);

    // R4-009: a page the file cannot draw completely throws, and the bitmap on screen still measures it.
    [Fact]
    public void AFailedRenderFallsThroughToTheBitmapOnScreen()
    {
        ScanLineFit screen = Fit(11);
        ScanLineFit? measured = null;

        Exception? failure = Record.Exception(() => measured = ScanLineEdit.MeasureFromSharpest(
        [
            new ScanMeasureSource(4, () => throw new InvalidOperationException("Page 1 did not render completely.")),
            new ScanMeasureSource(1, () => screen)
        ]));

        Assert.Null(failure);
        Assert.Same(screen, measured);
    }

    // A large sheet at a high zoom holds more pixels on screen than the fixed-size render, so the
    // screen measures it first and nothing is rendered.
    [Fact]
    public void TheSourceWithMorePixelsMeasuresFirst()
    {
        ScanLineFit screen = Fit(11);
        bool rendered = false;

        ScanLineFit? measured = ScanLineEdit.MeasureFromSharpest(
        [
            new ScanMeasureSource(ScanLineEdit.RenderPixelsPerUnit(3370, 2384), () => { rendered = true; return Fit(9); }),
            new ScanMeasureSource(2.5, () => screen)
        ]);

        Assert.Same(screen, measured);
        Assert.False(rendered);
    }

    [Fact]
    public void ASourceThatReadsNothingGivesWayToTheNext()
    {
        ScanLineFit file = Fit(12);

        Assert.Same(file, ScanLineEdit.MeasureFromSharpest(
            [new ScanMeasureSource(3, () => null), new ScanMeasureSource(2, () => file)]));
    }
}
