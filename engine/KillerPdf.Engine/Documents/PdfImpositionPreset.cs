using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>The source-page boundary fitted into an imposed sheet slot.</summary>
public enum PdfImpositionSourceBox
{
    /// <summary>Use the visible crop boundary.</summary>
    Crop,
    /// <summary>Use the bleed boundary.</summary>
    Bleed,
    /// <summary>Use the finished trim boundary.</summary>
    Trim,
    /// <summary>Use the meaningful-art boundary.</summary>
    Art
}

/// <summary>The sheet edge used when turning duplex imposition output.</summary>
public enum PdfImpositionBindingEdge
{
    /// <summary>Turns each sheet along its long edge.</summary>
    Long,
    /// <summary>Turns each sheet along its short edge.</summary>
    Short
}

/// <summary>Reusable sheet and grid settings for an N-up imposition job.</summary>
public sealed partial record PdfImpositionPreset
{
    private static readonly PdfImpositionPresetJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfImpositionPresetJsonContext IndentedJson = new(JsonOptions(true));
    private static readonly PdfImpositionPresetJsonContext ReaderJson = new(
        JsonOptions(false, caseInsensitive: true));

    /// <summary>Creates a validated reusable imposition preset.</summary>
    public PdfImpositionPreset(string name, int columns, int rows,
        double sheetWidth, double sheetHeight, double margin = 0, double gutter = 0,
        bool duplex = false, bool rotateToFit = true,
        bool includeCropMarks = false, bool includeRegistrationMarks = false,
        double creepPerSheet = 0, bool includeFoldMarks = false,
        bool includeColorBars = false, bool includePageInformation = false,
        PdfImpositionSourceBox sourceBox = PdfImpositionSourceBox.Crop,
        PdfImpositionBindingEdge bindingEdge = PdfImpositionBindingEdge.Long)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A preset name is required.", nameof(name));
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));
        if (!double.IsFinite(sheetWidth) || sheetWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheetWidth));
        if (!double.IsFinite(sheetHeight) || sheetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheetHeight));
        if (!double.IsFinite(margin) || margin < 0)
            throw new ArgumentOutOfRangeException(nameof(margin));
        if (!double.IsFinite(gutter) || gutter < 0)
            throw new ArgumentOutOfRangeException(nameof(gutter));
        if (!double.IsFinite(creepPerSheet) || creepPerSheet < 0)
            throw new ArgumentOutOfRangeException(nameof(creepPerSheet));
        if (!Enum.IsDefined(sourceBox))
            throw new ArgumentOutOfRangeException(nameof(sourceBox));
        if (!Enum.IsDefined(bindingEdge))
            throw new ArgumentOutOfRangeException(nameof(bindingEdge));
        double usableWidth = sheetWidth - margin * 2 - gutter * (columns - 1);
        double usableHeight = sheetHeight - margin * 2 - gutter * (rows - 1);
        if (usableWidth <= 0 || usableHeight <= 0)
            throw new ArgumentException("Margins and gutters leave no printable grid area.");
        Name = name;
        Columns = columns;
        Rows = rows;
        SheetWidth = sheetWidth;
        SheetHeight = sheetHeight;
        Margin = margin;
        Gutter = gutter;
        Duplex = duplex;
        RotateToFit = rotateToFit;
        IncludeCropMarks = includeCropMarks;
        IncludeRegistrationMarks = includeRegistrationMarks;
        CreepPerSheet = creepPerSheet;
        IncludeFoldMarks = includeFoldMarks;
        IncludeColorBars = includeColorBars;
        IncludePageInformation = includePageInformation;
        SourceBox = sourceBox;
        BindingEdge = bindingEdge;
    }

    /// <summary>Gets the preset name.</summary>
    public string Name { get; }
    /// <summary>Gets the number of sheet columns.</summary>
    public int Columns { get; }
    /// <summary>Gets the number of sheet rows.</summary>
    public int Rows { get; }
    /// <summary>Gets the sheet width in PDF points.</summary>
    public double SheetWidth { get; }
    /// <summary>Gets the sheet height in PDF points.</summary>
    public double SheetHeight { get; }
    /// <summary>Gets the outside margin in PDF points.</summary>
    public double Margin { get; }
    /// <summary>Gets the grid gutter in PDF points.</summary>
    public double Gutter { get; }
    /// <summary>Gets whether sheet sides are paired for duplex output.</summary>
    public bool Duplex { get; }
    /// <summary>Gets whether source pages may rotate for a better fit.</summary>
    public bool RotateToFit { get; }
    /// <summary>Gets whether crop marks are requested.</summary>
    public bool IncludeCropMarks { get; }
    /// <summary>Gets whether registration marks are requested.</summary>
    public bool IncludeRegistrationMarks { get; }
    /// <summary>Gets the outward content offset added for each inner sheet.</summary>
    public double CreepPerSheet { get; }
    /// <summary>Gets whether grid fold marks are requested.</summary>
    public bool IncludeFoldMarks { get; }
    /// <summary>Gets whether process-color control bars are requested.</summary>
    public bool IncludeColorBars { get; }
    /// <summary>Gets whether sheet and side information is requested.</summary>
    public bool IncludePageInformation { get; }
    /// <summary>Gets the source-page boundary fitted into each sheet slot.</summary>
    public PdfImpositionSourceBox SourceBox { get; }
    /// <summary>Gets the edge used to turn duplex sheets.</summary>
    public PdfImpositionBindingEdge BindingEdge { get; }

    /// <summary>Plans sequential source pages using this preset's grid and duplex setting.</summary>
    public IReadOnlyList<PdfImposedSheetSide> Plan(int pageCount) =>
        PdfImpositionPlanner.PlanNUp(pageCount, Columns, Rows, Duplex);

    /// <summary>Fits a planned side onto this preset's sheet.</summary>
    public IReadOnlyList<PdfImposedPlacement> Place(PdfImposedSheetSide side,
        IReadOnlyList<PdfContentBounds> sourcePageBounds)
    {
        IReadOnlyList<PdfImposedPlacement> placements =
            PdfImpositionPlanner.PlaceOnSheet(side, Columns, Rows, SheetWidth, SheetHeight,
                sourcePageBounds, Margin, Gutter, RotateToFit);
        return CreepPerSheet == 0 ? placements
            : PdfImpositionPlanner.ApplyCreep(side, placements, Columns, CreepPerSheet);
    }

    /// <summary>Exports a data-only preview of sequential sheet sides and placements.</summary>
    public string PreviewJson(IReadOnlyList<PdfContentBounds> sourcePageBounds,
        bool indented = false)
    {
        ArgumentNullException.ThrowIfNull(sourcePageBounds);
        IReadOnlyList<PdfImposedSheetSide> sides = Plan(sourcePageBounds.Count);
        var preview = new PreviewFile(1, Name, SheetWidth, SheetHeight,
            sourcePageBounds.Count, [.. sides.Select(side => new PreviewSide(
                side.SheetIndex, side.Face, side.CreepDepth,
                side.SourcePageIndices.ToArray(), [.. Place(side, sourcePageBounds)]))]);
        return JsonSerializer.Serialize(preview, indented
            ? IndentedJson.PreviewFile : CompactJson.PreviewFile);
    }

    /// <summary>Formats a readable preview of sequential sheet sides and placements.</summary>
    public string PreviewText(IReadOnlyList<PdfContentBounds> sourcePageBounds)
    {
        ArgumentNullException.ThrowIfNull(sourcePageBounds);
        IReadOnlyList<PdfImposedSheetSide> sides = Plan(sourcePageBounds.Count);
        var output = new StringBuilder();
        output.Append("Imposition preset: ").AppendLine(Name);
        output.Append("Sheet: ").Append(Format(SheetWidth)).Append(" x ")
            .Append(Format(SheetHeight)).AppendLine(" PDF points");
        output.Append("Source pages: ")
            .AppendLine(sourcePageBounds.Count.ToString(CultureInfo.InvariantCulture));
        output.Append("Sheet sides: ").AppendLine(sides.Count.ToString(CultureInfo.InvariantCulture));
        foreach (PdfImposedSheetSide side in sides)
        {
            output.Append("  Sheet ").Append((side.SheetIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(side.Face).Append(", creep depth ")
                .AppendLine(side.CreepDepth.ToString(CultureInfo.InvariantCulture));
            for (int slotIndex = 0; slotIndex < side.SourcePageIndices.Count; slotIndex++)
            {
                int? sourcePage = side.SourcePageIndices[slotIndex];
                output.Append("    Slot ").Append((slotIndex + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(": ").AppendLine(sourcePage.HasValue
                        ? $"source page {(sourcePage.Value + 1).ToString(CultureInfo.InvariantCulture)}"
                        : "blank");
            }
            foreach (PdfImposedPlacement placement in Place(side, sourcePageBounds))
            {
                output.Append("      Placement ")
                    .Append((placement.SlotIndex + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(": ").Append(Format(placement.SheetBounds.Left)).Append(", ")
                    .Append(Format(placement.SheetBounds.Bottom)).Append(" to ")
                    .Append(Format(placement.SheetBounds.Right)).Append(", ")
                    .Append(Format(placement.SheetBounds.Top)).Append(", scale ")
                    .Append(Format(placement.Scale)).Append(", rotation ")
                    .Append(placement.Rotation.ToString(CultureInfo.InvariantCulture)).AppendLine(" degrees");
            }
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>Serializes the preset without source document data.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new PresetFile(1, Name, Columns, Rows, SheetWidth, SheetHeight, Margin, Gutter,
            Duplex, RotateToFit, IncludeCropMarks, IncludeRegistrationMarks, CreepPerSheet,
            IncludeFoldMarks, IncludeColorBars, IncludePageInformation, SourceBox, BindingEdge),
        indented ? IndentedJson.PresetFile : CompactJson.PresetFile);

    /// <summary>Reads and validates a saved preset.</summary>
    public static PdfImpositionPreset FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PresetFile file = JsonSerializer.Deserialize(json, ReaderJson.PresetFile)
            ?? throw new JsonException("The imposition preset is empty.");
        if (file.Version != 1)
            throw new NotSupportedException(
                $"Imposition preset version {file.Version} is not supported.");
        return new PdfImpositionPreset(file.Name, file.Columns, file.Rows,
            file.SheetWidth, file.SheetHeight, file.Margin, file.Gutter,
            file.Duplex, file.RotateToFit, file.IncludeCropMarks,
            file.IncludeRegistrationMarks, file.CreepPerSheet, file.IncludeFoldMarks,
            file.IncludeColorBars, file.IncludePageInformation, file.PageBox, file.BindingEdge);
    }

    private sealed record PresetFile(int Version, string Name, int Columns, int Rows,
        double SheetWidth, double SheetHeight, double Margin, double Gutter,
        bool Duplex, bool RotateToFit, bool IncludeCropMarks,
        bool IncludeRegistrationMarks, double CreepPerSheet,
        bool IncludeFoldMarks, bool IncludeColorBars, bool IncludePageInformation,
        PdfImpositionSourceBox PageBox, PdfImpositionBindingEdge BindingEdge);

    private sealed record PreviewFile(int Version, string Preset,
        double SheetWidth, double SheetHeight, int SourcePageCount, PreviewSide[] Sides);

    private sealed record PreviewSide(int SheetIndex, PdfImposedSheetFace Face,
        int CreepDepth, int?[] Slots, PdfImposedPlacement[] Placements);

    private static JsonSerializerOptions JsonOptions(
        bool indented, bool caseInsensitive = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = caseInsensitive,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter<PdfImpositionSourceBox>(
            JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<PdfImpositionBindingEdge>(
            JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<PdfImposedSheetFace>(
            JsonNamingPolicy.CamelCase));
        return options;
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(PresetFile))]
    [JsonSerializable(typeof(PreviewFile))]
    private sealed partial class PdfImpositionPresetJsonContext : JsonSerializerContext;

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
