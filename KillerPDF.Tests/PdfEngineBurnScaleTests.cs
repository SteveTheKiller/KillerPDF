using System.Diagnostics;
using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using Xunit;

namespace KillerPDF.Tests;

// Saving a long PDF marked up on every page read the whole page tree again for every page to count its annotations,
// so a 3,000-page document with a highlight and a pen stroke per page took about 12 s to save on SHREDDER where 1.9.2
// took under a second. These pin that a save's work grows with the page count, not with its square.
[Collection(nameof(PdfEngineBurnScaleTests))]
public sealed class PdfEngineBurnScaleTests
{
    private const int Pages = 3000;

    [Fact]
    public void SavingALongPdfWithMarkupOnEveryPageIsQuick()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-burn-scale-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, BlankPdf(Pages));
            var annotations = new Dictionary<int, List<PageAnnotation>>();
            var dimensions = new Dictionary<int, (int w, int h)>();
            for (int page = 0; page < Pages; page++)
            {
                annotations[page] =
                [
                    new HighlightAnnotation { PageIndex = page, Bounds = new Rect(50, 50, 200, 20) },
                    new InkAnnotation { PageIndex = page, Points = [new Point(60, 200), new Point(120, 260), new Point(180, 210)] }
                ];
                dimensions[page] = (816, 1056);
            }

            var clock = Stopwatch.StartNew();
            PdfEngineBurn.Burn(path, annotations, dimensions, measurementsAsAnnotations: true);
            clock.Stop();

            // 1.9.2 saved this in about 0.7 s and the fix in about 1 s (5 s on a fully loaded machine); counting each
            // page's annotations with its own page tree read took about 13 s.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Edited text keeps its editing layer in the saved file. Each edited page was written as its own few incremental
    // revisions, so a PDF with text edited on a few hundred pages passed the reader's 1,024-revision limit and the
    // save failed outright, and 200 edited pages already took about 9 s.
    [Fact]
    public void SavingTextEditedOnHundredsOfPagesKeepsEveryEdit()
    {
        const int pages = 600;
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-burn-edits-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, BlankPdf(pages));
            var annotations = new Dictionary<int, List<PageAnnotation>>();
            var dimensions = new Dictionary<int, (int w, int h)>();
            for (int page = 0; page < pages; page++)
            {
                annotations[page] = [new TextAnnotation { PageIndex = page, PairId = $"edit-{page}", Content = $"Edited line {page + 1}",
                    Position = new Point(60, 400), Width = 220, Height = 30 }];
                dimensions[page] = (816, 1056);
            }

            var clock = Stopwatch.StartNew();
            PdfEngineBurn.Burn(path, annotations, dimensions, measurementsAsAnnotations: true);
            clock.Stop();

            var edits = PdfOverlayAnnotations.Read(KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(path)))
                .Select(record => record.Annotation).OfType<TextAnnotation>().ToList();
            Assert.Equal(pages, edits.Count);
            Assert.Equal("Edited line 600", edits.Single(edit => edit.PageIndex == pages - 1).Content);
            // 1.9.2 burned 750 such pages flat in about 3 s; the fix saves these 600 in about 4 s (13 s on a fully
            // loaded machine), where 200 pages alone took about 9 s before.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25), $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Opening a saved PDF moves its edited text back into the editing layer. Each edited page's check read every
    // page's information and the whole page tree again, so 3,000 edited pages took about 31 s to reopen.
    [Fact]
    public void ReopeningAPdfWithTextEditedOnEveryPageIsQuick()
    {
        const int pages = 1500;
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-reopen-edits-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, BlankPdf(pages));
            var annotations = new Dictionary<int, List<PageAnnotation>>();
            var dimensions = new Dictionary<int, (int w, int h)>();
            for (int page = 0; page < pages; page++)
            {
                annotations[page] = [new TextAnnotation { PageIndex = page, PairId = $"edit-{page}", Content = $"Edited line {page + 1}",
                    Position = new Point(60, 400), Width = 220, Height = 30 }];
                dimensions[page] = (816, 1056);
            }
            PdfEngineBurn.Burn(path, annotations, dimensions, measurementsAsAnnotations: true);
            byte[] saved = File.ReadAllBytes(path);

            var clock = Stopwatch.StartNew();
            OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(saved);
            clock.Stop();

            Assert.Equal(pages, extracted.Records.Select(record => record.Annotation).OfType<TextAnnotation>().Count());
            // Reading 1,500 pages once is well under a second; the per-page rereads took about 8 s here.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"Took {clock.Elapsed.TotalMilliseconds:F0} ms.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static byte[] BlankPdf(int pages)
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 0; page < pages; page++) builder.AddBlankPage(612, 792);
        return builder.Build();
    }
}
