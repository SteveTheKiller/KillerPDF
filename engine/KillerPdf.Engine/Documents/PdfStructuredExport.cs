using System.Net;
using System.IO.Compression;
using System.Globalization;
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
    WordDocument,
    /// <summary>Editable Excel workbook.</summary>
    Spreadsheet,
    /// <summary>Editable PowerPoint presentation.</summary>
    Presentation
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

/// <summary>One PDF supplied for isolated structured export.</summary>
public sealed record PdfStructuredExportBatchItem
{
    /// <summary>Creates a validated batch input with an isolated source copy.</summary>
    public PdfStructuredExportBatchItem(string sourceName, ReadOnlyMemory<byte> source,
        IEnumerable<int>? pageIndices = null)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new ArgumentException("A source name is required.", nameof(sourceName));
        SourceName = sourceName;
        Source = source.ToArray();
        PageIndices = pageIndices is null
            ? null : Array.AsReadOnly(pageIndices.ToArray());
    }

    /// <summary>Gets the source file name.</summary>
    public string SourceName { get; }
    /// <summary>Gets an isolated copy of the source PDF.</summary>
    public ReadOnlyMemory<byte> Source { get; }
    /// <summary>Gets the optional zero-based page selection.</summary>
    public IReadOnlyList<int>? PageIndices { get; }
}

/// <summary>The isolated outcome of exporting one PDF.</summary>
public sealed record PdfStructuredExportBatchResult(
    PdfStructuredExportBatchItem Input, ReadOnlyMemory<byte>? Data,
    string? Error, bool WasCanceled, string OutputName)
{
    /// <summary>Gets whether export completed.</summary>
    public bool Succeeded => Data.HasValue && Error is null && !WasCanceled;
}

/// <summary>A data-safe aggregate report for a structured export batch.</summary>
public sealed record PdfStructuredExportBatchReport
{
    /// <summary>Creates a report from an isolated batch result prefix.</summary>
    public PdfStructuredExportBatchReport(int totalDocumentCount,
        IEnumerable<PdfStructuredExportBatchResult> results)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalDocumentCount);
        ArgumentNullException.ThrowIfNull(results);
        PdfStructuredExportBatchResult[] values = results.ToArray();
        if (values.Length > totalDocumentCount)
            throw new ArgumentException(
                "Export batch results cannot exceed the supplied document count.", nameof(results));
        TotalDocumentCount = totalDocumentCount;
        Results = Array.AsReadOnly(values);
    }

    /// <summary>Gets the number of supplied documents.</summary>
    public int TotalDocumentCount { get; }
    /// <summary>Gets completed, failed, or canceled document results.</summary>
    public IReadOnlyList<PdfStructuredExportBatchResult> Results { get; }
    /// <summary>Gets the number of successful documents.</summary>
    public int SucceededCount => Results.Count(result => result.Succeeded);
    /// <summary>Gets the number of failed documents.</summary>
    public int FailedCount => Results.Count(result => result.Error is not null);
    /// <summary>Gets the number of canceled documents.</summary>
    public int CanceledCount => Results.Count(result => result.WasCanceled);
    /// <summary>Gets documents not reached after cancellation.</summary>
    public int UnprocessedCount => TotalDocumentCount - Results.Count;

    /// <summary>Exports outcomes without source or generated document data.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        TotalDocumentCount,
        SucceededCount,
        FailedCount,
        CanceledCount,
        UnprocessedCount,
        Results = Results.Select(result => new
        {
            result.Input.SourceName,
            result.OutputName,
            result.Succeeded,
            result.WasCanceled,
            result.Error,
            OutputByteCount = result.Data?.Length
        })
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });
}

/// <summary>Runs isolated structured exports with failure containment and cancellation.</summary>
public static class PdfStructuredExportBatchRunner
{
    /// <summary>Exports a document batch and returns aggregate, data-safe outcomes.</summary>
    public static PdfStructuredExportBatchReport RunReport(
        IEnumerable<PdfStructuredExportBatchItem> items, PdfStructuredExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        PdfStructuredExportBatchItem[] supplied = items.ToArray();
        return new PdfStructuredExportBatchReport(supplied.Length,
            Run(supplied, format, cancellationToken));
    }

    /// <summary>Exports each document independently.</summary>
    public static IReadOnlyList<PdfStructuredExportBatchResult> Run(
        IEnumerable<PdfStructuredExportBatchItem> items, PdfStructuredExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        PdfStructuredExportBatchItem[] supplied = items.ToArray();
        string[] outputNames = supplied.Select(item => OutputName(item.SourceName, format)).ToArray();
        if (outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputNames.Length)
            throw new ArgumentException("Structured export output names must be unique.", nameof(items));
        var results = new List<PdfStructuredExportBatchResult>();
        for (int itemIndex = 0; itemIndex < supplied.Length; itemIndex++)
        {
            if (cancellationToken.IsCancellationRequested) break;
            PdfStructuredExportBatchItem suppliedItem = supplied[itemIndex];
            var item = new PdfStructuredExportBatchItem(suppliedItem.SourceName,
                suppliedItem.Source, suppliedItem.PageIndices);
            try
            {
                PdfDocument document = PdfDocument.Open(item.Source);
                byte[] data = Export(document, format, item.PageIndices, cancellationToken);
                results.Add(new PdfStructuredExportBatchResult(
                    item, data, null, false, outputNames[itemIndex]));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(new PdfStructuredExportBatchResult(
                    item, null, null, true, outputNames[itemIndex]));
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                and not StackOverflowException and not AccessViolationException)
            {
                results.Add(new PdfStructuredExportBatchResult(
                    item, null, exception.Message, false, outputNames[itemIndex]));
            }
        }
        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>Returns the deterministic output file name for a source and export format.</summary>
    public static string OutputName(string sourceName, PdfStructuredExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        string fileName = Path.GetFileName(sourceName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length == 0) throw new ArgumentException(
            "A structured export source name requires a base name.", nameof(sourceName));
        string extension = format switch
        {
            PdfStructuredExportFormat.PlainText => ".txt",
            PdfStructuredExportFormat.Html => ".html",
            PdfStructuredExportFormat.Markdown => ".md",
            PdfStructuredExportFormat.Json => ".json",
            PdfStructuredExportFormat.WordDocument => ".docx",
            PdfStructuredExportFormat.Spreadsheet => ".xlsx",
            PdfStructuredExportFormat.Presentation => ".pptx",
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        return stem + extension;
    }

    private static byte[] Export(PdfDocument document, PdfStructuredExportFormat format,
        IEnumerable<int>? pageIndices, CancellationToken cancellationToken) => format switch
    {
        PdfStructuredExportFormat.PlainText => Encoding.UTF8.GetBytes(
            PdfStructuredExport.ToPlainText(document, pageIndices, cancellationToken)),
        PdfStructuredExportFormat.Html => Encoding.UTF8.GetBytes(
            PdfStructuredExport.ToHtml(document, pageIndices, cancellationToken)),
        PdfStructuredExportFormat.Markdown => Encoding.UTF8.GetBytes(
            PdfStructuredExport.ToMarkdown(document, pageIndices, cancellationToken)),
        PdfStructuredExportFormat.Json => Encoding.UTF8.GetBytes(
            PdfStructuredExport.ToJson(document, pageIndices, cancellationToken)),
        PdfStructuredExportFormat.WordDocument =>
            PdfStructuredExport.ToDocx(document, pageIndices, cancellationToken),
        PdfStructuredExportFormat.Spreadsheet =>
            PdfStructuredExport.ToXlsx(document, pageIndices, cancellationToken),
        PdfStructuredExportFormat.Presentation =>
            PdfStructuredExport.ToPptx(document, pageIndices, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
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
            if (format != PdfStructuredExportFormat.Json)
            {
                int directionalTextCount = page.Content.Lines.Count(line =>
                    line.WritingDirection != PdfWritingDirection.LeftToRight);
                if (directionalTextCount > 0)
                    findings.Add(new PdfStructuredExportFinding(
                        "TextDirectionNotPreserved", page.Index, directionalTextCount,
                        "Non-horizontal or reversed text direction is not preserved by this export."));
            }
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
                    PdfStructuredExportFormat.Spreadsheet =>
                        "Spreadsheet export omits image data.",
                    PdfStructuredExportFormat.Presentation =>
                        "Presentation export contains image placeholders without image data.",
                    _ => throw new ArgumentOutOfRangeException(nameof(format))
                };
                findings.Add(new PdfStructuredExportFinding("ImageContentNotExported",
                    page.Index, page.Content.Images.Count, message));
            }
            if (format is not (PdfStructuredExportFormat.Html or PdfStructuredExportFormat.Json))
            {
                int linkCount = PdfLinkReader.ReadPage(document, page.Index).Count;
                if (linkCount > 0)
                    findings.Add(new PdfStructuredExportFinding("LinkContentNotExported",
                        page.Index, linkCount,
                        "Links are not represented by this structured export."));
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
            output.Append("<section id=\"page-").Append(page.Index + 1)
                .Append("\" data-page=\"").Append(page.Index + 1).Append("\">");
            foreach (PdfExtractedLine line in page.Content.Lines)
                AppendHtmlLine(output, line);
            foreach (PdfExtractedImage image in page.Content.Images)
                output.Append("<figure data-image=\"").Append(WebUtility.HtmlEncode(image.ResourceName ?? "inline"))
                    .Append("\"></figure>");
            IReadOnlyList<PdfLinkInfo> links = PdfLinkReader.ReadPage(document, page.Index);
            if (links.Count > 0)
            {
                output.Append("<nav aria-label=\"Page links\"><ul>");
                foreach (PdfLinkInfo link in links)
                {
                    string target = link.Uri ?? (link.DestinationPageIndex.HasValue
                        ? $"#page-{link.DestinationPageIndex.Value + 1}"
                        : $"#{Uri.EscapeDataString(link.NamedDestination!)}");
                    string label = link.Description ?? link.Uri ?? (link.DestinationPageIndex.HasValue
                        ? $"Page {link.DestinationPageIndex.Value + 1}"
                        : link.NamedDestination!);
                    output.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(target))
                        .Append("\">").Append(WebUtility.HtmlEncode(label)).Append("</a></li>");
                }
                output.Append("</ul></nav>");
            }
            output.Append("</section>");
        }
        return output.Append("</body></html>").ToString();
    }

    private static void AppendHtmlLine(StringBuilder output, PdfExtractedLine line)
    {
        output.Append("<p>");
        string runText = string.Concat(line.Runs.Select(run => run.Text));
        if (line.Runs.Count == 0 || runText != line.Text)
            output.Append(WebUtility.HtmlEncode(line.Text));
        else
            foreach (PdfExtractedTextRun run in line.Runs)
            {
                string font = string.IsNullOrWhiteSpace(run.FontName)
                    ? "sans-serif" : run.FontName;
                double size = double.IsFinite(run.PointSize) && run.PointSize > 0
                    ? run.PointSize : 12;
                output.Append("<span style=\"font-family:&quot;")
                    .Append(WebUtility.HtmlEncode(font))
                    .Append("&quot;;font-size:")
                    .Append(size.ToString("R", CultureInfo.InvariantCulture))
                    .Append("pt\">")
                    .Append(WebUtility.HtmlEncode(run.Text))
                    .Append("</span>");
            }
        output.Append("</p>");
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
            links = PdfLinkReader.ReadPage(document, page.Index).Select(link => new
            {
                annotationIndex = link.AnnotationIndex,
                objectNumber = link.ObjectNumber,
                generation = link.Generation,
                bounds = new
                {
                    left = link.Left,
                    bottom = link.Bottom,
                    right = link.Right,
                    top = link.Top
                },
                destinationPage = link.DestinationPageIndex.HasValue
                    ? link.DestinationPageIndex.Value + 1 : (int?)null,
                namedDestination = link.NamedDestination,
                uri = link.Uri,
                description = link.Description
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
                {
                    body.Append("<w:p>");
                    string runText = string.Concat(line.Runs.Select(run => run.Text));
                    if (line.Runs.Count == 0 || runText != line.Text)
                        AppendWordRun(body, line.Text, line.Runs.FirstOrDefault());
                    else
                        foreach (PdfExtractedTextRun run in line.Runs)
                            AppendWordRun(body, run.Text, run);
                    body.Append("</w:p>");
                }
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

    private static void AppendWordRun(
        StringBuilder output, string text, PdfExtractedTextRun? run)
    {
        string font = string.IsNullOrWhiteSpace(run?.FontName) ? "Arial" : run.FontName;
        int halfPoints = run is null || !double.IsFinite(run.PointSize) || run.PointSize <= 0
            ? 24 : Math.Clamp((int)Math.Round(run.PointSize * 2), 2, 3276);
        string encodedFont = WebUtility.HtmlEncode(font);
        output.Append("<w:r><w:rPr><w:rFonts w:ascii=\"")
            .Append(encodedFont).Append("\" w:hAnsi=\"").Append(encodedFont)
            .Append("\"/><w:sz w:val=\"").Append(halfPoints)
            .Append("\"/><w:szCs w:val=\"").Append(halfPoints)
            .Append("\"/></w:rPr><w:t xml:space=\"preserve\">")
            .Append(WebUtility.HtmlEncode(text)).Append("</w:t></w:r>");
    }

    /// <summary>Exports selected pages as an editable Office Open XML spreadsheet.</summary>
    public static byte[] ToXlsx(PdfDocument document, IEnumerable<int>? pageIndices = null,
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
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "</Types>");
            WriteEntry(archive, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>");
            WriteEntry(archive, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"PDF export\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            WriteEntry(archive, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "</Relationships>");

            var rows = new StringBuilder();
            AppendSpreadsheetRow(rows, 1, "Page", "Line", "Text");
            int row = 2;
            foreach (Page page in pages)
            {
                for (int line = 0; line < page.Content.Lines.Count; line++)
                    AppendSpreadsheetRow(rows, row++, (page.Index + 1).ToString(),
                        (line + 1).ToString(), page.Content.Lines[line].Text);
            }
            WriteEntry(archive, "xl/worksheets/sheet1.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                rows + "</sheetData></worksheet>");
        }
        return output.ToArray();
    }

    /// <summary>Exports selected pages as editable Office Open XML presentation slides.</summary>
    public static byte[] ToPptx(PdfDocument document, IEnumerable<int>? pageIndices = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Page> pages = Read(document, pageIndices, cancellationToken);
        const long slideWidth = 9_144_000;
        double aspect = pages.Count == 0 || pages[0].Content.Width <= 0
            ? 0.75 : pages[0].Content.Height / pages[0].Content.Width;
        long slideHeight = Math.Max(1, (long)Math.Round(slideWidth * aspect));
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var overrides = new StringBuilder();
            var slideIds = new StringBuilder();
            var relationships = new StringBuilder();
            for (int index = 0; index < pages.Count; index++)
            {
                int number = index + 1;
                overrides.Append("<Override PartName=\"/ppt/slides/slide").Append(number)
                    .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
                slideIds.Append("<p:sldId id=\"").Append(255 + number)
                    .Append("\" r:id=\"rId").Append(number + 1).Append("\"/>");
                relationships.Append("<Relationship Id=\"rId").Append(number + 1)
                    .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide\" Target=\"slides/slide")
                    .Append(number).Append(".xml\"/>");
                WritePresentationSlide(archive, pages[index], number, slideWidth, slideHeight);
            }
            WriteEntry(archive, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
                "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
                "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
                "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>" +
                overrides + "</Types>");
            WriteEntry(archive, "_rels/.rels", PackageRelationship("ppt/presentation.xml"));
            WriteEntry(archive, "ppt/presentation.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<p:presentation xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">" +
                "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>" +
                slideIds + "</p:sldIdLst><p:sldSz cx=\"" + slideWidth + "\" cy=\"" + slideHeight +
                "\" type=\"custom\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
            WriteEntry(archive, "ppt/_rels/presentation.xml.rels",
                Relationships("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>" + relationships));
            WritePresentationFoundation(archive);
        }
        return output.ToArray();
    }

    private static void WritePresentationSlide(ZipArchive archive, Page page, int number,
        long slideWidth, long slideHeight)
    {
        var shapes = new StringBuilder();
        PresentationPageLayout layout = FitPresentationPage(
            page.Content.Width, page.Content.Height, slideWidth, slideHeight);
        int id = 2;
        foreach (PdfExtractedLine line in page.Content.Lines)
            AppendPresentationText(shapes, id++, line.Text, line.BoundingBox,
                page.Content.Height, layout, line.Runs);
        foreach (PdfExtractedImage image in page.Content.Images)
            AppendPresentationText(shapes, id++, "[Image: " + (image.ResourceName ?? "inline") + "]",
                image.BoundingBox, page.Content.Height, layout, null);
        WriteEntry(archive, $"ppt/slides/slide{number}.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree>" +
            GroupShape + shapes + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        WriteEntry(archive, $"ppt/slides/_rels/slide{number}.xml.rels",
            Relationships("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>"));
    }

    private static void AppendPresentationText(StringBuilder output, int id, string text,
        PdfContentBounds bounds, double pageHeight, PresentationPageLayout layout,
        IReadOnlyList<PdfExtractedTextRun>? runs)
    {
        long x = layout.OffsetX + (long)Math.Round(bounds.Left * layout.EmuPerPoint);
        long y = layout.OffsetY
            + (long)Math.Round((pageHeight - bounds.Top) * layout.EmuPerPoint);
        long width = Math.Max(1, (long)Math.Round(bounds.Width * layout.EmuPerPoint));
        long height = Math.Max(1, (long)Math.Round(bounds.Height * layout.EmuPerPoint));
        output.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id)
            .Append("\" name=\"PDF text ").Append(id - 1)
            .Append("\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"")
            .Append(x).Append("\" y=\"").Append(y).Append("\"/><a:ext cx=\"")
            .Append(width).Append("\" cy=\"").Append(height)
            .Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></p:spPr><p:txBody><a:bodyPr wrap=\"none\"/><a:lstStyle/><a:p>");
        string runText = runs is null ? string.Empty : string.Concat(runs.Select(run => run.Text));
        if (runs is null || runs.Count == 0 || runText != text)
            AppendRun(text, runs?.FirstOrDefault());
        else
            foreach (PdfExtractedTextRun run in runs) AppendRun(run.Text, run);
        output.Append("<a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>");

        void AppendRun(string value, PdfExtractedTextRun? run)
        {
            string font = string.IsNullOrWhiteSpace(run?.FontName) ? "Arial" : run.FontName;
            int size = run is null || !double.IsFinite(run.PointSize) || run.PointSize <= 0
                ? 1200 : Math.Clamp((int)Math.Round(run.PointSize * 100), 100, 400000);
            output.Append("<a:r><a:rPr lang=\"en-US\" sz=\"").Append(size)
                .Append("\"><a:latin typeface=\"")
                .Append(WebUtility.HtmlEncode(font))
                .Append("\"/></a:rPr><a:t>")
                .Append(WebUtility.HtmlEncode(value)).Append("</a:t></a:r>");
        }
    }

    private static long Scale(double value, double source, long target) =>
        source <= 0 ? 0 : (long)Math.Round(value / source * target);

    private static PresentationPageLayout FitPresentationPage(
        double pageWidth, double pageHeight, long slideWidth, long slideHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
            return new PresentationPageLayout(0, 0, 1);
        double scale = Math.Min(slideWidth / pageWidth, slideHeight / pageHeight);
        long width = (long)Math.Round(pageWidth * scale);
        long height = (long)Math.Round(pageHeight * scale);
        return new PresentationPageLayout(
            (slideWidth - width) / 2, (slideHeight - height) / 2, scale);
    }

    private const string GroupShape = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    private static string PackageRelationship(string target) => Relationships(
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"" + target + "\"/>");

    private static string Relationships(string values) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + values + "</Relationships>";

    private static void WritePresentationFoundation(ZipArchive archive)
    {
        WriteEntry(archive, "ppt/slideMasters/slideMaster1.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldMaster xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree>" + GroupShape + "</p:spTree></p:cSld><p:clrMap accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" bg1=\"lt1\" bg2=\"lt2\" folHlink=\"folHlink\" hlink=\"hlink\" tx1=\"dk1\" tx2=\"dk2\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"1\" r:id=\"rId1\"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle/><p:bodyStyle/><p:otherStyle/></p:txStyles></p:sldMaster>");
        WriteEntry(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels",
            Relationships("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme\" Target=\"../theme/theme1.xml\"/>"));
        WriteEntry(archive, "ppt/slideLayouts/slideLayout1.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><p:sldLayout xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" type=\"blank\"><p:cSld name=\"Blank\"><p:spTree>" + GroupShape + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        WriteEntry(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels",
            Relationships("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/>"));
        WriteEntry(archive, "ppt/theme/theme1.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"KillerPDF\"><a:themeElements><a:clrScheme name=\"KillerPDF\"><a:dk1><a:srgbClr val=\"000000\"/></a:dk1><a:lt1><a:srgbClr val=\"FFFFFF\"/></a:lt1><a:dk2><a:srgbClr val=\"1F1F1F\"/></a:dk2><a:lt2><a:srgbClr val=\"F2F2F2\"/></a:lt2><a:accent1><a:srgbClr val=\"4472C4\"/></a:accent1><a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4><a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme><a:fontScheme name=\"KillerPDF\"><a:majorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont><a:minorFont><a:latin typeface=\"Arial\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme><a:fmtScheme name=\"KillerPDF\"><a:fillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w=\"9525\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>");
    }

    private static void AppendSpreadsheetRow(StringBuilder output, int row,
        string page, string line, string text) => output.Append("<row r=\"").Append(row)
        .Append("\"><c r=\"A").Append(row).Append("\" t=\"inlineStr\"><is><t>")
        .Append(WebUtility.HtmlEncode(page)).Append("</t></is></c><c r=\"B").Append(row)
        .Append("\" t=\"inlineStr\"><is><t>").Append(WebUtility.HtmlEncode(line))
        .Append("</t></is></c><c r=\"C").Append(row)
        .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
        .Append(WebUtility.HtmlEncode(text)).Append("</t></is></c></row>");

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
    private sealed record PresentationPageLayout(
        long OffsetX, long OffsetY, double EmuPerPoint);
}
