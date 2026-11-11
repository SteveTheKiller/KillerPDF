using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

// A shape dictionary search follows INCL chunks through the bundle's include components. A component that holds many
// INCL chunks to the next, and no Djbz, must be searched once however many paths lead to it.
public sealed class DjvuNestedIncludeTests
{
    [Fact]
    public void ABundleWithADirectoryFromTheTestWriterResolvesItsIncludes()
    {
        var document = DjvuDocument.Open(DjvuTestStreams.NestedInclBundle(levels: 3, fanOut: 2));
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == "IncludedComponentUnsupported");
        Assert.Equal("page.djvu", Assert.Single(document.Pages).ComponentId);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(20)]
    public void EachIncludeComponentIsSearchedOnceWhateverNumberOfPathsLeadToIt(int fanOut)
    {
        const int levels = 7;
        var document = DjvuDocument.Open(DjvuTestStreams.NestedInclBundle(levels, fanOut));
        // Without the fix fan-out 20 never finishes; the token turns that into a failure instead of a hang.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var error = Assert.Throws<InvalidDataException>(() => document.RenderPage(0, timeout.Token));

        Assert.Contains("requires a shared dictionary the document does not contain", error.Message, StringComparison.Ordinal);
        Assert.True(document.IncludeComponentVisits <= levels,
            $"{document.IncludeComponentVisits} component visits for {levels} components");
    }
}
