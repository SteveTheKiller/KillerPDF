using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The page list's thumbnail renderer is let go from a cancellation callback, which runs on the UI thread when a page
// edit or reload rebuilds the list. Letting go must never wait for a thumbnail that is still being drawn.
public sealed class ThumbnailSessionOwnerTests
{
    [Fact]
    public async Task ReleasingWhileAThumbnailIsDrawnReturnsAtOnceAndClosesTheRendererWhenTheDrawEnds()
    {
        var owner = new ThumbnailSessionOwner<Renderer>();
        var renderer = new Renderer();
        using var drawing = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        // The draw runs on its own thread, so a busy thread pool cannot hold back its start; the timeout only ends
        // the wait if the draw never starts.
        var draw = Task.Factory.StartNew(() => owner.Use(() => renderer, _ =>
        {
            drawing.Set();
            finish.Wait();
            return "thumbnail";
        }, CancellationToken.None), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(drawing.Wait(TimeSpan.FromSeconds(30)));

        // The draw is held open until Release returns, so Release can only return by not waiting for the draw: there
        // is no race against a clock. The timeout only ends the wait if Release is stuck behind the draw for good.
        var release = Task.Run(owner.Release);
        bool returned;
        try
        {
            await release.WaitAsync(TimeSpan.FromSeconds(30));
            returned = true;
        }
        catch (TimeoutException) { returned = false; }
        bool closedEarly = renderer.Closed;
        finish.Set();
        Assert.Equal("thumbnail", await draw.WaitAsync(TimeSpan.FromSeconds(60)));
        await release.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(returned, "Release waited for the thumbnail being drawn.");
        Assert.False(closedEarly, "The renderer was closed while it was drawing.");
        Assert.True(renderer.Closed, "The renderer stayed open after the draw it was released during.");
    }

    [Fact]
    public void ADrawAfterAReleaseOpensANewRenderer()
    {
        var owner = new ThumbnailSessionOwner<Renderer>();
        var opened = new List<Renderer>();
        Renderer Open() { var renderer = new Renderer(); opened.Add(renderer); return renderer; }

        owner.Use(Open, _ => "first", CancellationToken.None);
        owner.Release();
        owner.Use(Open, _ => "second", CancellationToken.None);

        Assert.Equal(2, opened.Count);
        Assert.True(opened[0].Closed);
        Assert.False(opened[1].Closed);
    }

    private sealed class Renderer : IDisposable
    {
        public bool Closed { get; private set; }
        public void Dispose() => Closed = true;
    }
}
