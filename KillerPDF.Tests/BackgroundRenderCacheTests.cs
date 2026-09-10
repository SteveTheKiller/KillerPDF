using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

public sealed class BackgroundRenderCacheTests
{
    [Fact]
    public async Task OverlappingWorkersKeepTheirSnapshotAfterDocumentReplacement()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var cache = new PdfBackgroundRenderCache();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<byte[]>[] workers = [];
        try
        {
            File.WriteAllBytes(path, Document(100, 200));
            using var baseline = PdfPageRenderSession.OpenEngineFirst(path, 128, 128);
            byte[] expected = baseline.RenderPage(0, includeFormFields: false).Pixels;
            var request = cache.Capture(path, 0);
            int acquired = 0;
            workers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                using var lease = request.Rent();
                if (Interlocked.Increment(ref acquired) == 2) ready.TrySetResult();
                await release.Task;
                return lease.Render(0, 128, 128).Pixels;
            })).ToArray();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cache.Clear();
            File.WriteAllBytes(path, Document(200, 100));
            var current = cache.Capture(path, 1);
            using (var replacement = current.Rent())
                Assert.Equal(128, replacement.Render(0, 128, 128).Width);
            release.TrySetResult();
            byte[][] rendered = await Task.WhenAll(workers);
            Assert.Equal(expected, rendered[0]);
            Assert.Equal(expected, rendered[1]);
            Assert.NotSame(rendered[0], rendered[1]);
            File.Delete(path);
            using var reused = current.Rent();
            Assert.Equal(128, reused.Render(0, 128, 128).Width);
        }
        finally
        {
            release.TrySetResult();
            try { await Task.WhenAll(workers); }
            finally { cache.Clear(); File.Delete(path); }
        }
    }

    [Fact]
    public void ActiveLeaseIsExclusiveAndIdleLeaseReusesSnapshot()
    {
        WithFile((path, cache) =>
        {
            var request = cache.Capture(path, 0);
            using var first = request.Rent();
            var expected = first.Render(0, 128, 256);
            File.Delete(path);
            Assert.Throws<FileNotFoundException>(() => request.Rent());
            first.Dispose();
            using var reused = request.Rent();
            Assert.Equal(expected.Pixels, reused.Render(0, 128, 256).Pixels);
            Assert.Equal(256, reused.Render(0, 256, 512).Width);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldLeaseCannotRepopulateCacheAfterInvalidation(bool clear)
    {
        WithFile((path, cache) =>
        {
            var oldRequest = cache.Capture(path, 0);
            using var old = oldRequest.Rent();
            Assert.Equal(64, old.Render(0, 128, 128).Width);
            File.WriteAllBytes(path, Document(200, 100));
            if (clear) cache.Clear();
            var current = cache.Capture(path, clear ? 0 : 1);
            old.Dispose();
            using var replacement = current.Rent();
            Assert.Equal(128, replacement.Render(0, 128, 128).Width);
        });
    }

    [Fact]
    public void LateOldRequestCannotTakeOrReplaceCurrentIdleSession()
    {
        WithFile((path, cache) =>
        {
            var oldRequest = cache.Capture(path, 0);
            var current = cache.Capture(path, 1);
            using (var prepared = current.Rent()) prepared.Render(0, 128, 128);
            File.WriteAllBytes(path, Document(200, 100));
            using (var late = oldRequest.Rent())
                Assert.Equal(128, late.Render(0, 128, 128).Width);
            File.Delete(path);
            using var reused = current.Rent();
            Assert.Equal(64, reused.Render(0, 128, 128).Width);
        });
    }

    [Fact]
    public void KeepsOnlyFirstReturnedIdleSession()
    {
        WithFile((path, cache) =>
        {
            var request = cache.Capture(path, 0);
            using var first = request.Rent();
            File.WriteAllBytes(path, Document(200, 100));
            using var second = request.Rent();
            first.Dispose();
            second.Dispose();
            File.Delete(path);
            using var reused = request.Rent();
            Assert.Equal(64, reused.Render(0, 128, 128).Width);
            Assert.Throws<FileNotFoundException>(() => request.Rent());
        });
    }

    [Fact]
    public void CancellationPreservesIdleSessionAndStopsRendering()
    {
        WithFile((path, cache) =>
        {
            var request = cache.Capture(path, 0);
            using (var prepared = request.Rent()) prepared.Render(0, 128, 128);
            File.Delete(path);
            var canceled = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => request.Rent(canceled));
            using var reused = request.Rent();
            Assert.Throws<OperationCanceledException>(() => reused.Render(0, 128, 128, canceled));
            Assert.Equal(64, reused.Render(0, 128, 128).Width);
        });
    }

    private static void WithFile(Action<string, PdfBackgroundRenderCache> action)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        var cache = new PdfBackgroundRenderCache();
        try { File.WriteAllBytes(path, Document(100, 200)); action(path, cache); }
        finally { cache.Clear(); File.Delete(path); }
    }

    private static byte[] Document(double width, double height) => new PdfDocumentBuilder()
        .AddPage(width, height, new PdfContentStreamBuilder().SetFillRgb(0.2, 0.7, 0.3)
            .Rectangle(10, 10, width / 2, height / 2).Fill()).Build();
}
