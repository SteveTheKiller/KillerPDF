using KillerPDF.Features;
using Xunit;

namespace KillerPDF.Tests;

// The bookkeeping behind reading an EPUB page's search highlight areas. Starting a new search stops those readings;
// if that search is then dropped with Esc, the pages on screen must be readable again at once.
public sealed class PageAreaReadsTests
{
    [Fact]
    public void APageWhoseReadingWasStoppedCanBeReadAgainBeforeTheStoppedReadingLands()
    {
        var reads = new PageAreaReads();
        Assert.True(reads.TryStart(4, out int first, out CancellationToken firstToken));

        reads.Cancel();

        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(reads.TryStart(4, out _, out CancellationToken second),
            "The page stayed marked as being read after its reading was stopped.");
        Assert.False(second.IsCancellationRequested);
    }

    [Fact]
    public void AStoppedReadingThatLandsLateDoesNotClearTheMarkOfTheReadingThatReplacedIt()
    {
        var reads = new PageAreaReads();
        Assert.True(reads.TryStart(4, out int stopped, out _));
        reads.Cancel();
        Assert.True(reads.TryStart(4, out int replacement, out _));

        Assert.False(reads.Finish(4, stopped));

        Assert.False(reads.TryStart(4, out _, out _));
        Assert.True(reads.Finish(4, replacement));
    }
}
