using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfSurvivingTextTests
{
    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [6 0 R] >>";
    private const string Font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string Link = "<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest (SECRETCLAUSE) >>";

    [Fact]
    public void APhraseLeftInBookmarksDestinationsAndScriptsIsReported()
    {
        byte[] pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 7 0 R /Names << /Dests << /Names [(SECRETCLAUSE) [3 0 R /Fit]] >> /JavaScript << /Names [(a) 9 0 R] >> >> /Dests << /LEGACYSECRET [3 0 R /Fit] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page,
            Stream("BT /F1 12 Tf 10 100 Td (Ordinary words) Tj ET"),
            Font,
            Link,
            "<< /Type /Outlines /First 8 0 R /Last 8 0 R /Count 1 >>",
            "<< /Title (The SECRET plan) /Parent 7 0 R /Dest [3 0 R /Fit] >>",
            "<< /S /JavaScript /JS (app.alert('secret')) >>");

        PdfRedactionVerificationReport report = PdfContentRedaction.FindSurvivingText(pdf, ["SECRET"]);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Findings, finding => finding.Message.Contains("named destination", StringComparison.Ordinal));
        Assert.True(report.Findings.Count(finding => finding.Code == "ProhibitedObjectText") >= 4,
            string.Join(Environment.NewLine, report.Findings.Select(finding => finding.Message)));
    }

    [Fact]
    public void MatchCaseLeavesADifferentlyCasedWordAlone()
    {
        byte[] pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page.Replace(" /Annots [6 0 R]", ""),
            Stream("BT /F1 12 Tf 10 100 Td (SECRET agent) Tj ET"),
            Font);

        Assert.True(PdfContentRedaction.FindSurvivingText(pdf, ["Secret"], matchCase: true).Succeeded);
        Assert.False(PdfContentRedaction.FindSurvivingText(pdf, ["Secret"]).Succeeded);
    }

    [Fact]
    public void AShortNumberInsideAnEmbeddedFontProgramIsNotAMatch()
    {
        byte[] pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page.Replace(" /Annots [6 0 R]", ""),
            Stream("BT /F1 12 Tf 10 100 Td (Total) Tj ET"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /Fake /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Fake /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile2 7 0 R >>",
            Stream("\u0001\u0002 glyf 123 loca", "/Length1 16"));

        Assert.True(PdfContentRedaction.FindSurvivingText(pdf, ["123"]).Succeeded);
    }

    [Fact]
    public void RenamedDestinationsKeepTheirLinksAndLoseTheirNames()
    {
        byte[] pdf = Pdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Kids [7 0 R] >> >> /Dests << /LEGACYSECRET [3 0 R /Fit] >> /OpenAction << /S /GoTo /D /LEGACYSECRET >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            Page,
            Stream("BT /F1 12 Tf 10 100 Td (Ordinary words) Tj ET"),
            Font,
            Link,
            "<< /Limits [(SECRETCLAUSE) (SECRETCLAUSE)] /Names [(SECRETCLAUSE) [3 0 R /XYZ 0 100 0]] >>");

        PdfDocument renamed = PdfNamedDestinationAnonymizer.Apply(PdfDocument.Open(pdf));
        byte[] output = PdfDocumentWriter.WriteWithReport(renamed, new PdfDocumentWriteOptions { PruneUnreachableObjects = true }).Data.ToArray();

        Assert.True(PdfContentRedaction.FindSurvivingText(output, ["SECRET"]).Succeeded);
        PdfLinkInfo link = Assert.Single(PdfLinkReader.ReadPage(PdfDocument.Open(output), 0));
        Assert.Equal(0, link.DestinationPageIndex);
    }

    private static byte[] Pdf(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";
}
