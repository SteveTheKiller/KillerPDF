using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfFormActionInspectorTests
{
    [Fact]
    public void InspectReportsFieldAndWidgetActionsWithoutExposingScriptSource()
    {
        PdfDocument document = Document(
            "[5 0 R]",
            "<< /FT /Tx /T (customer) /Kids [6 0 R] /AA << /V 7 0 R /C << /S /GoTo /D [3 0 R /Fit] >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [0 0 10 10] /A << /S /SubmitForm /F (https://example.test) >> /AA << /Fo << /S /URI /URI (https://example.test/help) >> >> >>",
            "<< /S /JavaScript /JS (secret source) >>");

        IReadOnlyList<PdfFormActionInfo> actions = PdfFormActionInspector.Inspect(document);

        Assert.Equal(4, actions.Count);
        Assert.Contains(actions, item => item.FieldName == "customer" && item.Trigger == "V"
            && item.ActionType == "JavaScript" && item.Safety == PdfFormActionSafety.Unsafe
            && item.SourceObjectNumber == 7 && item.Target is null);
        Assert.Contains(actions, item => item.Trigger == "C" && item.ActionType == "GoTo"
            && item.Safety == PdfFormActionSafety.Supported);
        Assert.Contains(actions, item => item.Trigger == "A" && item.ActionType == "SubmitForm"
            && item.Safety == PdfFormActionSafety.Unsafe);
        Assert.Contains(actions, item => item.Trigger == "Fo" && item.ActionType == "URI"
            && item.Safety == PdfFormActionSafety.RequiresReview
            && item.Target == "https://example.test/help");

        string json = PdfFormActionInspector.ExportJson(document);
        Assert.DoesNotContain("secret source", json);
        using JsonDocument report = JsonDocument.Parse(json);
        Assert.Equal("Unsafe", report.RootElement[0].GetProperty("Safety").GetString());
    }

    [Fact]
    public void InspectBoundsCircularNextActionChains()
    {
        PdfDocument document = Document(
            "[5 0 R]",
            "<< /FT /Tx /T (total) /A 6 0 R >>",
            "<< /S /ResetForm /Next 7 0 R >>",
            "<< /S /UnknownAction /Next 6 0 R >>");

        IReadOnlyList<PdfFormActionInfo> actions = PdfFormActionInspector.Inspect(document);

        Assert.Equal(["ResetForm", "UnknownAction", "Circular"],
            actions.Select(item => item.ActionType));
        Assert.Equal(PdfFormActionSafety.Unsafe, actions[^1].Safety);
    }

    [Fact]
    public void InspectReturnsEmptyListWithoutAnAcroForm()
    {
        Assert.Empty(PdfFormActionInspector.Inspect(Document("[]")));
    }

    [Fact]
    public void ClearFormFieldActionsRemovesFieldAndWidgetActionsWithoutExecutingThem()
    {
        PdfDocument source = Document(
            "[5 0 R]",
            "<< /FT /Tx /T (customer) /Kids [6 0 R] /AA << /V 7 0 R >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /Rect [0 0 10 10] /A << /S /SubmitForm /F (https://example.test) >> >>",
            "<< /S /JavaScript /JS (secret source) >>");

        byte[] updatedBytes = new PdfIncrementalPageEditor(source)
            .ClearFormFieldActions("customer")
            .Build();
        PdfDocument updated = PdfDocument.Open(updatedBytes);

        Assert.Empty(PdfFormActionInspector.Inspect(updated));
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(source)
                .ClearFormFieldActions("missing")
                .Build());
    }

    private static PdfDocument Document(string fields, params string[] extras)
    {
        string[] objects =
        [
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields {fields} >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< >>",
            .. extras
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
