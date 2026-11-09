using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Opening a PDF moves its editable overlay tail into the editing layer before the first page shows. That step read the
// whole page tree again for every page, so a 10,000-page PDF sat on a blank window for about six minutes on SHREDDER
// and a 2,000-page one for about seven seconds. These pin that pages without KillerPDF overlays are no longer read
// that way; a page that carries an overlay is still counted with its own page tree read.
[Collection(nameof(PdfOverlayTailScaleTests))]
public sealed class PdfOverlayTailScaleTests
{
    private const int Pages = 3000;

    [Fact]
    public void ALongPdfWithACommentOnEveryPageGivesUpItsTailQuickly()
    {
        byte[] source = PlainPdf(Pages, commentOnEveryPage: true);

        var clock = Stopwatch.StartNew();
        OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(source);
        clock.Stop();

        // Ordinary notes are not KillerPDF overlays: nothing moves and the bytes stay the file's own.
        Assert.Empty(extracted.Records);
        Assert.Same(source, extracted.Bytes);
        // Reading 3,000 pages once takes well under a second; reading the tree again per page took about 20 s.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void ALongPdfWithNoCommentsGivesUpItsTailQuickly()
    {
        byte[] source = PlainPdf(Pages, commentOnEveryPage: false);

        var clock = Stopwatch.StartNew();
        OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(source);
        clock.Stop();

        Assert.Empty(extracted.Records);
        Assert.Same(source, extracted.Bytes);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
    }

    // A long document a user annotated in KillerPDF on every page: each page's comment is an overlay that moves to the
    // editing layer at open. Counting each page's annotations read the whole page tree again, about 11 s at 1,500 pages.
    [Fact]
    public void ALongPdfWithAnOverlayOnEveryPageGivesUpItsTailQuickly()
    {
        const int pages = 1500;
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-overlay-scale-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PlainPdf(pages, commentOnEveryPage: false));
            var annotations = new Dictionary<int, List<PageAnnotation>>();
            var dimensions = new Dictionary<int, (int w, int h)>();
            for (int page = 0; page < pages; page++)
            {
                annotations[page] = [new TextAnnotation { Content = $"Note {page + 1}", Position = new System.Windows.Point(10, 70),
                    Width = 160, Height = 25, PageIndex = page }];
                dimensions[page] = (200, 100);
            }
            PdfEngineBurn.Burn(path, annotations, dimensions);
            byte[] saved = File.ReadAllBytes(path);

            var clock = Stopwatch.StartNew();
            OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(saved);
            clock.Stop();

            Assert.Equal(pages, extracted.Records.Count);
            Assert.Equal("Note 1500", Assert.IsType<TextAnnotation>(extracted.Records[^1].Annotation).Content);
            // Linear work is under a second here; each removal checked every annotation in the file, about 3.4 s.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Opening a PDF read the measurements and then the overlay tail, each from its own read of the file and its own
    // parse, which copies every byte again: a 330 MB scan left about 1.3 GB of copies behind for the next collection.
    // Given the parse the measurements were read from, the tail takes no copy of the file.
    [Fact]
    public void TheTailReadsTheParseItIsGivenWithoutCopyingTheFile()
    {
        byte[] source = PlainPdf(200, commentOnEveryPage: true, padding: 8 << 20);
        var parsed = KillerPdf.Engine.Documents.PdfDocument.Open(source);
        PdfOverlayAnnotations.TakeEditableTail(source, parsed);   // warm the code paths

        long before = GC.GetAllocatedBytesForCurrentThread();
        OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(source, parsed);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(extracted.Records);
        Assert.Same(source, extracted.Bytes);
        Assert.True(allocated < source.Length / 4, $"Allocated {allocated:N0} bytes for a {source.Length:N0}-byte file.");
    }

    // A flat page tree of blank pages, each optionally carrying one sticky note, written by hand so the fixture
    // itself costs nothing to build.
    private static byte[] PlainPdf(int pages, bool commentOnEveryPage, int padding = 0)
    {
        var objects = new List<string>();
        int pagesObject = 2;
        int firstPage = 3;
        int firstNote = firstPage + pages;
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = new StringBuilder();
        for (int i = 0; i < pages; i++) kids.Append(CultureInfo.InvariantCulture, $"{firstPage + i} 0 R ");
        objects.Add($"<< /Type /Pages /Count {pages} /Kids [{kids}] >>");
        for (int i = 0; i < pages; i++)
            objects.Add(commentOnEveryPage
                ? $"<< /Type /Page /Parent {pagesObject} 0 R /MediaBox [0 0 200 100] /Annots [{firstNote + i} 0 R] >>"
                : $"<< /Type /Page /Parent {pagesObject} 0 R /MediaBox [0 0 200 100] >>");
        if (commentOnEveryPage)
            for (int i = 0; i < pages; i++)
                objects.Add($"<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents (Note {i + 1}) >>");
        // An unreferenced stream that stands in for a scan's page images: bytes a parse copies but never reads.
        if (padding > 0) objects.Add($"<< /Length {padding} >>\nstream\n{new string('x', padding)}\nendstream");

        using var stream = new MemoryStream();
        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n");
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = stream.Position;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }
}

// Timed tests run on their own, so the rest of the suite does not share their cores.
[CollectionDefinition(nameof(PdfOverlayTailScaleTests), DisableParallelization = true)]
public sealed class PdfOverlayTailScaleTestsCollection;
