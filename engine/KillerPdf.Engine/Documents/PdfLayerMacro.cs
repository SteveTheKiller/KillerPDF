using System.Text.Json;
using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed PDF layer macro steps.</summary>
public static class PdfLayerMacro
{
    /// <summary>Creates a layer-flattening step using default visibility.</summary>
    public static PdfMacroStep FlattenStep() => new(PdfMacroOperation.FlattenLayers);

    /// <summary>Creates a layer-flattening step with an explicit visible layer set.</summary>
    public static PdfMacroStep FlattenStep(IEnumerable<string> visibleLayerNames)
    {
        ArgumentNullException.ThrowIfNull(visibleLayerNames);
        string[] names = visibleLayerNames.ToArray();
        if (names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException(
                "Visible layer names must be nonempty and unique.", nameof(visibleLayerNames));
        return new PdfMacroStep(PdfMacroOperation.FlattenLayers,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["visibleLayers"] = JsonSerializer.Serialize(names)
            });
    }

    /// <summary>Executes one layer macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Operation != PdfMacroOperation.FlattenLayers)
            throw new ArgumentException("The macro step is not a layer operation.", nameof(step));
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        PdfDocument document = PdfDocument.Open(source);
        IReadOnlyCollection<int>? visible = VisibleGroups(step, document);
        cancellationToken.ThrowIfCancellationRequested();
        return PdfOptionalContentEditor.FlattenPageContent(document, visible);
    }

    private static IReadOnlyCollection<int>? VisibleGroups(
        PdfMacroStep step, PdfDocument document)
    {
        if (step.Settings is null) return null;
        if (step.Settings.Count != 1
            || !step.Settings.TryGetValue("visibleLayers", out string? json))
            throw new ArgumentException("The layer macro settings are invalid.", nameof(step));
        string[] names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(json)
                ?? throw new JsonException("The visible layer list is empty.");
        }
        catch (JsonException error)
        {
            throw new ArgumentException("The visible layer list is invalid.", nameof(step), error);
        }
        if (names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException(
                "Visible layer names must be nonempty and unique.", nameof(step));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        Dictionary<string, int> groups;
        try
        {
            groups = info.Groups.ToDictionary(group => group.Name,
                group => group.ObjectNumber, StringComparer.Ordinal);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException(
                "Layer names are not unique, so a name-based macro is ambiguous.", error);
        }
        if (names.Any(name => !groups.ContainsKey(name)))
            throw new ArgumentException(
                "The visible layer list contains a layer not found in the document.", nameof(step));
        return Array.AsReadOnly(names.Select(name => groups[name]).ToArray());
    }
}
