using System.Net;
using System.Text;
using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>Exports engine-owned page extraction to editable text and web formats.</summary>
public static class PdfStructuredExport
{
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

    private sealed record Page(int Index, PdfPageContent Content);
}
