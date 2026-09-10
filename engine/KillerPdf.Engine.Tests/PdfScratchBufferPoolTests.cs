using Xunit;

namespace KillerPdf.Engine.Tests;

public sealed class PdfScratchBufferPoolTests
{
    [Fact]
    public void Return_CountLimitDoesNotLetSmallBuffersDisplaceLargeWorkingSet()
    {
        var pool = new PdfScratchBufferPool<byte>(8 * 1024 * 1024);
        byte[][] small = Enumerable.Range(0, 64).Select(_ => pool.Rent(16)).ToArray();
        foreach (byte[] buffer in small) pool.Return(buffer);
        byte[][] large = Enumerable.Range(0, 32).Select(_ => pool.Rent(128 * 1024)).ToArray();
        foreach (byte[] buffer in large) pool.Return(buffer);

        var expected = new HashSet<byte[]>(large);
        for (int index = 0; index < large.Length; index++)
            Assert.True(expected.Remove(pool.Rent(128 * 1024)));
        Assert.Empty(expected);
        Assert.InRange(pool.RetainedBytes, 0, 8 * 1024 * 1024);
    }

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
