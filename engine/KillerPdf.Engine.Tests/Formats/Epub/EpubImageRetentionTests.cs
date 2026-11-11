using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Item A (KP-B3 acceptance, Monte Cristo): a long illustrated book never loses images to a book-wide budget.
public sealed class EpubImageRetentionTests
{
    [Fact]
    public void ABookWithMoreDecodedImagesThanTheOldBookBudgetKeepsEveryImage()
    {
        // An 800 by 1000 page with 2px margins decodes at most twice its 796 by 996 box, so a 1592 by 1992 RGB image
        // stays full size: 9,513,792 bytes each. Twenty-nine of them pass the old 256 MiB (268,435,456 byte) book budget.
        const int count = 29;
        var png = EpubImageCompatibilityTests.Png(1592, 1992, 2);
        var book = new Book
        {
            Chapters = { Chapter(string.Concat(Enumerable.Range(0, count).Select(index => $"<p><img src='../images/i{index}.png' alt=''/></p>"))) },
            ExtraManifest = string.Concat(Enumerable.Range(0, count).Select(index => $"<item id=\"i{index}\" href=\"images/i{index}.png\" media-type=\"image/png\"/>"))
        };
        for (int index = 0; index < count; index++) book.Resources.Add(($"images/i{index}.png", png));

        var layout = Layout(book, 800, 1000);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Empty(layout.ContentDiagnostics);
        Assert.Equal(count, layout.Pages.Sum(page => page.Glyphs.OfType<NativeImagePaint>().Count()));
        // The last image still carries its own samples (the fixture fills every channel with 128).
        var last = layout.Pages.Last(page => page.Glyphs.OfType<NativeImagePaint>().Any()).Glyphs.OfType<NativeImagePaint>().Single();
        Assert.Equal((1592, 1992, (byte)128), (last.Image.Width, last.Image.Height, last.Image.Samples[^1]));
    }

    // The decode size follows each use: a picture first drawn small in a narrow table cell is still drawn sharp when the
    // same file later fills the page width.
    [Fact]
    public void ALaterLargerUseOfThePictureIsNotDrawnFromTheFirstSmallDecode()
    {
        var book = new Book
        {
            Chapters = { Chapter("<table style='width:40px'><tr><td style='width:40px'><img src='../images/p.png' alt=''/></td></tr></table><p><img src='../images/p.png' alt=''/></p>") },
            ExtraManifest = "<item id=\"p\" href=\"images/p.png\" media-type=\"image/png\"/>"
        };
        book.Resources.Add(("images/p.png", EpubImageCompatibilityTests.Png(300, 300, 2)));

        var layout = Layout(book, 400, 600);

        Assert.Empty(layout.ChapterDiagnostics);
        var paints = layout.Pages.SelectMany(page => page.Glyphs.OfType<NativeImagePaint>()).OrderBy(paint => paint.Top).ToArray();
        Assert.Equal(2, paints.Length);
        Assert.True(paints[0].Width < 60, "The first use sits in the narrow cell.");
        // Drawn at 300 by 300 CSS pixels, so the decode must hold all 300 source columns.
        Assert.Equal(300, paints[1].Width, 3);
        Assert.Equal(300, paints[1].Image.Width);
    }
}
