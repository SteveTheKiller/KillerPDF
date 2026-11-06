using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using KillerPdf.Engine.ColorManagement;

namespace KillerPDF.Services;

/// <summary>A named profile identified by validated ICC content, without a source path.</summary>
public sealed record ProofProfileEntry(string Name, string ContentSha256);

/// <summary>Describes profile content resolution, independently of proof transform support.</summary>
public enum ProofProfileResolutionKind
{
    /// <summary>The referenced profile content was loaded and matched.</summary>
    Matched,
    /// <summary>The requested profile copy is absent.</summary>
    Missing,
    /// <summary>The stored copy has different validated content.</summary>
    Changed,
    /// <summary>No profile content reference was selected.</summary>
    NotSelected,
    /// <summary>The stored copy cannot be loaded as a supported ICC profile.</summary>
    Invalid,
    /// <summary>The stored copy cannot currently be read.</summary>
    Unreadable
}

/// <summary>Requested preferences and the result of resolving their profile reference.</summary>
public sealed record ProofProfileResolution(PdfDocumentProofSettings Settings, ProofProfileResolutionKind Kind);

/// <summary>Owns application profile copies and resolves explicit proof content references.</summary>
public sealed class ProofProfileLibrary(string directory)
{
    /// <summary>Uses the application's current user-data location.</summary>
    public ProofProfileLibrary() : this(AppDataPaths.ProofProfilesDirectory) { }

    private readonly string _directory = Path.GetFullPath(directory);
    private string ManifestPath => Path.Combine(_directory, "profiles.json");
    private string GatePath => Path.Combine(_directory, "library.lock");

    /// <summary>Imports one owned, validated ICC snapshot.</summary>
    public ProofProfileEntry Import(string name, ReadOnlyMemory<byte> data)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("A profile name must contain 1 to 128 characters without controls.", nameof(name));
        if (data.Length > 32 * 1024 * 1024)
            throw new FormatException("The ICC profile exceeds the 32 MiB input limit.");
        byte[] snapshot = data.ToArray();
        var profile = PdfColorProfile.Load(snapshot);
        Array.Resize(ref snapshot, BinaryPrimitives.ReadInt32BigEndian(snapshot));
        try
        {
            Directory.CreateDirectory(_directory);
            using var gate = new FileStream(GatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var profiles = GetProfiles().ToList();
            var entry = profiles.FirstOrDefault(item => item.ContentSha256 == profile.ContentSha256)
                ?? new ProofProfileEntry(name, profile.ContentSha256);
            if (!profiles.Any(item => item.ContentSha256 == entry.ContentSha256))
            {
                if (profiles.Count >= 128)
                    throw new InvalidOperationException("The proof profile library is limited to 128 profiles.");
                profiles.Add(entry);
            }
            string profilePath = Path.Combine(_directory, entry.ContentSha256 + ".icc");
            bool existed = File.Exists(profilePath);
            long storedBytes = snapshot.Length;
            int storedCount = existed ? 0 : 1;
            foreach (string stored in Directory.EnumerateFiles(_directory, "*.icc"))
            {
                if (++storedCount > 128)
                    throw new InvalidOperationException("The proof profile library is limited to 128 copies.");
                if (string.Equals(stored, profilePath, StringComparison.OrdinalIgnoreCase)) continue;
                long length = new FileInfo(stored).Length;
                if (length > 256L * 1024 * 1024 - storedBytes)
                    throw new InvalidOperationException("The proof profile library exceeds its 256 MiB storage limit.");
                storedBytes += length;
            }
            bool published = false;
            try
            {
                WriteAtomic(profilePath, snapshot, overwrite: existed);
                published = true;
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteStartArray("profiles");
                    foreach (var item in profiles)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", item.Name);
                        writer.WriteString("contentSha256", item.ContentSha256);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                WriteAtomic(ManifestPath, buffer.ToArray());
                return entry;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (published && !existed) File.Delete(profilePath);
                throw;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The proof profile library could not be updated.");
        }
    }

    /// <summary>Lists named profile content references retained in this library.</summary>
    public IReadOnlyList<ProofProfileEntry> GetProfiles()
    {
        try
        {
            using var document = JsonDocument.Parse(ReadBoundedFile(ManifestPath, 1024 * 1024),
                new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            ValidateProperties(root, "schemaVersion", "profiles");
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new NotSupportedException("The proof profile library schema is unsupported.");
            var profiles = new List<ProofProfileEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("profiles").EnumerateArray())
            {
                ValidateProperties(item, "name", "contentSha256");
                string? name = item.GetProperty("name").GetString();
                string? hash = item.GetProperty("contentSha256").GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl)
                    || hash is null || hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character)))
                    throw new FormatException();
                hash = hash.ToUpperInvariant();
                if (!seen.Add(hash) || profiles.Count == 128) throw new FormatException();
                profiles.Add(new(name, hash));
            }
            return profiles.AsReadOnly();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The proof profile library could not be read.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new FormatException("The proof profile library is damaged or has invalid settings.");
        }
    }

    /// <summary>Resolves an explicit proof preset without fetching external resources.</summary>
    public ProofProfileResolution Resolve(PdfProofPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.PrinterProfileSha256 is null)
            return new(preset.ResolveProfile(null), ProofProfileResolutionKind.NotSelected);
        byte[] bytes;
        try
        {
            using var gate = new FileStream(GatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = ReadBoundedFile(Path.Combine(_directory, preset.PrinterProfileSha256 + ".icc"), 32 * 1024 * 1024);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(preset.ResolveProfile(null), ProofProfileResolutionKind.Missing);
        }
        catch (FormatException)
        {
            return new(preset.ResolveProfile(null), ProofProfileResolutionKind.Invalid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(preset.ResolveProfile(null), ProofProfileResolutionKind.Unreadable);
        }
        PdfColorProfile profile;
        try
        {
            profile = PdfColorProfile.Load(bytes);
        }
        catch (Exception error) when (error is FormatException or NotSupportedException)
        {
            return new(preset.ResolveProfile(null), ProofProfileResolutionKind.Invalid);
        }
        if (profile.ContentSha256 != preset.PrinterProfileSha256)
            return new(preset.ResolveProfile(null), ProofProfileResolutionKind.Changed);
        return new(preset.ResolveProfile(profile), ProofProfileResolutionKind.Matched);
    }

    private static void ValidateProperties(JsonElement element, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateObject())
            if (!names.Contains(item.Name, StringComparer.Ordinal) || !seen.Add(item.Name))
                throw new FormatException();
        if (seen.Count != names.Length) throw new FormatException();
    }

    private static byte[] ReadBoundedFile(string path, int maximum)
    {
        using var file = File.OpenRead(path);
        if (file.Length > maximum)
            throw new FormatException("The proof library file exceeds its size limit.");
        byte[] bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        if (file.ReadByte() != -1)
            throw new FormatException("The proof library file changed while it was read.");
        return bytes;
    }

    private static void WriteAtomic(string destination, byte[] bytes, bool overwrite = true)
    {
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
