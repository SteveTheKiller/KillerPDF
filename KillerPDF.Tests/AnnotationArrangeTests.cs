using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #504: aligning, spacing and sizing several selected boxes at once.
public sealed class AnnotationArrangeTests
{
    private static readonly Rect[] Boxes =
    [
        new(10, 50, 40, 20),
        new(100, 10, 60, 30),
        new(60, 120, 20, 10)
    ];

    [Theory]
    [InlineData("AlignLeft", 10, 10, 10)]
    [InlineData("AlignRight", 120, 100, 140)]
    [InlineData("AlignCenter", 65, 55, 75)]
    public void HorizontalAlignmentLinesUpOnTheSelectionEdges(string action, double a, double b, double c)
    {
        Rect[] result = AnnotationArrange.Apply(Boxes, Parse(action));

        Assert.Equal([a, b, c], result.Select(box => box.X));
        Assert.Equal(Boxes.Select(box => box.Y), result.Select(box => box.Y));
        Assert.Equal(Boxes.Select(box => box.Size), result.Select(box => box.Size));
    }

    [Theory]
    [InlineData("AlignTop", 10, 10, 10)]
    [InlineData("AlignBottom", 110, 100, 120)]
    [InlineData("AlignMiddle", 60, 55, 65)]
    public void VerticalAlignmentLinesUpOnTheSelectionEdges(string action, double a, double b, double c)
    {
        Rect[] result = AnnotationArrange.Apply(Boxes, Parse(action));

        Assert.Equal([a, b, c], result.Select(box => box.Y));
        Assert.Equal(Boxes.Select(box => box.X), result.Select(box => box.X));
    }

    [Fact]
    public void DistributeLeavesTheEndsAndEvensTheGaps()
    {
        Rect[] result = AnnotationArrange.Apply(Boxes, AnnotationArrange.Action.DistributeHorizontally);

        // Left to right: 10..50, then 20 wide, then 100..160. Free span 150 minus 120 of boxes is 30, so 15 per gap.
        Assert.Equal(10, result[0].X);
        Assert.Equal(65, result[2].X);
        Assert.Equal(100, result[1].X);
    }

    [Fact]
    public void DistributeEvensTheCentersWhenTheBoxesOverlapTooMuchForGaps()
    {
        // A 300-wide mark under two small ones leaves no room for gaps, so the centers are evened
        // between the outermost ones instead of piling the boxes up past where they started.
        Rect[] boxes = [new(0, 0, 300, 10), new(50, 20, 20, 10), new(100, 40, 20, 10)];

        Rect[] result = AnnotationArrange.Apply(boxes, AnnotationArrange.Action.DistributeHorizontally);

        // Centers 150, 60 and 110: lowest 60 and highest 150 stay, the middle one goes to 105.
        Assert.Equal(0, result[0].X);
        Assert.Equal(50, result[1].X);
        Assert.Equal(95, result[2].X);
    }

    [Fact]
    public void DistributeVerticallyUsesTheTopToBottomOrder()
    {
        Rect[] result = AnnotationArrange.Apply(Boxes, AnnotationArrange.Action.DistributeVertically);

        // Top to bottom: 10..40, 50..70, 120..130. Span 120 minus 60 is 60, so 30 per gap.
        Assert.Equal(10, result[1].Y);
        Assert.Equal(70, result[0].Y);
        Assert.Equal(120, result[2].Y);
    }

    [Fact]
    public void SpacingGrowsAndShrinksEveryGapFromTheFirstBox()
    {
        Rect[] wider = AnnotationArrange.Apply(Boxes, AnnotationArrange.Action.MoreHorizontalSpace);
        Rect[] closer = AnnotationArrange.Apply(Boxes, AnnotationArrange.Action.LessHorizontalSpace);

        Assert.Equal([10, 100 + 2 * AnnotationArrange.SpacingStep, 60 + AnnotationArrange.SpacingStep], wider.Select(box => box.X));
        Assert.Equal([10, 100 - 2 * AnnotationArrange.SpacingStep, 60 - AnnotationArrange.SpacingStep], closer.Select(box => box.X));
    }

    [Fact]
    public void LessSpaceStopsWhenBoxesTouch()
    {
        Rect[] touching = [new(0, 0, 10, 10), new(12, 0, 10, 10)];

        Rect[] result = AnnotationArrange.Apply(touching, AnnotationArrange.Action.LessHorizontalSpace);

        Assert.Equal(10, result[1].X);
    }

    [Theory]
    [InlineData("SameWidth", 60, 20, 60, 30, 60, 10)]
    [InlineData("SameHeight", 40, 30, 60, 30, 20, 30)]
    [InlineData("SameSize", 60, 30, 60, 30, 60, 30)]
    public void SameSizeMatchesTheLargestAndKeepsEachCorner(string action,
        double w0, double h0, double w1, double h1, double w2, double h2)
    {
        Rect[] result = AnnotationArrange.Apply(Boxes, Parse(action));

        Assert.Equal([new Size(w0, h0), new Size(w1, h1), new Size(w2, h2)], result.Select(box => box.Size));
        Assert.Equal(Boxes.Select(box => box.Location), result.Select(box => box.Location));
    }

    [Fact]
    public void DistributeNeedsThreeBoxes()
    {
        Rect[] two = [Boxes[0], Boxes[1]];

        Assert.Equal(two, AnnotationArrange.Apply(two, AnnotationArrange.Action.DistributeHorizontally));
        Assert.False(AnnotationArrange.Applies(AnnotationArrange.Action.DistributeVertically, 2));
        Assert.True(AnnotationArrange.Applies(AnnotationArrange.Action.AlignLeft, 2));
    }

    private static AnnotationArrange.Action Parse(string name) => Enum.Parse<AnnotationArrange.Action>(name);
}
