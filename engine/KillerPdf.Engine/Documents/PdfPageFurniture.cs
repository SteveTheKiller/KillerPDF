using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Values available while formatting a repeated page header or footer.</summary>
public sealed record PdfPageFurnitureContext
{
    /// <summary>Gets the one-based physical page number.</summary>
    public required int PageNumber { get; init; }
    /// <summary>Gets the total physical page count.</summary>
    public required int TotalPages { get; init; }
    /// <summary>Gets the optional logical page label.</summary>
    public string? PageLabel { get; init; }
    /// <summary>Gets the optional source filename.</summary>
    public string? FileName { get; init; }
    /// <summary>Gets the optional document title.</summary>
    public string? Title { get; init; }
    /// <summary>Gets the optional document author.</summary>
    public string? Author { get; init; }
    /// <summary>Gets the date used for deterministic formatting.</summary>
    public required DateOnly Date { get; init; }
    /// <summary>Gets additional case-sensitive token values.</summary>
    public IReadOnlyDictionary<string, string?> CustomTokens { get; init; }
        = new Dictionary<string, string?>();
}

/// <summary>Formats header and footer templates with bounded, explicit tokens.</summary>
public static class PdfPageFurnitureFormatter
{
    /// <summary>Expands page, pages, label, filename, title, author, date, and custom tokens.</summary>
    public static string Format(string template, PdfPageFurnitureContext context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        if (context.PageNumber <= 0 || context.TotalPages <= 0
            || context.PageNumber > context.TotalPages)
            throw new ArgumentOutOfRangeException(nameof(context),
                "Page numbering must be within the document page count.");
        if (template.Length > 1_000_000)
            throw new ArgumentException("A page-furniture template cannot exceed 1,000,000 characters.",
                nameof(template));

        var values = new Dictionary<string, string?>(context.CustomTokens, StringComparer.Ordinal)
        {
            ["page"] = context.PageNumber.ToString(CultureInfo.InvariantCulture),
            ["pages"] = context.TotalPages.ToString(CultureInfo.InvariantCulture),
            ["label"] = context.PageLabel,
            ["filename"] = context.FileName,
            ["title"] = context.Title,
            ["author"] = context.Author,
            ["date"] = context.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        var output = new StringBuilder(template.Length);
        for (int index = 0; index < template.Length;)
        {
            int opening = template.IndexOf('{', index);
            if (opening < 0)
            {
                output.Append(template, index, template.Length - index);
                break;
            }
            output.Append(template, index, opening - index);
            if (opening + 1 < template.Length && template[opening + 1] == '{')
            {
                output.Append('{');
                index = opening + 2;
                continue;
            }
            int closing = template.IndexOf('}', opening + 1);
            if (closing < 0) throw new FormatException("A page-furniture token is not closed.");
            string name = template[(opening + 1)..closing];
            if (name.Length == 0) throw new FormatException("A page-furniture token has no name.");
            if (!values.TryGetValue(name, out string? value))
                throw new KeyNotFoundException($"The page-furniture token '{name}' is not defined.");
            output.Append(value);
            index = closing + 1;
        }
        return output.ToString();
    }
}

/// <summary>The vertical edge used for repeated page furniture.</summary>
public enum PdfPageFurnitureEdge
{
    /// <summary>The top edge of the page.</summary>
    Header,
    /// <summary>The bottom edge of the page.</summary>
    Footer
}

/// <summary>The horizontal alignment used for repeated page furniture.</summary>
public enum PdfPageFurnitureAlignment
{
    /// <summary>Align to the left margin.</summary>
    Left,
    /// <summary>Center between the page edges.</summary>
    Center,
    /// <summary>Align to the right margin.</summary>
    Right
}

/// <summary>A planned page-furniture placement and its content collisions.</summary>
public sealed record PdfPageFurniturePlacement(
    PdfContentBounds Bounds, IReadOnlyList<PdfContentBounds> Collisions)
{
    /// <summary>Gets whether the planned placement overlaps existing content.</summary>
    public bool HasCollision => Collisions.Count > 0;
}

/// <summary>Plans header and footer placement before any page is changed.</summary>
public static class PdfPageFurniturePlacementPlanner
{
    /// <summary>Places measured furniture inside the selected page edge and reports overlaps.</summary>
    public static PdfPageFurniturePlacement Plan(double pageWidth, double pageHeight,
        double contentWidth, double contentHeight, double horizontalMargin,
        double verticalMargin, PdfPageFurnitureEdge edge,
        PdfPageFurnitureAlignment alignment, IEnumerable<PdfContentBounds>? pageContent = null)
    {
        if (!double.IsFinite(pageWidth) || pageWidth <= 0
            || !double.IsFinite(pageHeight) || pageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageWidth),
                "Page dimensions must be finite and positive.");
        if (!double.IsFinite(contentWidth) || contentWidth <= 0
            || !double.IsFinite(contentHeight) || contentHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentWidth),
                "Furniture dimensions must be finite and positive.");
        if (!double.IsFinite(horizontalMargin) || horizontalMargin < 0
            || !double.IsFinite(verticalMargin) || verticalMargin < 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalMargin),
                "Furniture margins must be finite and nonnegative.");
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        if (contentWidth + horizontalMargin * 2 > pageWidth
            || contentHeight + verticalMargin > pageHeight)
            throw new ArgumentException("The furniture does not fit within the requested page margins.");

        double left = alignment switch
        {
            PdfPageFurnitureAlignment.Left => horizontalMargin,
            PdfPageFurnitureAlignment.Center => (pageWidth - contentWidth) / 2,
            PdfPageFurnitureAlignment.Right => pageWidth - horizontalMargin - contentWidth,
            _ => throw new ArgumentOutOfRangeException(nameof(alignment))
        };
        double bottom = edge == PdfPageFurnitureEdge.Header
            ? pageHeight - verticalMargin - contentHeight : verticalMargin;
        var bounds = new PdfContentBounds(left, bottom, left + contentWidth, bottom + contentHeight);
        PdfContentBounds[] collisions = [.. (pageContent ?? [])
            .Where(candidate => candidate.Right > bounds.Left && candidate.Left < bounds.Right
                && candidate.Top > bounds.Bottom && candidate.Bottom < bounds.Top)];
        return new PdfPageFurniturePlacement(bounds, Array.AsReadOnly(collisions));
    }
}

/// <summary>One text mark to write into a page header or footer.</summary>
public sealed record PdfPageFurnitureMark(
    int PageIndex, string Text, double X, double Baseline, double FontSize = 10,
    PdfRgbColor? Color = null, double Opacity = 1, double RotationDegrees = 0);

/// <summary>Writes reviewed page-furniture marks as decorative page artifacts.</summary>
public static class PdfPageFurnitureWriter
{
    /// <summary>Appends the supplied marks without changing the document's logical structure.</summary>
    public static byte[] Apply(PdfDocument document, IEnumerable<PdfPageFurnitureMark> marks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(marks);
        PdfPageFurnitureMark[] requested = marks.ToArray();
        if (requested.Length == 0)
            throw new ArgumentException("At least one page-furniture mark is required.", nameof(marks));
        IReadOnlyList<PdfPageBoxInformation> pages = PdfPageBoxInformation.Read(document);
        var editor = new PdfIncrementalPageEditor(document);
        foreach (PdfPageFurnitureMark mark in requested)
        {
            if (mark.PageIndex < 0 || mark.PageIndex >= pages.Count)
                throw new ArgumentOutOfRangeException(nameof(marks));
            if (string.IsNullOrEmpty(mark.Text))
                throw new ArgumentException("Page-furniture text cannot be empty.", nameof(marks));
            if (!double.IsFinite(mark.X) || !double.IsFinite(mark.Baseline)
                || !double.IsFinite(mark.FontSize) || mark.FontSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(marks));
            if (!double.IsFinite(mark.Opacity) || mark.Opacity is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(marks));
            if (!double.IsFinite(mark.RotationDegrees))
                throw new ArgumentOutOfRangeException(nameof(marks));
            PdfPageBoxBounds media = pages[mark.PageIndex].MediaBox;
            var content = new PdfContentStreamBuilder().SaveState();
            if (mark.Color is PdfRgbColor color)
                content.SetFillRgb(color.Red, color.Green, color.Blue);
            if (mark.Opacity != 1) content.SetOpacity(mark.Opacity);
            if (mark.RotationDegrees != 0)
            {
                double radians = mark.RotationDegrees * Math.PI / 180;
                content.Transform(Math.Cos(radians), Math.Sin(radians),
                    -Math.Sin(radians), Math.Cos(radians), mark.X, mark.Baseline);
            }
            content.BeginText()
                .SetFont(PdfStandardFont.Helvetica, mark.FontSize)
                .MoveText(mark.RotationDegrees == 0 ? mark.X : 0,
                    mark.RotationDegrees == 0 ? mark.Baseline : 0)
                .ShowLatin1Text(mark.Text).EndText().RestoreState();
            editor.AppendPageArtifact(mark.PageIndex, media.Width, media.Height, content);
        }
        return editor.Build();
    }
}

/// <summary>Settings for continuous Bates numbering across an ordered document batch.</summary>
public sealed record PdfBatesNumberingOptions
{
    /// <summary>Gets the first numeric value.</summary>
    public long StartNumber { get; init; } = 1;
    /// <summary>Gets the minimum zero-padded digit count.</summary>
    public int DigitCount { get; init; } = 6;
    /// <summary>Gets the text before the numeric value.</summary>
    public string Prefix { get; init; } = string.Empty;
    /// <summary>Gets the text after the numeric value.</summary>
    public string Suffix { get; init; } = string.Empty;
}

/// <summary>One deterministic Bates value assigned to a page.</summary>
public sealed record PdfBatesNumber(int DocumentIndex, int PageIndex, long Number, string Text);

/// <summary>Plans continuous Bates numbering in document and page order.</summary>
public static class PdfBatesNumbering
{
    /// <summary>Assigns one Bates value to every page in an ordered batch.</summary>
    public static IReadOnlyList<PdfBatesNumber> Plan(
        IEnumerable<int> documentPageCounts, PdfBatesNumberingOptions options)
    {
        ArgumentNullException.ThrowIfNull(documentPageCounts);
        ArgumentNullException.ThrowIfNull(options);
        if (options.StartNumber < 0) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.DigitCount is < 1 or > 18) throw new ArgumentOutOfRangeException(nameof(options));
        int[] counts = documentPageCounts.ToArray();
        if (counts.Any(count => count < 0))
            throw new ArgumentException("Document page counts cannot be negative.", nameof(documentPageCounts));
        long pageCount = counts.Aggregate(0L, (total, count) => checked(total + count));
        if (pageCount > 0) _ = checked(options.StartNumber + pageCount - 1);

        var result = new List<PdfBatesNumber>();
        long number = options.StartNumber;
        for (int documentIndex = 0; documentIndex < counts.Length; documentIndex++)
        {
            for (int pageIndex = 0; pageIndex < counts[documentIndex]; pageIndex++)
            {
                string text = options.Prefix
                    + number.ToString("D" + options.DigitCount, CultureInfo.InvariantCulture)
                    + options.Suffix;
                result.Add(new PdfBatesNumber(documentIndex, pageIndex, number, text));
                number++;
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }
}
