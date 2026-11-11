using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubEmbeddedStyleBudgetTests
{
    // The 8 MiB stylesheet budget charges a sheet once however many chapters repeat it, embedded or linked.
    [Fact]
    public void AnEmbeddedSheetRepeatedInEveryChapterIsChargedOnce()
    {
        // Three copies of 3 MiB would use 9 MiB; as one distinct sheet they use 3.
        string head = "<style>/*" + new string('x', 3 * 1024 * 1024) + "*/</style>";
        var book = new Book { Chapters = { Chapter("<p>A</p>", head), Chapter("<p>A</p>", head), Chapter("<p>A</p>", head) } };

        var layout = Layout(book);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(3, layout.Pages.Count);
    }

    [Fact]
    public void DifferentEmbeddedSheetsAreStillEachCharged()
    {
        var chapters = new[] { 'x', 'y', 'z' }.Select(letter =>
            Chapter("<p>A</p>", "<style>/*" + new string(letter, 3 * 1024 * 1024) + "*/</style>"));
        var book = new Book();
        book.Chapters.AddRange(chapters);

        var layout = Layout(book);

        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(2, diagnostic.SpineIndex);
        Assert.Equal(EpubOmissionReason.LimitExceeded, diagnostic.Reason);
    }
}
