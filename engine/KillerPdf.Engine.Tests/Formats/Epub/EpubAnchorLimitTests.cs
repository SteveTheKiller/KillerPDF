using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubAnchorLimitTests
{
    // The book keeps at most 100,000 link destinations. Reaching that never refuses a chapter: the chapter and every
    // later one is still drawn, the destinations kept stay usable, and one diagnostic says some were left out.
    [Fact]
    public void ReachingTheDestinationLimitKeepsTheChapterAndItsDestinationsAndReportsIt()
    {
        string ids = string.Concat(Enumerable.Range(0, 100_005).Select(index => $"<span id='a{index}'/>"));
        var book = new Book { Chapters = { Chapter("<p>A</p>" + ids), Chapter("<p id='later'>A</p>") } };

        var layout = Layout(book);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(2, layout.Pages.Count);
        // One notice for each chapter that lost a destination: the first overflowed, the second had none left.
        var limited = layout.ContentDiagnostics.Where(item => item.Reason == EpubOmissionReason.LimitExceeded).ToArray();
        Assert.Equal(new[] { 0, 1 }, limited.Select(item => item.SpineIndex));
        Assert.All(limited, item => Assert.Contains("100,000", item.Message));
        Assert.Equal(0, layout.ResolveNavigationTarget("OEBPS/text/c0.xhtml#a99999").PageIndex);
    }

    // The notice promises that a link to an id left out of the destination table goes to its chapter, so following one
    // lands on the chapter's first page instead of failing.
    [Fact]
    public void ALinkToAnIdPastTheDestinationLimitGoesToItsChapter()
    {
        string ids = string.Concat(Enumerable.Range(0, 100_000).Select(index => $"<span id='a{index}'/>"));
        var book = new Book { Chapters = { Chapter("<p>A</p>" + ids), Chapter("<p id='z1'>A</p>") } };

        var layout = Layout(book);

        Assert.Contains(layout.ContentDiagnostics, item => item.SpineIndex == 1 && item.Reason == EpubOmissionReason.LimitExceeded);
        var chapter = layout.ResolveNavigationTarget("OEBPS/text/c1.xhtml");
        var link = layout.ResolveNavigationTarget("OEBPS/text/c1.xhtml#z1");
        Assert.Equal(chapter.PageIndex, link.PageIndex);
        Assert.Equal(1, link.PageIndex);
    }
}
