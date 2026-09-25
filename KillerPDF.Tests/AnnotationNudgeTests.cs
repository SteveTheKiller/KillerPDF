using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class AnnotationNudgeTests
{
    [Fact]
    public void ClampDelta_LeavesMovementUnchangedInsidePage()
    {
        var constraints = new[]
        {
            new AnnotationNudge.Constraint(new Rect(10, 20, 30, 40), new Size(100, 100))
        };

        Assert.Equal(new Vector(1, -1), AnnotationNudge.ClampDelta(constraints, new Vector(1, -1)));
    }

    [Fact]
    public void ClampDelta_StopsAtTopLeftEdges()
    {
        var constraints = new[]
        {
            new AnnotationNudge.Constraint(new Rect(0, 0, 30, 40), new Size(100, 100))
        };

        Assert.Equal(default, AnnotationNudge.ClampDelta(constraints, new Vector(-1, -1)));
    }

    [Fact]
    public void ClampDelta_UsesOneSharedDeltaForMultiSelection()
    {
        var constraints = new[]
        {
            new AnnotationNudge.Constraint(new Rect(10, 10, 10, 10), new Size(100, 100)),
            new AnnotationNudge.Constraint(new Rect(90, 40, 10, 10), new Size(100, 100))
        };

        Assert.Equal(default, AnnotationNudge.ClampDelta(constraints, new Vector(1, 0)));
    }

    [Fact]
    public void ClampDelta_PinsOversizedBoundsToStartingEdge()
    {
        var constraints = new[]
        {
            new AnnotationNudge.Constraint(new Rect(0, 0, 120, 140), new Size(100, 100))
        };

        Assert.Equal(default, AnnotationNudge.ClampDelta(constraints, new Vector(1, 1)));
    }

    [Fact]
    public void ClampDelta_ReturnsZeroWithoutUsableConstraints()
    {
        Assert.Equal(default, AnnotationNudge.ClampDelta([], new Vector(1, 1)));
    }
}
