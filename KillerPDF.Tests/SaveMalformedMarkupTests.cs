using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

// A save must not fail over damage the save does not touch (a broken annotation entry on another page), and
// must not fail over markup the editing layer cannot hold. Both saved in 1.9.2.
public sealed class SaveMalformedMarkupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "killerpdf-save-malformed-" + Guid.NewGuid().ToString("N"));

    public SaveMalformedMarkupTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    // Two 200 x 200 pages. Objects 1 to 5 are the catalog, the page tree, page 1, a content stream and page 2;
    // `page2Annots` is the raw text of page 2's /Annots value and `extraObject` an optional object 6.
    // Every object number up to 99 that is not written is a free xref entry.
    private string TwoPagePdf(string page2Annots, string? extraObject)
    {
        var objects = new SortedDictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
            [4] = "<< /Length 0 >>\nstream\n\nendstream",
            [5] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> /Annots "
                + page2Annots + " >>"
        };
        if (extraObject is not null) objects[6] = extraObject;
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach ((int number, string body) in objects)
        {
            offsets[number] = text.Length;
            text.Append(number).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 100\n0000000000 65535 f \n");
        for (int number = 1; number < 100; number++)
            text.Append(offsets.TryGetValue(number, out int offset)
                ? $"{offset:D10} 00000 n \n" : "0000000000 00001 f \n");
        text.Append("trailer\n<< /Size 100 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text.ToString()));
        return path;
    }

    private static Dictionary<int, (int w, int h)> Dims() => new() { [0] = (400, 400), [1] = (400, 400) };

    public static TheoryData<string, string?> DamagedPageTwo => new()
    {
        { "[null]", null },
        { "[99 0 R]", null },
        { "[6 0 R]", "<< /Type /Annot /Contents (note) /Rect [0 0 10 10] >>" }
    };

    [Theory]
    [MemberData(nameof(DamagedPageTwo))]
    public void AddingATextBoxSavesWhenAnotherPageHasAMalformedAnnotation(string annots, string? extra)
    {
        string path = TwoPagePdf(annots, extra);
        // The fixture is damaged the way the finding describes: reading every comment fails.
        Assert.ThrowsAny<Exception>(() => PdfCommentReader.Read(PdfDocument.Open(File.ReadAllBytes(path)), true));

        PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
        {
            [0] = [new TextAnnotation { PageIndex = 0, Content = "x", Position = new Point(40, 40), Width = 120, Height = 40 }]
        }, Dims(), measurementsAsAnnotations: true);

        byte[] saved = File.ReadAllBytes(path);
        PdfDocument reopened = PdfDocument.Open(saved);
        Assert.Equal(1, PdfCommentReader.ReadPageAnnotationCount(reopened, 0));
        Assert.Contains("/FreeText", Encoding.Latin1.GetString(saved));
    }

    [Theory]
    [MemberData(nameof(DamagedPageTwo))]
    public void ATextBoxSavedBesideAMalformedAnnotationReopensEditable(string annots, string? extra)
    {
        string path = TwoPagePdf(annots, extra);

        PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
        {
            [0] = [new TextAnnotation { PageIndex = 0, Content = "x", Position = new Point(40, 40), Width = 120, Height = 40 }]
        }, Dims(), measurementsAsAnnotations: true);

        var records = PdfOverlayAnnotations.Read(PdfDocument.Open(File.ReadAllBytes(path)));
        var text = Assert.IsType<TextAnnotation>(Assert.Single(records).Annotation);
        Assert.Equal("x", text.Content);
        Assert.Equal(0, text.PageIndex);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("7")]
    public void AddingATextBoxSavesOnThePageWhoseOwnAnnotationListIsDamaged(string annots)
    {
        string path = TwoPagePdf(annots, null);

        PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
        {
            [1] = [new TextAnnotation { PageIndex = 1, Content = "x", Position = new Point(40, 40), Width = 120, Height = 40 }]
        }, Dims(), measurementsAsAnnotations: true);

        // The text box is on page 2 either as a saved annotation or as page content; the file reopens.
        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))).Count);
    }
}
