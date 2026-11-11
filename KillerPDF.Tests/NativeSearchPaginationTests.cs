using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Searching a book that is still being paginated waits for the last page before it counts, and meanwhile the search
// bar says how far pagination has come. Chapter 1 of the test book makes the font resolver wait for the test.
public sealed class NativeSearchPaginationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task ASearchWaitingForPaginationShowsItsProgressThenTheWholeBooksCount()
    {
        using var gate = new ManualResetEventSlim();
        using var whole = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(), NativeOpenTests.Options());
        int expected = SearchService.Search(new CurrentSearchSnapshot([], []) { NativeRevision = whole.CaptureReadRevision() },
            "AAAAA", true, false, CancellationToken.None).TotalHits;
        using var owner = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(),
            NativeProgressiveOpenTests.Gated(gate), inBackground: true);
        var host = new Host(new CurrentSearchSnapshot([], []) { NativeRevision = owner.CaptureReadRevision() });
        var controller = new KillerPDF.Features.SearchController(host);

        controller.Run("AAAAA");
        string waiting = await host.NextText().WaitAsync(Patience);
        string? tooltip = host.Tooltip;
        bool countedEarly = host.Counts.Count > 0;
        gate.Set();
        string count = await host.Count.Task.WaitAsync(Patience);

        // Chapter 0 of 4 is paginated when the open returns; chapter 1 waits for the gate. The counter is narrow, so it
        // shows the share of chapters and its tooltip says the rest.
        Assert.Equal("waiting 25%", waiting);
        Assert.Equal("waiting for chapter 1 of 4", tooltip);
        // The note's tooltip does not outlive it.
        Assert.Null(host.Tooltip);
        Assert.False(countedEarly, "The search counted before the book was paginated.");
        Assert.Equal($"1 / {expected}", count);
    }

    [Fact]
    public async Task CancellingASearchThatWaitsForPaginationStopsItsProgressAndDeliversNothing()
    {
        using var gate = new ManualResetEventSlim();
        using var owner = NativeWorkingDocument.OpenEpub(NativeProgressiveOpenTests.Book(),
            NativeProgressiveOpenTests.Gated(gate), inBackground: true);
        var host = new Host(new CurrentSearchSnapshot([], []) { NativeRevision = owner.CaptureReadRevision() });
        var controller = new KillerPDF.Features.SearchController(host);
        controller.Run("AAAAA");
        await host.NextText().WaitAsync(Patience);

        controller.CancelPending();
        // The cancelled search ends by taking its waiting note down, which is the last thing it writes: wait for that
        // write instead of for a stretch of time. A search that kept running would never write it.
        await host.NoteTakenDown.Task.WaitAsync(Patience);
        int textsAfterCancel = host.Texts.Count;
        string last = host.Texts[^1];
        gate.Set();
        await owner.Pagination.WaitAsync(Patience);

        // The waiting note is taken down, no more progress is written and no count arrives.
        Assert.Equal("", last);
        Assert.Equal(textsAfterCancel, host.Texts.Count);
        Assert.Empty(host.Counts);
        Assert.False(controller.IsSearching);
    }

    private sealed class Host(CurrentSearchSnapshot source) : KillerPDF.Features.ISearchHost
    {
        private readonly object _gate = new();
        private TaskCompletionSource<string> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Texts { get; } = [];
        public List<string> Counts { get; } = [];
        public TaskCompletionSource<string> Count { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NoteTakenDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> NextText() { lock (_gate) return _next.Task; }
        public Guid? NativeSearchRevisionId => source.NativeRevision?.Id;
        public System.Windows.Window Window => throw new NotSupportedException();
        public string? CurrentFile => "native-search-pagination.epub";
        public long SearchRevision => 0;
        public int CurrentPageIndex => 0;
        public CurrentSearchSnapshot CaptureSearchSnapshot() => source;
        public string Loc(string key) => key switch
        {
            "Str_Search_WaitingShort" => "waiting {0}%",
            "Str_Search_WaitingForPages" => "waiting for chapter {0} of {1}",
            _ => key
        };
        public string? Tooltip { get; private set; }
        public void SetResultNote(string text, string? tooltip)
        {
            Tooltip = tooltip;
            SetResultText(text);
        }
        public void SetStatus(string text) { }
        public void GoToPage(int pageIdx) { }
        public void SetResultText(string text)
        {
            lock (_gate)
            {
                Texts.Add(text);
                if (text.Length == 0) NoteTakenDown.TrySetResult();
                _next.TrySetResult(text);
                _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        public void SetResultCount(string text, string? tooltip)
        {
            lock (_gate) Counts.Add(text);
            Count.TrySetResult(text);
        }
        public void ClearHighlights() { }
        public void RepaintHighlights() { }
        public bool IsCovered(int pageIdx, double left, double bottom, double right, double top) => false;
    }
}
