using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>An in-memory EPUB package.</summary>
public sealed partial class EpubDocument
{
    private readonly Dictionary<string, byte[]> _resources;
    private const int MaxEntryBytes = 8 * 1024 * 1024;
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private EpubDocument(Dictionary<string, byte[]> resources, XDocument package, string packagePath)
    {
        _resources = resources;
        var root = package.Root ?? throw new InvalidDataException("Missing package.");
        if (root.Name != Opf + "package")
            throw new InvalidDataException("Invalid EPUB package namespace.");
        PackagePath = packagePath;
        Version = (string?)root.Attribute("version") ?? "";
        if (Version is not ("2.0" or "3.0"))
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
        foreach (var item in root.Element(Opf + "manifest")?.Elements(Opf + "item") ?? [])
        {
            var id = Required(item, "id");
            var href = ResolvePath(prefix, Required(item, "href"));
            if (!resources.ContainsKey(href) || !manifest.TryAdd(id, new EpubManifestItem(id, href,
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
        if (data.Length > 64 * 1024 * 1024)
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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchivePath(entry.FullName);
            if (!names.Add(entry.FullName.Normalize()))
                throw new InvalidDataException("Ambiguous EPUB resource name.");
            if (entry.FullName.Equals("META-INF/encryption.xml", StringComparison.OrdinalIgnoreCase) &&
                entry.FullName != "META-INF/encryption.xml")
                throw new InvalidDataException("Encrypted EPUB publications are unsupported.");
            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException("EPUB resource exceeds the expanded size limit.");
            if (entry.Length > 64 * 1024 * 1024 - expandedBytes)
                throw new InvalidDataException("EPUB exceeds the total expanded size limit.");
            using var stream = entry.Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Length + count > MaxEntryBytes)
                    throw new InvalidDataException("EPUB resource exceeds the expanded size limit.");
                expandedBytes += count;
                if (expandedBytes > 64 * 1024 * 1024)
                    throw new InvalidDataException("EPUB exceeds the total expanded size limit.");
                output.Write(buffer, 0, count);
            }
            resources[entry.FullName] = output.ToArray();
        }
        if (!resources.TryGetValue("mimetype", out var mimetype) || !mimetype.AsSpan().SequenceEqual("application/epub+zip"u8))
            throw new InvalidDataException("Invalid EPUB mimetype.");
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
        if (!resources.TryGetValue(path, out var packageData))
            throw new InvalidDataException("Missing EPUB package document.");
        var document = new EpubDocument(resources, Xml(packageData), path);
        document.RestoreObfuscatedFonts(cancellationToken);
        return document;
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Contains('%') || path.Any(char.IsControl) || path.Split('/').Any(x => x is "." or ".."))
            throw new InvalidDataException("Unsafe EPUB resource path.");
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
