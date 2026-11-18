using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

// #466: a stale /Fields list beside widgets still on the page. The widget reader lists the
// unlisted widget under its own /T, so the editor has to find it under that same name.
public sealed class PdfUnlistedWidgetTests
{
    [Fact]
    public void RemovingAnUnlistedWidgetByItsReaderNameTakesItOffThePage()
    {
        var original = PdfDocument.Open(PageWithUnlistedWidget());
        Assert.Contains(PdfFormWidgetReader.ReadPage(original, 0), widget => widget.FieldName == "0");

        var reopened = PdfDocument.Open(new PdfIncrementalPageEditor(original).RemoveFormField("0").Build());

        Assert.Equal("Listed", Assert.Single(PdfFormWidgetReader.ReadPage(reopened, 0)).FieldName);
    }

    [Fact]
    public void SettingAnUnlistedWidgetValueByItsReaderNameWorks()
    {
        var original = PdfDocument.Open(PageWithUnlistedWidget());

        byte[] saved = new PdfIncrementalPageEditor(original).SetTextFieldValue("0", "Filled").Build();

        Assert.Contains("(Filled)", Encoding.ASCII.GetString(saved));
        Assert.Equal(2, PdfFormWidgetReader.ReadPage(PdfDocument.Open(saved), 0).Count);
    }

    [Fact]
    public void AnUnlistedCheckboxSharingTheNameIsRefusedLikeAListedOne()
    {
        var original = PdfDocument.Open(PageWithUnlistedWidget(withCheckbox: true));

        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(original).SetTextFieldValue("0", "Filled").Build());
    }

    [Fact]
    public void ListedFieldsStillWorkBesideAnUnlistedWidget()
    {
        var original = PdfDocument.Open(PageWithUnlistedWidget());

        var reopened = PdfDocument.Open(new PdfIncrementalPageEditor(original).RemoveFormField("Listed").Build());

        Assert.Equal("0", Assert.Single(PdfFormWidgetReader.ReadPage(reopened, 0)).FieldName);
    }

    private static byte[] PageWithUnlistedWidget(bool withCheckbox = false)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [{(withCheckbox ? "7 0 R " : "")}4 0 R 5 0 R] /Resources << >> >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Listed) /P 3 0 R /Rect [10 10 110 30] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (0) /P 3 0 R /Rect [10 40 110 60] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (0) /P 3 0 R /Rect [150 40 160 50] /AS /Off >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int table = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
