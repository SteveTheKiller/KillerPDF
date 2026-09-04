using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits optional-content groups while preserving their existing configuration.</summary>
public static class PdfOptionalContentEditor
{
    private static readonly PdfName NameKey = new("Name"u8);
    private static readonly PdfName UsageKey = new("Usage"u8);

    /// <summary>Renames one registered layer by its source object number.</summary>
    public static byte[] RenameGroup(PdfDocument document, int objectNumber, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A layer name is required.", nameof(name));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        PdfOptionalContentGroupInfo group = FindGroup(info, objectNumber);
        if (info.Groups.Any(item => item.ObjectNumber != objectNumber
                && string.Equals(item.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Layer names must be unique.", nameof(name));
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The optional-content group is not a dictionary.");
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[NameKey] = UnicodeString(name);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(entries))
            .Build();
    }

    /// <summary>Sets or clears a layer's preferred print visibility.</summary>
    public static byte[] SetPrintVisibility(
        PdfDocument document, int objectNumber, bool? visible) =>
        SetUsageVisibility(document, objectNumber, "Print", "PrintState", visible);

    /// <summary>Sets or clears a layer's preferred export visibility.</summary>
    public static byte[] SetExportVisibility(
        PdfDocument document, int objectNumber, bool? visible) =>
        SetUsageVisibility(document, objectNumber, "Export", "ExportState", visible);

    private static byte[] SetUsageVisibility(PdfDocument document, int objectNumber,
        string categoryName, string stateName, bool? visible)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), objectNumber);
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The optional-content group is not a dictionary.");
        var groupEntries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        var categoryKey = new PdfName(System.Text.Encoding.ASCII.GetBytes(categoryName));
        var stateKey = new PdfName(System.Text.Encoding.ASCII.GetBytes(stateName));
        var usageEntries = ResolveDictionary(document, groupEntries.GetValueOrDefault(UsageKey));
        var categoryEntries = ResolveDictionary(document, usageEntries.GetValueOrDefault(categoryKey));
        if (visible.HasValue)
            categoryEntries[stateKey] = new PdfName(visible.Value ? "ON"u8 : "OFF"u8);
        else
            categoryEntries.Remove(stateKey);
        if (categoryEntries.Count == 0)
            usageEntries.Remove(categoryKey);
        else
            usageEntries[categoryKey] = new PdfDictionary(categoryEntries);
        if (usageEntries.Count == 0)
            groupEntries.Remove(UsageKey);
        else
            groupEntries[UsageKey] = new PdfDictionary(usageEntries);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(groupEntries))
            .Build();
    }

    private static PdfOptionalContentGroupInfo FindGroup(
        PdfOptionalContentInfo info, int objectNumber) =>
        info.Groups.FirstOrDefault(item => item.ObjectNumber == objectNumber)
        ?? throw new KeyNotFoundException(
            $"Optional-content group {objectNumber} is not registered.");

    private static Dictionary<PdfName, PdfObject> ResolveDictionary(
        PdfDocument document, PdfObject? value) => value is null
        ? []
        : ((value is PdfIndirectReference reference ? document.Resolve(reference) : value)
            as PdfDictionary
            ?? throw new InvalidOperationException("Optional-content usage metadata is not a dictionary."))
        .ToDictionary(entry => entry.Key, entry => entry.Value);

    private static PdfString UnicodeString(string value)
    {
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(value);
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        text.CopyTo(bytes, 2);
        return new PdfString(bytes, PdfStringForm.Hexadecimal);
    }
}
