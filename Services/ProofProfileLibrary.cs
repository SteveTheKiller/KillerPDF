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
            using var gate = ProofLibraryGate.Open(GatePath, FileMode.OpenOrCreate);
            List<ProofProfileEntry> profiles;
            bool damaged = false;
            try { profiles = ReadManifest(ManifestPath).ToList(); }
            catch (FormatException) when (File.Exists(ManifestPath + ".bak"))
            {
                // Continue from the previous manifest; the damaged one is kept for review.
                profiles = ReadManifest(ManifestPath + ".bak").ToList();
                damaged = true;
            }
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
                WriteAtomic(ManifestPath, buffer.ToArray(), backup: ManifestPath + (damaged ? ".recovery" : ".bak"));
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
    /// <remarks>A damaged manifest reads as the previous manifest when one was kept.</remarks>
    public IReadOnlyList<ProofProfileEntry> GetProfiles()
    {
        try { return ReadManifest(ManifestPath); }
        catch (FormatException) when (File.Exists(ManifestPath + ".bak"))
        {
            return ReadManifest(ManifestPath + ".bak");
        }
    }

    private static IReadOnlyList<ProofProfileEntry> ReadManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(ReadBoundedFile(path, 1024 * 1024),
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
            // The same exclusive gate as Import, so a resolve waits for a running import.
            using var gate = ProofLibraryGate.Open(GatePath, FileMode.Open);
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

    private string WorkingCmykPath => Path.Combine(_directory, "working-cmyk.json");

    /// <summary>Lists library profiles whose device color space is CMYK.</summary>
    /// <remarks>Only the 20-byte ICC header of each owned copy is read.</remarks>
    public IReadOnlyList<ProofProfileEntry> GetCmykProfiles() =>
        GetProfiles().Where(entry => DeviceSpace(entry.ContentSha256) == "CMYK").ToList().AsReadOnly();

    /// <summary>Gets the chosen working CMYK profile identity, or null for the built-in profile.</summary>
    /// <remarks>A missing, unreadable or damaged choice reads as the built-in profile.</remarks>
    public string? GetWorkingCmykProfileSha256()
    {
        try
        {
            using var document = JsonDocument.Parse(ReadBoundedFile(WorkingCmykPath, 4096),
                new JsonDocumentOptions { MaxDepth = 4 });
            ValidateProperties(document.RootElement, "schemaVersion", "contentSha256");
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1) return null;
            string? hash = document.RootElement.GetProperty("contentSha256").GetString();
            return hash is { Length: 64 } && hash.All(char.IsAsciiHexDigit) ? hash.ToUpperInvariant() : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Chooses a library CMYK profile as the working profile, or the built-in one with null.</summary>
    public void SetWorkingCmykProfile(string? contentSha256)
    {
        if (contentSha256 is not null && !GetCmykProfiles().Any(entry =>
                string.Equals(entry.ContentSha256, contentSha256, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The working CMYK profile must be a CMYK profile in this library.", nameof(contentSha256));
        try
        {
            Directory.CreateDirectory(_directory);
            using var gate = ProofLibraryGate.Open(GatePath, FileMode.OpenOrCreate);
            if (contentSha256 is null)
            {
                File.Delete(WorkingCmykPath);
                return;
            }
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("contentSha256", contentSha256.ToUpperInvariant());
                writer.WriteEndObject();
            }
            WriteAtomic(WorkingCmykPath, buffer.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The working CMYK profile could not be saved.");
        }
    }

    /// <summary>Loads the chosen working CMYK profile, or null for the built-in one.</summary>
    /// <remarks>A choice whose owned copy is missing, changed or not a usable CMYK profile resolves to null.</remarks>
    public PdfColorProfile? ResolveWorkingCmykProfile()
    {
        if (GetWorkingCmykProfileSha256() is not { } reference) return null;
        try
        {
            byte[] bytes;
            using (ProofLibraryGate.Open(GatePath, FileMode.Open))
                bytes = ReadBoundedFile(Path.Combine(_directory, reference + ".icc"), 32 * 1024 * 1024);
            var profile = PdfColorProfile.Load(bytes);
            if (profile.ContentSha256 != reference || profile.DeviceColorSpaceSignature != "CMYK") return null;
            // The engine decides whether the profile has a usable forward table.
            _ = new KillerPdf.Engine.Rendering.PdfRenderOptions(1, 1) { WorkingCmykProfile = profile };
            return profile;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or FormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private string? DeviceSpace(string contentSha256)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(_directory, contentSha256 + ".icc"));
            Span<byte> header = stackalloc byte[20];
            file.ReadExactly(header);
            return System.Text.Encoding.ASCII.GetString(header[16..20]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    private static void WriteAtomic(string destination, byte[] bytes, bool overwrite = true, string? backup = null)
    {
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            // Reach the disk before the rename publishes the file.
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (backup is not null && File.Exists(destination)) File.Replace(temporary, destination, backup);
            else File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
