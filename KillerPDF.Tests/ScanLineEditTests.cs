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
    public void ImageWordsLandOnTheBitmapsPixels()
    {
        var word = ScanLineEdit.ToPixelWord("sum", new Rect(10, 20, 30, 10), 2, 3);

        Assert.Equal(("sum", 20, 60, 80, 90), (word.Text, word.Left, word.Top, word.Right, word.Bottom));
    }
}
