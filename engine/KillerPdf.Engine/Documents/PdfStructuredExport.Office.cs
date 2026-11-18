using System.Text;

namespace KillerPdf.Engine.Documents;

public static partial class PdfStructuredExport
{
    private static KillerPdf.Engine.Rendering.PdfRenderedPage? RenderOfficeImage(
        KillerPdf.Engine.Rendering.PdfPageRenderer renderer, PdfPageContent page,
        PdfExtractedImage image, CancellationToken cancellationToken)
    {
        // Extracted placements do not retain clipping, external opacity or blend state.
        if (page.Instructions.Any(instruction => instruction.Operator is "gs" or "W" or "W*")) return null;
        PdfContentBounds bounds = image.BoundingBox;
        if (bounds.Left < 0 || bounds.Bottom < 0 || bounds.Right > page.Width || bounds.Top > page.Height) return null;
        return renderer.RenderExportImage(image, cancellationToken);
    }

    internal static void ValidateOfficeMode(PdfStructuredExportFormat format, PdfOfficeExportMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode != PdfOfficeExportMode.Editable && format is not (PdfStructuredExportFormat.WordDocument or PdfStructuredExportFormat.Presentation))
            throw new ArgumentException("Page images are available only for Word and PowerPoint export.", nameof(mode));
    }

    private static (double Width, double Height) OfficePageSize(PdfDocument document, int index)
    {
        PdfPageInformation page = PdfPageInformation.Read(document)[index];
        return page.Rotation is 90 or 270 ? (page.Height, page.Width) : (page.Width, page.Height);
    }

    private static KillerPdf.Engine.Rendering.PdfRenderedPage RenderOfficePage(PdfDocument document,
        int index, CancellationToken cancellationToken)
    {
        var size = OfficePageSize(document, index);
        double scale = Math.Min(2, 4096 / Math.Max(size.Width, size.Height));
        var renderer = new KillerPdf.Engine.Rendering.PdfPageRenderer(document);
        var bitmap = renderer.Render(index, new KillerPdf.Engine.Rendering.PdfRenderOptions(
            Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)))
            { CacheResult = false }, cancellationToken);
        if (bitmap.Diagnostics.Count > 0)
            throw new NotSupportedException("The page image could not be rendered completely: " + string.Join(" ", bitmap.Diagnostics));
        return bitmap;
    }

    private static string WordPageSection(double width, double height) =>
        "<w:sectPr><w:pgSz w:w=\"" + (int)Math.Round(width * 20) + "\" w:h=\"" + (int)Math.Round(height * 20)
        + "\"/><w:pgMar w:top=\"0\" w:right=\"0\" w:bottom=\"0\" w:left=\"0\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>";

    private static void WritePresentationPageImage(System.IO.Compression.ZipArchive archive, PdfDocument document,
        int index, int number, long slideWidth, long slideHeight, CancellationToken cancellationToken)
    {
        var size = OfficePageSize(document, index);
        var bitmap = RenderOfficePage(document, index, cancellationToken);
        using (Stream asset = archive.CreateEntry($"ppt/media/page{number}.png").Open())
            asset.Write(PdfOfficeImage.Encode(bitmap, cancellationToken));
        var shapes = new StringBuilder();
        AppendPresentationImage(shapes, 2, new(0, 0, size.Width, size.Height), size.Height,
            FitPresentationPage(size.Width, size.Height, slideWidth, slideHeight));
        WriteEntry(archive, $"ppt/slides/slide{number}.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:sld xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"><p:cSld><p:spTree>"
            + GroupShape + shapes + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
        WriteEntry(archive, $"ppt/slides/_rels/slide{number}.xml.rels", Relationships(
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>"
            + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/page" + number + ".png\"/>"));
    }

    private static void AppendPresentationImage(StringBuilder output, int id, PdfContentBounds bounds,
        double pageHeight, PresentationPageLayout layout)
    {
        long x = layout.OffsetX + (long)Math.Round(bounds.Left * layout.EmuPerPoint);
        long y = layout.OffsetY + (long)Math.Round((pageHeight - bounds.Top) * layout.EmuPerPoint);
        output.Append("<p:pic><p:nvPicPr><p:cNvPr id=\"").Append(id).Append("\" name=\"PDF image ").Append(id)
            .Append("\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr><p:blipFill><a:blip r:embed=\"rId").Append(id)
            .Append("\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr><a:xfrm><a:off x=\"")
            .Append(x).Append("\" y=\"").Append(y).Append("\"/><a:ext cx=\"")
            .Append(Math.Max(1, (long)Math.Round(bounds.Width * layout.EmuPerPoint))).Append("\" cy=\"")
            .Append(Math.Max(1, (long)Math.Round(bounds.Height * layout.EmuPerPoint)))
            .Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
    }

    private static void AppendWordImage(StringBuilder output, int id, double width, double height)
    {
        long cx = Math.Max(1, (long)Math.Round(width * 12700));
        long cy = Math.Max(1, (long)Math.Round(height * 12700));
        output.Append("<w:p><w:r><w:drawing><wp:inline xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">")
            .Append("<wp:extent cx=\"").Append(cx).Append("\" cy=\"").Append(cy).Append("\"/><wp:docPr id=\"").Append(id)
            .Append("\" name=\"PDF image ").Append(id).Append("\"/><a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">")
            .Append("<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"Image\"/><pic:cNvPicPr/></pic:nvPicPr>")
            .Append("<pic:blipFill><a:blip xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:embed=\"rId").Append(id)
            .Append("\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"").Append(cx)
            .Append("\" cy=\"").Append(cy).Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
    }

    private static IReadOnlyList<PdfExtractedTable> FindOfficeTables(
        PdfDocument document, Page page, CancellationToken cancellationToken)
    {
        // Bound automatic discovery; the explicit table tool supports larger selections.
        if (page.Content.Paths.Count > 2048) return [];
        var regions = new List<PdfContentBounds>();
        foreach (PdfExtractedPath path in page.Content.Paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path.IsClippingPath || path.PaintOperator is not ("S" or "s")) continue;
            PdfContentBounds bounds = path.BoundingBox;
            for (int index = 0; index < regions.Count; index++)
            {
                PdfContentBounds other = regions[index];
                if (bounds.Left > other.Right + 0.75 || bounds.Right < other.Left - 0.75
                    || bounds.Bottom > other.Top + 0.75 || bounds.Top < other.Bottom - 0.75) continue;
                bounds = PdfContentBounds.Union([bounds, other]);
                regions.RemoveAt(index);
                index = -1;
            }
            regions.Add(bounds);
        }
        var tables = new List<PdfExtractedTable>();
        foreach (PdfContentBounds region in regions.OrderByDescending(region => region.Top))
        {
            if (region.Width <= 0 || region.Height <= 0) continue;
            PdfTableExtractionResult result = PdfTableExtractor.Extract(document, page.Index,
                new(region.Left - 0.75, region.Bottom - 0.75, region.Right + 0.75, region.Top + 0.75),
                cancellationToken);
            if (result.Table is { } table && (table.RowCount > 1 || table.ColumnCount > 1))
                tables.Add(table);
        }
        return tables;
    }

    private static IEnumerable<PdfExtractedLine> OfficeLines(PdfPageContent page,
        IReadOnlyList<PdfExtractedTable> tables)
    {
        var tableLetters = tables.SelectMany(table => table.Cells).SelectMany(cell => cell.Letters).ToHashSet();
        foreach (PdfExtractedLine line in page.Lines)
        {
            PdfExtractedTextRun[] runs = line.Runs.Select(run => run.Letters.Where(letter => !tableLetters.Contains(letter)).ToArray())
                .Where(letters => letters.Length > 0).Select(letters => new PdfExtractedTextRun(letters)).ToArray();
            if (runs.Length > 0) yield return new PdfExtractedLine(runs);
        }
    }

    private static void AppendPresentationTable(StringBuilder output, int id, PdfExtractedTable table,
        double pageHeight, PresentationPageLayout layout, PdfStructuredExportFontSubstitutions? fonts)
    {
        long x = layout.OffsetX + (long)Math.Round(table.Bounds.Left * layout.EmuPerPoint);
        long y = layout.OffsetY + (long)Math.Round((pageHeight - table.Bounds.Top) * layout.EmuPerPoint);
        output.Append("<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"").Append(id)
            .Append("\" name=\"PDF table ").Append(id).Append("\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>")
            .Append("<p:xfrm><a:off x=\"").Append(x).Append("\" y=\"").Append(y)
            .Append("\"/><a:ext cx=\"").Append((long)Math.Round(table.Bounds.Width * layout.EmuPerPoint))
            .Append("\" cy=\"").Append((long)Math.Round(table.Bounds.Height * layout.EmuPerPoint))
            .Append("\"/></p:xfrm><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr/><a:tblGrid>");
        for (int column = 0; column < table.ColumnCount; column++)
            output.Append("<a:gridCol w=\"").Append((long)Math.Round(table.GetCell(0, column).Bounds.Width * layout.EmuPerPoint)).Append("\"/>");
        output.Append("</a:tblGrid>");
        for (int row = 0; row < table.RowCount; row++)
        {
            output.Append("<a:tr h=\"").Append((long)Math.Round(table.GetCell(row, 0).Bounds.Height * layout.EmuPerPoint)).Append("\">");
            for (int column = 0; column < table.ColumnCount; column++)
            {
                PdfExtractedTableCell cell = table.GetCell(row, column);
                string font = fonts?.Resolve(cell.Letters.FirstOrDefault()?.FontName, "Arial")
                    ?? cell.Letters.FirstOrDefault()?.FontName ?? "Arial";
                int size = Math.Clamp((int)Math.Round((cell.Letters.FirstOrDefault()?.PointSize ?? 12) * layout.EmuPerPoint / 127), 100, 400000);
                output.Append("<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>");
                foreach (string line in cell.Text.Split('\n'))
                {
                    output.Append("<a:p>");
                    if (line.Length > 0) output.Append("<a:r><a:rPr sz=\"").Append(size)
                        .Append("\"><a:latin typeface=\"").Append(System.Net.WebUtility.HtmlEncode(font))
                        .Append("\"/></a:rPr><a:t>").Append(System.Net.WebUtility.HtmlEncode(line)).Append("</a:t></a:r>");
                    output.Append("</a:p>");
                }
                output.Append("</a:txBody><a:tcPr>");
                foreach (string edge in new[] { "L", "R", "T", "B" })
                    output.Append("<a:ln").Append(edge).Append(" w=\"6350\"><a:solidFill><a:srgbClr val=\"000000\"/></a:solidFill></a:ln").Append(edge).Append('>');
                output.Append("</a:tcPr></a:tc>");
            }
            output.Append("</a:tr>");
        }
        output.Append("</a:tbl></a:graphicData></a:graphic></p:graphicFrame>");
    }

    private static bool ContinuesOfficeParagraph(PdfExtractedLine previous, PdfExtractedLine next)
    {
        double size = previous.Runs.FirstOrDefault()?.PointSize ?? 12;
        double gap = previous.BoundingBox.Bottom - next.BoundingBox.Top;
        return Math.Abs(previous.BoundingBox.Left - next.BoundingBox.Left) <= 3
            && gap >= 0 && gap <= Math.Max(3, size * 0.8)
            && Math.Abs(size - (next.Runs.FirstOrDefault()?.PointSize ?? 12)) <= 0.5
            && previous.Runs.FirstOrDefault()?.FontName == next.Runs.FirstOrDefault()?.FontName;
    }

    private static void AppendWordParagraph(StringBuilder output, IReadOnlyList<PdfExtractedLine> lines,
        double spaceBefore, PdfStructuredExportFontSubstitutions? fonts)
    {
        output.Append("<w:p><w:pPr><w:spacing w:before=\"")
            .Append(Math.Clamp((int)Math.Round(spaceBefore * 20), 0, 14400)).Append("\" w:after=\"0\"");
        if (lines.Count > 1)
            output.Append(" w:line=\"").Append(Math.Max(20, (int)Math.Round(
                (lines[0].BoundingBox.Top - lines[1].BoundingBox.Top) * 20))).Append("\" w:lineRule=\"atLeast\"");
        output.Append("/></w:pPr>");
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) output.Append("<w:r><w:br/></w:r>");
            PdfExtractedLine line = lines[index];
            if (line.Runs.Count == 0 || string.Concat(line.RunTexts) != line.Text)
                AppendWordRun(output, line.Text, line.Runs.FirstOrDefault(), fonts);
            else
                for (int run = 0; run < line.Runs.Count; run++) AppendWordRun(output, line.RunTexts[run], line.Runs[run], fonts);
        }
        output.Append("</w:p>");
    }

    private static void AppendWordTable(StringBuilder output, PdfExtractedTable table,
        PdfStructuredExportFontSubstitutions? fonts)
    {
        output.Append("<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>");
        foreach (string edge in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            output.Append("<w:").Append(edge).Append(" w:val=\"single\" w:sz=\"4\" w:color=\"000000\"/>");
        output.Append("</w:tblBorders></w:tblPr><w:tblGrid>");
        for (int column = 0; column < table.ColumnCount; column++)
            output.Append("<w:gridCol w:w=\"").Append((int)Math.Round(table.GetCell(0, column).Bounds.Width * 20)).Append("\"/>");
        output.Append("</w:tblGrid>");
        for (int row = 0; row < table.RowCount; row++)
        {
            output.Append("<w:tr>");
            for (int column = 0; column < table.ColumnCount; column++)
            {
                PdfExtractedTableCell cell = table.GetCell(row, column);
                output.Append("<w:tc><w:tcPr><w:tcW w:w=\"")
                    .Append((int)Math.Round(cell.Bounds.Width * 20)).Append("\" w:type=\"dxa\"/></w:tcPr>");
                foreach (string line in cell.Text.Split('\n'))
                {
                    output.Append("<w:p>");
                    if (line.Length > 0) AppendWordRun(output, line,
                        cell.Letters.Count > 0 ? new PdfExtractedTextRun(cell.Letters) : null, fonts);
                    output.Append("</w:p>");
                }
                output.Append("</w:tc>");
            }
            output.Append("</w:tr>");
        }
        output.Append("</w:tbl><w:p/>");
    }
}
