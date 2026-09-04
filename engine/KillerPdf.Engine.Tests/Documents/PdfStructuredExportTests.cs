using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfStructuredExportTests
{
    [Fact]
    public void ExportsTextHtmlMarkdownAndStructuredJsonFromTheSamePageModel()
    {
        PdfDocument document = Document("BT /F1 12 Tf 10 30 Td (A & B) Tj ET");

        Assert.Equal("A & B", PdfStructuredExport.ToPlainText(document));
        string html = PdfStructuredExport.ToHtml(document);
        Assert.Contains("<p>A &amp; B</p>", html);
        Assert.Equal("# Page 1\r\n\r\nA & B", PdfStructuredExport.ToMarkdown(document));
        using JsonDocument json = JsonDocument.Parse(PdfStructuredExport.ToJson(document));
        JsonElement page = json.RootElement[0];
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal("A & B", page.GetProperty("lines")[0].GetProperty("text").GetString());
        Assert.Equal("Helvetica", page.GetProperty("lines")[0].GetProperty("runs")[0]
            .GetProperty("font").GetString());
    }

    [Fact]
    public void MarkdownEscapesFormattingCharactersFromSourceText()
    {
        PdfDocument document = Document("BT /F1 12 Tf 10 30 Td ([draft] *copy*) Tj ET");

        string markdown = PdfStructuredExport.ToMarkdown(document);

        Assert.Contains("\\[draft\\] \\*copy\\*", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportsEditableWordDocumentWithEscapedText()
    {
        PdfDocument document = Document("BT /F1 12 Tf 10 30 Td (A & B) Tj ET");

        byte[] docx = PdfStructuredExport.ToDocx(document);

        using var archive = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        ZipArchiveEntry documentEntry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("word/document.xml"));
        using var reader = new StreamReader(documentEntry.Open());
        string xml = reader.ReadToEnd();
        Assert.Contains("<w:t xml:space=\"preserve\">A &amp; B</w:t>", xml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsDuplicateAndOutOfRangePageSelections()
    {
        PdfDocument document = Document("BT /F1 12 Tf (A) Tj ET");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToJson(document, [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToHtml(document, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToDocx(document, [1]));
    }

    [Fact]
    public void ReportsContentThatTargetsCannotFullyRepresent()
    {
        PdfDocument document = Document("10 10 20 20 re f");

        PdfStructuredExportReport report = PdfStructuredExport.InspectLosses(
            document, PdfStructuredExportFormat.Html);
        using JsonDocument json = JsonDocument.Parse(report.ToJson());

        PdfStructuredExportFinding finding = Assert.Single(report.Findings);
        Assert.Equal("VectorContentNotExported", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.False(report.IsLossless);
        Assert.False(json.RootElement.GetProperty("isLossless").GetBoolean());
    }

    private static PdfDocument Document(string content)
    {
        string stream = $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 400] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", stream];
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
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
