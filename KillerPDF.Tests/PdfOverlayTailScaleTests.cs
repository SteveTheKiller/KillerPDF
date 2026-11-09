using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Opening a PDF moves its editable overlay tail into the editing layer before the first page shows. That step read the
// whole page tree again for every page, so a 10,000-page PDF sat on a blank window for about six minutes on SHREDDER
// and a 2,000-page one for about seven seconds. These pin that the step stays linear in the page count.
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

    // A flat page tree of blank pages, each optionally carrying one sticky note, written by hand so the fixture
    // itself costs nothing to build.
    private static byte[] PlainPdf(int pages, bool commentOnEveryPage)
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
