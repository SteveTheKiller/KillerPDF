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
                    PdfStructuredExportFormat.Spreadsheet =>
                        "Spreadsheet export omits image data.",
                    PdfStructuredExportFormat.Presentation =>
                        "Presentation export contains image placeholders without image data.",
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
        int id = 2;
        foreach (PdfExtractedLine line in page.Content.Lines)
            AppendPresentationText(shapes, id++, line.Text, line.BoundingBox,
                page.Content.Width, page.Content.Height, slideWidth, slideHeight);
        foreach (PdfExtractedImage image in page.Content.Images)
            AppendPresentationText(shapes, id++, "[Image: " + (image.ResourceName ?? "inline") + "]",
                image.BoundingBox, page.Content.Width, page.Content.Height, slideWidth, slideHeight);
        WriteEntry(archive, $"ppt/slides/slide{number}.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree>" +
            GroupShape + shapes + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        WriteEntry(archive, $"ppt/slides/_rels/slide{number}.xml.rels",
            Relationships("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>"));
    }

    private static void AppendPresentationText(StringBuilder output, int id, string text,
        PdfContentBounds bounds, double pageWidth, double pageHeight,
        long slideWidth, long slideHeight)
    {
        long x = Scale(bounds.Left, pageWidth, slideWidth);
        long y = Scale(pageHeight - bounds.Top, pageHeight, slideHeight);
        long width = Math.Max(1, Scale(bounds.Width, pageWidth, slideWidth));
        long height = Math.Max(1, Scale(bounds.Height, pageHeight, slideHeight));
        output.Append("<p:sp><p:nvSpPr><p:cNvPr id=\"").Append(id)
            .Append("\" name=\"PDF text ").Append(id - 1)
            .Append("\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"")
            .Append(x).Append("\" y=\"").Append(y).Append("\"/><a:ext cx=\"")
            .Append(width).Append("\" cy=\"").Append(height)
            .Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln><a:noFill/></a:ln></p:spPr><p:txBody><a:bodyPr wrap=\"none\"/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>")
            .Append(WebUtility.HtmlEncode(text))
            .Append("</a:t></a:r><a:endParaRPr lang=\"en-US\"/></a:p></p:txBody></p:sp>");
    }

    private static long Scale(double value, double source, long target) =>
        source <= 0 ? 0 : (long)Math.Round(value / source * target);

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
}
