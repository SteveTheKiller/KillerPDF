using System.Text;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using Xunit.Abstractions;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// Held-heap measurements need the process to themselves, so this collection runs after the parallel ones, alone.
[CollectionDefinition(nameof(EpubHeldMemoryCollection), DisableParallelization = true)]
public sealed class EpubHeldMemoryCollection;

[Collection(nameof(EpubHeldMemoryCollection))]
public sealed class EpubHeldMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public void ALongLaidOutBookHoldsAtMostThreeTimesItsUncompressedText()
    {
        // 40 chapters of 200 paragraphs, each 100 characters: 800,000 laid-out characters in 856,000 XHTML bytes
        // of paragraphs. Pages that kept every placement, paint and cluster held about 140 bytes per character.
        string paragraph = "<p>" + string.Join(" ", Enumerable.Repeat("AAAAAAAAA", 10)).PadRight(100, 'A') + "</p>";
        var book = new Book();
        for (int chapter = 0; chapter < 40; chapter++)
            book.Chapters.Add(Chapter(string.Concat(Enumerable.Repeat(paragraph, 200))));
        long xhtmlBytes = book.Chapters.Sum(chapter => (long)Encoding.UTF8.GetByteCount(chapter));
        byte[] package = book.Build();
        var options = Options(800, 1000);

        long before = GC.GetTotalMemory(forceFullCollection: true);
        var layout = EpubDocument.Open(package).CreateLayout(options);
        long held = GC.GetTotalMemory(forceFullCollection: true) - before;

        output.WriteLine($"Pages: {layout.Pages.Count}; XHTML bytes: {xhtmlBytes}; held bytes: {held}.");
        Assert.Empty(layout.ChapterDiagnostics);
        Assert.True(held <= 3 * xhtmlBytes, $"The laid-out book holds {held} bytes; the budget is {3 * xhtmlBytes}.");
        GC.KeepAlive(layout);
        GC.KeepAlive(package);
    }
}
