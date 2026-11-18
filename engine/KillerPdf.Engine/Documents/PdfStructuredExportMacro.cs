using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed structured-export macro steps.</summary>
public static partial class PdfStructuredExportMacro
{
    private const string OptionsKey = "options";
    private static readonly PdfStructuredExportMacroJsonContext Json = new(JsonOptions());

    /// <summary>Creates an export step with an optional zero-based page selection.</summary>
    public static PdfMacroStep Step(PdfStructuredExportFormat format,
        IEnumerable<int>? pageIndices = null, PdfOfficeExportMode officeMode = PdfOfficeExportMode.Editable)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        PdfStructuredExport.ValidateOfficeMode(format, officeMode);
        int[]? pages = pageIndices?.ToArray();
        if (pages is not null && (pages.Any(page => page < 0)
            || pages.Distinct().Count() != pages.Length))
            throw new ArgumentOutOfRangeException(nameof(pageIndices));
        return new PdfMacroStep(PdfMacroOperation.Export,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [OptionsKey] = JsonSerializer.Serialize(new ExportOptions(1, format, pages, officeMode),
                    Json.ExportOptions)
            });
    }

    /// <summary>Executes one structured-export step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        ExportOptions options = ReadOptions(step);
        cancellationToken.ThrowIfCancellationRequested();
        PdfDocument document = PdfDocument.Open(source);
        if (PdfPageInformation.Read(document).Select((_, index) => index)
            .Any(index => PdfFormWidgetReader.ReadPage(document, index).Count != 0))
            document = PdfDocument.Open(PdfFormFlattener.Flatten(document));
        IEnumerable<int>? pages = options.PageIndices;
        return options.Format switch
        {
            PdfStructuredExportFormat.PlainText => Encoding.UTF8.GetBytes(
                PdfStructuredExport.ToPlainText(document, pages, cancellationToken)),
            PdfStructuredExportFormat.Html => Encoding.UTF8.GetBytes(
                PdfStructuredExport.ToHtml(document, pages, cancellationToken: cancellationToken)),
            PdfStructuredExportFormat.Markdown => Encoding.UTF8.GetBytes(
                PdfStructuredExport.ToMarkdown(document, pages, cancellationToken)),
            PdfStructuredExportFormat.Json => Encoding.UTF8.GetBytes(
                PdfStructuredExport.ToJson(document, pages, cancellationToken)),
            PdfStructuredExportFormat.WordDocument =>
                PdfStructuredExport.ToDocx(document, pages, cancellationToken: cancellationToken, officeMode: options.OfficeMode),
            PdfStructuredExportFormat.Spreadsheet =>
                PdfStructuredExport.ToXlsx(document, pages, cancellationToken),
            PdfStructuredExportFormat.Presentation =>
                PdfStructuredExport.ToPptx(document, pages, cancellationToken: cancellationToken, officeMode: options.OfficeMode),
            PdfStructuredExportFormat.Epub =>
                KillerPdf.Engine.Formats.Epub.PdfEpubExport.Write(document, pages, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(step))
        };
    }

    /// <summary>Reads the selected export format without opening a document.</summary>
    public static PdfStructuredExportFormat ReadFormat(PdfMacroStep step) => ReadOptions(step).Format;

    /// <summary>Reads the Office layout mode; older saved steps default to editable content.</summary>
    public static PdfOfficeExportMode ReadOfficeMode(PdfMacroStep step) => ReadOptions(step).OfficeMode;

    /// <summary>Changes the Office layout mode while retaining the format and page selection.</summary>
    public static PdfMacroStep WithOfficeMode(PdfMacroStep step, PdfOfficeExportMode officeMode)
    {
        ExportOptions options = ReadOptions(step);
        return Step(options.Format, options.PageIndices, officeMode);
    }

    /// <summary>Changes the format while retaining the saved page selection.</summary>
    public static PdfMacroStep WithFormat(PdfMacroStep step, PdfStructuredExportFormat format) =>
        Step(format, ReadOptions(step).PageIndices,
            format is PdfStructuredExportFormat.WordDocument or PdfStructuredExportFormat.Presentation
                ? ReadOptions(step).OfficeMode : PdfOfficeExportMode.Editable);

    /// <summary>Gets the file extension for an exported artifact.</summary>
    public static string Extension(PdfStructuredExportFormat format) =>
        Path.GetExtension(PdfStructuredExportBatchRunner.OutputName("export.pdf", format));

    private static ExportOptions ReadOptions(PdfMacroStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.Export)
            throw new ArgumentException("The macro step is not an export operation.", nameof(step));
        if (step.Settings is null || step.Settings.Count != 1
            || !step.Settings.TryGetValue(OptionsKey, out string? json))
            throw new ArgumentException("The export macro settings are invalid.", nameof(step));
        ExportOptions options;
        try
        {
            options = JsonSerializer.Deserialize(json, Json.ExportOptions)
                ?? throw new JsonException("The export options are empty.");
            if (options.Version != 1)
                throw new NotSupportedException(
                    $"Export macro version {options.Version} is not supported.");
            if (!Enum.IsDefined(options.Format))
                throw new JsonException("The export format is invalid.");
            PdfStructuredExport.ValidateOfficeMode(options.Format, options.OfficeMode);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new ArgumentException("The export macro settings are invalid.",
                nameof(step), error);
        }

        return options;
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter<PdfStructuredExportFormat>(JsonNamingPolicy.CamelCase),
            new JsonStringEnumConverter<PdfOfficeExportMode>(JsonNamingPolicy.CamelCase)
        }
    };

    private sealed record ExportOptions(int Version, PdfStructuredExportFormat Format,
        int[]? PageIndices, PdfOfficeExportMode OfficeMode = PdfOfficeExportMode.Editable);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ExportOptions))]
    private sealed partial class PdfStructuredExportMacroJsonContext : JsonSerializerContext;
}
