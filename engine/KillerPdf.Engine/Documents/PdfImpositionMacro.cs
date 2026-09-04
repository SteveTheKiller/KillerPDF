using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed imposition macro steps.</summary>
public static class PdfImpositionMacro
{
    private const string PresetKey = "preset";
    private const string SignaturePagesKey = "signaturePages";

    /// <summary>Creates an N-up imposition step from a reusable preset.</summary>
    public static PdfMacroStep NUpStep(PdfImpositionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new PdfMacroStep(PdfMacroOperation.ImposeNUp,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PresetKey] = preset.ToJson()
            });
    }

    /// <summary>Creates a booklet imposition step from a two-slot duplex preset.</summary>
    public static PdfMacroStep BookletStep(
        PdfImpositionPreset preset, int signaturePageCount = 0)
    {
        ValidateBooklet(preset, signaturePageCount);
        return new PdfMacroStep(PdfMacroOperation.ImposeBooklet,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PresetKey] = preset.ToJson(),
                [SignaturePagesKey] = signaturePageCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    /// <summary>Executes one N-up imposition macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation is not (PdfMacroOperation.ImposeNUp
                or PdfMacroOperation.ImposeBooklet))
            throw new ArgumentException(
                "The macro step is not an imposition operation.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        int expectedSettingCount = step.Operation == PdfMacroOperation.ImposeBooklet ? 2 : 1;
        if (step.Settings is null || step.Settings.Count != expectedSettingCount
            || !step.Settings.TryGetValue(PresetKey, out string? presetJson))
            throw new ArgumentException(
                "The imposition macro settings are invalid.", nameof(step));
        PdfImpositionPreset preset;
        try
        {
            preset = PdfImpositionPreset.FromJson(presetJson);
        }
        catch (Exception error) when (error is ArgumentException
            or InvalidOperationException or JsonException or NotSupportedException)
        {
            throw new ArgumentException(
                "The imposition macro preset is invalid.", nameof(step), error);
        }
        cancellationToken.ThrowIfCancellationRequested();
        PdfDocument document = PdfDocument.Open(source);
        int pageCount = PdfPageTree.Read(document).Pages.Count;
        IReadOnlyList<PdfImposedSheetSide> sides;
        if (step.Operation == PdfMacroOperation.ImposeBooklet)
        {
            if (!step.Settings!.TryGetValue(SignaturePagesKey, out string? value)
                || !int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int signaturePageCount))
                throw new ArgumentException(
                    "The booklet macro signature size is invalid.", nameof(step));
            ValidateBooklet(preset, signaturePageCount);
            sides = signaturePageCount == 0
                ? PdfImpositionPlanner.PlanBooklet(pageCount)
                : PdfImpositionPlanner.PlanBookletSignatures(
                    pageCount, signaturePageCount);
        }
        else
        {
            sides = preset.Plan(pageCount);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return PdfImpositionExporter.Build(document, sides,
            preset.Columns, preset.Rows, preset.SheetWidth, preset.SheetHeight,
            preset.Margin, preset.Gutter, preset.RotateToFit, preset.CreepPerSheet,
            preset.IncludeCropMarks, preset.IncludeRegistrationMarks,
            preset.IncludeFoldMarks, preset.IncludeColorBars,
            preset.IncludePageInformation, preset.SourceBox, preset.BindingEdge);
    }

    private static void ValidateBooklet(
        PdfImpositionPreset preset, int signaturePageCount)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Columns * preset.Rows != 2 || !preset.Duplex)
            throw new ArgumentException(
                "A booklet preset must use two slots and duplex output.", nameof(preset));
        if (signaturePageCount != 0
            && (signaturePageCount < 4 || signaturePageCount % 4 != 0))
            throw new ArgumentOutOfRangeException(nameof(signaturePageCount),
                "A booklet signature size must be zero or a positive multiple of four.");
    }
}
