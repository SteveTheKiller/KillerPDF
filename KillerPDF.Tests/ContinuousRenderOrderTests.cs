using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ContinuousRenderOrderTests
{
    [Fact]
    public void Around_PrioritizesCenterThenAlternatesForwardAndBackward()
        => Assert.Equal([5, 6, 4, 7, 3], ContinuousRenderOrder.Around(5, 3, 7));

    [Fact]
    public void Around_ClampsCenterAndCoversEachPageOnce()
        => Assert.Equal([2, 3, 4], ContinuousRenderOrder.Around(-4, 2, 4));
}
