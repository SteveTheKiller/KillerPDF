using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class DeferredManualZoomTests
{
    [Theory]
    [InlineData(0, 0.055)]
    [InlineData(double.PositiveInfinity, 5)]
    public void InternalBaseAcceptsUnderflowAndOverflowBeforeReplayingCommands(double initial, double expected)
    {
        var pending = new DeferredManualZoom(2);
        pending.QueueScale(1.1);
        Assert.Equal(expected, pending.ResolveFromInternal(initial, 0.05, 5), 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(double.NegativeInfinity)]
    public void InternalBaseRejectsInvalidValues(double invalid)
    {
        var pending = new DeferredManualZoom(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => pending.ResolveFromInternal(invalid, 0.05, 5));
    }

    [Fact]
    public void DeferredFitBaseReplaysOrderedCommandsWithEachClamp()
    {
        var pending = new DeferredManualZoom(2);
        pending.QueueStep(-0.15);
        pending.QueueStep(-0.15);
        pending.QueueStep(0.15);
        pending.QueueScale(1.1);

        Assert.Equal(0.22, pending.ResolveFromInternal(0.10, 0.05, 5), 12);
        Assert.Equal(2, pending.TrueZoom);
    }

    [Fact]
    public void MixedCommandsRetainTheirOrderAndIntermediateUpperClamp()
    {
        var pending = new DeferredManualZoom(2);
        pending.QueueScale(10);
        pending.QueueStep(-0.15);
        pending.QueueScale(0.1);
        pending.QueueStep(0.15);

        Assert.True(pending.HasOperations);
        Assert.Equal(0.635, pending.Resolve(1, 0.05, 5), 12);
        Assert.Equal(2, pending.TrueZoom);
    }

    [Fact]
    public void WheelUnderflowAndOverflowClampBeforeFollowingCommands()
    {
        var pending = new DeferredManualZoom(2);
        pending.QueueScale(0);
        pending.QueueStep(0.15);
        pending.QueueScale(double.PositiveInfinity);
        pending.QueueStep(-0.15);

        Assert.Equal(4.85, pending.Resolve(7.68, 0.05, 5), 12);
    }

    [Fact]
    public void AbsoluteBaseRetainsFractionalPrecisionAndCanResolveAgainForAnotherFactor()
    {
        var pending = new DeferredManualZoom(2.1234567890123457);

        Assert.False(pending.HasOperations);
        Assert.Equal(2.1234567890123457, pending.TrueZoom);
        Assert.Equal(1.0617283945061728, pending.Resolve(2, 0.05, 5));
        Assert.Equal(2.1234567890123457, pending.Resolve(1, 0.05, 5));
    }

    [Fact]
    public void RepeatedResolutionDoesNotConsumeOrApplyCommandsTwice()
    {
        var pending = new DeferredManualZoom(2);
        pending.QueueStep(0.15);
        pending.QueueScale(2);

        Assert.Equal(4.3, pending.Resolve(1, 0.05, 5), 12);
        Assert.Equal(4.3, pending.Resolve(1, 0.05, 5), 12);
        Assert.True(pending.HasOperations);
    }

    [Theory]
    [InlineData(double.Epsilon, double.MaxValue, 0.05)]
    [InlineData(double.MaxValue, double.Epsilon, 5)]
    public void ExtremeFiniteRatiosClampWithoutLosingTheAbsoluteBase(double ratio, double factor, double expected)
    {
        var pending = new DeferredManualZoom(ratio);
        Assert.Equal(expected, pending.Resolve(factor, 0.05, 5));
        Assert.Equal(ratio, pending.TrueZoom);
    }

    [Fact]
    public void EqualPositiveBoundsRemainValidAcrossExtremeCommands()
    {
        var pending = new DeferredManualZoom(2);
        pending.QueueScale(double.PositiveInfinity);
        pending.QueueScale(0);
        pending.QueueStep(-double.MaxValue);
        Assert.Equal(0.5, pending.Resolve(1, 0.5, 0.5));
    }

    [Theory]
    [InlineData(double.NaN, 0.05, 5)]
    [InlineData(double.PositiveInfinity, 0.05, 5)]
    [InlineData(0, 0.05, 5)]
    [InlineData(-1, 0.05, 5)]
    [InlineData(1, double.NaN, 5)]
    [InlineData(1, double.PositiveInfinity, 5)]
    [InlineData(1, 0, 5)]
    [InlineData(1, -1, 5)]
    [InlineData(1, 0.05, double.NaN)]
    [InlineData(1, 0.05, double.PositiveInfinity)]
    [InlineData(1, 0.05, double.NegativeInfinity)]
    [InlineData(1, 5, 0.05)]
    public void ResolveRejectsInvalidFactorAndBounds(double factor, double min, double max)
    {
        var pending = new DeferredManualZoom(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => pending.Resolve(factor, min, max));
        if (factor == 1)
            Assert.Throws<ArgumentOutOfRangeException>(() => pending.ResolveFromInternal(2, min, max));
    }

    [Theory]
    [InlineData(double.NaN, true)]
    [InlineData(-1, true)]
    [InlineData(double.NegativeInfinity, true)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    public void InvalidCommandsAreRejectedWithoutQueuing(double invalid, bool scale)
    {
        var pending = new DeferredManualZoom(2);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (scale) pending.QueueScale(invalid);
            else pending.QueueStep(invalid);
        });
        Assert.False(pending.HasOperations);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void AbsoluteBaseMustBeFiniteAndPositive(double invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeferredManualZoom(invalid));
    }

    [Fact]
    public void ScaleStartsFromTheClampedInternalZoom()
    {
        var pending = new DeferredManualZoom(0.2);
        pending.QueueScale(1.1);

        Assert.Equal(0.055, pending.Resolve(7.68, 0.05, 5), 12);
    }

    [Fact]
    public void DeferredStepsClampEachCommandBeforeTheNextCommand()
    {
        var pending = new DeferredManualZoom(2);
        pending.QueueStep(-0.15);
        pending.QueueStep(-0.15);
        pending.QueueStep(0.15);

        Assert.Equal(0.20, pending.Resolve(7.68, 0.05, 5), 12);
    }
}
