using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>A structured export target whose representation limits can be inspected.</summary>
public enum PdfStructuredExportFormat
{
    /// <summary>Plain text.</summary>
    PlainText,
    /// <summary>Standalone HTML.</summary>
    Html,
    /// <summary>Editable Markdown.</summary>
    Markdown,
    /// <summary>Structured JSON.</summary>
    Json,
    /// <summary>Editable Word document.</summary>
    WordDocument
}

/// <summary>One type of source content that an export does not fully represent.</summary>
public sealed record PdfStructuredExportFinding(
    string Code, int PageIndex, int Count, string Message);

/// <summary>A report of content that a structured export cannot fully represent.</summary>
public sealed record PdfStructuredExportReport(IReadOnlyList<PdfStructuredExportFinding> Findings)
{
    /// <summary>Gets whether the selected pages can be represented without known loss.</summary>
    public bool IsLossless => Findings.Count == 0;

    /// <summary>Exports the report as machine-readable JSON.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new { Version = 1, IsLossless, Findings },
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        });
}

/// <summary>Exports engine-owned page extraction to editable text and web formats.</summary>
public static class PdfStructuredExport
{
    /// <summary>Reports source content that the selected export cannot fully represent.</summary>
    public static PdfStructuredExportReport InspectLosses(PdfDocument document,
        PdfStructuredExportFormat format, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        var findings = new List<PdfStructuredExportFinding>();
        foreach (Page page in Read(document, pageIndices, cancellationToken))
        {
            if (page.Content.Paths.Count > 0)
                findings.Add(new PdfStructuredExportFinding("VectorContentNotExported",
                    page.Index, page.Content.Paths.Count,
                    "Vector paths are not represented by this structured export."));
            if (page.Content.Images.Count > 0)
            {
                string message = format switch
                {
                    PdfStructuredExportFormat.PlainText =>
                        "Images are omitted from plain-text export.",
                    PdfStructuredExportFormat.Html =>
                        "HTML export contains image placeholders without image data.",
                    PdfStructuredExportFormat.Markdown =>
                        "Markdown export contains image placeholders without image data.",
                    PdfStructuredExportFormat.Json =>
                        "JSON export contains image placement without image data.",
                    PdfStructuredExportFormat.WordDocument =>
                        "Word export contains image placeholders without image data.",
                    _ => throw new ArgumentOutOfRangeException(nameof(format))
                };
                findings.Add(new PdfStructuredExportFinding("ImageContentNotExported",
                    page.Index, page.Content.Images.Count, message));
            }
        }
        return new PdfStructuredExportReport(Array.AsReadOnly(findings.ToArray()));
    }

    /// <summary>Exports selected pages as plain text separated by form feeds.</summary>
    public static string ToPlainText(PdfDocument document, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default) =>
        string.Join("\f", Read(document, pageIndices, cancellationToken)
            .Select(page => string.Join(Environment.NewLine, page.Content.Lines.Select(line => line.Text))));

    /// <summary>Exports selected pages as a standalone semantic HTML document.</summary>
    public static string ToHtml(PdfDocument document, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Page> pages = Read(document, pageIndices, cancellationToken);
        var output = new StringBuilder("<!doctype html><html><head><meta charset=\"utf-8\"><title>PDF export</title></head><body>");
        foreach (Page page in pages)
        {
            output.Append("<section data-page=\"").Append(page.Index + 1).Append("\">");
            foreach (PdfExtractedLine line in page.Content.Lines)
                output.Append("<p>").Append(WebUtility.HtmlEncode(line.Text)).Append("</p>");
            foreach (PdfExtractedImage image in page.Content.Images)
                output.Append("<figure data-image=\"").Append(WebUtility.HtmlEncode(image.ResourceName ?? "inline"))
                    .Append("\"></figure>");
            output.Append("</section>");
        }
        return output.Append("</body></html>").ToString();
    }

    /// <summary>Exports selected pages as editable Markdown with page headings.</summary>
    public static string ToMarkdown(PdfDocument document, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Page> pages = Read(document, pageIndices, cancellationToken);
        var output = new StringBuilder();
        foreach (Page page in pages)
        {
            if (output.Length > 0) output.AppendLine().AppendLine();
            output.Append("# Page ").Append(page.Index + 1);
            foreach (PdfExtractedLine line in page.Content.Lines)
                output.AppendLine().AppendLine().Append(EscapeMarkdown(line.Text));
            foreach (PdfExtractedImage image in page.Content.Images)
                output.AppendLine().AppendLine().Append("![Image: ")
                    .Append(EscapeMarkdown(image.ResourceName ?? "inline"))
                    .Append("]()");
        }
        return output.ToString();
    }

    /// <summary>Exports selected pages, lines, runs, images, geometry, and diagnostics as JSON.</summary>
    public static string ToJson(PdfDocument document, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default)
    {
        var pages = Read(document, pageIndices, cancellationToken).Select(page => new
        {
            page = page.Index + 1,
            width = page.Content.Width,
            height = page.Content.Height,
            lines = page.Content.Lines.Select(line => new
            {
                text = line.Text,
                bounds = line.BoundingBox,
                direction = line.WritingDirection.ToString(),
                runs = line.Runs.Select(run => new
                {
                    text = run.Text,
                    font = run.FontName,
                    size = run.PointSize,
                    bounds = run.BoundingBox
                })
            }),
            images = page.Content.Images.Select(image => new
            {
                resource = image.ResourceName,
                inline = image.IsInline,
                bounds = image.BoundingBox
            }),
            diagnostics = page.Content.Diagnostics
        });
        return JsonSerializer.Serialize(pages, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Exports selected pages as an editable Office Open XML Word document.</summary>
    public static byte[] ToDocx(PdfDocument document, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Page> pages = Read(document, pageIndices, cancellationToken);
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "</Types>");
            WriteEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            var body = new StringBuilder();
            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                if (pageIndex > 0) body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
                foreach (PdfExtractedLine line in pages[pageIndex].Content.Lines)
                    body.Append("<w:p><w:r><w:t xml:space=\"preserve\">")
                        .Append(WebUtility.HtmlEncode(line.Text)).Append("</w:t></w:r></w:p>");
                foreach (PdfExtractedImage image in pages[pageIndex].Content.Images)
                    body.Append("<w:p><w:r><w:t>[Image: ")
                        .Append(WebUtility.HtmlEncode(image.ResourceName ?? "inline"))
                        .Append("]</w:t></w:r></w:p>");
            }
            WriteEntry(archive, "word/document.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:body>" + body + "<w:sectPr/></w:body></w:document>");
        }
        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static IReadOnlyList<Page> Read(PdfDocument document, IEnumerable<int>? pageIndices,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        int[] indices = pageIndices?.ToArray() ?? Enumerable.Range(0, pageCount).ToArray();
        if (indices.Distinct().Count() != indices.Length || indices.Any(index => index < 0 || index >= pageCount))
            throw new ArgumentOutOfRangeException(nameof(pageIndices),
                "Page indices must be unique and within the document.");
        var reader = new PdfPageContentReader(document);
        return Array.AsReadOnly(indices.Select(index =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new Page(index, reader.Read(index, cancellationToken));
        }).ToArray());
    }

    private static string EscapeMarkdown(string value)
    {
        var output = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']'
                or '<' or '>' or '(' or ')' or '#' or '+' or '-' or '.' or '!'
                or '|')
                output.Append('\\');
            output.Append(character);
        }
        return output.ToString();
    }

    private sealed record Page(int Index, PdfPageContent Content);
}
