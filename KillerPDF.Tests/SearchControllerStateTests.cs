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

    internal sealed class Host(CurrentSearchSnapshot source) : KillerPDF.Features.ISearchHost
    {
        public TaskCompletionSource<string> NextText { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Tooltip { get; private set; }
        public Guid? NativeSearchRevisionId => source.NativeRevision?.Id;
        public System.Windows.Window Window => throw new NotSupportedException();
        public string? CurrentFile => "search-controller-state.djvu";
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