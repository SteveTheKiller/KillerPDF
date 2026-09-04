using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfImpositionPlannerTests
{
    [Fact]
    public void PresetRoundTripsAndDrivesPlanningAndPlacement()
    {
        var preset = new PdfImpositionPreset("Two-up letter", 2, 1, 792, 612,
            margin: 18, gutter: 12, duplex: true, includeCropMarks: true,
            includeRegistrationMarks: true, creepPerSheet: 0.5);

        PdfImpositionPreset restored = PdfImpositionPreset.FromJson(preset.ToJson());
        IReadOnlyList<PdfImposedSheetSide> sides = restored.Plan(3);
        IReadOnlyList<PdfImposedPlacement> placements = restored.Place(sides[0],
            [new PdfContentBounds(0, 0, 300, 500), new PdfContentBounds(0, 0, 500, 300),
                new PdfContentBounds(0, 0, 300, 500)]);

        Assert.Equal(preset, restored);
        Assert.Equal(2, sides.Count);
        Assert.Equal(PdfImposedSheetFace.Back, sides[1].Face);
        Assert.Equal(2, placements.Count);
        Assert.True(restored.IncludeCropMarks);
        Assert.True(restored.IncludeRegistrationMarks);
        Assert.Equal(0.5, restored.CreepPerSheet);
        Assert.DoesNotContain("source", preset.ToJson(), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NotSupportedException>(() => PdfImpositionPreset.FromJson(
            preset.ToJson().Replace("\"version\":1", "\"version\":2",
                StringComparison.Ordinal)));
    }

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
    public void CutStackOrdersEachFinishedPileAndKeepsTrailingBlanks()
    {
        IReadOnlyList<PdfImposedSheetSide> sides =
            PdfImpositionPlanner.PlanCutStack(10, columns: 2, rows: 2);

        Assert.Collection(sides,
            side => AssertSide(side, 0, PdfImposedSheetFace.Front, 0, 3, 6, 9),
            side => AssertSide(side, 1, PdfImposedSheetFace.Front, 1, 4, 7, null),
            side => AssertSide(side, 2, PdfImposedSheetFace.Front, 2, 5, 8, null));
        Assert.Empty(PdfImpositionPlanner.PlanCutStack(0, 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.PlanCutStack(10, 0, 2));
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

    [Fact]
    public void BookletSignaturesKeepBoundedPageGroupsSeparate()
    {
        IReadOnlyList<PdfImposedSheetSide> sides =
            PdfImpositionPlanner.PlanBookletSignatures(10, 8);

        Assert.Equal(6, sides.Count);
        AssertSide(sides[0], 0, PdfImposedSheetFace.Front, 7, 0);
        AssertSide(sides[3], 1, PdfImposedSheetFace.Back, 3, 4);
        AssertSide(sides[4], 2, PdfImposedSheetFace.Front, null, 8);
        AssertSide(sides[5], 2, PdfImposedSheetFace.Back, 9, null);
        Assert.Equal(1, sides[2].CreepDepth);
        Assert.Equal(0, sides[4].CreepDepth);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.PlanBookletSignatures(10, 6));
    }

    [Fact]
    public void PosterTilesCoverTheSourceWithRequestedOverlap()
    {
        IReadOnlyList<PdfPosterTile> tiles = PdfImpositionPlanner.PlanPosterTiles(
            sourceWidth: 500, sourceHeight: 700,
            tileWidth: 300, tileHeight: 400, overlap: 20);

        Assert.Equal(4, tiles.Count);
        Assert.Equal(new PdfContentBounds(0, 300, 300, 700), tiles[0].SourceBounds);
        Assert.Equal(new PdfContentBounds(280, 300, 500, 700), tiles[1].SourceBounds);
        Assert.Equal(new PdfContentBounds(0, 0, 300, 320), tiles[2].SourceBounds);
        Assert.Equal(new PdfContentBounds(280, 0, 500, 320), tiles[3].SourceBounds);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.PlanPosterTiles(500, 700, 300, 400, 300));
    }

    [Fact]
    public void SheetPlacementFitsMixedPagesIntoMarginsAndGutters()
    {
        var side = new PdfImposedSheetSide(0, PdfImposedSheetFace.Front,
            Array.AsReadOnly<int?>([0, 1]));
        PdfContentBounds[] pages = [
            new(0, 0, 50, 100),
            new(0, 0, 100, 50)];

        IReadOnlyList<PdfImposedPlacement> placements = PdfImpositionPlanner.PlaceOnSheet(
            side, columns: 2, rows: 1, sheetWidth: 220, sheetHeight: 100,
            pages, margin: 10, gutter: 20);

        Assert.Equal(2, placements.Count);
        Assert.All(placements, placement => Assert.Equal(0.9, placement.Scale, 10));
        Assert.Equal(90, placements[0].Rotation);
        Assert.Equal(0, placements[1].Rotation);
        Assert.Equal(new PdfContentBounds(10, 27.5, 100, 72.5), placements[0].SheetBounds);
        Assert.Equal(new PdfContentBounds(120, 27.5, 210, 72.5), placements[1].SheetBounds);
    }

    [Fact]
    public void CropMarksStayOutsideThePlacedPage()
    {
        var placement = new PdfImposedPlacement(0, 0,
            new PdfContentBounds(20, 30, 120, 180), 1, 0);

        IReadOnlyList<PdfImpositionMark> marks =
            PdfImpositionPlanner.PlanCropMarks(placement, length: 10, offset: 2);

        Assert.Equal(8, marks.Count);
        Assert.Equal(new PdfImpositionMark(8, 30, 18, 30), marks[0]);
        Assert.Equal(new PdfImpositionMark(20, 18, 20, 28), marks[1]);
        Assert.Equal(new PdfImpositionMark(122, 180, 132, 180), marks[6]);
        Assert.Equal(new PdfImpositionMark(120, 182, 120, 192), marks[7]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.PlanCropMarks(placement, offset: -1));
    }

    [Fact]
    public void CreepMovesInnerSheetPlacementsAwayFromTheFold()
    {
        var side = new PdfImposedSheetSide(2, PdfImposedSheetFace.Back,
            Array.AsReadOnly<int?>([0, 1])) { CreepDepth = 2 };
        IReadOnlyList<PdfImposedPlacement> placements =
            PdfImpositionPlanner.PlaceOnSheet(side, 2, 1, 220, 100,
            [
                new PdfContentBounds(0, 0, 100, 80),
                new PdfContentBounds(0, 0, 100, 80)
            ], margin: 10);

        IReadOnlyList<PdfImposedPlacement> crept =
            PdfImpositionPlanner.ApplyCreep(side, placements, 2, 0.75);

        Assert.Equal(placements[0].SheetBounds.Left - 1.5,
            crept[0].SheetBounds.Left, 10);
        Assert.Equal(placements[1].SheetBounds.Left + 1.5,
            crept[1].SheetBounds.Left, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.ApplyCreep(side, placements, 2, -0.1));
    }

    [Fact]
    public void RegistrationMarksStayInsideSheetCorners()
    {
        IReadOnlyList<PdfImpositionRegistrationMark> marks =
            PdfImpositionPlanner.PlanRegistrationMarks(
                sheetWidth: 600, sheetHeight: 800, inset: 20,
                radius: 4, crosshairLength: 12);

        Assert.Equal(4, marks.Count);
        Assert.Equal(new PdfImpositionRegistrationMark(20, 20, 4, 12), marks[0]);
        Assert.Equal(new PdfImpositionRegistrationMark(580, 780, 4, 12), marks[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.PlanRegistrationMarks(30, 40, inset: 15));
    }

    [Fact]
    public void FoldMarksStayOutsideTheSheetEdges()
    {
        IReadOnlyList<PdfImpositionMark> marks = PdfImpositionPlanner.PlanFoldMarks(
            600, 800, [300], [200, 400], 10);

        Assert.Equal(6, marks.Count);
        Assert.Equal(new PdfImpositionMark(300, -10, 300, 0), marks[0]);
        Assert.Equal(new PdfImpositionMark(300, 800, 300, 810), marks[1]);
        Assert.Equal(new PdfImpositionMark(-10, 200, 0, 200), marks[2]);
        Assert.Equal(new PdfImpositionMark(600, 400, 610, 400), marks[5]);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImpositionPlanner.PlanFoldMarks(600, 800, [600], []));
    }

    [Fact]
    public void ExporterWritesFittedSourcePagesAsReusableVectorContent()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .AddPage(100, 200, Text("First", 10, 100))
            .AddPage(200, 100, Text("Second", 20, 50))
            .Build();
        PdfDocument source = PdfDocument.Open(sourceBytes);
        IReadOnlyList<PdfImposedSheetSide> sides = [
            new(0, PdfImposedSheetFace.Front, Array.AsReadOnly<int?>([0, 1])),
            new(1, PdfImposedSheetFace.Front, Array.AsReadOnly<int?>([null, null]))];

        PdfDocument output = PdfDocument.Open(PdfImpositionExporter.Build(
            source, sides, columns: 2, rows: 1,
            sheetWidth: 420, sheetHeight: 200, margin: 10, gutter: 20));

        Assert.Equal(2, PdfPageBoxInformation.Read(output).Count);
        PdfPageContent content = new PdfPageContentReader(output).Read(0);
        Assert.Equal("First Second", content.Text);
        Assert.Contains(content.TextRuns, run => run.Text == "First"
            && run.BoundingBox.Left < 200
            && run.WritingDirection == PdfWritingDirection.BottomToTop);
        Assert.Contains(content.TextRuns, run => run.Text == "Second"
            && run.BoundingBox.Left > 200
            && run.WritingDirection == PdfWritingDirection.LeftToRight);
        Assert.Empty(new PdfPageContentReader(output).Read(1).TextRuns);

        static PdfContentStreamBuilder Text(string value, double x, double y) =>
            new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 10).MoveText(x, y)
                .ShowLatin1Text(value).EndText();
    }

    private static void AssertSide(PdfImposedSheetSide side, int sheet,
        PdfImposedSheetFace face, params int?[] pages)
    {
        Assert.Equal(sheet, side.SheetIndex);
        Assert.Equal(face, side.Face);
        Assert.Equal(pages, side.SourcePageIndices);
    }
}
