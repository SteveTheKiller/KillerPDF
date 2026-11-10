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
        var draw = Task.Run(() => owner.Use(() => renderer, _ =>
        {
            drawing.Set();
            finish.Wait(TimeSpan.FromSeconds(10));
            return "thumbnail";
        }, CancellationToken.None));
        Assert.True(drawing.Wait(TimeSpan.FromSeconds(5)));

        var release = Task.Run(owner.Release);
        bool returned = release.Wait(TimeSpan.FromMilliseconds(500));
        bool closedEarly = renderer.Closed;
        finish.Set();
        Assert.Equal("thumbnail", await draw.WaitAsync(TimeSpan.FromSeconds(5)));
        await release.WaitAsync(TimeSpan.FromSeconds(5));

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
