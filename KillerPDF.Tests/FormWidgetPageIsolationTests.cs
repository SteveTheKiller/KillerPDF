using System.IO;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// 1.9.2 read each page's form widgets on its own, so a page with a damaged annotation list lost only its own fields.
// Building one form plan for the whole document must keep that: one bad page cannot hide the fields on every page.
public sealed class FormWidgetPageIsolationTests
{
    [Fact]
    public void APageWithADamagedAnnotationListDoesNotHideTheOtherPagesFormFields()
    {
        PdfDocument document = PdfDocument.Open(TwoPageForm());

        var first = PdfEngineIntegration.ReadPageFormWidgets(document, 0);
        var second = PdfEngineIntegration.ReadPageFormWidgets(document, 1);

        Assert.Equal("Name", Assert.Single(first).FieldName);
        Assert.Empty(second);
    }

    // Two unlisted combo boxes share the name Pick, so KillerPDF gives each its own key. Saving a choice whose display
    // text needs a Unicode font must find that font under the key the user edited.
    [Fact]
    public void ARenamedUnlistedChoiceFieldWithAUnicodeDisplayValueSaves()
    {
        string path = Path.Combine(Path.GetTempPath(), $"orphan-choice-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, Pdf(
            [
                "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Annots [4 0 R 5 0 R] >>",
                "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (Pick) /Opt [[(a) <FEFF20AC>]] /Rect [50 650 300 675] /DA (/Helv 12 Tf 0 g) >>",
                "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (Pick) /Opt [[(a) <FEFF20AC>]] /Rect [50 600 300 625] /DA (/Helv 12 Tf 0 g) >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
            ]));
            string key = PdfEngineIntegration.ReadPageFormWidgets(PdfDocument.Open(File.ReadAllBytes(path)), 0)[0].FieldName;

            PdfEngineIntegration.ApplyFormValues(path, new PdfEngineIntegration.FormEdits(
                new Dictionary<string, string>(), new Dictionary<string, string> { [key] = "a" },
                new Dictionary<string, IReadOnlyList<string>>(), new Dictionary<string, bool>(),
                new Dictionary<string, string>(), new Dictionary<string, double>()));

            var saved = PdfEngineIntegration.ReadPageFormWidgets(PdfDocument.Open(File.ReadAllBytes(path)), 0);
            Assert.Equal("a", saved.Single(widget => widget.FieldName == key).Value);
        }
        finally { File.Delete(path); }
    }

    private static byte[] TwoPageForm() => Pdf(
    [
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Annots [5 0 R] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Annots 5 >>",
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [50 650 300 675] /DA (/Helv 12 Tf 0 g) >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
    ]);

    private static byte[] Pdf(string[] bodies)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
