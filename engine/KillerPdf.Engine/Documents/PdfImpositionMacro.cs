using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed N-up imposition macro steps.</summary>
public static class PdfImpositionMacro
{
    private const string PresetKey = "preset";

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

    /// <summary>Executes one N-up imposition macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.ImposeNUp)
            throw new ArgumentException(
                "The macro step is not an N-up imposition operation.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        if (step.Settings is null || step.Settings.Count != 1
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
        IReadOnlyList<PdfImposedSheetSide> sides = preset.Plan(pageCount);
        cancellationToken.ThrowIfCancellationRequested();
        return PdfImpositionExporter.Build(document, sides,
            preset.Columns, preset.Rows, preset.SheetWidth, preset.SheetHeight,
            preset.Margin, preset.Gutter, preset.RotateToFit, preset.CreepPerSheet,
            preset.IncludeCropMarks, preset.IncludeRegistrationMarks,
            preset.IncludeFoldMarks, preset.IncludeColorBars,
            preset.IncludePageInformation);
    }
}
