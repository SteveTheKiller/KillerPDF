using System.IO;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.ColorManagement;

namespace KillerPDF.Services;

/// <summary>Describes whether recorded proof preferences belong to the loaded file.</summary>
public enum DocumentProofMatch
{
    /// <summary>No preferences are recorded for the path.</summary>
    NotFound,
    /// <summary>The recorded path and contents both match.</summary>
    Matched,
    /// <summary>The path has preferences for different contents.</summary>
    DifferentContent
}

/// <summary>Restored requested preferences and any previous-file settings offered for review.</summary>
public sealed record DocumentProofRestore(DocumentProofMatch Match, PdfDocumentProofSettings Settings,
    ProofProfileResolutionKind ProfileKind, IReadOnlyList<DocumentProofReview>? PreviousForReview = null);

/// <summary>Identifies the recorded contents to which a reviewable snapshot belongs.</summary>
public sealed record DocumentProofReview(string SourceSha256, PdfProofPreset Preset);

/// <summary>Remembers document proof preferences using a path and coherent loaded-byte identity.</summary>
public sealed class DocumentProofStore(string directory)
{
    /// <summary>Uses the application's current user-data location.</summary>
    public DocumentProofStore() : this(AppDataPaths.DocumentProofDirectory) { }

    private readonly ProofPresetLibrary _library = new(directory);

    /// <summary>Stores an immutable preference snapshot for the supplied source-content SHA256.</summary>
    public void Save(string documentPath, string sourceSha256, PdfDocumentProofSettings settings)
    {
        var preset = PdfProofPreset.Capture(Key(documentPath, sourceSha256), settings);
        _ = ReadRecords();
        _library.Update(records =>
        {
            records.RemoveAll(item => item.Name == preset.Name);
            records.Add(preset);
            if (records.Count > ProofPresetLibrary.MaximumPresets)
                records.RemoveRange(0, records.Count - ProofPresetLibrary.MaximumPresets);
        });
    }

    /// <summary>Restores only matching source contents, resolving profiles from owned copies.</summary>
    public DocumentProofRestore Restore(string documentPath, string sourceSha256, ProofProfileLibrary profiles)
    {
        string key = Key(documentPath, sourceSha256);
        var recorded = ReadRecords();
        var preset = recorded.SingleOrDefault(item => item.Name == key);
        if (preset is null)
        {
            var previous = recorded.Where(item => item.Name.StartsWith(key[..64], StringComparison.Ordinal))
                .Select(item => new DocumentProofReview(item.Name[64..],
                    PdfProofPreset.Capture("Previous proof", item.ResolveProfile(null)))).ToList().AsReadOnly();
            return previous.Count == 0
                ? new(DocumentProofMatch.NotFound, new PdfDocumentProofSettings(), ProofProfileResolutionKind.NotSelected)
                : new(DocumentProofMatch.DifferentContent, new PdfDocumentProofSettings(), ProofProfileResolutionKind.NotSelected, previous);
        }
        var resolved = profiles.Resolve(preset);
        return new(DocumentProofMatch.Matched, resolved.Settings, resolved.Kind);
    }

    private IReadOnlyList<PdfProofPreset> ReadRecords()
    {
        var records = _library.GetPresets();
        if (records.Any(item => item.Name.Length != 128 || item.Name.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsLower(character))))
            throw new FormatException("The document proof store has an invalid content identity.");
        return records;
    }

    private static string Key(string documentPath, string sourceSha256)
    {
        if (sourceSha256 is null || sourceSha256.Length != 64 || sourceSha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("A source-content identity must be a SHA256 digest.", nameof(sourceSha256));
        if (string.IsNullOrWhiteSpace(documentPath) || !Path.IsPathFullyQualified(documentPath))
            throw new ArgumentException("A document preference path must be fully qualified.", nameof(documentPath));
        string normalized = Path.GetFullPath(documentPath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))) + sourceSha256.ToUpperInvariant();
    }
}
