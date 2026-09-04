using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfStructuredExportTests
{
    [Fact]
    public void UnifiedExportUsesCorrectedReviewedOcrWithoutChangingSource()
    {
        byte[] source = new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .AddBlankPage(300, 400).Build();
        byte[] original = source.ToArray();
        PdfDocument document = PdfDocument.Open(source);
        var review = new PdfOcrReview([
            new PdfOcrWord("word-1", 0, 0, "wrong", "wrong",
                new PdfContentBounds(10, 20, 70, 32), 0.4, "en")
        ]).Correct("word-1", "A");
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));

        string exported = Encoding.UTF8.GetString(PdfStructuredExport.Export(
            document, PdfStructuredExportFormat.PlainText,
            ocrReview: review, ocrFont: font));

        Assert.Equal("A", exported);
        Assert.Equal(original, source);
        Assert.Throws<ArgumentNullException>(() => PdfStructuredExport.Export(
            document, PdfStructuredExportFormat.Json, ocrReview: review));
        Assert.Throws<ArgumentException>(() => PdfStructuredExport.Export(
            document, PdfStructuredExportFormat.Json, ocrFont: font));
    }

    [Fact]
    public void BatchExportIncludesReviewedOcrAndKeepsSourceIsolated()
    {
        byte[] source = new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .AddBlankPage(300, 400).Build();
        byte[] original = source.ToArray();
        var review = new PdfOcrReview([
            new PdfOcrWord("word-1", 0, 0, "wrong", "wrong",
                new PdfContentBounds(10, 20, 30, 32), 0.4, "en")
        ]).Correct("word-1", "A");
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));

        PdfStructuredExportBatchResult result = Assert.Single(
            PdfStructuredExportBatchRunner.Run([
                new PdfStructuredExportBatchItem(
                    "scan.pdf", source, ocrReview: review, ocrFont: font)
            ], PdfStructuredExportFormat.PlainText));

        Assert.True(result.Succeeded);
        Assert.Equal("A", Encoding.UTF8.GetString(
            Assert.IsType<ReadOnlyMemory<byte>>(result.Data).Span));
        Assert.Equal(original, source);
        Assert.Throws<ArgumentException>(() =>
            new PdfStructuredExportBatchItem("scan.pdf", source, ocrReview: review));
    }

    [Fact]
    public void ExportsTextHtmlMarkdownAndStructuredJsonFromTheSamePageModel()
    {
        PdfDocument document = Document("BT /F1 12 Tf 10 30 Td (A & B) Tj ET");

        Assert.Equal("A & B", PdfStructuredExport.ToPlainText(document));
        string html = PdfStructuredExport.ToHtml(document);
        Assert.Contains("<p><span style=\"font-family:&quot;Helvetica&quot;;font-size:12pt\">A &amp; B</span></p>", html);
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
    public void HtmlAndJsonPreserveUriAndLocalPageLinks()
    {
        PdfDocument document = PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .AddPage(300, 400, ReadOnlyMemory<byte>.Empty)
            .AddPage(300, 400, ReadOnlyMemory<byte>.Empty)
            .AddUriLink(0, 10, 20, 80, 15, "https://example.test/a?x=1&y=2",
                contents: "Help & support")
            .AddPageLink(0, 10, 50, 80, 15, 1, contents: "Next page")
            .Build());

        string html = PdfStructuredExport.ToHtml(document);
        Assert.Contains("<section id=\"page-1\" data-page=\"1\">", html,
            StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.test/a?x=1&amp;y=2\">Help &amp; support</a>",
            html, StringComparison.Ordinal);
        Assert.Contains("href=\"#page-2\">Next page</a>", html, StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(PdfStructuredExport.ToJson(document));
        JsonElement links = json.RootElement[0].GetProperty("links");
        Assert.Equal("https://example.test/a?x=1&y=2", links[0].GetProperty("uri").GetString());
        Assert.Equal("Help & support", links[0].GetProperty("description").GetString());
        Assert.Equal(2, links[1].GetProperty("destinationPage").GetInt32());
        Assert.Equal(10, links[1].GetProperty("bounds").GetProperty("left").GetDouble());
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
        Assert.Contains("<w:rFonts w:ascii=\"Helvetica\" w:hAnsi=\"Helvetica\"/>", xml,
            StringComparison.Ordinal);
        Assert.Contains("<w:sz w:val=\"24\"/>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExportsEditableSpreadsheetWithPageLineAndEscapedText()
    {
        PdfDocument document = Document("BT /F1 12 Tf 10 30 Td (A & B) Tj ET");

        byte[] xlsx = PdfStructuredExport.ToXlsx(document);

        using var archive = new ZipArchive(new MemoryStream(xlsx), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("xl/workbook.xml"));
        ZipArchiveEntry sheetEntry = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("xl/worksheets/sheet1.xml"));
        using var reader = new StreamReader(sheetEntry.Open());
        string xml = reader.ReadToEnd();
        Assert.Contains("<t>Page</t>", xml, StringComparison.Ordinal);
        Assert.Contains("<t>1</t>", xml, StringComparison.Ordinal);
        Assert.Contains("<t xml:space=\"preserve\">A &amp; B</t>", xml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExportsEditablePresentationWithOneSlidePerSelectedPage()
    {
        PdfDocument document = Document("BT /F1 12 Tf 10 30 Td (A & B) Tj ET");

        byte[] pptx = PdfStructuredExport.ToPptx(document);

        using var archive = new ZipArchive(new MemoryStream(pptx), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("ppt/presentation.xml"));
        Assert.NotNull(archive.GetEntry("ppt/slideMasters/slideMaster1.xml"));
        ZipArchiveEntry slide = Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("ppt/slides/slide1.xml"));
        using var reader = new StreamReader(slide.Open());
        string xml = reader.ReadToEnd();
        Assert.Contains("<a:t>A &amp; B</a:t>", xml, StringComparison.Ordinal);
        Assert.Contains("sz=\"1200\"><a:latin typeface=\"Helvetica\"/>", xml,
            StringComparison.Ordinal);
        Assert.Contains("<a:xfrm>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationCentersMixedPageSizesWithoutStretchingGeometry()
    {
        PdfDocument document = PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .AddPage(300, 400, new KillerPdf.Engine.Authoring.PdfContentStreamBuilder()
                .BeginText().SetFont(KillerPdf.Engine.Authoring.PdfStandardFont.Helvetica, 12)
                .MoveText(10, 30).ShowLatin1Text("Portrait").EndText())
            .AddPage(400, 200, new KillerPdf.Engine.Authoring.PdfContentStreamBuilder()
                .BeginText().SetFont(KillerPdf.Engine.Authoring.PdfStandardFont.Helvetica, 12)
                .MoveText(10, 30).ShowLatin1Text("Landscape").EndText())
            .Build());

        using var archive = new ZipArchive(
            new MemoryStream(PdfStructuredExport.ToPptx(document)), ZipArchiveMode.Read);
        using var reader = new StreamReader(Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry("ppt/slides/slide2.xml")).Open());
        string xml = reader.ReadToEnd();

        Assert.Contains("<a:t>Landscape</a:t>", xml, StringComparison.Ordinal);
        XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
        XElement offset = XDocument.Parse(xml).Descendants(drawing + "off").Last();
        long x = long.Parse(Assert.IsType<XAttribute>(offset.Attribute("x")).Value);
        long y = long.Parse(Assert.IsType<XAttribute>(offset.Attribute("y")).Value);
        Assert.InRange(x, 220_000, 260_000);
        Assert.True(y > 3_800_000);
    }

    [Fact]
    public void RejectsDuplicateAndOutOfRangePageSelections()
    {
        PdfDocument document = Document("BT /F1 12 Tf (A) Tj ET");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToJson(document, [0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToHtml(document, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToDocx(document, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToXlsx(document, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfStructuredExport.ToPptx(document, [1]));
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
        Assert.Contains("Structured export: known losses", report.ToText(),
            StringComparison.Ordinal);
        Assert.Contains("Page 1: VectorContentNotExported (count 1)", report.ToText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsLinksOnlyWhenTheTargetOmitsThem()
    {
        PdfDocument document = PdfDocument.Open(new KillerPdf.Engine.Authoring.PdfDocumentBuilder()
            .AddBlankPage(300, 400)
            .AddUriLink(0, 10, 20, 80, 15, "https://example.test")
            .Build());

        PdfStructuredExportFinding finding = Assert.Single(
            PdfStructuredExport.InspectLosses(
                document, PdfStructuredExportFormat.WordDocument).Findings);

        Assert.Equal("LinkContentNotExported", finding.Code);
        Assert.Equal(1, finding.Count);
        Assert.True(PdfStructuredExport.InspectLosses(
            document, PdfStructuredExportFormat.Html).IsLossless);
        Assert.True(PdfStructuredExport.InspectLosses(
            document, PdfStructuredExportFormat.Json).IsLossless);
    }

    [Fact]
    public void ReportsTextDirectionLossExceptForStructuredJson()
    {
        PdfDocument document = Document(
            "BT /F1 12 Tf 0 1 -1 0 30 20 Tm (Vertical) Tj ET");

        PdfStructuredExportFinding finding = Assert.Single(
            PdfStructuredExport.InspectLosses(
                document, PdfStructuredExportFormat.Html).Findings);

        Assert.Equal("TextDirectionNotPreserved", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.Equal(1, finding.Count);
        Assert.True(PdfStructuredExport.InspectLosses(
            document, PdfStructuredExportFormat.Json).IsLossless);
    }

    [Fact]
    public void BatchExportIsolatesFailuresAndProducesADataSafeReport()
    {
        byte[] valid = DocumentBytes(
            "BT /F1 12 Tf 10 30 Td (first) Tj ET 10 10 20 20 re f");
        var items = new[]
        {
            new PdfStructuredExportBatchItem("first.pdf", valid),
            new PdfStructuredExportBatchItem("broken.pdf", "not a PDF"u8.ToArray()),
            new PdfStructuredExportBatchItem("last.pdf",
                DocumentBytes("BT /F1 12 Tf 10 30 Td (last) Tj ET"))
        };

        PdfStructuredExportBatchReport report =
            PdfStructuredExportBatchRunner.RunReport(items, PdfStructuredExportFormat.PlainText);

        Assert.Equal((3, 2, 1, 0, 0), (report.TotalDocumentCount, report.SucceededCount,
            report.FailedCount, report.CanceledCount, report.UnprocessedCount));
        Assert.Equal("first", Encoding.UTF8.GetString(
            Assert.IsType<ReadOnlyMemory<byte>>(report.Results[0].Data).Span));
        Assert.True(report.Results[1].Error?.Length > 0);
        Assert.Equal("last", Encoding.UTF8.GetString(
            Assert.IsType<ReadOnlyMemory<byte>>(report.Results[2].Data).Span));

        string jsonText = report.ToJson();
        using JsonDocument json = JsonDocument.Parse(jsonText);
        Assert.Equal("first.pdf", json.RootElement.GetProperty("results")[0]
            .GetProperty("sourceName").GetString());
        Assert.Equal("first.txt", report.Results[0].OutputName);
        PdfStructuredExportFinding loss = Assert.Single(
            Assert.IsType<PdfStructuredExportReport>(report.Results[0].LossReport).Findings);
        Assert.Equal("VectorContentNotExported", loss.Code);
        Assert.Null(report.Results[1].LossReport);
        Assert.Equal("first.txt", json.RootElement.GetProperty("results")[0]
            .GetProperty("outputName").GetString());
        Assert.False(json.RootElement.GetProperty("results")[0]
            .GetProperty("isLossless").GetBoolean());
        Assert.Equal("VectorContentNotExported", json.RootElement.GetProperty("results")[0]
            .GetProperty("losses")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("not a PDF", jsonText, StringComparison.Ordinal);
        Assert.DoesNotContain("first\"", jsonText, StringComparison.Ordinal);
        string text = report.ToText();
        Assert.Contains("Structured exports: 3, succeeded 2, failed 1", text,
            StringComparison.Ordinal);
        Assert.Contains("first.pdf: succeeded, output first.txt", text, StringComparison.Ordinal);
        Assert.Contains("Representation: 1 finding(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not a PDF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchExportStopsAfterCanceledDocument()
    {
        byte[] valid = DocumentBytes("BT /F1 12 Tf 10 30 Td (text) Tj ET");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        PdfStructuredExportBatchReport report = PdfStructuredExportBatchRunner.RunReport(
            [new PdfStructuredExportBatchItem("first.pdf", valid)],
            PdfStructuredExportFormat.Json, cancellation.Token);

        Assert.Equal(1, report.UnprocessedCount);
        Assert.Empty(report.Results);
    }

    [Fact]
    public void BatchExportPlansFormatSpecificNamesAndRejectsCollisions()
    {
        Assert.Equal("report.docx", PdfStructuredExportBatchRunner.OutputName(
            @"C:\input\report.pdf", PdfStructuredExportFormat.WordDocument));
        Assert.Equal("report.xlsx", PdfStructuredExportBatchRunner.OutputName(
            "report", PdfStructuredExportFormat.Spreadsheet));

        byte[] valid = DocumentBytes("BT /F1 12 Tf 10 30 Td (text) Tj ET");
        Assert.Throws<ArgumentException>(() => PdfStructuredExportBatchRunner.Run([
            new PdfStructuredExportBatchItem("report.pdf", valid),
            new PdfStructuredExportBatchItem("REPORT.PDF", valid)
        ], PdfStructuredExportFormat.Html));
    }

    [Fact]
    public void FormattedExportsApplyValidatedFontSubstitutions()
    {
        PdfDocument document = Document("BT /F1 12 Tf 10 30 Td (A & B) Tj ET");
        var substitutions = new PdfStructuredExportFontSubstitutions(
            new Dictionary<string, string> { ["helvetica"] = "Aptos" });

        string html = PdfStructuredExport.ToHtml(
            document, fontSubstitutions: substitutions);
        string wordXml = PackageEntry(PdfStructuredExport.ToDocx(
            document, fontSubstitutions: substitutions), "word/document.xml");
        string slideXml = PackageEntry(PdfStructuredExport.Export(document,
            PdfStructuredExportFormat.Presentation, fontSubstitutions: substitutions),
            "ppt/slides/slide1.xml");

        Assert.Contains("font-family:&quot;Aptos&quot;", html, StringComparison.Ordinal);
        Assert.Contains("w:ascii=\"Aptos\" w:hAnsi=\"Aptos\"", wordXml,
            StringComparison.Ordinal);
        Assert.Contains("typeface=\"Aptos\"", slideXml, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() =>
            new PdfStructuredExportFontSubstitutions(
                new Dictionary<string, string> { ["Helvetica"] = " " }));
    }

    private static string PackageEntry(byte[] package, string name)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        using var reader = new StreamReader(Assert.IsType<ZipArchiveEntry>(
            archive.GetEntry(name)).Open());
        return reader.ReadToEnd();
    }

    private static PdfDocument Document(string content)
        => PdfDocument.Open(DocumentBytes(content));

    private static byte[] DocumentBytes(string content)
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
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
