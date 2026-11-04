using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfMarkedContentCompatibilityTests
{
    [Fact]
    public void RecoveryExtractsVisibleTextWhenMarkedContentPropertiesAreMissing()
    {
        const string content = "/Span /Missing BDC BT /F1 12 Tf 20 50 Td (Visible text) Tj ET EMC";

        PdfPageContent page = new PdfPageContentReader(Document(content, true)).Read(0);

        Assert.Equal("Visible text", page.Text);
        Assert.Contains("Missing marked-content properties were ignored.", page.Diagnostics);
        Assert.Throws<FormatException>(() => new PdfPageContentReader(Document(content, false)).Read(0));
    }

    [Fact]
    public void RecoveryIgnoresOrphanMarkedContentClosersAndPreservesActualText()
    {
        const string content = "EMC /Span << /ActualText (word) >> BDC /Span BMC BT /F1 12 Tf 20 50 Td (ABC) Tj ET EMC EMC";

        PdfPageContent page = new PdfPageContentReader(Document(content, true)).Read(0);

        Assert.Equal("word", page.Text);
        Assert.Contains("An unmatched marked-content closing operator was ignored.", page.Diagnostics);
        Assert.Throws<FormatException>(() => new PdfPageContentReader(Document(content, false)).Read(0));
    }

    [Theory]
    [InlineData("/OC /Missing BDC EMC")]
    [InlineData("/Span BMC BT /F1 12 Tf 20 50 Td (Visible text) Tj ET")]
    public void RecoveryRetainsErrorsForMissingVisibilityPropertiesAndUnclosedSections(string content)
    {
        Assert.Throws<FormatException>(() => new PdfPageContentReader(Document(content, true)).Read(0));
    }

    private static PdfDocument Document(string content, bool recovery)
    {
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        byte[] bytes = Encoding.ASCII.GetBytes(pdf.ToString());
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }
}
