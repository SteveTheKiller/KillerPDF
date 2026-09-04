using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.Documents;

/// <summary>Reusable settings for a page-numbering macro step.</summary>
public sealed record PdfPageNumberMacroOptions
{
    /// <summary>Gets the header or footer template.</summary>
    public string Template { get; init; } = "{page}";
    /// <summary>Gets the deterministic date used by the date token.</summary>
    public required DateOnly Date { get; init; }
    /// <summary>Gets the optional source filename used by the filename token.</summary>
    public string? FileName { get; init; }
    /// <summary>Gets the page edge used for placement.</summary>
    public PdfPageFurnitureEdge Edge { get; init; } = PdfPageFurnitureEdge.Footer;
    /// <summary>Gets the horizontal alignment.</summary>
    public PdfPageFurnitureAlignment Alignment { get; init; } = PdfPageFurnitureAlignment.Center;
    /// <summary>Gets the visible page-number format.</summary>
    public PdfPageNumberFormat NumberFormat { get; init; }
    /// <summary>Gets optional zero-based pages. Null selects every page.</summary>
    public IReadOnlyList<int>? PageIndices { get; init; }
    /// <summary>Gets the font size in PDF points.</summary>
    public double FontSize { get; init; } = 10;
    /// <summary>Gets the horizontal page margin in PDF points.</summary>
    public double HorizontalMargin { get; init; } = 18;
    /// <summary>Gets the vertical page margin in PDF points.</summary>
    public double VerticalMargin { get; init; } = 18;
    /// <summary>Gets the optional text color.</summary>
    public PdfRgbColor? Color { get; init; }
    /// <summary>Gets the text opacity.</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>Gets the clockwise text rotation in degrees.</summary>
    public double RotationDegrees { get; init; }
    /// <summary>Gets whether reviewed content collisions may be written.</summary>
    public bool AllowCollisions { get; init; }
}

/// <summary>Creates and executes typed page-numbering macro steps.</summary>
public static class PdfPageFurnitureMacro
{
    private const string OptionsKey = "options";

    /// <summary>Creates a reusable page-numbering step.</summary>
    public static PdfMacroStep NumberPagesStep(PdfPageNumberMacroOptions options)
    {
        Validate(options);
        return new PdfMacroStep(PdfMacroOperation.NumberPages,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OptionsKey] = JsonSerializer.Serialize(options, JsonOptions())
            });
    }

    /// <summary>Formats, places, checks, and writes one page-numbering step.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.NumberPages)
            throw new ArgumentException(
                "The macro step is not a page-numbering operation.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        if (step.Settings is null || step.Settings.Count != 1
            || !step.Settings.TryGetValue(OptionsKey, out string? json))
            throw new ArgumentException(
                "The page-numbering macro settings are invalid.", nameof(step));
        PdfPageNumberMacroOptions options;
        try
        {
            options = JsonSerializer.Deserialize<PdfPageNumberMacroOptions>(json, JsonOptions())
                ?? throw new JsonException("The page-numbering options are empty.");
            Validate(options);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The page-numbering macro settings are invalid.", nameof(step), error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        PdfDocument document = PdfDocument.Open(source);
        IReadOnlyList<PdfPageBoxInformation> boxes = PdfPageBoxInformation.Read(document);
        IReadOnlyList<PdfPageFurnitureContext> contexts =
            PdfPageFurnitureFormatter.CreateContexts(document, options.Date, options.FileName);
        int[] pages = options.PageIndices?.ToArray() ?? [.. Enumerable.Range(0, boxes.Count)];
        if (pages.Any(page => page >= boxes.Count))
            throw new ArgumentOutOfRangeException(nameof(step),
                "A selected page is outside the source document.");
        var content = new PdfPageContentReader(document);
        var marks = new List<PdfPageFurnitureMark>(pages.Length);
        foreach (int pageIndex in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageFurnitureContext context = contexts[pageIndex] with
            {
                PageNumberFormat = options.NumberFormat
            };
            string text = PdfPageFurnitureFormatter.Format(options.Template, context);
            if (text.Length == 0)
                throw new InvalidOperationException(
                    $"The page-numbering template produced empty text for page {pageIndex + 1}.");
            PdfPageBoxBounds crop = boxes[pageIndex].CropBox;
            PdfPageContent pageContent = content.Read(pageIndex);
            PdfContentBounds[] occupied = [.. pageContent.Lines.Select(line => line.BoundingBox)
                .Concat(pageContent.Images.Select(image => image.BoundingBox))
                .Concat(pageContent.Paths.Select(path => path.BoundingBox))
                .Concat(pageContent.Shadings.Select(shading => shading.BoundingBox))];
            double width = Math.Max(options.FontSize * 0.55, text.Length * options.FontSize * 0.55);
            PdfPageFurniturePlacement placement = PdfPageFurniturePlacementPlanner.Plan(
                crop.Width, crop.Height, width, options.FontSize,
                options.HorizontalMargin, options.VerticalMargin,
                options.Edge, options.Alignment,
                occupied.Select(bounds => new PdfContentBounds(
                    bounds.Left - crop.Left, bounds.Bottom - crop.Bottom,
                    bounds.Right - crop.Left, bounds.Top - crop.Bottom)));
            if (placement.HasCollision && !options.AllowCollisions)
                throw new InvalidOperationException(
                    $"Page-numbering placement collides with content on page {pageIndex + 1}.");
            marks.Add(new PdfPageFurnitureMark(pageIndex, text,
                crop.Left + placement.Bounds.Left,
                crop.Bottom + placement.Bounds.Bottom + options.FontSize * 0.8,
                options.FontSize, options.Color, options.Opacity, options.RotationDegrees));
        }
        return PdfPageFurnitureWriter.Apply(document, marks);
    }

    private static void Validate(PdfPageNumberMacroOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.Template))
            throw new ArgumentException("A page-numbering template is required.", nameof(options));
        if (!Enum.IsDefined(options.Edge) || !Enum.IsDefined(options.Alignment)
            || !Enum.IsDefined(options.NumberFormat))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!double.IsFinite(options.FontSize) || options.FontSize <= 0
            || !double.IsFinite(options.HorizontalMargin) || options.HorizontalMargin < 0
            || !double.IsFinite(options.VerticalMargin) || options.VerticalMargin < 0
            || !double.IsFinite(options.Opacity) || options.Opacity is < 0 or > 1
            || !double.IsFinite(options.RotationDegrees))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.PageIndices is not null)
        {
            int[] pages = options.PageIndices.ToArray();
            if (pages.Length == 0 || pages.Any(page => page < 0)
                || pages.Distinct().Count() != pages.Length)
                throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
