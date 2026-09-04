using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed imposition macro steps.</summary>
public static class PdfImpositionMacro
{
    private const string PresetKey = "preset";
    private const string SignaturePagesKey = "signaturePages";
    private const string SourcePageKey = "sourcePage";
    private const string CopyCountKey = "copyCount";
    private const string PageSequenceKey = "pageSequence";

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

    /// <summary>Creates a step-and-repeat imposition step from a reusable preset.</summary>
    public static PdfMacroStep StepAndRepeatStep(
        PdfImpositionPreset preset, int sourcePageIndex, int copyCount)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (sourcePageIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sourcePageIndex));
        if (copyCount < 0)
            throw new ArgumentOutOfRangeException(nameof(copyCount));
        return new PdfMacroStep(PdfMacroOperation.ImposeStepAndRepeat,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PresetKey] = preset.ToJson(),
                [SourcePageKey] = sourcePageIndex.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                [CopyCountKey] = copyCount.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    /// <summary>Creates a simplex cut-stack imposition step from a reusable preset.</summary>
    public static PdfMacroStep CutStackStep(PdfImpositionPreset preset)
    {
        ValidateCutStack(preset);
        return new PdfMacroStep(PdfMacroOperation.ImposeCutStack,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PresetKey] = preset.ToJson()
            });
    }

    /// <summary>Creates a manual page-sequence imposition step from a reusable preset.</summary>
    public static PdfMacroStep ManualSequenceStep(
        PdfImpositionPreset preset, IReadOnlyList<int?> pageSequence)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(pageSequence);
        if (pageSequence.Any(page => page < 0))
            throw new ArgumentOutOfRangeException(nameof(pageSequence));
        return new PdfMacroStep(PdfMacroOperation.ImposeManualSequence,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PresetKey] = preset.ToJson(),
                [PageSequenceKey] = JsonSerializer.Serialize(pageSequence)
            });
    }

    /// <summary>Executes one N-up imposition macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation is not (PdfMacroOperation.ImposeNUp
                or PdfMacroOperation.ImposeBooklet
                or PdfMacroOperation.ImposeStepAndRepeat
                or PdfMacroOperation.ImposeCutStack
                or PdfMacroOperation.ImposeManualSequence))
            throw new ArgumentException(
                "The macro step is not an imposition operation.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        int expectedSettingCount = step.Operation switch
        {
            PdfMacroOperation.ImposeBooklet => 2,
            PdfMacroOperation.ImposeStepAndRepeat => 3,
            PdfMacroOperation.ImposeManualSequence => 2,
            _ => 1
        };
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
        else if (step.Operation == PdfMacroOperation.ImposeStepAndRepeat)
        {
            int sourcePageIndex = SettingInteger(step, SourcePageKey,
                "The step-and-repeat source page is invalid.");
            int copyCount = SettingInteger(step, CopyCountKey,
                "The step-and-repeat copy count is invalid.");
            if (sourcePageIndex < 0 || sourcePageIndex >= pageCount)
                throw new ArgumentOutOfRangeException(nameof(step),
                    "The step-and-repeat source page is outside the source document.");
            if (copyCount < 0)
                throw new ArgumentOutOfRangeException(nameof(step),
                    "The step-and-repeat copy count cannot be negative.");
            sides = PdfImpositionPlanner.PlanStepAndRepeat(sourcePageIndex,
                copyCount, preset.Columns, preset.Rows, preset.Duplex);
        }
        else if (step.Operation == PdfMacroOperation.ImposeCutStack)
        {
            ValidateCutStack(preset);
            sides = PdfImpositionPlanner.PlanCutStack(
                pageCount, preset.Columns, preset.Rows);
        }
        else if (step.Operation == PdfMacroOperation.ImposeManualSequence)
        {
            IReadOnlyList<int?> pageSequence = ReadPageSequence(step);
            sides = PdfImpositionPlanner.PlanManual(pageCount, pageSequence,
                checked(preset.Columns * preset.Rows), preset.Duplex);
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

    private static int SettingInteger(
        PdfMacroStep step, string key, string errorMessage)
    {
        if (!step.Settings!.TryGetValue(key, out string? value)
            || !int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int result))
            throw new ArgumentException(errorMessage, nameof(step));
        return result;
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

    private static void ValidateCutStack(PdfImpositionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Duplex)
            throw new ArgumentException(
                "A cut-stack preset must use simplex output.", nameof(preset));
    }

    private static IReadOnlyList<int?> ReadPageSequence(PdfMacroStep step)
    {
        if (!step.Settings!.TryGetValue(PageSequenceKey, out string? json))
            throw new ArgumentException(
                "The manual page sequence is invalid.", nameof(step));
        try
        {
            return JsonSerializer.Deserialize<int?[]>(json)
                ?? throw new JsonException("The manual page sequence is empty.");
        }
        catch (Exception error) when (error is JsonException or NotSupportedException)
        {
            throw new ArgumentException(
                "The manual page sequence is invalid.", nameof(step), error);
        }
    }
}
