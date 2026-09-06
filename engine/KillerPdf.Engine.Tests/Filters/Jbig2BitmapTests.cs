using KillerPdf.Engine.Filters.Jbig2;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class Jbig2BitmapTests
{
    [Fact]
    public void Constructor_RejectsBitmapLargerThanActiveAllocationLimit()
    {
        using IDisposable allocationLimit = Jbig2Bitmap.BeginAllocationLimit(4);

        Jbig2Exception error = Assert.Throws<Jbig2Exception>(
            () => new Jbig2Bitmap(40, 1));

        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RejectsCombinedBitmapsLargerThanActiveAllocationLimit()
    {
        using IDisposable allocationLimit = Jbig2Bitmap.BeginAllocationLimit(8, 8);
        _ = new Jbig2Bitmap(32, 1);
        _ = new Jbig2Bitmap(24, 1);

        Jbig2Exception error = Assert.Throws<Jbig2Exception>(
            () => new Jbig2Bitmap(16, 1));

        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllocationLimit_RestoresPreviousLimitWhenNestedScopeEnds()
    {
        using IDisposable outerLimit = Jbig2Bitmap.BeginAllocationLimit(8);
        using (Jbig2Bitmap.BeginAllocationLimit(4))
        {
            Assert.Throws<Jbig2Exception>(() => new Jbig2Bitmap(40, 1));
        }

        var bitmap = new Jbig2Bitmap(40, 1);

        Assert.Equal(5, bitmap.ByteArray.Length);
    }
}
