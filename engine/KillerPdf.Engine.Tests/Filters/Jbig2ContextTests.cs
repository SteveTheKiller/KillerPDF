using KillerPdf.Engine.Filters.Jbig2;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class Jbig2ContextTests
{
    [Fact]
    public void ProbabilityUpdatesPreserveIndependentPredictedBits()
    {
        var context = new CX(128, 0);
        for (int index = 0; index < 128; index++)
        {
            context.Index = index;
            Assert.Equal(0, context.Cx);
            Assert.Equal(0, context.Mps);
            if ((index & 1) != 0) context.ToggleMps();
            context.Cx = index;
        }
        for (int index = 127; index >= 0; index--)
        {
            context.Index = index;
            Assert.Equal(index, context.Cx);
            Assert.Equal(index & 1, context.Mps);
            context.ToggleMps();
            Assert.Equal(index, context.Cx);
            Assert.Equal(1 - (index & 1), context.Mps);
            context.Cx = index + 128;
            Assert.Equal(index, context.Cx);
            Assert.Equal(1 - (index & 1), context.Mps);
        }
    }

    [Fact]
    public void CopiedContextsRetainBothStatesAndMutateIndependently()
    {
        var original = new CX(2, 1);
        original.Cx = 46;
        original.ToggleMps();
        var copy = original.Copy();
        Assert.Equal(1, copy.Index);
        Assert.Equal(46, copy.Cx);
        Assert.Equal(1, copy.Mps);
        copy.Cx = 3;
        copy.ToggleMps();
        Assert.Equal(46, original.Cx);
        Assert.Equal(1, original.Mps);
        original.Index = 0;
        original.Cx = 7;
        original.ToggleMps();
        copy.Index = 0;
        Assert.Equal(0, copy.Cx);
        Assert.Equal(0, copy.Mps);
    }
}
