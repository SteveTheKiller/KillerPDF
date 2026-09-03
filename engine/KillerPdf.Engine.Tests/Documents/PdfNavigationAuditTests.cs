using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfNavigationAuditTests
{
    [Fact]
    public void ReportsUnsupportedAndUnresolvedLinks()
    {
        PdfDocument document = Document("[5 0 R 6 0 R]",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (javascript:alert) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest (missing) >>");

        IReadOnlyList<PdfNavigationFinding> findings = PdfNavigationAudit.Inspect(document);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, finding => finding.Message.Contains("URI scheme"));
        Assert.Contains(findings, finding => finding.Message.Contains("local page"));
        Assert.Equal([5, 6], findings.Select(finding => finding.SourceObjectNumber));
    }

    private static PdfDocument Document(string annots, params string[] extras)
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            $"<< /Type /Page /Parent 2 0 R /Annots {annots} >>", "<< >>", .. extras];
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
