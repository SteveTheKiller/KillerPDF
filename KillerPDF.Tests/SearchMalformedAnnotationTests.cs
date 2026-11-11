using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SearchMalformedAnnotationTests
{
    // Page 1 carries ordinary text; page 2 lists an integer where an annotation dictionary belongs.
    private static byte[] PageWithMalformedAnnotsSibling()
    {
        string content = "BT /F1 12 Tf 1 0 0 1 20 80 Tm (Ordinary) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [42] >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // Plain text boxes are saved as native FreeText annotations, which is the text the overlay search pass reads back.
    [Fact]
    public void OneMalformedAnnotationDoesNotFailTheSearchOfTextBoxes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-search-malformed-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PageWithMalformedAnnotsSibling());
            var text = new TextAnnotation { PageIndex = 0, Content = "Replacement words", Position = new Point(20, 10), Width = 160, Height = 40 };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) }, measurementsAsAnnotations: true);
            byte[] saved = File.ReadAllBytes(path);

            var result = new SearchResult();
            SearchService.AddOverlayHits(result, saved, null, "Replacement", false, false, CancellationToken.None);
            Assert.Equal(1, result.TotalHits);
            Assert.Equal(1, SearchService.Search(saved, null, "Ordinary", false, false, CancellationToken.None).TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // The tolerant comment read skips the malformed annotation on page 2 and says so; the search result must carry
    // that page into its "pages not searched" list instead of dropping it.
    [Fact]
    public void ThePageOfASkippedMalformedAnnotationIsListedAsNotSearched()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-search-malformed-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, PageWithMalformedAnnotsSibling());
            var text = new TextAnnotation { PageIndex = 0, Content = "Replacement words", Position = new Point(20, 10), Width = 160, Height = 40 };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) }, measurementsAsAnnotations: true);
            byte[] saved = File.ReadAllBytes(path);

            var result = new SearchResult();
            SearchService.AddOverlayHits(result, saved, null, "Replacement", false, false, CancellationToken.None);

            Assert.Contains(1, result.UnreadablePages);
            Assert.Equal(1, result.TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
