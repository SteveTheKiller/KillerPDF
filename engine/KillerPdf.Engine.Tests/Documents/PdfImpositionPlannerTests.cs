using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImpositionPlannerTests
{
    [Fact]
    public void BookletUsesCorrectFrontAndBackPageOrder()
    {
        IReadOnlyList<PdfImposedSheetSide> sides = PdfImpositionPlanner.PlanBooklet(8);

        Assert.Collection(sides,
            side => AssertSide(side, 0, PdfImposedSheetFace.Front, 7, 0),
            side => AssertSide(side, 0, PdfImposedSheetFace.Back, 1, 6),
            side => AssertSide(side, 1, PdfImposedSheetFace.Front, 5, 2),
            side => AssertSide(side, 1, PdfImposedSheetFace.Back, 3, 4));
    }

    [Fact]
    public void StepAndRepeatFillsSlotsAndKeepsTrailingBlanks()
    {
        IReadOnlyList<PdfImposedSheetSide> sides =
            PdfImpositionPlanner.PlanStepAndRepeat(
                sourcePageIndex: 3, copyCount: 5,
                columns: 2, rows: 2, duplex: true);

        AssertSide(sides[0], 0, PdfImposedSheetFace.Front, 3, 3, 3, 3);
        AssertSide(sides[1], 0, PdfImposedSheetFace.Back, 3, null, null, null);
    }

    [Fact]
    public void ManualSequencePreservesOrderBlanksAndDuplexFaces()
    {
        IReadOnlyList<PdfImposedSheetSide> sides =
            PdfImpositionPlanner.PlanManual(
                sourcePageCount: 6,
                sequence: [5, null, 0, 4, 1],
                slotsPerSide: 3, duplex: true);

        AssertSide(sides[0], 0, PdfImposedSheetFace.Front, 5, null, 0);
        AssertSide(sides[1], 0, PdfImposedSheetFace.Back, 4, 1, null);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.PlanManual(6, [6], 1));
    }

    [Fact]
    public void BookletAndNUpInsertExplicitBlankSlots()
    {
        IReadOnlyList<PdfImposedSheetSide> booklet = PdfImpositionPlanner.PlanBooklet(5);
        Assert.Equal(4, booklet.Count);
        Assert.Equal(3, booklet.SelectMany(side => side.SourcePageIndices).Count(page => page is null));

        IReadOnlyList<PdfImposedSheetSide> nup = PdfImpositionPlanner.PlanNUp(5, 2, 2, duplex: true);
        Assert.Equal(2, nup.Count);
        Assert.Equal(PdfImposedSheetFace.Front, nup[0].Face);
        Assert.Equal(PdfImposedSheetFace.Back, nup[1].Face);
        Assert.Equal(new int?[] { 4, null, null, null }, nup[1].SourcePageIndices);
    }

    private static void AssertSide(PdfImposedSheetSide side, int sheet,
        PdfImposedSheetFace face, params int?[] pages)
    {
        Assert.Equal(sheet, side.SheetIndex);
        Assert.Equal(face, side.Face);
        Assert.Equal(pages, side.SourcePageIndices);
    }
}
