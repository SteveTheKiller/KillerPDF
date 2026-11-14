using System.Runtime.CompilerServices;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// WPF keeps an unused collection view over a page list's rows until its own binding cleanup runs, so the rows of a
// closed window can outlive it. A row must not lead from there back to the viewer that owned the list: a cancelled list
// lets go of its owner even while something still holds its rows.
public sealed class PageListThumbnailsTests
{
    [Fact]
    public void ACancelledListLetsGoOfItsOwnerWhileItsRowsAreStillHeld()
    {
        var (rows, owner) = WireAndCancel();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(owner.IsAlive, "The rows of a cancelled page list still reach the viewer that owned it.");
        GC.KeepAlive(rows);
    }

    [Fact]
    public void ACancelledListIsMarkedUnfinishedAndItsRowsStopAsking()
    {
        using var cts = new CancellationTokenSource();
        var rows = Rows(3);
        PageThumbnailVm[]? marked = null;
        PageListThumbnails.Wire(rows, new ThumbnailSessionOwner<Session>(), static (_, _) => null,
            cache => marked = cache, cts.Token);
        Assert.All(rows, row => Assert.NotNull(row.RequestThumbnail));

        cts.Cancel();

        Assert.Same(rows, marked);
        Assert.All(rows, row => Assert.Null(row.RequestThumbnail));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PageThumbnailVm[] Rows, WeakReference Owner) WireAndCancel()
    {
        var rows = Rows(3);
        var owner = WireToNewOwner(rows, out var cts);
        cts.Cancel();
        cts.Dispose();
        return (rows, owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference WireToNewOwner(PageThumbnailVm[] rows, out CancellationTokenSource cts)
    {
        var owner = new Owner();
        cts = new CancellationTokenSource();
        PageListThumbnails.Wire(rows, new ThumbnailSessionOwner<Session>(), static (_, _) => null,
            owner.MarkIncomplete, cts.Token);
        return new WeakReference(owner);
    }

    private static PageThumbnailVm[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(index => new PageThumbnailVm(index, "owner.pdf", null))];

    // Stands in for the viewer, which holds its window and everything in it.
    private sealed class Owner
    {
        internal int Marked { get; private set; }
        internal void MarkIncomplete(PageThumbnailVm[] _) => Marked++;
    }

    private sealed class Session : IDisposable
    {
        public void Dispose() { }
    }
}
