using System.IO;
using System.Text.Json;
using KillerPdf.Engine.ColorManagement;

namespace KillerPDF.Services;

/// <summary>Stores named, immutable requested proof preferences.</summary>
public sealed class ProofPresetLibrary(string directory)
{
    /// <summary>Uses the application's current user-data location.</summary>
    public ProofPresetLibrary() : this(AppDataPaths.ProofPresetsDirectory) { }

    private readonly string _directory = Path.GetFullPath(directory);
    private string CatalogPath => Path.Combine(_directory, "proof-presets.json");

    /// <summary>Saves a snapshot without changing any open document.</summary>
    public void Save(PdfProofPreset preset, bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(preset);
        Update(presets =>
        {
            int index = presets.FindIndex(item => string.Equals(item.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && !replaceExisting)
                throw new InvalidOperationException("A proof preset with this name already exists.");
            if (index < 0 && presets.Count >= MaximumPresets)
                throw new InvalidOperationException("The proof preset library is limited to 128 presets.");
            if (index >= 0) presets[index] = preset;
            else presets.Add(preset);
        });
    }

    internal const int MaximumPresets = 128;

    /// <summary>Applies one change to the stored list under the library lock and writes it atomically.</summary>
    internal void Update(Action<List<PdfProofPreset>> change)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            using var gate = ProofLibraryGate.Open(Path.Combine(_directory, "presets.lock"), FileMode.OpenOrCreate);
            List<PdfProofPreset> presets;
            bool damaged = false;
            try { presets = ReadCatalog(CatalogPath, missingIsEmpty: true).ToList(); }
            catch (FormatException)
            {
                // Start from the previous good catalog, and keep the damaged bytes for review.
                presets = ReadPrevious().ToList();
                damaged = true;
            }
            change(presets);
            if (presets.Count > MaximumPresets)
                throw new InvalidOperationException("The proof preset library is limited to 128 presets.");
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("presets");
                foreach (var item in presets) writer.WriteRawValue(item.ToJson());
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            string temporary = CatalogPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(buffer.ToArray());
                    file.Flush(flushToDisk: true);
                }
                if (File.Exists(CatalogPath))
                    File.Replace(temporary, CatalogPath, CatalogPath + (damaged ? ".recovery" : ".bak"));
                else File.Move(temporary, CatalogPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The proof preset library could not be updated.");
        }
    }

    // The catalog a damaged one replaced, or nothing when that copy is missing or damaged too.
    private IReadOnlyList<PdfProofPreset> ReadPrevious()
    {
        try { return ReadCatalog(CatalogPath + ".bak", missingIsEmpty: true); }
        catch (FormatException) { return []; }
    }

    /// <summary>Explicitly restores the previous catalog, retaining the replaced catalog for review.</summary>
    /// <remarks>The newest replaced catalog is retained; an earlier retained copy never blocks a restore.</remarks>
    public IReadOnlyList<PdfProofPreset> RestorePrevious()
    {
        try
        {
            using var gate = ProofLibraryGate.Open(Path.Combine(_directory, "presets.lock"), FileMode.OpenOrCreate);
            var presets = ReadCatalog(CatalogPath + ".bak", missingIsEmpty: false);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("presets");
                foreach (var item in presets) writer.WriteRawValue(item.ToJson());
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            string temporary = CatalogPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(buffer.ToArray());
                    file.Flush(flushToDisk: true);
                }
                if (File.Exists(CatalogPath)) File.Replace(temporary, CatalogPath, CatalogPath + ".recovery");
                else File.Move(temporary, CatalogPath);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return presets;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The previous proof preset library could not be restored.");
        }
    }

    /// <summary>Reads stored presets without loading external profiles.</summary>
    /// <remarks>
    /// A damaged catalog reads as the previous catalog, or as empty without one, so the library
    /// keeps working. The next save retains the damaged bytes beside the catalog for review.
    /// </remarks>
    public IReadOnlyList<PdfProofPreset> GetPresets()
    {
        try { return ReadCatalog(CatalogPath, missingIsEmpty: true); }
        catch (FormatException) { return ReadPrevious(); }
    }

    private static IReadOnlyList<PdfProofPreset> ReadCatalog(string path, bool missingIsEmpty)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > 1024 * 1024) throw new FormatException();
            byte[] bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            if (file.ReadByte() != -1) throw new FormatException();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name is not ("schemaVersion" or "presets") || !seen.Add(property.Name))
                    throw new FormatException();
            if (seen.Count != 2) throw new FormatException();
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                throw new NotSupportedException("The proof preset library schema is unsupported.");
            var presets = new List<PdfProofPreset>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.GetProperty("presets").EnumerateArray())
            {
                var preset = PdfProofPreset.FromJson(item.GetRawText());
                if (presets.Count == 128 || !names.Add(preset.Name)) throw new FormatException();
                presets.Add(preset);
            }
            return presets.AsReadOnly();
        }
        catch (Exception error) when (missingIsEmpty && error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The proof preset library could not be read.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new FormatException("The proof preset library is damaged or has invalid settings.");
        }
    }
}

/// <summary>Opens a proof library's exclusive lock file, waiting briefly for another writer.</summary>
internal static class ProofLibraryGate
{
    // About two seconds in total: long enough for another instance's save, short enough for the UI.
    private const int Attempts = 10;

    internal static FileStream Open(string path, FileMode mode)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, mode, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when (attempt < Attempts - 1 && IsSharingViolation(error))
            {
                Thread.Sleep(20 << Math.Min(attempt, 4));
            }
        }
    }

    private static bool IsSharingViolation(IOException error) => (error.HResult & 0xFFFF) is 32 or 33;
}
