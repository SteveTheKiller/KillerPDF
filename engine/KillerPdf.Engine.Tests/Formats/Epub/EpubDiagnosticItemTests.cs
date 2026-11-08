using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// A content diagnostic names the package-relative path of the one item it left out, so a host can show it.
public sealed class EpubDiagnosticItemTests
{
    [Fact]
    public void ContentDiagnosticsNameTheOmittedItemAndOnlyThatItem()
    {
        var book = new Book
        {
            Chapters = { Chapter("<p>A&#x2720;<img src='../images/a.gif' alt=''/><img src='../images/none.png' alt=''/>"
                + "<img src='https://example.invalid/far.png' alt=''/><a href='lost.xhtml#x'>A</a></p>") },
            ExtraManifest = "<item id=\"gif\" href=\"images/a.gif\" media-type=\"image/gif\"/>"
        };
        book.Resources.Add(("images/a.gif", Encoding.ASCII.GetBytes("GIF89a")));

        var layout = Layout(book);

        Assert.Empty(layout.ChapterDiagnostics);
        var items = layout.ContentDiagnostics.ToDictionary(diagnostic => diagnostic.Reason == EpubOmissionReason.MissingGlyph ? "glyph"
            : diagnostic.Message.Contains("a.gif") ? "gif" : diagnostic.Message.Contains("none.png") ? "png"
            : diagnostic.Message.Contains("far.png") ? "far" : "link", diagnostic => diagnostic.ItemHref);
        Assert.Equal(5, items.Count);
        Assert.Null(items["glyph"]);
        Assert.Equal("OEBPS/images/a.gif", items["gif"]);
        Assert.Equal("OEBPS/images/none.png", items["png"]);
        // A reference outside the package has no package path.
        Assert.Null(items["far"]);
        Assert.Equal("OEBPS/text/lost.xhtml", items["link"]);
    }

    [Fact]
    public void AChapterRefusalHasNoSingleItem()
    {
        var layout = Layout(new Book { Chapters = { Chapter("<p>A</p><script>x</script>") } });

        Assert.Null(Assert.Single(layout.ChapterDiagnostics).ItemHref);
    }
}
