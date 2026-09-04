using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>A declared output intent and its validated embedded destination profile.</summary>
public sealed record PdfOutputIntentInformation(
    string Subtype,
    string OutputConditionIdentifier,
    string? OutputCondition,
    string? RegistryName,
    string? Information,
    PdfIccProfile Profile);

/// <summary>Reads document-level output intents used for color-managed output.</summary>
public static class PdfOutputIntentInspection
{
    /// <summary>Returns declared output intents in catalog order without changing the document.</summary>
    public static IReadOnlyList<PdfOutputIntentInformation> Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before inspecting output intents.");
        PdfDictionary catalog = Resolve(document, document.Trailer[Name("Root")]) as PdfDictionary
            ?? throw new FormatException("The document catalog is not a dictionary.");
        if (!catalog.TryGetValue(Name("OutputIntents"), out PdfObject? value)) return [];
        PdfArray intents = Resolve(document, value) as PdfArray
            ?? throw new FormatException("The catalog output intents value is not an array.");
        var result = new List<PdfOutputIntentInformation>(intents.Count);
        foreach (PdfObject item in intents)
        {
            PdfDictionary intent = Resolve(document, item) as PdfDictionary
                ?? throw new FormatException("An output intent is not a dictionary.");
            string subtype = RequiredName(document, intent, "S");
            string identifier = RequiredText(document, intent, "OutputConditionIdentifier");
            if (!intent.TryGetValue(Name("DestOutputProfile"), out PdfObject? profileValue)
                || Resolve(document, profileValue) is not PdfStream profileStream)
                throw new FormatException(
                    "An output intent has no embedded destination ICC profile.");
            byte[] profileBytes = PdfStreamDecoder.Decode(
                profileStream, document.Resolve, 64 * 1024 * 1024);
            result.Add(new PdfOutputIntentInformation(
                subtype,
                identifier,
                OptionalText(document, intent, "OutputCondition"),
                OptionalText(document, intent, "RegistryName"),
                OptionalText(document, intent, "Info"),
                PdfIccProfile.Load(profileBytes)));
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static string RequiredName(
        PdfDocument document, PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
        && Resolve(document, value) is PdfName name
            ? name.ValueAsLatin1()
            : throw new FormatException($"An output intent has no valid /{key} name.");

    private static string RequiredText(
        PdfDocument document, PdfDictionary dictionary, string key) =>
        OptionalText(document, dictionary, key)
        ?? throw new FormatException($"An output intent has no valid /{key} text value.");

    private static string? OptionalText(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(
                text.Bytes.Span, $"An output intent /{key} value")
            : throw new FormatException($"An output intent /{key} value is not text.");
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new FormatException("An output intent reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
