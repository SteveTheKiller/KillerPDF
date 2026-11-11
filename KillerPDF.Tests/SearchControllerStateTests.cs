using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// What the search bar's result text and highlights do when a search ends badly, is superseded or finds too much.
public sealed class SearchControllerStateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AMatchLimitHitShowsALimitMessageInsteadOfAGenericSearchError()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(
            NativeSearchServiceTests.Page(new string('A', 100_001))));
        var host = new Host(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host);

        controller.Run("A");

        Assert.Equal("Str_Search_TooMany", await host.NextText.Task.WaitAsync(Patience));
        Assert.Equal("Str_Search_TooManyTT", host.Tooltip);
        Assert.False(controller.HasResults);
    }

    [Fact]
    public async Task ASearchErrorClearsTheTooltipTheEarlierResultLeft()
    {
        using var revision = new PdfCapturedReadRevision(PdfReadSnapshot.OpenDjvu(
            NativeSearchServiceTests.Page("A A", NativeSearchServiceTests.Zone(10, 20, 10, 10, 0, 1),
                NativeSearchServiceTests.Zone(30, 20, 10, 10, 2, 1))));
        var host = new Host(new CurrentSearchSnapshot([], []) { NativeRevision = revision });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("A");
        Assert.Equal("1 / 2", await host.NextText.Task.WaitAsync(Patience));
        Assert.NotNull(host.Tooltip);

        host.NextText = new(TaskCreationOptions.RunContinuationsAsynchronously);
        revision.Dispose();
        controller.Run("A");

        Assert.Equal("Str_Search_Error", await host.NextText.Task.WaitAsync(Patience));
        Assert.Null(host.Tooltip);
    }

    // Reading a hit's highlight areas lays out its page. A new search ends the results those pages belong to, so the
    // reading stops instead of running on behind it.
    [Fact]
    public async Task ANewSearchCancelsTheNativeHighlightReadingOfTheResultsItReplaces()
    {
        using var owner = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(), NativeOpenTests.Options());
        var host = new Host(new CurrentSearchSnapshot([], []) { NativeRevision = owner.CaptureReadRevision() });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("AAAAA");
        Assert.StartsWith("1 / ", await host.NextText.Task.WaitAsync(Patience));
        // The count lands before the current hit's page starts being read.
        using var deadline = new CancellationTokenSource(Patience);
        while (!controller.LocationToken.CanBeCanceled) await Task.Delay(1, deadline.Token);
        var reading = controller.LocationToken;
        Assert.False(reading.IsCancellationRequested);

        host.NextText = new(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Run("AAAAA");

        Assert.True(reading.IsCancellationRequested);
        await host.NextText.Task.WaitAsync(Patience);
    }

    // While a newer search runs, scrolling an old result's page onto the screen must not start reading that page's
    // highlight areas again: the results are about to be replaced, and the reading would only slow the new search.
    [Fact]
    public async Task ScrollingOldResultsOntoTheScreenDuringANewSearchDoesNotRestartTheirHighlightReading()
    {
        using var owner = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(), NativeOpenTests.Options());
        var host = new Host(new CurrentSearchSnapshot([], []) { NativeRevision = owner.CaptureReadRevision() });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("AAAAA");
        Assert.StartsWith("1 / ", await host.NextText.Task.WaitAsync(Patience));
        using var deadline = new CancellationTokenSource(Patience);
        while (!controller.LocationToken.CanBeCanceled) await Task.Delay(1, deadline.Token);

        // Hold the second search just before its results land, while it is still running.
        using var held = new ManualResetEventSlim();
        using var reached = new ManualResetEventSlim();
        host.Hold(held, reached, Environment.CurrentManagedThreadId);
        host.NextText = new(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Run("AAAAA");
        Assert.True(reached.Wait(Patience));
        Assert.True(controller.IsSearching);
        Assert.False(controller.LocationToken.CanBeCanceled);

        foreach (int page in controller.PagesWithResults.ToArray()) controller.TryGetPageRects(page, out _);

        bool restarted = controller.LocationToken.CanBeCanceled;
        held.Set();
        await host.NextText.Task.WaitAsync(Patience);
        Assert.False(restarted, "Showing an old result's page started reading its highlight areas again.");
    }

    internal sealed class Host(CurrentSearchSnapshot source) : KillerPDF.Features.ISearchHost
    {
        public TaskCompletionSource<string> NextText { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Tooltip { get; private set; }
        public Guid? NativeSearchRevisionId => source.NativeRevision?.Id;
        public System.Windows.Window Window => throw new NotSupportedException();
        private ManualResetEventSlim? _held, _reached;
        private int _owner;
        // From now on a read of CurrentFile off the given thread signals reached and waits for held.
        public void Hold(ManualResetEventSlim held, ManualResetEventSlim reached, int ownerThread)
        {
            _held = held; _reached = reached; _owner = ownerThread;
        }
        public string? CurrentFile
        {
            get
            {
                if (_held is { } held && Environment.CurrentManagedThreadId != _owner)
                {
                    _reached!.Set();
                    held.Wait(Patience);
                }
                return "search-controller-state.djvu";
            }
        }
        public long SearchRevision => 0;
        public int CurrentPageIndex => 0;
        public CurrentSearchSnapshot CaptureSearchSnapshot() => source;
        public string Loc(string key) => key;
        public void SetStatus(string text) { }
        public void GoToPage(int pageIdx) { }
        public void SetResultText(string text) => NextText.TrySetResult(text);
        public void SetResultNote(string text, string? tooltip)
        {
            Tooltip = tooltip;
            NextText.TrySetResult(text);
        }
        public void SetResultCount(string text, string? tooltip)
        {
            Tooltip = tooltip;
            NextText.TrySetResult(text);
        }
        public void ClearHighlights() { }
        public void RepaintHighlights() { }
        public bool IsCovered(int pageIdx, double left, double bottom, double right, double top) => false;
    }
}