using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Row ranges of a large paint run on several threads. A failure surfaces the way the serial row loop would
// have raised it, so callers that fall back on an expected exception still do.
public sealed class PdfRowFailureSelectionTests
{
    private const long LargePaint = 1_000_000;

    // Row range 0 fails last: it waits until row range 1 has thrown, so an aggregate lists range 1 first.
    private static void ForRanges(Func<Exception> first, Func<Exception> second)
    {
        using var secondThrown = new ManualResetEventSlim();
        PdfPageRenderer.ForEachRow(0, 8, LargePaint, (start, end) =>
        {
            if (start == 0)
            {
                secondThrown.Wait(TimeSpan.FromSeconds(20));
                throw first();
            }
            if (start == 1)
            {
                Exception error = second();
                secondThrown.Set();
                throw error;
            }
        }, 4, CancellationToken.None);
    }

    [Fact]
    public void MixedExpectedFailuresSurfaceTheFirstFailingRowRange()
    {
        var error = Assert.ThrowsAny<Exception>(() => ForRanges(
            () => new NotSupportedException("first"), () => new InvalidDataException("second")));

        Assert.IsType<NotSupportedException>(error);
    }

    // The serial loop raises the first failing row's exception whatever its type, so an engine bug in a lower
    // row range is not hidden behind a later range's refusal that callers treat as a fallback.
    [Fact]
    public void AnUnexpectedFailureInALowerRowRangeIsNotMaskedByALaterExpectedOne()
    {
        var error = Assert.ThrowsAny<Exception>(() => ForRanges(
            () => new NullReferenceException("first"), () => new NotSupportedException("second")));

        Assert.IsNotType<NotSupportedException>(error);
        Assert.Contains(error is AggregateException aggregate ? aggregate.InnerExceptions : [error],
            inner => inner is NullReferenceException);
    }

    [Fact]
    public void CancellationInAnyRowRangeSurfacesAsCancellation()
    {
        var error = Assert.ThrowsAny<Exception>(() => ForRanges(
            () => new NotSupportedException("first"), () => new OperationCanceledException()));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Fact]
    public void UnexpectedFailuresStayAnAggregate()
    {
        Assert.Throws<AggregateException>(() => ForRanges(
            () => new FormatException("a"), () => new KeyNotFoundException("b")));
    }
}
