using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ViewportRenderQueueTests
{
    [Fact]
    public async Task CancelingOneRequestDoesNotCancelTheSharedPump()
    {
        var queue = new ViewportRenderQueue();
        using var source = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        var first = queue.Enqueue("first", 0, 16, token =>
        {
            entered.Set();
            Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(60)));
            token.ThrowIfCancellationRequested();
            return 0;
        }, source.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(60)));
        var next = queue.Enqueue("next", 0, 16, _ => 7);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(7, await next.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(8, await queue.Enqueue("later", 0, 16, _ => 8).WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task NavigationRunsBeforePrefetchWithoutConcurrentRasterization()
    {
        var queue = new ViewportRenderQueue();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int caller = Environment.CurrentManagedThreadId;
        var first = queue.Enqueue("active", 10, 16, _ =>
        {
            Assert.NotEqual(caller, Environment.CurrentManagedThreadId);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)));
            return 0;
        });
        // The pump starts on the thread pool, which the rest of the suite can keep busy for over ten seconds on the
        // lab VM; these waits only guard against a hang, so they are long.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(60)));
        var order = new List<int>();
        var prefetch = queue.Enqueue("prefetch", 1000, 16, _ => { order.Add(2); return 2; });
        var navigation = queue.Enqueue("navigation", 0, 16, _ => { order.Add(1); return 1; });
        release.Set();
        await Task.WhenAll(first, prefetch, navigation).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public async Task SupersededZoomAndExcessPrefetchNeverRun()
    {
        var queue = new ViewportRenderQueue(2, 32);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var active = queue.Enqueue("active", 0, 16, _ => { entered.Set(); release.Wait(); return 0; });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(60)));
        var oldZoom = queue.Enqueue<int>("zoom", 0, 16, _ => throw new Exception("Old zoom ran"));
        var zoom = queue.Enqueue("zoom", 0, 16, _ => 2);
        var distant = queue.Enqueue<int>("distant", 1000, 16, _ => throw new Exception("Distant page ran"));
        var visible = queue.Enqueue("visible", 10, 16, _ => 3);
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldZoom);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => distant);
        Assert.Equal(2, await zoom);
        Assert.Equal(3, await visible);
        await active;
    }

    [Fact]
    public async Task TabSwitchCancelsRunningAndQueuedWorkAndAllowsNewDocument()
    {
        var queue = new ViewportRenderQueue();
        using var entered = new ManualResetEventSlim();
        var active = queue.Enqueue("old", 0, 16, token =>
        {
            entered.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(60));
            token.ThrowIfCancellationRequested();
            return 0;
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(60)));
        var pending = queue.Enqueue<int>("neighbor", 10, 16, _ => throw new Exception("Old document ran"));
        queue.CancelAll();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(7, await queue.Enqueue("new", 0, 16, _ => 7).WaitAsync(TimeSpan.FromSeconds(60)));
    }
}
