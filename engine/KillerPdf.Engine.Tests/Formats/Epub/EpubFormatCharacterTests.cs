using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// ZWNJ, LRM and RLM advance nothing and draw nothing; U+2028 and U+2029 are line and paragraph breaks. Characters that
// change joining or direction (ZWJ and the embedding, override and isolate controls) stay refused.
public sealed class EpubFormatCharacterTests
{
    private static (double Left, double Top, double Width)[] Placements(string body)
    {
        var layout = Layout(new Book { Chapters = { Chapter(body) } });
        Assert.Empty(layout.ChapterDiagnostics);
        return Assert.Single(layout.Pages).TextPlacements.Where(p => p.Width > 0).Select(p => (p.Left, Math.Round(p.Top, 3), Math.Round(p.Width, 3))).ToArray();
    }

    [Theory]
    [InlineData("\u200C")]
    [InlineData("\u200E")]
    [InlineData("\u200F")]
    public void ZeroAdvanceFormatCharactersKeepTheTextOnItsLine(string mark)
    {
        var plain = Placements("<p>AA</p>");

        var marked = Placements("<p>A" + mark + "A</p>");

        Assert.Equal(plain, marked);
    }

    [Fact]
    public void ALineSeparatorBreaksTheLineLikeABreakElement()
    {
        Assert.Equal(Placements("<p>A<br/>A</p>"), Placements("<p>A\u2028A</p>"));
    }

    [Fact]
    public void AParagraphSeparatorBreaksTheLineTwice()
    {
        Assert.Equal(Placements("<p>A<br/><br/>A</p>"), Placements("<p>A\u2029A</p>"));
    }

    [Fact]
    public void ALineSeparatorInPreformattedTextBreaksTheLine()
    {
        Assert.Equal(Placements("<pre>A\nA</pre>"), Placements("<pre>A\u2028A</pre>"));
    }

    [Theory]
    [InlineData("\u200D")]
    [InlineData("\u202A")]
    [InlineData("\u202B")]
    [InlineData("\u202C")]
    [InlineData("\u202D")]
    [InlineData("\u202E")]
    [InlineData("\u2066")]
    [InlineData("\u2067")]
    [InlineData("\u2068")]
    [InlineData("\u2069")]
    public void JoiningAndDirectionControlsStayRefused(string mark)
    {
        var layout = Layout(new Book { Chapters = { Chapter("<p>A" + mark + "A</p>") } });

        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(EpubOmissionReason.UnsupportedScript, diagnostic.Reason);
    }
}
