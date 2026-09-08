using Xunit;

namespace KillerPdf.Engine.Tests;

public sealed class PdfScratchBufferPoolTests
{
    [Fact]
    public void Rent_ReusesConcurrentSameSizedBuffersWithinByteBudget()
    {
        var pool = new PdfScratchBufferPool<float>(1024);
        float[] first = pool.Rent(100), second = pool.Rent(100);
        Assert.NotSame(first, second);
        pool.Return(first);
        pool.Return(second);
        Assert.Equal(1024, pool.RetainedBytes);
        Assert.Same(second, pool.Rent(100));
        Assert.Same(first, pool.Rent(100));
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void Return_EvictsOldBuffersAndDoesNotRetainOversizedArrays()
    {
        var pool = new PdfScratchBufferPool<byte>(1024);
        byte[] first = pool.Rent(512), second = pool.Rent(1024);
        pool.Return(first);
        pool.Return(second);
        Assert.Equal(1024, pool.RetainedBytes);
        Assert.Same(second, pool.Rent(1024));
        Assert.NotSame(first, pool.Rent(512));
        byte[] oversized = pool.Rent(1025);
        Assert.Equal(1025, oversized.Length);
        pool.Return(oversized);
        Assert.Equal(0, pool.RetainedBytes);
    }

    [Fact]
    public void Return_ClearsSamplesWhenRequested()
    {
        var pool = new PdfScratchBufferPool<int>(1024);
        int[] samples = pool.Rent(10);
        Array.Fill(samples, 123);
        pool.Return(samples, clearArray: true);
        Assert.All(pool.Rent(10), sample => Assert.Equal(0, sample));
    }
}
