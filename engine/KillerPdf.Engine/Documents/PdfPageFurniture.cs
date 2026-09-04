using System.Globalization;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Values available while formatting a repeated page header or footer.</summary>
public sealed record PdfPageFurnitureContext
{
    /// <summary>Gets the one-based physical page number.</summary>
    public required int PageNumber { get; init; }
    /// <summary>Gets the total physical page count.</summary>
    public required int TotalPages { get; init; }
    /// <summary>Gets the visible format used by the page token.</summary>
    public PdfPageNumberFormat PageNumberFormat { get; init; }
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
    /// <summary>Creates one formatting context per page using the document's logical labels.</summary>
    public static IReadOnlyList<PdfPageFurnitureContext> CreateContexts(
        PdfDocument document, DateOnly date, string? fileName = null,
        string? title = null, string? author = null,
        IReadOnlyDictionary<string, string?>? customTokens = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<string> labels = PdfPageLabelReader.Read(document);
        IReadOnlyDictionary<string, string?> tokens = customTokens
            ?? new Dictionary<string, string?>();
        return Array.AsReadOnly(labels.Select((label, index) =>
            new PdfPageFurnitureContext
            {
                PageNumber = index + 1,
                TotalPages = labels.Count,
                PageLabel = label,
                FileName = fileName,
                Title = title,
                Author = author,
                Date = date,
                CustomTokens = tokens
            }).ToArray());
    }

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
            ["page"] = FormatNumber(context.PageNumber, context.PageNumberFormat),
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

    /// <summary>Formats a positive page number independently from the document page label.</summary>
    public static string FormatNumber(int number, PdfPageNumberFormat format)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        return format switch
        {
            PdfPageNumberFormat.Decimal => number.ToString(CultureInfo.InvariantCulture),
            PdfPageNumberFormat.UpperRoman => Roman(number),
            PdfPageNumberFormat.LowerRoman => Roman(number).ToLowerInvariant(),
            PdfPageNumberFormat.UpperLetters => Letters(number),
            PdfPageNumberFormat.LowerLetters => Letters(number).ToLowerInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
    }

    private static string Roman(int number)
    {
        if (number > 3999) throw new ArgumentOutOfRangeException(nameof(number));
        (int Value, string Text)[] values = [(1000, "M"), (900, "CM"), (500, "D"),
            (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var result = new StringBuilder();
        foreach ((int value, string text) in values)
            while (number >= value) { result.Append(text); number -= value; }
        return result.ToString();
    }

    private static string Letters(int number)
    {
        var result = new StringBuilder();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + number % 26));
            number /= 26;
        }
        return result.ToString();
    }
}

/// <summary>The visible numbering format used independently from logical page labels.</summary>
public enum PdfPageNumberFormat
{
    /// <summary>Decimal Arabic numerals.</summary>
    Decimal,
    /// <summary>Uppercase Roman numerals.</summary>
    UpperRoman,
    /// <summary>Lowercase Roman numerals.</summary>
    LowerRoman,
    /// <summary>Uppercase alphabetic sequences.</summary>
    UpperLetters,
    /// <summary>Lowercase alphabetic sequences.</summary>
    LowerLetters
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
    PdfRgbColor? Color = null, double Opacity = 1, double RotationDegrees = 0,
    PdfStandardFont Font = PdfStandardFont.Helvetica);

/// <summary>One KillerPDF-created page-furniture mark recovered from a saved document.</summary>
public sealed record PdfPageFurnitureReportEntry(
    int PageIndex, string Text, double X, double Baseline, double FontSize,
    PdfRgbColor? Color, double Opacity, double RotationDegrees, PdfStandardFont Font);

/// <summary>Inspects versioned metadata attached to KillerPDF-created page furniture.</summary>
public static class PdfPageFurnitureReport
{
    private static readonly PdfName MarkerName = new("KillerPDFPageFurniture"u8);

    /// <summary>Reports recognized furniture without treating ordinary PDF artifacts as owned content.</summary>
    public static IReadOnlyList<PdfPageFurnitureReportEntry> Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<PdfPageFurnitureReportEntry>();
        var reader = new PdfPageContentReader(document);
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            foreach (var instruction in reader.ReadInstructions(pageIndex))
            {
                if (instruction.Operator != "BDC" || instruction.Operands.Count != 2
                    || instruction.Operands[0] is not PdfName tag
                    || tag.ValueAsLatin1() != "Artifact"
                    || instruction.Operands[1] is not PdfDictionary properties
                    || !properties.TryGetValue(MarkerName, out PdfObject? value)
                    || value is not PdfString marker)
                    continue;
                string encoded = Encoding.UTF8.GetString(marker.Bytes.Span);
                if (!encoded.StartsWith("KPF1:", StringComparison.Ordinal)) continue;
                try
                {
                    byte[] json = Convert.FromBase64String(encoded[5..]);
                    MarkerData? data = JsonSerializer.Deserialize<MarkerData>(json);
                    if (data is null || string.IsNullOrEmpty(data.Text)) continue;
                    result.Add(new PdfPageFurnitureReportEntry(pageIndex, data.Text,
                        data.X, data.Baseline, data.FontSize,
                        data.Color is null ? null : new PdfRgbColor(
                            data.Color.Red, data.Color.Green, data.Color.Blue),
                        data.Opacity, data.RotationDegrees, data.Font));
                }
                catch (JsonException) { }
                catch (FormatException) { }
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Exports recognized furniture as stable JSON for review and automation.</summary>
    public static string ToJson(PdfDocument document, bool indented = false)
    {
        IReadOnlyList<PdfPageFurnitureReportEntry> entries = Inspect(document);
        return JsonSerializer.Serialize(new
        {
            Version = 1,
            Count = entries.Count,
            Entries = entries
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        });
    }

    /// <summary>Exports recognized furniture as a readable page-by-page report.</summary>
    public static string ToText(PdfDocument document)
    {
        IReadOnlyList<PdfPageFurnitureReportEntry> entries = Inspect(document);
        var output = new StringBuilder()
            .Append("Page furniture: ")
            .AppendLine(entries.Count.ToString(CultureInfo.InvariantCulture));
        if (entries.Count == 0) return output.AppendLine("No entries.").ToString();
        foreach (PdfPageFurnitureReportEntry entry in entries)
        {
            output.Append("Page ").Append(entry.PageIndex + 1)
                .Append(" | ").Append(JsonSerializer.Serialize(entry.Text))
                .Append(" | X ").Append(entry.X.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" | Baseline ").Append(entry.Baseline.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" | ").Append(entry.FontSize.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" pt | ").Append(entry.Font)
                .Append(" | Opacity ").Append(entry.Opacity.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" | Rotation ").Append(entry.RotationDegrees.ToString("0.###", CultureInfo.InvariantCulture));
            if (entry.Color is PdfRgbColor color)
                output.Append(" | RGB ")
                    .Append(color.Red.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(color.Green.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(color.Blue.ToString("0.###", CultureInfo.InvariantCulture));
            output.AppendLine();
        }
        return output.ToString();
    }

    internal static string CreateMarker(PdfPageFurnitureMark mark) => "KPF1:" +
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new MarkerData
        {
            Text = mark.Text,
            X = mark.X,
            Baseline = mark.Baseline,
            FontSize = mark.FontSize,
            Color = mark.Color is not PdfRgbColor color ? null : new MarkerColor
            {
                Red = color.Red,
                Green = color.Green,
                Blue = color.Blue
            },
            Opacity = mark.Opacity,
            RotationDegrees = mark.RotationDegrees,
            Font = mark.Font
        }));

    internal static bool IsMarker(PdfContentInstruction instruction) =>
        instruction.Operator == "BDC" && instruction.Operands.Count == 2
        && instruction.Operands[0] is PdfName tag
        && tag.ValueAsLatin1() == "Artifact"
        && instruction.Operands[1] is PdfDictionary properties
        && properties.TryGetValue(MarkerName, out PdfObject? value)
        && value is PdfString marker
        && Encoding.UTF8.GetString(marker.Bytes.Span)
            .StartsWith("KPF1:", StringComparison.Ordinal);

    private sealed class MarkerData
    {
        public string? Text { get; set; }
        public double X { get; set; }
        public double Baseline { get; set; }
        public double FontSize { get; set; }
        public MarkerColor? Color { get; set; }
        public double Opacity { get; set; }
        public double RotationDegrees { get; set; }
        public PdfStandardFont Font { get; set; }
    }

    private sealed class MarkerColor
    {
        public double Red { get; set; }
        public double Green { get; set; }
        public double Blue { get; set; }
    }
}

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
            if (!Enum.IsDefined(mark.Font))
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
                .SetFont(mark.Font, mark.FontSize)
                .MoveText(mark.RotationDegrees == 0 ? mark.X : 0,
                    mark.RotationDegrees == 0 ? mark.Baseline : 0)
                .ShowLatin1Text(mark.Text).EndText().RestoreState();
            editor.AppendPageArtifact(mark.PageIndex, media.Width, media.Height, content,
                PdfPageFurnitureReport.CreateMarker(mark));
        }
        return editor.Build();
    }
}

/// <summary>Removes or replaces only versioned KillerPDF-created page furniture.</summary>
public static class PdfPageFurnitureEditor
{
    /// <summary>Removes all recognized furniture while preserving ordinary page content.</summary>
    public static byte[] RemoveAll(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var reader = new PdfPageContentReader(document);
        var editor = new PdfIncrementalPageEditor(document);
        bool changed = false;
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            IReadOnlyList<PdfContentInstruction> source = reader.ReadInstructions(pageIndex);
            var retained = new List<PdfContentInstruction>(source.Count);
            for (int index = 0; index < source.Count; index++)
            {
                if (!PdfPageFurnitureReport.IsMarker(source[index]))
                {
                    retained.Add(source[index]);
                    continue;
                }

                changed = true;
                int depth = 1;
                while (depth > 0 && ++index < source.Count)
                {
                    if (source[index].Operator is "BMC" or "BDC") depth++;
                    else if (source[index].Operator == "EMC") depth--;
                }
                if (depth != 0)
                    throw new FormatException(
                        "KillerPDF page-furniture marked content is not closed.");
            }
            if (retained.Count != source.Count)
                editor.SetPageContent(pageIndex, retained);
        }
        return changed ? editor.Build() : document.Source.ToArray();
    }

    /// <summary>Replaces all recognized furniture with the supplied reviewed marks.</summary>
    public static byte[] ReplaceAll(
        PdfDocument document, IEnumerable<PdfPageFurnitureMark> marks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(marks);
        PdfPageFurnitureMark[] requested = marks.ToArray();
        byte[] removed = RemoveAll(document);
        if (requested.Length == 0) return removed;
        return PdfPageFurnitureWriter.Apply(PdfDocument.Open(removed), requested);
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
    /// <summary>Gets the suffix inserted before the extension of named batch outputs.</summary>
    public string OutputNameSuffix { get; init; } = "_bates";
}

/// <summary>One deterministic Bates value assigned to a page.</summary>
public sealed record PdfBatesNumber(int DocumentIndex, int PageIndex, long Number, string Text);

/// <summary>One named document in an ordered Bates-numbering batch.</summary>
public sealed record PdfBatesBatchInput(string Name, PdfDocument Document);

/// <summary>One named result from an ordered Bates-numbering batch.</summary>
public sealed record PdfBatesBatchResult(string InputName, string OutputName, byte[] Data);

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

    /// <summary>Applies continuous Bates values to an ordered document batch.</summary>
    public static IReadOnlyList<byte[]> ApplyBatch(
        IEnumerable<PdfDocument> documents,
        PdfBatesNumberingOptions options,
        Func<PdfBatesNumber, PdfPageFurnitureMark> createMark)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createMark);
        PdfDocument[] sources = documents.ToArray();
        int[] pageCounts = sources.Select(document =>
        {
            ArgumentNullException.ThrowIfNull(document);
            return PdfPageBoxInformation.Read(document).Count;
        }).ToArray();
        IReadOnlyList<PdfBatesNumber> numbers = Plan(pageCounts, options);
        var outputs = new List<byte[]>(sources.Length);
        for (int documentIndex = 0; documentIndex < sources.Length; documentIndex++)
        {
            PdfPageFurnitureMark[] marks = numbers
                .Where(number => number.DocumentIndex == documentIndex)
                .Select(number =>
                {
                    PdfPageFurnitureMark mark = createMark(number)
                        ?? throw new InvalidOperationException(
                            "The Bates mark factory returned no mark.");
                    if (mark.PageIndex != number.PageIndex)
                        throw new InvalidOperationException(
                            "The Bates mark page does not match its assigned number.");
                    return mark;
                }).ToArray();
            outputs.Add(marks.Length == 0
                ? sources[documentIndex].Source.ToArray()
                : PdfPageFurnitureWriter.Apply(sources[documentIndex], marks));
        }
        return Array.AsReadOnly(outputs.ToArray());
    }

    /// <summary>Applies continuous Bates values and assigns deterministic output names.</summary>
    public static IReadOnlyList<PdfBatesBatchResult> ApplyNamedBatch(
        IEnumerable<PdfBatesBatchInput> inputs,
        PdfBatesNumberingOptions options,
        Func<PdfBatesNumber, PdfPageFurnitureMark> createMark)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        PdfBatesBatchInput[] values = inputs.Select(input => input
            ?? throw new ArgumentException("A Bates batch input cannot be null.", nameof(inputs))).ToArray();
        if (string.IsNullOrWhiteSpace(options.OutputNameSuffix)
            || options.OutputNameSuffix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The Bates output-name suffix is invalid.", nameof(options));
        string[] outputNames = values.Select(input => OutputName(input.Name, options.OutputNameSuffix)).ToArray();
        if (outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputNames.Length)
            throw new ArgumentException("Bates batch output names must be unique.", nameof(inputs));
        IReadOnlyList<byte[]> data = ApplyBatch(values.Select(input => input.Document), options, createMark);
        return Array.AsReadOnly(values.Select((input, index) =>
            new PdfBatesBatchResult(input.Name, outputNames[index], data[index])).ToArray());
    }

    private static string OutputName(string inputName, string suffix)
    {
        if (string.IsNullOrWhiteSpace(inputName) || Path.GetFileName(inputName) != inputName
            || inputName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A Bates batch input name must be a plain file name.", nameof(inputName));
        string extension = Path.GetExtension(inputName);
        string stem = Path.GetFileNameWithoutExtension(inputName);
        if (stem.Length == 0) throw new ArgumentException("A Bates batch input name requires a base name.", nameof(inputName));
        return stem + suffix + (extension.Length == 0 ? ".pdf" : extension);
    }
}
