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
        // Four times the pages should take about four or five times as long. The per-page tree reads made it about
        // nine times as long (1.5 s at 750 pages, 13.9 s at 3,000), and a ratio holds up on a loaded machine where a
        // fixed time budget does not.
        TimeMarkupSave(50);   // warms up the code paths so the first measured save is not mostly start-up cost
        TimeSpan quarter = TimeMarkupSave(Pages / 4);
        TimeSpan full = TimeMarkupSave(Pages);
        double growth = full.TotalMilliseconds / Math.Max(1, quarter.TotalMilliseconds);
        Assert.True(growth < 7,$"750 pages took {quarter.TotalMilliseconds:F0} ms and 3,000 took {full.TotalMilliseconds:F0} ms.");
        Assert.True(full < TimeSpan.FromSeconds(20), $"Took {full.TotalMilliseconds:F0} ms.");
    }

    private static TimeSpan TimeMarkupSave(int pages)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-burn-scale-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, BlankPdf(pages));
            var annotations = new Dictionary<int, List<PageAnnotation>>();
            var dimensions = new Dictionary<int, (int w, int h)>();
            for (int page = 0; page < pages; page++)
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
            return clock.Elapsed;
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

    // Each text box's look was written as a one-page PDF of its own, read back and copied in, so every box carried
    // its own copy of the font. Saving 3,000 notes took 16 to 18 s, about 850 MiB more memory and an 87 MB file,
    // where 1.9.2 took 12 to 15 s and about 580 MiB. Boxes in one font now share one embedded font program, and the
    // same save takes 6 to 10 s, about 150 to 200 MiB and a 12 MB file. Counting font programs holds on a busy
    // machine where a time budget does not.
    [Fact]
    public void TextBoxesInOneFontShareOneEmbeddedFont()
    {
        const int pages = 12;
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-burn-fonts-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, BlankPdf(pages));
            var annotations = new Dictionary<int, List<PageAnnotation>>();
            var dimensions = new Dictionary<int, (int w, int h)>();
            for (int page = 0; page < pages; page++)
            {
                annotations[page] =
                [
                    new TextAnnotation { PageIndex = page, Content = $"Note on page {page + 1}", FontName = "Arial",
                        Position = new Point(60, 400), Width = 220, Height = 30 },
                    new TextAnnotation { PageIndex = page, Content = "Second note", FontName = "Arial",
                        Position = new Point(60, 500), Width = 220, Height = 30 }
                ];
                dimensions[page] = (816, 1056);
            }

            PdfEngineBurn.Burn(path, annotations, dimensions, measurementsAsAnnotations: true);

            var saved = KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(path));
            Assert.Equal(pages * 2, PdfOverlayAnnotations.Read(saved).Count);
            int fontPrograms = 0;
            foreach (var entry in saved.CrossReferences)
            {
                if (entry.Value.Type is not (KillerPdf.Engine.CrossReference.PdfCrossReferenceEntryType.InUse
                    or KillerPdf.Engine.CrossReference.PdfCrossReferenceEntryType.Compressed)) continue;
                if (saved.Resolve(entry.Key) is KillerPdf.Engine.Objects.PdfDictionary descriptor
                    && descriptor.Any(item => item.Key.ValueAsLatin1() is "FontFile" or "FontFile2" or "FontFile3"))
                    fontPrograms++;
            }
            Assert.Equal(1, fontPrograms);
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
