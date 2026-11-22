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
            catch (FormatException)
            {
                // Continue from the previous manifest, or from an empty one when there is no readable previous
                // manifest; the damaged one is kept for review.
                profiles = ReadPreviousManifest().ToList();
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
    /// <remarks>A damaged manifest reads as the previous manifest when a readable one was kept, otherwise as
    /// empty, so the library never stays locked by a damaged file.</remarks>
    public IReadOnlyList<ProofProfileEntry> GetProfiles()
    {
        try { return ReadManifest(ManifestPath); }
        catch (FormatException)
        {
            return ReadPreviousManifest();
        }
    }

    // The manifest a damaged one replaced, or nothing when that copy is missing or damaged too.
    private IReadOnlyList<ProofProfileEntry> ReadPreviousManifest()
    {
        try { return ReadManifest(ManifestPath + ".bak"); }
        catch (FormatException) { return []; }
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

    /// <summary>A value that changes whenever the working CMYK choice file is written or removed.</summary>
    /// <remarks>0 when there is no choice file; -1 when its metadata cannot be read.</remarks>
    internal long GetWorkingCmykChoiceStamp()
    {
        try
        {
            var file = new FileInfo(WorkingCmykPath);
            return file.Exists ? file.LastWriteTimeUtc.Ticks ^ file.Length << 48 | 1 : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Lists library profiles whose device color space is CMYK.</summary>
    /// <remarks>Only the 20-byte ICC header of each owned copy is read.</remarks>
    public IReadOnlyList<ProofProfileEntry> GetCmykProfiles() =>
        GetProfiles().Where(entry => Header(entry.ContentSha256).Space == "CMYK").ToList().AsReadOnly();

    /// <summary>Lists library output profiles whose device color space is RGB, the ones color-managed printing can use.</summary>
    /// <remarks>Only the 20-byte ICC header of each owned copy is read.</remarks>
    public IReadOnlyList<ProofProfileEntry> GetRgbPrinterProfiles() =>
        GetProfiles().Where(entry => Header(entry.ContentSha256) is ("prtr", "RGB ")).ToList().AsReadOnly();

    /// <summary>Loads a library profile by content identity, or null when its copy is missing, changed or unusable.</summary>
    /// <remarks>A library that is busy or cannot be read throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>.</remarks>
    public PdfColorProfile? LoadProfile(string contentSha256)
    {
        if (contentSha256 is not { Length: 64 } || !contentSha256.All(char.IsAsciiHexDigit)) return null;
        string reference = contentSha256.ToUpperInvariant();
        try
        {
            byte[] bytes;
            using (ProofLibraryGate.Open(GatePath, FileMode.Open))
                bytes = ReadBoundedFile(Path.Combine(_directory, reference + ".icc"), 32 * 1024 * 1024);
            var profile = PdfColorProfile.Load(bytes);
            return profile.ContentSha256 == reference ? profile : null;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException
            or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Gets the chosen working CMYK profile identity, or null for the built-in profile.</summary>
    /// <remarks>A missing, unreadable or damaged choice reads as the built-in profile.</remarks>
    public string? GetWorkingCmykProfileSha256()
    {
        try { return ReadWorkingCmykChoice(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A missing or damaged choice is no choice. A choice file that exists but cannot be read now throws, so a
    // caller that remembers the result can tell it from no choice.
    private string? ReadWorkingCmykChoice()
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
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or JsonException
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
    /// <remarks>A choice whose owned copy is missing, changed or not a usable CMYK profile resolves to null. A library
    /// that is busy or cannot be read now throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
    /// instead, since that is not a choice of the built-in profile.</remarks>
    public PdfColorProfile? ResolveWorkingCmykProfile()
    {
        if (ReadWorkingCmykChoice() is not { } reference) return null;
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
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException
            or FormatException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    // The profile class and device color space signatures of an owned copy, or nulls when it cannot be read.
    private (string? Class, string? Space) Header(string contentSha256)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(_directory, contentSha256 + ".icc"));
            Span<byte> header = stackalloc byte[20];
            file.ReadExactly(header);
            return (System.Text.Encoding.ASCII.GetString(header[12..16]), System.Text.Encoding.ASCII.GetString(header[16..20]));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return (null, null);
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

/// <summary>The chosen working CMYK profile, read once per choice for every render in this process.</summary>
/// <remarks>
/// Untagged CMYK displays, prints and exports through it everywhere. Null keeps the engine's
/// built-in CGATS TR 001 profile. A changed choice is picked up after <see cref="Invalidate"/>,
/// or in any process once the saved choice file changes.
/// </remarks>
internal static partial class WorkingCmykProfiles
{
    private sealed record Choice(long Generation, long Stamp, PdfColorProfile? Profile);
    private static long _generation;
    private static Choice? _choice;

    /// <summary>Gets or sets where the choice is read; the application's profile library by default.</summary>
    internal static Func<PdfColorProfile?> Source { get; set; } = () => new ProofProfileLibrary().ResolveWorkingCmykProfile();

    /// <summary>Gets or sets the stamp of the saved choice; the application's profile library by default.</summary>
    internal static Func<long> Stamp { get; set; } = () => new ProofProfileLibrary().GetWorkingCmykChoiceStamp();

    internal static long Generation => Interlocked.Read(ref _generation);

    internal static void Invalidate() => Interlocked.Increment(ref _generation);

    internal static PdfColorProfile? Current()
    {
        long generation = Generation;
        // The watched-folder host and the Explorer preview pane may be other processes than the
        // one where the choice changes, so a rewritten or removed choice file also counts.
        long stamp = Stamp();
        if (Volatile.Read(ref _choice) is { } cached && cached.Generation == generation && cached.Stamp == stamp)
            return cached.Profile;
        PdfColorProfile? profile;
        try { profile = Source(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A busy or unreadable library is not remembered, so the next call reads it again. Until then the
            // profile last read for this choice file still applies.
            return Volatile.Read(ref _choice) is { } previous && previous.Stamp == stamp ? previous.Profile : null;
        }
        catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException)
        {
            profile = null;
        }
        Volatile.Write(ref _choice, new Choice(generation, stamp, profile));
        return profile;
    }

    /// <summary>
    /// Builds the chosen profile's RGB to ink table on a background thread, so the first RGB paint
    /// into a CMYK surface finds it ready. Called once after startup and again when the choice changes.
    /// </summary>
    internal static void WarmDisplayTableInBackground() =>
        StartWarmUp(() => KillerPdf.Engine.Rendering.PdfCmykWarmUp.Prepare(Current()));

    /// <summary>Runs the warm-up body on its own thread, at Normal priority: a render that needs the table waits for this
    /// fit, and a lower priority lets the render's own busy threads starve it (measured at 60 s instead of 2 s with every
    /// core busy). Nothing the body throws can leave the thread,
    /// because an unhandled exception on a thread of this kind ends the process; the first paint builds the table
    /// itself and reports any real problem then.</summary>
    internal static Thread StartWarmUp(Action prepare)
    {
        var thread = new Thread(() =>
        {
            try { prepare(); }
            catch (Exception error) { LogWarmUpFailure(error); }
        })
        { IsBackground = true, Priority = ThreadPriority.Normal, Name = "Working CMYK warm-up" };
        thread.Start();
        return thread;
    }

    /// <summary>Records a warm-up failure in the app's local log. The app supplies the body.</summary>
    static partial void LogWarmUpFailure(Exception error);

    /// <summary>Gives render options the chosen working CMYK profile; never sets null.</summary>
    internal static KillerPdf.Engine.Rendering.PdfRenderOptions Apply(KillerPdf.Engine.Rendering.PdfRenderOptions options) =>
        Current() is { } working ? options with { WorkingCmykProfile = working } : options;

    /// <summary>The color conversion macro step, reading untagged CMYK through the chosen working profile.</summary>
    internal static KillerPdf.Engine.Documents.PdfMacroOperationResult ConvertColor(KillerPdf.Engine.Documents.PdfMacroStep step,
        ReadOnlyMemory<byte> source, IReadOnlyDictionary<string, string> values, CancellationToken token) =>
        KillerPdf.Engine.Documents.PdfColorConversionMacro.Execute(step, source, values, Current(), token);
}
