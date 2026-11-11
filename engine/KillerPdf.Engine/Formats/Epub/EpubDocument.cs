using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>An in-memory EPUB package.</summary>
public sealed partial class EpubDocument
{
    private readonly Dictionary<string, byte[]> _resources;
    private readonly HashSet<string> _oversizedResources;
    // Entries that could not be read; they are also in _oversizedResources, so a chapter keeps its place in the spine.
    private readonly HashSet<string> _damagedResources;
    private readonly List<EpubChapterDiagnostic> _packageDiagnostics = [];
    private const int MaxEntryBytes = 8 * 1024 * 1024;
    /// <summary>The largest EPUB package the engine opens.</summary>
    public const int MaxPackageBytes = 128 * 1024 * 1024;
    private const long MaxExpandedBytes = 256L * 1024 * 1024;
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private EpubDocument(Dictionary<string, byte[]> resources, XDocument package, string packagePath,
        HashSet<string> oversizedResources, HashSet<string> damagedResources)
    {
        _resources = resources;
        _oversizedResources = oversizedResources;
        _damagedResources = damagedResources;
        var root = package.Root ?? throw new InvalidDataException("Missing package.");
        if (root.Name != Opf + "package")
            throw new InvalidDataException("Invalid EPUB package namespace.");
        PackagePath = packagePath;
        Version = (string?)root.Attribute("version") ?? "";
        if (!Version.StartsWith("2.", StringComparison.Ordinal) && !Version.StartsWith("3.", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported EPUB package version.");
        var metadata = root.Element(Opf + "metadata");
        var identifierId = Required(root, "unique-identifier");
        var identifier = metadata?.Elements(Dc + "identifier").FirstOrDefault(x => (string?)x.Attribute("id") == identifierId)?.Value
            ?? throw new InvalidDataException("Missing EPUB unique identifier.");
        Metadata = new EpubMetadata(metadata?.Element(Dc + "title")?.Value ?? "",
            identifier, metadata?.Element(Dc + "language")?.Value ?? "",
            metadata?.Element(Dc + "creator")?.Value ?? "");
        var prefix = packagePath.Contains('/') ? packagePath[..(packagePath.LastIndexOf('/') + 1)] : "";
        var manifest = new Dictionary<string, EpubManifestItem>(StringComparer.Ordinal);
        var missingItems = new HashSet<string>(StringComparer.Ordinal);
        var spineIds = (root.Element(Opf + "spine")?.Elements(Opf + "itemref") ?? [])
            .Select(item => (string?)item.Attribute("idref")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var item in root.Element(Opf + "manifest")?.Elements(Opf + "item") ?? [])
        {
            var id = Required(item, "id");
            string reference = Required(item, "href");
            string href;
            try { href = ResolvePath(prefix, reference); }
            catch (InvalidDataException error)
            {
                missingItems.Add(id);
                _packageDiagnostics.Add(new(-1, packagePath, "A resource was left out: " + error.Message)
                { Reason = EpubOmissionReason.MissingResource, ItemHref = reference });
                continue;
            }
            // An item whose file is not in the package is skipped with any spine reference to it (EP-BF-018).
            if (!resources.ContainsKey(href) && !(spineIds.Contains(id) && oversizedResources.Contains(href)))
            {
                missingItems.Add(id);
                continue;
            }
            if (!manifest.TryAdd(id, new EpubManifestItem(id, href,
                Required(item, "media-type"), (string?)item.Attribute("properties") ?? "")) || manifest.Count > 4096)
                throw new InvalidDataException("Invalid EPUB manifest resource.");
        }
        Manifest = Array.AsReadOnly(manifest.Values.ToArray());
        var spine = root.Element(Opf + "spine");
        _navigationTocId = (string?)spine?.Attribute("toc");
        PageProgressionDirection = (string?)spine?.Attribute("page-progression-direction") ?? "default";
        Layout = metadata?.Elements(Opf + "meta").FirstOrDefault(x => (string?)x.Attribute("property") == "rendition:layout")?.Value.Trim() ?? "reflowable";
        var readingOrder = new List<EpubSpineItem>();
        foreach (var item in spine?.Elements(Opf + "itemref") ?? [])
        {
            if (missingItems.Contains(Required(item, "idref"))) continue;
            if (!manifest.TryGetValue(Required(item, "idref"), out var resource) || readingOrder.Count >= 4096)
                throw new InvalidDataException("Invalid EPUB spine reference.");
            readingOrder.Add(new EpubSpineItem(resource, (string?)item.Attribute("linear") != "no",
                (string?)item.Attribute("properties") ?? ""));
        }
        if (Manifest.Count == 0 || readingOrder.Count == 0)
            throw new InvalidDataException("Missing EPUB manifest or spine.");
        Spine = readingOrder.AsReadOnly();
    }

    /// <summary>Opens a bounded EPUB package.</summary>
    public static EpubDocument Open(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Illustrated editions run large (The Count of Monte Cristo with 450 images is 84 MB), so the
        // package may be up to 128 MiB and expand to 256 MiB; each entry keeps its 8 MiB bound.
        if (data.Length > MaxPackageBytes)
            throw new InvalidDataException("EPUB exceeds the input size limit.");
        var bytes = data.ToArray();
        if (bytes.Length < 57 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x04034b50 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) != 0 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) & 1) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(26)) != 8 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)) != 0 ||
            !bytes.AsSpan(30, 8).SequenceEqual("mimetype"u8))
            throw new InvalidDataException("Invalid EPUB mimetype header.");
        using var input = new MemoryStream(bytes, false);
        ValidateZipDirectory(bytes, cancellationToken);
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count > 4096)
            throw new InvalidDataException("EPUB exceeds the resource count limit.");
        var resources = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var packageDiagnostics = new List<EpubChapterDiagnostic>();
        var oversizedResources = new HashSet<string>(StringComparer.Ordinal);
        void OmitOversized(string href)
        {
            oversizedResources.Add(href);
            packageDiagnostics.Add(new(-1, href, OversizedResource(href).Message)
            { Reason = EpubOmissionReason.LimitExceeded, ItemHref = href });
        }
        var damagedResources = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchivePath(entry.FullName);
            if (entry.FullName.Contains('%') || entry.FullName.Contains(':'))
            {
                packageDiagnostics.Add(new(-1, entry.FullName, "A resource with an unsupported name was left out.")
                { Reason = EpubOmissionReason.MissingResource, ItemHref = entry.FullName });
                continue;
            }
            if (!names.Add(entry.FullName.Normalize()))
                throw new InvalidDataException("Ambiguous EPUB resource name.");
            if (entry.FullName.Equals("META-INF/encryption.xml", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName != "META-INF/encryption.xml")
                throw new InvalidDataException("Encrypted EPUB publications are unsupported.");
            if (entry.Length > MaxEntryBytes)
            {
                OmitOversized(entry.FullName);
                continue;
            }
            if (entry.Length > MaxExpandedBytes - expandedBytes)
                throw new InvalidDataException("EPUB exceeds the total expanded size limit.");
            // An entry the ZIP reader cannot decode (damaged data or a compression method it lacks) is left out.
            Exception? damage = null;
            Stream? stream = null;
            try { stream = entry.Open(); }
            catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException) { damage = error; }
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while (damage is null && stream is not null)
            {
                try { count = stream.Read(buffer); }
                catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException)
                {
                    damage = error;
                    break;
                }
                if (count == 0) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Length + count > MaxEntryBytes)
                {
                    OmitOversized(entry.FullName);
                    break;
                }
                expandedBytes += count;
                if (expandedBytes > MaxExpandedBytes)
                    throw new InvalidDataException("EPUB exceeds the total expanded size limit.");
                output.Write(buffer, 0, count);
            }
            stream?.Dispose();
            if (damage is not null)
            {
                damagedResources.Add(entry.FullName);
                oversizedResources.Add(entry.FullName);
                packageDiagnostics.Add(new(-1, entry.FullName,
                    "EPUB resource " + entry.FullName + " could not be read and was left out: " + damage.Message)
                { Reason = EpubOmissionReason.MissingResource, ItemHref = entry.FullName });
                continue;
            }
            if (!oversizedResources.Contains(entry.FullName)) resources[entry.FullName] = output.ToArray();
        }
        if (damagedResources.Contains("mimetype")) throw new InvalidDataException("Invalid EPUB mimetype.");
        if (damagedResources.Contains("META-INF/container.xml")) throw new InvalidDataException("Missing EPUB container document.");
        if (!resources.TryGetValue("mimetype", out var mimetype) || !mimetype.AsSpan().SequenceEqual("application/epub+zip"u8))
            throw new InvalidDataException("Invalid EPUB mimetype.");
        if (oversizedResources.Contains("META-INF/container.xml")) throw OversizedResource("META-INF/container.xml");
        if (!resources.TryGetValue("META-INF/container.xml", out var containerData))
            throw new InvalidDataException("Missing EPUB container document.");
        var container = Xml(containerData);
        XNamespace containerNamespace = "urn:oasis:names:tc:opendocument:xmlns:container";
        if (container.Root?.Name != containerNamespace + "container")
            throw new InvalidDataException("Invalid EPUB container namespace.");
        var rootfile = container.Root.Element(containerNamespace + "rootfiles")?.Elements(containerNamespace + "rootfile")
            .FirstOrDefault(x => (string?)x.Attribute("media-type") == "application/oebps-package+xml")
            ?? throw new InvalidDataException("Missing EPUB package reference.");
        var path = ResolvePath("", Required(rootfile, "full-path"));
        if (damagedResources.Contains(path)) throw new InvalidDataException("Missing EPUB package document.");
        if (oversizedResources.Contains(path)) throw OversizedResource(path);
        if (!resources.TryGetValue(path, out var packageData))
            throw new InvalidDataException("Missing EPUB package document.");
        var document = new EpubDocument(resources, Xml(packageData), path, oversizedResources, damagedResources);
        var spinePaths = document.Spine.Select(item => item.Item.Href).ToHashSet(StringComparer.Ordinal);
        document._packageDiagnostics.AddRange(packageDiagnostics.Where(item => !spinePaths.Contains(item.Href)));
        document.RestoreObfuscatedFonts(cancellationToken);
        return document;
    }

    private static InvalidDataException OversizedResource(string href) =>
        new("EPUB resource exceeds its 8 MiB expanded size limit: " + href + ".");

    // Why a resource kept out of _resources is unavailable: it was too large, or its ZIP entry could not be read.
    private InvalidDataException UnavailableResource(string href) => _damagedResources.Contains(href)
        ? new("EPUB resource could not be read: " + href + ".") : OversizedResource(href);

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Unsafe(path) || Unsafe(Uri.UnescapeDataString(path)))
            throw new InvalidDataException("Unsafe EPUB resource path.");

        static bool Unsafe(string value) => Path.IsPathRooted(value) || value.Contains('\\')
            || value.Any(char.IsControl) || value.Split('/').Any(segment => segment is "." or "..");
    }

    private static string Required(XElement element, string attribute)
    {
        var value = (string?)element.Attribute(attribute);
        return string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException("Missing EPUB " + attribute + ".") : value;
    }

    private static string ResolvePath(string prefix, string reference)
    {
        var decoded = Uri.UnescapeDataString(reference);
        if (decoded.StartsWith('/') || decoded.Contains(':') || decoded.Contains('\\') ||
            decoded.Contains('?') || decoded.Contains('#') || decoded.Contains('%') || decoded.Any(char.IsControl))
            throw new InvalidDataException("Unsupported EPUB resource reference.");
        var segments = new List<string>();
        foreach (var segment in (prefix + decoded).Split('/'))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new InvalidDataException("EPUB resource escapes the container.");
                segments.RemoveAt(segments.Count - 1);
            }
            else if (segment.Length == 0) throw new InvalidDataException("Invalid EPUB resource reference.");
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    private static XDocument Xml(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024,
                MaxCharactersFromEntities = 1024
            });
            return XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("Invalid EPUB XML document.", exception);
        }
    }

    /// <summary>The publication metadata.</summary>
    public EpubMetadata Metadata { get; }
    /// <summary>The package version.</summary>
    public string Version { get; }
    /// <summary>The package path within the container.</summary>
    public string PackagePath { get; }
    /// <summary>The reading direction.</summary>
    public string PageProgressionDirection { get; }
    /// <summary>The publication layout.</summary>
    public string Layout { get; }
    /// <summary>Resources declared by the package.</summary>
    public IReadOnlyList<EpubManifestItem> Manifest { get; }
    /// <summary>Content documents in reading order.</summary>
    public IReadOnlyList<EpubSpineItem> Spine { get; }
    /// <summary>Returns an independent copy of a container resource.</summary>
    public byte[] ReadResource(string path) => (byte[])_resources[path].Clone();
}

/// <summary>Publication metadata from the package document.</summary>
public sealed record EpubMetadata(string Title, string Identifier, string Language, string Creator);
/// <summary>A resource declared in the package manifest.</summary>
public sealed record EpubManifestItem(string Id, string Href, string MediaType, string Properties);
/// <summary>A reference to a manifest resource in reading order.</summary>
public sealed record EpubSpineItem(EpubManifestItem Item, bool IsLinear, string Properties);
