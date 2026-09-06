using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrReadingOrderMetricsTests
{
    [Fact]
    public void CompareAcceptsExpectedReadingOrder()
    {
        PdfOcrReadingOrderMetrics metrics = PdfOcrReadingOrderMetrics.Compare(
        [
            new(0, 0, 1),
            new(1, 1, 1),
            new(2, 2, 1)
        ]);

        Assert.Equal(3, metrics.ComparablePairCount);
        Assert.Equal(0, metrics.InversionCount);
        Assert.Equal(1, metrics.Accuracy);
    }

    [Fact]
    public void CompareCountsRecognizedOrderInversions()
    {
        PdfOcrReadingOrderMetrics metrics = PdfOcrReadingOrderMetrics.Compare(
        [
            new(0, 2, 1),
            new(1, 0, 1),
            new(2, 1, 1)
        ]);

        Assert.Equal(3, metrics.ComparablePairCount);
        Assert.Equal(2, metrics.InversionCount);
        Assert.Equal(1d / 3, metrics.Accuracy, 12);
    }

    [Fact]
    public void CompareTreatsFewerThanTwoMatchesAsOrdered()
    {
        Assert.Equal(1, PdfOcrReadingOrderMetrics.Compare([]).Accuracy);
        Assert.Equal(1, PdfOcrReadingOrderMetrics.Compare([new(0, 0, 1)]).Accuracy);
    }
}
