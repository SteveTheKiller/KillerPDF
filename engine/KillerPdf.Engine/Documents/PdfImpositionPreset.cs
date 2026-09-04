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

/// <summary>Reusable sheet and grid settings for an N-up imposition job.</summary>
public sealed record PdfImpositionPreset
{
    /// <summary>Creates a validated reusable imposition preset.</summary>
    public PdfImpositionPreset(string name, int columns, int rows,
        double sheetWidth, double sheetHeight, double margin = 0, double gutter = 0,
        bool duplex = false, bool rotateToFit = true,
        bool includeCropMarks = false, bool includeRegistrationMarks = false,
        double creepPerSheet = 0, bool includeFoldMarks = false,
        bool includeColorBars = false, bool includePageInformation = false,
        PdfImpositionSourceBox sourceBox = PdfImpositionSourceBox.Crop)
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

    /// <summary>Serializes the preset without source document data.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new PresetFile(1, Name, Columns, Rows, SheetWidth, SheetHeight, Margin, Gutter,
            Duplex, RotateToFit, IncludeCropMarks, IncludeRegistrationMarks, CreepPerSheet,
            IncludeFoldMarks, IncludeColorBars, IncludePageInformation, SourceBox),
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });

    /// <summary>Reads and validates a saved preset.</summary>
    public static PdfImpositionPreset FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PresetFile file = JsonSerializer.Deserialize<PresetFile>(json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            })
            ?? throw new JsonException("The imposition preset is empty.");
        if (file.Version != 1)
            throw new NotSupportedException(
                $"Imposition preset version {file.Version} is not supported.");
        return new PdfImpositionPreset(file.Name, file.Columns, file.Rows,
            file.SheetWidth, file.SheetHeight, file.Margin, file.Gutter,
            file.Duplex, file.RotateToFit, file.IncludeCropMarks,
            file.IncludeRegistrationMarks, file.CreepPerSheet, file.IncludeFoldMarks,
            file.IncludeColorBars, file.IncludePageInformation, file.PageBox);
    }

    private sealed record PresetFile(int Version, string Name, int Columns, int Rows,
        double SheetWidth, double SheetHeight, double Margin, double Gutter,
        bool Duplex, bool RotateToFit, bool IncludeCropMarks,
        bool IncludeRegistrationMarks, double CreepPerSheet,
        bool IncludeFoldMarks, bool IncludeColorBars, bool IncludePageInformation,
        PdfImpositionSourceBox PageBox);
}
