using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Writing;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;

namespace KillerPdf.Engine.Editing;

/// <summary>An opaque, validated editable page-content unit.</summary>
public sealed class PdfEditablePageOverlay
{
    internal PdfEditablePageOverlay(int pageIndex, int contentsObjectNumber, int formObjectNumber, byte[] data,
        PdfIndirectReference prefix, string identity)
    {
        PageIndex = pageIndex;
        ContentsObjectNumber = contentsObjectNumber;
        FormObjectNumber = formObjectNumber;
        _data = data.ToArray();
        Prefix = prefix;
        Identity = identity;
    }
    private readonly byte[] _data;
    internal PdfIndirectReference Prefix { get; }
    internal string Identity { get; }
    /// <summary>The containing page index.</summary>
    public int PageIndex { get; }
    /// <summary>The owned invocation stream identity.</summary>
    public int ContentsObjectNumber { get; }
    /// <summary>The owned Form identity.</summary>
    public int FormObjectNumber { get; }
    /// <summary>A defensive copy of the opaque editing payload.</summary>
    public ReadOnlyMemory<byte> EditingData => _data.ToArray();
}

/// <summary>One page's content and opaque editing data for <see cref="PdfEditablePageOverlayEditor.AppendMany"/>.</summary>
public sealed record PdfEditablePageOverlayUnit(int PageIndex, double Width, double Height,
    PdfContentStreamBuilder Content, ReadOnlyMemory<byte> EditingData, string? Description, int? ExpectedRotation = null);

/// <summary>Persists removable, opaque editing data with normal PDF page content.</summary>
public static class PdfEditablePageOverlayEditor
{
    private const int MaximumData = 8 * 1024 * 1024;
    private const int MaximumGraph = 32 * 1024 * 1024;
    // Counts the Form graphs Read hashes on the current thread, so a test can check that a shared Form is hashed once.
    [ThreadStatic] internal static int FormGraphHashes;
    private static PdfName N(string name) => new(Encoding.ASCII.GetBytes(name));
    private static readonly PdfName Metadata = N("KillerPDFEditableOverlay");

    /// <summary>Appends one owned content unit.</summary>
    public static byte[] Append(PdfDocument document, int pageIndex, double width, double height,
        PdfContentStreamBuilder content, ReadOnlyMemory<byte> editingData, string? description,
        int? expectedRotation = null) =>
        AppendMany(document, [new PdfEditablePageOverlayUnit(pageIndex, width, height, content, editingData,
            description, expectedRotation)]);

    /// <summary>Appends one owned content unit to each of several distinct pages in one pass, so the
    /// number of incremental revisions does not grow with the number of pages.</summary>
    public static byte[] AppendMany(PdfDocument document, IReadOnlyList<PdfEditablePageOverlayUnit> units)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(units);
        if (units.Count == 0) throw new ArgumentException("Supply at least one overlay unit.", nameof(units));
        var unitPages = new HashSet<int>();
        foreach (PdfEditablePageOverlayUnit unit in units)
        {
            ArgumentNullException.ThrowIfNull(unit, nameof(units));
            ArgumentNullException.ThrowIfNull(unit.Content, nameof(units));
            if (unit.EditingData.Length > MaximumData) throw new ArgumentOutOfRangeException(nameof(units));
            if (unit.ExpectedRotation is int rotation && rotation is not (0 or 90 or 180 or 270))
                throw new ArgumentOutOfRangeException(nameof(units));
            if (!unitPages.Add(unit.PageIndex))
                throw new ArgumentException("Each overlay unit must be on a different page.", nameof(units));
        }
        IReadOnlyList<PdfPageTreeEntry> originals = PdfPageTree.Read(document).Pages;
        PdfIncrementalUpdateBuilder? normalize = null;
        foreach (PdfEditablePageOverlayUnit unit in units)
        {
            PdfPageTreeEntry original = originals[unit.PageIndex];
            if (original.Dictionary.ContainsKey(N("Contents"))) continue;
            normalize ??= new PdfIncrementalUpdateBuilder(document);
            var pageEntries = original.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
            pageEntries[N("Contents")] = normalize.AddObject(new PdfStream(new PdfDictionary([]), []));
            normalize.ReplaceObject(original.Reference.ObjectNumber, new PdfDictionary(pageEntries));
        }
        if (normalize is not null) document = Reopen(document, normalize.Build());
        var editor = new PdfIncrementalPageEditor(document);
        editor.AppendTypedPages([.. units.Select(unit => (unit.PageIndex, unit.Width, unit.Height, unit.Content, unit.Description))]);
        PdfDocument appended = Reopen(document, editor.Build());
        var lookup = new DocumentLookup(appended);
        IReadOnlyList<PdfPageTreeEntry> appendedPages = lookup.Tree.Pages;
        var update = new PdfIncrementalUpdateBuilder(appended);
        foreach (PdfEditablePageOverlayUnit unit in units)
            Describe(appended, appendedPages[unit.PageIndex], unit.EditingData, unit.ExpectedRotation, update, lookup);
        return update.Build();
    }

    private static void Describe(PdfDocument appended, PdfPageTreeEntry page, ReadOnlyMemory<byte> editingData,
        int? expectedRotation, PdfIncrementalUpdateBuilder update, DocumentLookup lookup)
    {
        PdfArray contents = Contents(appended, page);
        var prefix = contents[0] as PdfIndirectReference ?? throw Invalid();
        var invocation = contents[^1] as PdfIndirectReference ?? throw Invalid();
        PdfStream stream = Stream(appended, invocation);
        string commands = Encoding.ASCII.GetString(Decode(appended, stream));
        string resource = InvocationResource(commands);
        PdfDictionary xObjects = XObjects(appended, page);
        var form = xObjects[N(resource)] as PdfIndirectReference ?? throw Invalid();
        var entries = new Dictionary<PdfName, PdfObject>
        {
            [N("Version")] = new PdfInteger(1),
            [N("Prefix")] = prefix,
            [N("Contents")] = invocation,
            [N("Form")] = form,
            [N("Page")] = page.Reference,
            [N("Resource")] = N(resource),
            [N("Data")] = new PdfString(editingData.Span, PdfStringForm.Hexadecimal),
            [N("Frame")] = Bytes(Frame(appended, page, expectedRotation, lookup)),
            [N("Graph")] = Bytes(Graph(appended, form)),
            [N("InvocationGraph")] = Bytes(InvocationGraph(appended, stream)),
            [N("PrefixGraph")] = Bytes(Graph(appended, prefix)),
            [N("Structure")] = CaptureStructure(appended, page, form, lookup)
        };
        entries[N("UnitHash")] = Bytes(MetadataHash(new PdfDictionary(entries)));
        var dictionary = stream.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        dictionary.Add(Metadata, new PdfDictionary(entries));
        update.ReplaceObject(invocation.ObjectNumber, new PdfStream(new PdfDictionary(dictionary), stream.EncodedData.Span));
    }
    /// <summary>Reads intact owned tail units.</summary>
    public static IReadOnlyList<PdfEditablePageOverlay> Read(PdfDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        // Units that name one shared Form would otherwise hash that Form's whole graph once per unit.
        var formGraphs = new Dictionary<int, byte[]>();
        byte[] FormGraph(PdfIndirectReference form)
        {
            if (!formGraphs.TryGetValue(form.ObjectNumber, out byte[]? graph))
            {
                FormGraphHashes++;
                formGraphs.Add(form.ObjectNumber, graph = Graph(document, form));
            }
            return graph;
        }
        if (!document.IsDecrypted || document.PasswordAuthenticationRole == PdfPasswordAuthenticationRole.User
            && (document.DeclaredPermissions is not PdfDocumentPermissions permissions || !permissions.AllowContentCopying))
            throw new InvalidOperationException("The authenticated PDF must permit copying editable page content.");
        var result = new List<PdfEditablePageOverlay>();
        var lookup = new DocumentLookup(document);
        foreach (PdfPageTreeEntry page in lookup.Tree.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                int pageStart = result.Count;
                PdfArray contents = Contents(document, page);
                var seen = new HashSet<int>();
                for (int index = contents.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (contents[index] is not PdfIndirectReference invocation || invocation.Generation != 0
                        || !seen.Add(invocation.ObjectNumber)) break;
                    PdfStream stream = Stream(document, invocation);
                    if (!stream.Dictionary.TryGetValue(Metadata, out PdfObject? value)) break;
                    var metadata = value as PdfDictionary ?? throw Invalid();
                    ValidateMetadata(metadata);
                    if (!Same(metadata[N("Contents")], invocation)
                        || !StringBytes(metadata[N("UnitHash")]).SequenceEqual(MetadataHash(metadata))) throw Invalid();
                    var prefix = metadata[N("Prefix")] as PdfIndirectReference ?? throw Invalid();
                    var form = metadata[N("Form")] as PdfIndirectReference ?? throw Invalid();
                    if (!Same(metadata[N("Page")], page.Reference) || prefix.Generation != 0 || form.Generation != 0
                        || contents.Count(item => Same(item, prefix)) != 1
                        || contents.Count(item => Same(item, invocation)) != 1
                        || Array.FindIndex(contents.ToArray(), item => Same(item, prefix)) >= index) throw Invalid();
                    if (!Decode(document, Stream(document, prefix)).AsSpan().SequenceEqual("q\n"u8)) throw Invalid();
                    if (!StringBytes(metadata[N("PrefixGraph")]).SequenceEqual(Graph(document, prefix))
                        || !StringBytes(metadata[N("InvocationGraph")]).SequenceEqual(InvocationGraph(document, stream))) throw Invalid();
                    var name = metadata[N("Resource")] as PdfName ?? throw Invalid();
                    string resource = Encoding.ASCII.GetString(name.Bytes.Span);
                    if (InvocationResource(Encoding.ASCII.GetString(Decode(document, stream))) != resource
                        || !Same(XObjects(document, page)[name], form)) throw Invalid();
                    var data = metadata[N("Data")] as PdfString ?? throw Invalid();
                    if (data.Bytes.Length > MaximumData
                        || !StringBytes(metadata[N("Frame")]).SequenceEqual(Frame(document, page, null, lookup))
                        || !StringBytes(metadata[N("Graph")]).SequenceEqual(FormGraph(form))) throw Invalid();
                    if (!PdfObjectWriter.Write(metadata[N("Structure")]).AsSpan()
                        .SequenceEqual(PdfObjectWriter.Write(CaptureStructure(document, page, form, lookup)))) throw Invalid();
                    result.Add(new PdfEditablePageOverlay(page.Index, invocation.ObjectNumber, form.ObjectNumber,
                        data.Bytes.ToArray(), prefix, Convert.ToHexString(SHA256.HashData(PdfObjectWriter.Write(metadata)))));
                }
                result.Reverse(pageStart, result.Count - pageStart);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException
                or KeyNotFoundException or FormatException or NotSupportedException or OverflowException or PdfFilterException)
            {
                result.RemoveAll(unit => unit.PageIndex == page.Index);
            }
        }
        return result.AsReadOnly();
    }
    /// <summary>Removes revalidated owned units.</summary>
    public static byte[] Remove(PdfDocument document, IReadOnlyList<PdfEditablePageOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        if (PdfSignatureReader.ReadCertificationPermission(document).HasValue)
            throw new InvalidOperationException("The document certification signature prohibits removing page content.");
        IReadOnlyList<PdfEditablePageOverlay> current = Read(document);
        if (overlays.Count != overlays.Select(unit => unit.ContentsObjectNumber).Distinct().Count()) throw Invalid();
        foreach (PdfEditablePageOverlay unit in overlays)
            if (!current.Any(item => item.PageIndex == unit.PageIndex
                && item.ContentsObjectNumber == unit.ContentsObjectNumber && item.Identity == unit.Identity)) throw Invalid();
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        RemoveStructure(document, update, tree, overlays);
        foreach (var group in overlays.GroupBy(unit => unit.PageIndex))
        {
            PdfPageTreeEntry page = tree.Pages[group.Key];
            var removed = group.SelectMany(unit => new[] { unit.ContentsObjectNumber, unit.Prefix.ObjectNumber }).ToHashSet();
            PdfObject[] retained = Contents(document, page).Where(item => item is not PdfIndirectReference reference
                || !removed.Contains(reference.ObjectNumber)).ToArray();
            var entries = page.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
            entries[N("Contents")] = new PdfArray(retained);
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        }
        return update.Build();
    }

    // Reads each document-wide structure once for a pass over many units; reading the page tree, page information
    // or parent tree again per unit made opening and saving documents with many edited pages quadratic.
    private sealed class DocumentLookup(PdfDocument document)
    {
        private PdfPageTree? _tree;
        private IReadOnlyList<PdfPageInformation>? _pages;
        private ILookup<long, PdfNumberTreeEntry>? _parents;
        internal PdfPageTree Tree => _tree ??= PdfPageTree.Read(document);
        internal int Rotation(int pageIndex) => (_pages ??= PdfPageInformation.Read(document))[pageIndex].Rotation;
        internal PdfNumberTreeEntry[] ParentEntries(PdfObject parentTree, long key) =>
            [.. (_parents ??= PdfNumberTree.Read(document, parentTree).ToLookup(entry => entry.Key))[key]];
    }

    private static PdfObject CaptureStructure(PdfDocument document, PdfPageTreeEntry page, PdfIndirectReference form,
        DocumentLookup? lookup = null)
    {
        PdfStream stream = Stream(document, form);
        if (!stream.Dictionary.TryGetValue(N("StructParents"), out PdfObject? keyValue)) return PdfNull.Instance;
        if (keyValue is not PdfInteger key || key.Value < 0) throw Invalid();
        lookup ??= new DocumentLookup(document);
        PdfPageTree tree = lookup.Tree;
        var root = Resolve(document, tree.Catalog[N("StructTreeRoot")]) as PdfDictionary ?? throw Invalid();
        PdfNumberTreeEntry[] entries = lookup.ParentEntries(root[N("ParentTree")], key.Value);
        if (entries.Length != 1 || Resolve(document, entries[0].Value) is not PdfArray { Count: 1 } references
            || references[0] is not PdfIndirectReference reference) throw Invalid();
        var structure = Resolve(document, reference) as PdfDictionary ?? throw Invalid();
        if (structure.Count is < 6 or > 7
            || structure.Keys.Any(key => key.ValueAsLatin1() is not ("Type" or "S" or "P" or "Pg" or "Alt" or "K" or "NS"))
            || structure[N("Type")] is not PdfName structureType || !structureType.Equals(N("StructElem"))
            || structure[N("Alt")] is not PdfString alternate || alternate.Bytes.Length > MaximumData
            || structure.TryGetValue(N("NS"), out PdfObject? namespaceValue) && namespaceValue is not PdfIndirectReference
            || structure[N("P")] is not PdfIndirectReference parentReference
            || !Same(structure[N("Pg")], page.Reference)
            || structure[N("S")] is not PdfName role || !role.Equals(N("Figure"))
            || structure[N("K")] is not PdfDictionary mcr || mcr.Count != 4
            || mcr[N("Type")] is not PdfName type || !type.Equals(N("MCR"))
            || !Same(mcr[N("Stm")], form) || !Same(mcr[N("Pg")], page.Reference)
            || mcr[N("MCID")] is not PdfInteger { Value: 0 }) throw Invalid();
        var parent = Resolve(document, parentReference) as PdfDictionary ?? throw Invalid();
        if (Kids(document, parent[N("K")]).Count(child => Same(child, reference)) != 1) throw Invalid();
        return new PdfDictionary([
            new(N("Key"), key), new(N("Reference"), reference), new(N("Parent"), parentReference),
            new(N("Hash"), Bytes(SHA256.HashData(PdfObjectWriter.Write(structure))))
        ]);
    }

    private static PdfObject[] Kids(PdfDocument document, PdfObject value)
    {
        PdfObject resolved = Resolve(document, value);
        if (resolved is not PdfArray array) return [value];
        if (array.Count > 16384) throw Invalid();
        return array.ToArray();
    }

    private static void RemoveStructure(PdfDocument document, PdfIncrementalUpdateBuilder update,
        PdfPageTree tree, IReadOnlyList<PdfEditablePageOverlay> overlays)
    {
        var structures = new List<PdfDictionary>();
        var lookup = new DocumentLookup(document);
        foreach (PdfEditablePageOverlay unit in overlays)
        {
            PdfObject value = CaptureStructure(document, tree.Pages[unit.PageIndex],
                new PdfIndirectReference(unit.FormObjectNumber, 0), lookup);
            if (value is PdfDictionary structure) structures.Add(structure);
        }
        if (structures.Count == 0) return;
        foreach (var group in structures.GroupBy(value => ((PdfIndirectReference)value[N("Parent")]).ObjectNumber))
        {
            PdfIndirectReference reference = (PdfIndirectReference)group.First()[N("Parent")];
            var parent = Resolve(document, reference) as PdfDictionary ?? throw Invalid();
            PdfObject[] retained = Kids(document, parent[N("K")])
                .Where(child => !group.Any(value => Same(child, value[N("Reference")]))).ToArray();
            var entries = parent.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (retained.Length == 0) entries.Remove(N("K"));
            else entries[N("K")] = retained.Length == 1 ? retained[0] : new PdfArray(retained);
            update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries));
        }
        if (tree.Catalog[N("StructTreeRoot")] is not PdfIndirectReference rootReference) throw Invalid();
        var root = Resolve(document, rootReference) as PdfDictionary ?? throw Invalid();
        var removedKeys = structures.Select(value => ((PdfInteger)value[N("Key")]).Value).ToHashSet();
        PdfObject[] numbers = PdfNumberTree.Read(document, root[N("ParentTree")])
            .Where(entry => !removedKeys.Contains(entry.Key))
            .SelectMany(entry => new PdfObject[] { new PdfInteger(entry.Key), entry.Value }).ToArray();
        var rootEntries = root.ToDictionary(pair => pair.Key, pair => pair.Value);
        rootEntries[N("ParentTree")] = update.AddObject(new PdfDictionary([new(N("Nums"), new PdfArray(numbers))]));
        update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(rootEntries));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var seen = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (seen.Count >= 64 || !seen.Add((reference.ObjectNumber, reference.Generation))) throw Invalid();
            value = document.Resolve(reference);
        }
        return value;
    }
    private static PdfDocument Reopen(PdfDocument source, byte[] bytes) => source.CaptureEncryption() is { } encryption
        ? PdfDocument.OpenWithEncryption(bytes, encryption) : PdfDocument.Open(bytes);
    private static PdfArray Contents(PdfDocument document, PdfPageTreeEntry page)
    {
        PdfObject value = Resolve(document, page.Dictionary[N("Contents")]);
        if (value is PdfStream) return new PdfArray([page.Dictionary[N("Contents")]]);
        if (value is not PdfArray array || array.Count > 4096) throw Invalid();
        return array;
    }
    private static PdfDictionary XObjects(PdfDocument document, PdfPageTreeEntry page)
    {
        PdfObject resources = page.Dictionary.TryGetValue(N("Resources"), out PdfObject? direct)
            ? direct : page.InheritedValues[N("Resources")];
        var dictionary = Resolve(document, resources) as PdfDictionary ?? throw Invalid();
        return Resolve(document, dictionary[N("XObject")]) as PdfDictionary ?? throw Invalid();
    }
    private static PdfStream Stream(PdfDocument document, PdfIndirectReference reference)
        => document.Resolve(reference) as PdfStream ?? throw Invalid();
    private static byte[] Decode(PdfDocument document, PdfStream stream)
        => PdfStreamDecoder.Decode(stream, document.Resolve, 1024);
    private static bool Same(PdfObject left, PdfObject right) => left is PdfIndirectReference a
        && right is PdfIndirectReference b && a.ObjectNumber == b.ObjectNumber && a.Generation == b.Generation;
    private static string InvocationResource(string commands)
    {
        if (commands.StartsWith("Q\n/Artifact BMC\n", StringComparison.Ordinal)
            && commands.EndsWith("EMC\n", StringComparison.Ordinal))
            commands = "Q\n" + commands[16..^4];
        const string start = "Q\nq /";
        const string end = " Do Q\n";
        if (!commands.StartsWith(start, StringComparison.Ordinal) || !commands.EndsWith(end, StringComparison.Ordinal)) throw Invalid();
        string resource = commands[start.Length..^end.Length];
        if (!resource.StartsWith("KPO", StringComparison.Ordinal) || resource.Length > 16
            || resource.Length < 4 || resource[3..].Any(character => character is < '0' or > '9')) throw Invalid();
        return resource;
    }
    private static PdfString Bytes(byte[] bytes) => new(bytes, PdfStringForm.Hexadecimal);
    private static ReadOnlySpan<byte> StringBytes(PdfObject value) => value is PdfString text ? text.Bytes.Span : throw Invalid();
    private static void ValidateMetadata(PdfDictionary metadata)
    {
        if (metadata.Count != 13 || metadata[N("Version")] is not PdfInteger { Value: 1 }
            || metadata[N("Data")] is not PdfString data || data.Bytes.Length > MaximumData
            || metadata[N("Resource")] is not PdfName resource || resource.Bytes.Length > 16) throw Invalid();
        foreach (string key in new[] { "Prefix", "Contents", "Form" })
            if (metadata[N(key)] is not PdfIndirectReference { Generation: 0, ObjectNumber: > 0 }) throw Invalid();
        if (metadata[N("Page")] is not PdfIndirectReference { ObjectNumber: > 0 }) throw Invalid();
        foreach (string key in new[] { "Frame", "Graph", "UnitHash", "InvocationGraph", "PrefixGraph" })
            if (metadata[N(key)] is not PdfString hash || hash.Bytes.Length != 32) throw Invalid();
        PdfObject structureValue = metadata[N("Structure")];
        if (structureValue is PdfNull) return;
        if (structureValue is not PdfDictionary structure || structure.Count != 4
            || structure[N("Key")] is not PdfInteger { Value: >= 0 }
            || structure[N("Reference")] is not PdfIndirectReference { Generation: 0, ObjectNumber: > 0 }
            || structure[N("Parent")] is not PdfIndirectReference { Generation: >= 0 and <= 65535, ObjectNumber: > 0 }
            || structure[N("Hash")] is not PdfString { Bytes.Length: 32 }) throw Invalid();
    }
    private static byte[] MetadataHash(PdfDictionary metadata) => SHA256.HashData(PdfObjectWriter.Write(
        new PdfDictionary(metadata.Where(pair => !pair.Key.Equals(N("UnitHash")))
            .OrderBy(pair => pair.Key.ValueAsLatin1(), StringComparer.Ordinal))));
    private static byte[] Frame(PdfDocument document, PdfPageTreeEntry page, int? rotation, DocumentLookup? lookup = null)
    {
        var values = new List<PdfObject> { page.Reference };
        foreach (string key in new[] { "MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox", "UserUnit" })
        {
            PdfObject value = page.Dictionary.TryGetValue(N(key), out PdfObject? direct) ? direct
                : page.InheritedValues.GetValueOrDefault(N(key)) ?? PdfNull.Instance;
            value = Resolve(document, value);
            if (value is PdfArray array)
            {
                if (key == "UserUnit" || array.Count != 4) throw Invalid();
                value = new PdfArray(array.Select(item => Resolve(document, item)));
                if (((PdfArray)value).Any(item => item is not (PdfInteger or PdfReal))) throw Invalid();
            }
            else if (value is not PdfNull && (key != "UserUnit" || value is not (PdfInteger or PdfReal))) throw Invalid();
            values.Add(value);
        }
        int angle = rotation ?? (lookup ?? new DocumentLookup(document)).Rotation(page.Index);
        values.Add(new PdfInteger(angle));
        return SHA256.HashData(PdfObjectWriter.Write(new PdfArray(values)));
    }
    private static byte[] InvocationGraph(PdfDocument document, PdfStream stream) => Graph(document,
        new PdfStream(new PdfDictionary(stream.Dictionary.Where(pair => !pair.Key.Equals(Metadata))), stream.EncodedData.Span));
    private static byte[] Graph(PdfDocument document, PdfObject form)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var seen = new HashSet<(int, int)>();
        var active = new HashSet<(int, int)>();
        int remaining = MaximumGraph;
        int nodes = 0;
        Visit(form, 0);
        return hash.GetHashAndReset();
        void Add(ReadOnlySpan<byte> bytes)
        {
            remaining -= checked(bytes.Length + 4);
            if (remaining < 0) throw Invalid();
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        void Visit(PdfObject value, int depth)
        {
            if (++nodes > 16384 || depth > 64) throw Invalid();
            if (value is PdfIndirectReference reference)
            {
                Add(PdfObjectWriter.Write(reference));
                var identity = (reference.ObjectNumber, reference.Generation);
                if (active.Contains(identity)) throw Invalid();
                if (seen.Add(identity))
                {
                    PdfObject resolved = document.Resolve(reference);
                    if (resolved is PdfNull) throw Invalid();
                    active.Add(identity);
                    Visit(resolved, depth + 1);
                    active.Remove(identity);
                }
            }
            else if (value is PdfStream stream)
            {
                if (stream.EncodedData.Length > remaining) throw Invalid();
                Add("stream{"u8); Visit(stream.Dictionary, depth + 1); Add(stream.EncodedData.Span); Add("}stream"u8);
            }
            else if (value is PdfDictionary dictionary)
            {
                if (dictionary.Count > 16384 || dictionary.Keys.Sum(key => (long)key.Bytes.Length) > remaining) throw Invalid();
                if (dictionary.ContainsKey(N("Parent")) || dictionary.TryGetValue(N("Type"), out PdfObject? typeValue)
                    && Resolve(document, typeValue) is PdfName type
                    && type.ValueAsLatin1() is "Catalog" or "Pages" or "Page" or "StructTreeRoot" or "StructElem") throw Invalid();
                Add("<<"u8);
                foreach (var pair in dictionary.OrderBy(pair => Convert.ToHexString(pair.Key.Bytes.Span), StringComparer.Ordinal))
                { Add(PdfObjectWriter.Write(pair.Key)); Visit(pair.Value, depth + 1); }
                Add(">>"u8);
            }
            else if (value is PdfArray array)
            { Add("["u8); foreach (PdfObject item in array) Visit(item, depth + 1); Add("]"u8); }
            else Add(PdfObjectWriter.Write(value));
        }
    }
    private static InvalidOperationException Invalid() => new("The owned overlay is not intact or exceeds its bounds.");
}
