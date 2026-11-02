using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfDuplicateFieldNameTests
{
    private static string Field(string name, int top) =>
        $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /DA (/Helv 10 Tf 0 g) /P 3 0 R /Rect [10 {top} 110 {top + 20}] >>";

    // Written by hand because the builder refuses to author the repeated name.
    private static byte[] RepeatedNameForm()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R 6 0 R] >>",
            Field("Texte44", 10), Field("Texte44", 40), Field("other", 70)
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int table = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    [Fact]
    public void AFileThatAlreadyRepeatsAFieldNameStillSavesAnotherField()
    {
        byte[] bytes = RepeatedNameForm();

        byte[] saved = new PdfIncrementalPageEditor(PdfDocument.Open(bytes))
            .SetTextFieldValue("other", "typed").Build();

        Assert.True(saved.Length > bytes.Length);
    }

    [Fact]
    public void ARenameOntoAnExistingNameIsStillRefused()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 300, new PdfContentStreamBuilder())
            .AddTextField(0, "first", 10, 10, 100, 20)
            .AddTextField(0, "second", 10, 40, 100, 20)
            .Build());

        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(document).RenameFormField("second", "first").Build());
    }
}
