using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>
/// Checks a document that declares PDF/UA-2 against the machine rules of ISO 14289-2, each named by
/// its veraPDF PDF/UA-2 profile rule ID. Structure types are resolved through their namespaces: an
/// element without NS belongs to the PDF 1.7 namespace and follows the RoleMap; an element with NS
/// follows that namespace's RoleMapNS. The PDF 1.7, PDF 2.0 and MathML namespaces are standard.
/// </summary>
internal static partial class PdfUa2Checks
{
    private const int MaximumDepth = 256;
    private const int MaximumDecodedBytes = 64 * 1024 * 1024;
    private const string Pdf17Namespace = "http://iso.org/pdf/ssn";
    private const string Pdf20Namespace = "http://iso.org/pdf2/ssn";
    private const string MathMLNamespace = "http://www.w3.org/1998/Math/MathML";

    // Rules whose PDF/UA-1 check means the same thing in PDF/UA-2: the content and font walk and
    // the AS entry check run once and their findings carry the UA-2 ID.
    private static readonly Dictionary<string, string> SharedConditions = new(StringComparer.Ordinal)
    {
        ["01-005"] = "8.2.2-1",
        ["31-009"] = "8.4.5.5.1-1",
        ["20-003"] = "8.7-2"
    };

    internal static IReadOnlyList<PdfUaFailure> Run(PdfDocument document, IReadOnlyList<PdfUaFailure> pdfUa1Failures,
        CancellationToken cancellationToken)
    {
        var context = new Context(document, cancellationToken);
        context.Run();
        var failures = new List<PdfUaFailure>(context.Failures);
        foreach (PdfUaFailure failure in pdfUa1Failures)
            if (SharedConditions.TryGetValue(failure.Condition, out string? rule))
                failures.Add(failure with { Condition = rule, Clause = PdfUa2Rules.Clause(rule) });
        return failures.AsReadOnly();
    }

    private enum Space { Pdf17, Pdf20, MathML, Other }

    private readonly record struct StandardType(string Name, Space Space);

    private sealed record AnnotationParent(StandardType? Type, PdfDictionary Element);

    [GeneratedRegex("^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$")]
    private static partial Regex LanguageTag();

    private sealed class Context(PdfDocument document, CancellationToken cancellationToken)
    {
        // ISO 32000-1 standard structure types, the PDF 1.7 namespace.
        private static readonly HashSet<string> Pdf17Types =
        [
            "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI",
            "Index", "NonStruct", "Private", "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L",
            "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Span",
            "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB",
            "RT", "RP", "Warichu", "WT", "WP", "Figure", "Formula", "Form"
        ];

        // ISO 32000-2 standard structure types, the PDF 2.0 namespace. Hn headings of any level
        // are matched separately.
        private static readonly HashSet<string> Pdf20Types =
        [
            "Document", "DocumentFragment", "Part", "Sect", "Div", "Aside", "NonStruct", "P", "H",
            "Title", "FENote", "Sub", "Lbl", "Span", "Em", "Strong", "Link", "Annot", "Form",
            "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP", "L", "LI", "LBody", "Table", "TR",
            "TH", "TD", "THead", "TBody", "TFoot", "Caption", "Figure", "Formula", "Artifact"
        ];

        // ISO 32000-2 Table 171 markup annotation subtypes that are not covered by their own rule.
        private static readonly HashSet<string> MarkupSubtypes =
        [
            "Text", "FreeText", "Line", "Square", "Circle", "Polygon", "PolyLine", "Highlight",
            "Underline", "Squiggly", "StrikeOut", "Caret", "Stamp", "Ink", "FileAttachment",
            "Redact", "Projection"
        ];

        private static readonly XNamespace PdfUa = "http://www.aiim.org/pdfua/ns/id/";
        private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

        private readonly List<PdfUaFailure> _failures = [];
        private readonly HashSet<(string, int?, int?)> _reported = [];
        private readonly Dictionary<(int, int), AnnotationParent> _annotationParents = [];
        private readonly Dictionary<string, StandardType?> _resolved = new(StringComparer.Ordinal);
        private Dictionary<string, PdfObject> _roleMap = [];
        private PdfPageTree _tree = null!;
        private Dictionary<(int, int), int> _pageIndexes = [];

        internal IReadOnlyList<PdfUaFailure> Failures => _failures;

        internal void Run()
        {
            _tree = PdfPageTree.Read(document);
            _pageIndexes = _tree.Pages.ToDictionary(
                page => (page.Reference.ObjectNumber, page.Reference.Generation), page => page.Index);
            PdfDictionary catalog = _tree.Catalog;
            Guard("6.1-1", () => CheckVersion(catalog));
            Guard("6.2-1", () => CheckMarked(catalog));
            Guard("8.11.1-2", () => CheckMetadata(catalog));
            Guard("8.4.4-1", () => CheckLanguage(catalog));
            Guard("8.11.2-1", () => CheckViewerPreferences(catalog));
            Guard("8.2.1-1", () => CheckStructure(catalog));
            Guard("8.10.1-3", () => CheckXfa(catalog));
            Guard("8.14.1-1", () => CheckEmbeddedFiles(catalog));
            Guard("8.7-1", () => CheckOptionalContentNames(catalog));
            foreach (PdfPageTreeEntry page in _tree.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guard("8.9.3.3-1", () => CheckAnnotations(page), page.Index);
            }
        }

        private void CheckVersion(PdfDictionary catalog)
        {
            bool headerIs20 = document.Header.Version.CompareTo(PdfVersion.Pdf20) == 0;
            bool catalogIs20 = Get(catalog, "Version") is PdfName version && version.ValueAsLatin1() == "2.0";
            if (!headerIs20 && !catalogIs20)
                Fail("6.1-1", $"The file is PDF {document.Header.Version}; PDF/UA-2 needs PDF 2.0 in the header or the catalog Version entry.");
        }

        private void CheckMarked(PdfDictionary catalog)
        {
            if (Get(catalog, "MarkInfo") is not PdfDictionary markInfo
                || Get(markInfo, "Marked") is not PdfBoolean { Value: true })
                Fail("6.2-1", "The catalog has no MarkInfo dictionary with Marked set to true.");
        }

        private void CheckMetadata(PdfDictionary catalog)
        {
            if (Get(catalog, "Metadata") is not PdfStream metadata)
            {
                Fail("8.11.1-2", "The catalog has no XMP metadata stream.");
                return;
            }
            XDocument xmp;
            using (var input = new MemoryStream(
                PdfStreamDecoder.Decode(metadata, document.Resolve, MaximumDecodedBytes), writable: false))
            using (XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumDecodedBytes
            }))
                xmp = XDocument.Load(reader, LoadOptions.None);
            (string? part, string? partPrefix) = XmpProperty(xmp, PdfUa + "part");
            (string? rev, string? revPrefix) = XmpProperty(xmp, PdfUa + "rev");
            if (part is null)
                Fail("5-1", "The XMP metadata has no PDF/UA identification (pdfuaid:part).");
            else
            {
                if (part != "2")
                    Fail("5-2", $"The pdfuaid:part value is {part}, not 2.");
                if (partPrefix is not null && partPrefix != "pdfuaid")
                    Fail("5-3", $"The PDF/UA part property uses the prefix {partPrefix}, not pdfuaid.");
            }
            if (rev is not null && revPrefix is not null && revPrefix != "pdfuaid")
                Fail("5-4", $"The PDF/UA rev property uses the prefix {revPrefix}, not pdfuaid.");
            if (rev != "2024")
                Fail("5-5", rev is null
                    ? "The XMP metadata has no pdfuaid:rev value."
                    : $"The pdfuaid:rev value is {rev}, not 2024.");
            XElement? title = xmp.Descendants(Dc + "title").FirstOrDefault();
            if (title is null || string.IsNullOrWhiteSpace(title.Value))
                Fail("8.11.1-1", "The XMP metadata has no dc:title.");
        }

        private void CheckLanguage(PdfDictionary catalog)
        {
            if (Get(catalog, "Lang") is not PdfString lang
                || PdfUnicodeEncoding.DecodeTextString(lang.Bytes.Span, "The document language").Length == 0)
            {
                Fail("8.4.4-1", "The catalog has no non-empty Lang entry.",
                    objectNumber: _tree.CatalogReference.ObjectNumber,
                    objectGeneration: _tree.CatalogReference.Generation);
                return;
            }
            CheckLanguageTag(lang, "The catalog Lang entry", null, _tree.CatalogReference.ObjectNumber,
                _tree.CatalogReference.Generation);
        }

        private void CheckLanguageTag(PdfString lang, string owner, int? page, int? objectNumber, int? generation)
        {
            string value = PdfUnicodeEncoding.DecodeTextString(lang.Bytes.Span, "The Lang value");
            if (value.Length > 0 && !LanguageTag().IsMatch(value))
                Fail("8.4.4-2", $"{owner} \"{value}\" is not a language tag.", page, objectNumber, generation);
        }

        private void CheckViewerPreferences(PdfDictionary catalog)
        {
            if (Get(catalog, "ViewerPreferences") is not PdfDictionary preferences
                || Get(preferences, "DisplayDocTitle") is not PdfBoolean { Value: true })
                Fail("8.11.2-1", "The catalog ViewerPreferences dictionary does not set DisplayDocTitle to true.");
        }

        private void CheckXfa(PdfDictionary catalog)
        {
            if (Get(catalog, "AcroForm") is PdfDictionary form && form.ContainsKey(Name("XFA")))
                Fail("8.10.1-3", "The interactive form contains XFA.");
        }

        // Following the veraPDF profile, configuration names are required once the document offers
        // alternate configurations in Configs; then the default one needs a Name too.
        private void CheckOptionalContentNames(PdfDictionary catalog)
        {
            if (Get(catalog, "OCProperties") is not PdfDictionary properties
                || Get(properties, "Configs") is not PdfArray configs
                || !configs.Any(item => Resolve(item) is PdfDictionary)) return;
            if (Get(properties, "D") is PdfDictionary defaults && !HasText(defaults, "Name"))
                Fail("8.7-1", "The default optional content configuration has no Name.");
            foreach (PdfObject item in configs)
                if (Resolve(item) is PdfDictionary config && !HasText(config, "Name"))
                    Fail("8.7-1", "An optional content configuration in Configs has no Name.");
        }

        private void CheckEmbeddedFiles(PdfDictionary catalog)
        {
            if (Get(catalog, "Names") is not PdfDictionary names
                || Get(names, "EmbeddedFiles") is not PdfDictionary tree) return;
            var visited = new HashSet<(int, int)>();
            Walk(tree, 0);

            void Walk(PdfDictionary node, int depth)
            {
                if (depth >= MaximumDepth) return;
                if (Get(node, "Names") is PdfArray pairs)
                    for (int index = 1; index < pairs.Count; index += 2)
                    {
                        PdfIndirectReference? reference = pairs[index] as PdfIndirectReference;
                        if (Resolve(pairs[index]) is PdfDictionary specification
                            && !specification.ContainsKey(Name("Desc")))
                            Fail("8.14.1-1", "An embedded file specification has no Desc entry.",
                                objectNumber: reference?.ObjectNumber, objectGeneration: reference?.Generation);
                    }
                if (Get(node, "Kids") is PdfArray kids)
                    foreach (PdfObject kid in kids)
                    {
                        if (kid is PdfIndirectReference kidReference
                            && !visited.Add((kidReference.ObjectNumber, kidReference.Generation))) continue;
                        if (Resolve(kid) is PdfDictionary child) Walk(child, depth + 1);
                    }
            }
        }

        private void CheckStructure(PdfDictionary catalog)
        {
            if (Get(catalog, "StructTreeRoot") is not PdfDictionary root)
            {
                Fail("8.2.1-1", "The catalog has no StructTreeRoot.");
                return;
            }
            if (Get(root, "RoleMap") is PdfDictionary roleMap)
                _roleMap = roleMap.ToDictionary(item => item.Key.ValueAsLatin1(), item => item.Value);
            List<PdfDictionary> top = root.TryGetValue(Name("K"), out PdfObject? topKids)
                ? Elements(topKids) : [];
            if (top.Count != 1 || ResolveType(top[0]) is not { Name: "Document" } document20)
                Fail("8.2.5.2-1", "The structure tree root does not hold exactly one Document element.");
            else if (document20.Space != Space.Pdf20)
                Fail("8.2.5.2-2", "The Document element under the structure tree root is not in the PDF 2.0 namespace.");
            if (topKids is not null)
                Visit(topKids, null, null, 0, new HashSet<(int, int)>());
        }

        private void Visit(PdfObject value, StandardType? parentType, PdfDictionary? parentElement, int depth,
            HashSet<(int, int)> visited)
        {
            if (depth >= MaximumDepth)
                throw new InvalidOperationException("The structure tree exceeds the supported nesting depth.");
            PdfIndirectReference? reference = value as PdfIndirectReference;
            if (reference is not null && !visited.Add((reference.ObjectNumber, reference.Generation))) return;
            value = Resolve(value);
            if (value is PdfArray array)
            {
                foreach (PdfObject item in array) Visit(item, parentType, parentElement, depth + 1, visited);
                return;
            }
            if (value is not PdfDictionary element) return;
            if (IsName(element, "Type", "OBJR"))
            {
                if (parentElement is not null && element.TryGetValue(Name("Obj"), out PdfObject? target)
                    && target is PdfIndirectReference annotation)
                    _annotationParents[(annotation.ObjectNumber, annotation.Generation)] =
                        new AnnotationParent(parentType, parentElement);
                return;
            }
            if (IsName(element, "Type", "MCR") || Get(element, "S") is not PdfName) return;
            int? page = PageOf(element);
            int? objectNumber = reference?.ObjectNumber;
            int? generation = reference?.Generation;
            if (!element.ContainsKey(Name("P")))
                Fail("8.2.1-2", "A structure element has no P (parent) entry.", page, objectNumber, generation);
            if (Get(element, "Lang") is PdfString lang)
                CheckLanguageTag(lang, "A structure element Lang entry", page, objectNumber, generation);
            StandardType? type = ResolveType(element);
            if (type is StandardType known)
                CheckElement(known, parentType, element, page, objectNumber, generation);
            if (element.TryGetValue(Name("K"), out PdfObject? children))
                Visit(children, type, element, depth + 1, visited);
        }

        private void CheckElement(StandardType type, StandardType? parentType, PdfDictionary element,
            int? page, int? objectNumber, int? generation)
        {
            void Fail(string rule, string message) => this.Fail(rule, message, page, objectNumber, generation);

            if (type.Space == Space.MathML)
            {
                if (type.Name == "math" && parentType is not { Name: "Formula" } and not { Space: Space.MathML })
                    Fail("8.2.5.29-1", "A MathML math element is not a child of a Formula element.");
                return;
            }
            List<PdfObject> kids = element.TryGetValue(Name("K"), out PdfObject? value)
                ? Resolve(value) is PdfArray array ? [.. array] : [value] : [];
            List<string> kidTypes = [.. Elements(kids).Select(kid => ResolveType(kid)?.Name ?? "")];
            switch (type.Name)
            {
                case "H":
                    Fail("8.2.5.12-1", "The H structure type is used; PDF/UA-2 needs numbered headings.");
                    break;
                case "Note" when type.Space == Space.Pdf17:
                    Fail("8.2.5.14-1", "A Note element is not role mapped to the PDF 2.0 namespace (use FENote).");
                    break;
                case "TOCI" when type.Space == Space.Pdf17:
                    if (!element.ContainsKey(Name("Ref"))
                        && !Elements(kids).Any(kid => kid.ContainsKey(Name("Ref"))))
                        Fail("8.2.5.8-1", "A TOCI element identifies no target with a Ref entry.");
                    break;
                case "FENote":
                    if (Attribute(element, null, "NoteType") is PdfName noteType
                        && noteType.ValueAsLatin1() is not ("Footnote" or "Endnote" or "None"))
                        Fail("8.2.5.14-4", $"An FENote NoteType is {noteType.ValueAsLatin1()}, not Footnote, Endnote or None.");
                    break;
                case "Figure":
                    if (!HasText(element, "Alt") && !HasText(element, "ActualText"))
                        Fail("8.2.5.28.2-1", "A Figure structure element has neither Alt nor ActualText.");
                    break;
                case "Ruby":
                    if (!kidTypes.SequenceEqual(["RB", "RT"]) && !kidTypes.SequenceEqual(["RB", "RP", "RT", "RP"]))
                        Fail("8.2.5.23-1", $"A Ruby element holds {Sequence(kidTypes)}, not RB, RT or RB, RP, RT, RP.");
                    break;
                case "Warichu":
                    if (!kidTypes.SequenceEqual(["WP", "WT", "WP"]))
                        Fail("8.2.5.24-1", $"A Warichu element holds {Sequence(kidTypes)}, not WP, WT, WP.");
                    break;
                case "L":
                    bool labelled = Elements(kids).Where(kid => ResolveType(kid)?.Name == "LI")
                        .Any(item => item.TryGetValue(Name("K"), out PdfObject? itemKids)
                            && Elements(itemKids).Any(kid => ResolveType(kid)?.Name == "Lbl"));
                    if (labelled && (Attribute(element, "List", "ListNumbering") is not PdfName numbering
                        || numbering.ValueAsLatin1() == "None"))
                        Fail("8.2.5.25-1", "A list with Lbl elements has no ListNumbering attribute, or sets it to None.");
                    break;
                case "LI":
                    if (kids.Any(kid => Resolve(kid) is PdfInteger
                        || Resolve(kid) is PdfDictionary dictionary && IsName(dictionary, "Type", "MCR")))
                        Fail("8.2.5.25-2", "An LI element holds content directly instead of in Lbl or LBody.");
                    break;
                case "Form":
                    int widgets = kids.Count(kid => Resolve(kid) is PdfDictionary objr && IsName(objr, "Type", "OBJR")
                        && Get(objr, "Obj") is PdfDictionary annotation && IsName(annotation, "Subtype", "Widget"));
                    if (widgets > 1)
                        Fail("8.10.1-2", $"A Form structure element holds {widgets} widget annotations.");
                    break;
            }
            for (int index = 1; index < kidTypes.Count - 1; index++)
                if (kidTypes[index] == "Caption")
                {
                    Fail("8.2.5.27-1", $"A Caption is neither the first nor the last child of its {type.Name} element.");
                    break;
                }
        }

        private static string Sequence(List<string> types) =>
            types.Count == 0 ? "nothing" : string.Join(", ", types.Select(type => type.Length == 0 ? "a nonstandard element" : type));

        private List<PdfDictionary> Elements(PdfObject kids) => Elements(
            Resolve(kids) is PdfArray array ? [.. array] : [kids]);

        private List<PdfDictionary> Elements(IEnumerable<PdfObject> kids) =>
            [.. kids.Select(Resolve).OfType<PdfDictionary>()
                .Where(kid => Get(kid, "S") is PdfName && !IsName(kid, "Type", "OBJR") && !IsName(kid, "Type", "MCR"))];

        // Resolves an element's structure type to a standard type in the PDF 1.7, PDF 2.0 or MathML
        // namespace, reporting 8.2.4-1 to 8.2.4-4 along the way. Returns null when none is reached.
        private StandardType? ResolveType(PdfDictionary element)
        {
            if (Get(element, "S") is not PdfName name) return null;
            PdfDictionary? space = Get(element, "NS") as PdfDictionary;
            string key = name.ValueAsLatin1() + "\n" + NamespaceUri(space);
            if (_resolved.TryGetValue(key, out StandardType? cached)) return cached;
            StandardType? result = Map(name.ValueAsLatin1(), space);
            _resolved[key] = result;
            return result;
        }

        private StandardType? Map(string type, PdfDictionary? space)
        {
            string start = type;
            string current = type;
            PdfDictionary? currentSpace = space;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (seen.Add(current + "\n" + NamespaceUri(currentSpace)))
            {
                Space kind = Kind(currentSpace);
                (string Name, PdfDictionary? Space)? next = Next(current, currentSpace);
                if (IsStandard(current, kind))
                {
                    if (next is null) return new StandardType(current, kind);
                    // A PDF 1.7 type may be mapped onto the PDF 2.0 namespace, which is how a
                    // document keeps a 1.7 tag such as Note while meeting PDF/UA-2.
                    if (kind == Space.Pdf17 && currentSpace is not null && next.Value.Space is not null
                        && Kind(next.Value.Space) == Space.Pdf20)
                    {
                        (current, currentSpace) = next.Value;
                        continue;
                    }
                    Fail("8.2.4-4", $"The standard structure type {current} is role mapped to {next.Value.Name}.");
                    return new StandardType(current, kind);
                }
                if (next is null)
                {
                    Fail("8.2.4-1", $"The structure type {start} does not map to the PDF 1.7, PDF 2.0 or MathML namespace.");
                    return null;
                }
                if (currentSpace is not null && next.Value.Space is not null
                    && NamespaceUri(next.Value.Space) == NamespaceUri(currentSpace))
                    Fail("8.2.4-3", $"The structure type {current} is role mapped to {next.Value.Name} in its own namespace.");
                (current, currentSpace) = next.Value;
            }
            Fail("8.2.4-2", $"The role mapping for {start} is circular.");
            return null;
        }

        // An element without NS is in the default namespace, which uses the RoleMap. An element
        // with NS uses that namespace's RoleMapNS, where a name value targets the default
        // namespace and an array value names the type and its namespace.
        private (string Name, PdfDictionary? Space)? Next(string type, PdfDictionary? space)
        {
            if (space is null)
                return _roleMap.TryGetValue(type, out PdfObject? mapped) && Resolve(mapped) is PdfName target
                    ? (target.ValueAsLatin1(), null) : null;
            if (Get(space, "RoleMapNS") is not PdfDictionary roleMap
                || Get(roleMap, type) is not PdfObject value) return null;
            if (value is PdfName plain) return (plain.ValueAsLatin1(), null);
            if (value is PdfArray { Count: >= 2 } pair && Resolve(pair[0]) is PdfName mappedName)
                return (mappedName.ValueAsLatin1(), Resolve(pair[1]) as PdfDictionary);
            return null;
        }

        private Space Kind(PdfDictionary? space) => NamespaceUri(space) switch
        {
            null or Pdf17Namespace => Space.Pdf17,
            Pdf20Namespace => Space.Pdf20,
            MathMLNamespace => Space.MathML,
            _ => Space.Other
        };

        private string? NamespaceUri(PdfDictionary? space) =>
            space is null ? null
            : Get(space, "NS") is PdfString uri ? Encoding.Latin1.GetString(uri.Bytes.Span) : "";

        private static bool IsStandard(string type, Space kind) => kind switch
        {
            Space.Pdf17 => Pdf17Types.Contains(type),
            Space.Pdf20 => Pdf20Types.Contains(type)
                || type.Length > 1 && type[0] == 'H' && type[1] is >= '1' and <= '9' && type[1..].All(char.IsAsciiDigit),
            Space.MathML => true,
            _ => false
        };

        private PdfObject? Attribute(PdfDictionary element, string? owner, string key)
        {
            if (!element.TryGetValue(Name("A"), out PdfObject? attributes)) return null;
            PdfObject resolved = Resolve(attributes);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
            foreach (PdfObject item in items)
                if (Resolve(item) is PdfDictionary dictionary && (owner is null || IsName(dictionary, "O", owner))
                    && Get(dictionary, key) is PdfObject found)
                    return found;
            return null;
        }

        private void CheckAnnotations(PdfPageTreeEntry page)
        {
            if (Get(page.Dictionary, "Annots") is not PdfArray annotations) return;
            bool hasAnnotation = false;
            foreach (PdfObject item in annotations)
            {
                if (Resolve(item) is not PdfDictionary annotation) continue;
                hasAnnotation = true;
                string subtype = Get(annotation, "Subtype") is PdfName name ? name.ValueAsLatin1() : "";
                PdfIndirectReference? reference = item as PdfIndirectReference;
                int? number = reference?.ObjectNumber;
                int? generation = reference?.Generation;
                AnnotationParent? parent = reference is null ? null
                    : _annotationParents.GetValueOrDefault((reference.ObjectNumber, reference.Generation));
                bool tagged = parent is not null;
                string? parentType = parent?.Type?.Name;
                bool hasContents = annotation.ContainsKey(Name("Contents"));
                long flags = Get(annotation, "F") is PdfInteger value ? value.Value : 0;
                void Fail(string rule, string message) => this.Fail(rule, message, page.Index, number, generation);

                if (tagged && (flags & 1) != 0)
                    Fail("8.9.2.2-1", $"A {subtype} annotation with the Invisible flag is in the structure tree.");
                if (tagged && (flags & 32) != 0 && (flags & 256) == 0)
                    Fail("8.9.2.2-2", $"A {subtype} annotation with the NoView flag is in the structure tree.");
                if (tagged && hasContents && parent!.Element.ContainsKey(Name("Alt"))
                    && Text(annotation, "Contents") != Text(parent.Element, "Alt"))
                    Fail("8.9.4.2-1", $"A {subtype} annotation's Contents differs from the Alt of its structure element.");
                switch (subtype)
                {
                    case "Link":
                        if (tagged && parentType is not ("Link" or "Reference"))
                            Fail("8.2.5.20-1", "A link annotation is not in a Link or Reference structure element.");
                        break;
                    case "Widget":
                        if (tagged && parentType != "Form" && IsFieldWidget(annotation))
                            Fail("8.10.1-1", "A widget annotation is not in a Form structure element.");
                        if (tagged && annotation.ContainsKey(Name("AA")) && !hasContents && IsFieldWidget(annotation))
                            Fail("8.10.2.3-2", "A widget annotation has additional actions (AA) but no Contents.");
                        if (tagged && Get(annotation, "Rect") is PdfArray { Count: 4 } rect
                            && Number(rect[2]) == Number(rect[0]) && Number(rect[3]) == Number(rect[1]))
                            Fail("8.9.2.4.13-1", "A widget annotation of zero width and height is in the structure tree.");
                        break;
                    case "Popup":
                        if (tagged)
                            Fail("8.9.2.4.9-1", "A popup annotation is in the structure tree.");
                        break;
                    case "PrinterMark":
                        if (tagged)
                            Fail("8.9.2.4.14-1", "A printer's mark annotation is in the structure tree.");
                        break;
                    case "TrapNet":
                        Fail("8.9.2.4.15-1", "A TrapNet annotation is present.");
                        break;
                    case "Sound":
                        Fail("8.9.2.4.11-1", "A sound annotation is present.");
                        break;
                    case "Movie":
                        Fail("8.9.2.4.11-2", "A movie annotation is present.");
                        break;
                    case "Screen":
                        if (!hasContents) Fail("8.9.2.4.12-1", "A screen annotation has no Contents entry.");
                        break;
                    case "3D":
                        if (!hasContents) Fail("8.9.2.4.19-1", "A 3D annotation has no Contents entry.");
                        break;
                    case "RichMedia":
                        if (!hasContents) Fail("8.9.2.4.19-2", "A RichMedia annotation has no Contents entry.");
                        break;
                    case "Watermark":
                        if (tagged && parentType != "Annot")
                            Fail("8.9.2.4.16-1", "A watermark annotation in the structure tree is not in an Annot element.");
                        break;
                }
                if (MarkupSubtypes.Contains(subtype) && tagged && parentType != "Annot")
                    Fail("8.9.2.3-1", $"A {subtype} markup annotation is not in an Annot structure element.");
                if (subtype == "Stamp" && !annotation.ContainsKey(Name("Name")) && !hasContents)
                    Fail("8.9.2.4.7-1", "A stamp annotation has neither a Name nor a Contents entry.");
                if (subtype == "Ink" && !hasContents)
                    Fail("8.9.2.4.8-1", "An ink annotation has no Contents entry.");
                if (subtype == "FileAttachment" && Get(annotation, "FS") is PdfDictionary file
                    && !file.ContainsKey(Name("AFRelationship")))
                    Fail("8.9.2.4.10-1", "A file attachment annotation's file specification has no AFRelationship entry.");
            }
            if (!hasAnnotation) return;
            if (Get(page.Dictionary, "Tabs") is not PdfName { } tabs
                || tabs.ValueAsLatin1() is not ("A" or "W" or "S"))
                Fail("8.9.3.3-1", "A page with annotations has no Tabs entry of A, W or S.", page.Index,
                    page.Reference.ObjectNumber, page.Reference.Generation);
        }

        private bool IsFieldWidget(PdfDictionary widget) =>
            widget.ContainsKey(Name("FT")) || widget.ContainsKey(Name("T")) || widget.ContainsKey(Name("Parent"));

        private double Number(PdfObject value) => Resolve(value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => (double)real.Value,
            _ => double.NaN
        };

        private void Guard(string rule, Action check, int? pageIndex = null)
        {
            try
            {
                check();
            }
            catch (Exception error) when (error is ArgumentException or FormatException
                or InvalidOperationException or NotSupportedException or OverflowException
                or XmlException or InvalidDataException)
            {
                Fail(rule, $"The check could not read the document: {error.Message}", pageIndex);
            }
        }

        private void Fail(string rule, string message, int? pageIndex = null,
            int? objectNumber = null, int? objectGeneration = null)
        {
            if (!_reported.Add((rule + "|" + message, pageIndex, objectNumber))) return;
            _failures.Add(new PdfUaFailure(rule, PdfUa2Rules.Clause(rule), message)
            {
                PageIndex = pageIndex,
                ObjectNumber = objectNumber,
                ObjectGeneration = objectGeneration
            });
        }

        private int? PageOf(PdfDictionary element) =>
            element.TryGetValue(Name("Pg"), out PdfObject? value) && value is PdfIndirectReference page
                && _pageIndexes.TryGetValue((page.ObjectNumber, page.Generation), out int index)
                ? index : null;

        private string? Text(PdfDictionary dictionary, string key) =>
            Get(dictionary, key) is PdfString text
                ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"The /{key} value") : null;

        private bool HasText(PdfDictionary dictionary, string key) => !string.IsNullOrWhiteSpace(Text(dictionary, key));

        private PdfObject? Get(PdfDictionary? dictionary, string key) =>
            dictionary is not null && dictionary.TryGetValue(Name(key), out PdfObject? value) ? Resolve(value) : null;

        private PdfObject Resolve(PdfObject value)
        {
            var visited = new HashSet<(int, int)>();
            while (value is PdfIndirectReference reference)
            {
                if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                    throw new InvalidOperationException("A reference chain contains a cycle.");
                value = document.Resolve(reference);
            }
            return value;
        }

        private static bool IsName(PdfDictionary dictionary, string key, string expected) =>
            dictionary.TryGetValue(Name(key), out PdfObject? value)
            && value is PdfName name && name.ValueAsLatin1() == expected;

        // Returns the first value of an XMP property, written as an element or an attribute, and
        // the prefix the document binds to its namespace there.
        private static (string? Value, string? Prefix) XmpProperty(XDocument xmp, XName name)
        {
            foreach (XElement element in xmp.Descendants(name))
                if (element.Value.Trim() is { Length: > 0 } text)
                    return (text, element.GetPrefixOfNamespace(name.Namespace));
            foreach (XAttribute attribute in xmp.Descendants().Attributes(name))
                if (attribute.Value.Trim() is { Length: > 0 } text)
                    return (text, attribute.Parent?.GetPrefixOfNamespace(name.Namespace));
            return (null, null);
        }
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
