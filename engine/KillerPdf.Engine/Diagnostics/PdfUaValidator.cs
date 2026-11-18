using System.Text;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>
/// Validates a document against PDF/UA and reports a result per checkpoint. A document whose XMP
/// declares PDF/UA-2 (pdfuaid:part 2) is checked against the PDF/UA-2 (ISO 14289-2) machine rules,
/// named by the veraPDF PDF/UA-2 profile rule IDs; any other document is checked against PDF/UA-1
/// (ISO 14289-1) and its 31 Matterhorn Protocol 1.1 checkpoints. Machine-checkable conditions that
/// are implemented pass or fail; conditions left to a person, and machine conditions not yet
/// implemented, are listed by ID so the report never claims more than it checked.
/// </summary>
/// <remarks>
/// <see cref="PdfUaReport.Standard"/> and <see cref="PdfUaReport.RuleSet"/> name the rule set that
/// ran, and <see cref="PdfUaReport.DeclaredPart"/> records what the document claims.
/// </remarks>
public static class PdfUaValidator
{
    private const int MaximumDepth = 256;
    private const int MaximumDecodedBytes = 64 * 1024 * 1024;
    private const int MaximumFormDrawsPerPage = 10_000;

    /// <summary>Validates an authenticated document against the rule set for the PDF/UA part it declares.</summary>
    public static PdfUaReport Validate(PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException("Authenticate the document before PDF/UA validation.");
        var context = new Context(document, cancellationToken);
        context.Run();
        if (context.DeclaredPart == 2)
            return new PdfUaReport(2,
                PdfUa2Rules.Evaluate(PdfUa2Checks.Run(document, context.Failures, cancellationToken)),
                PdfUa2Rules.Standard, PdfUa2Rules.Protocol);
        return new PdfUaReport(context.DeclaredPart, PdfUaMatterhorn.Evaluate(context.Failures));
    }

    private sealed class Context(PdfDocument document, CancellationToken cancellationToken)
    {
        private static readonly HashSet<string> StandardTypes =
        [
            "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption", "TOC", "TOCI",
            "Index", "NonStruct", "Private", "P", "H", "H1", "H2", "H3", "H4", "H5", "H6", "L",
            "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot", "Span",
            "Quote", "Note", "Reference", "BibEntry", "Code", "Link", "Annot", "Ruby", "RB",
            "RT", "RP", "Warichu", "WT", "WP", "Figure", "Formula", "Form"
        ];
        private static readonly XNamespace PdfUa = "http://www.aiim.org/pdfua/ns/id/";
        private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
        private static readonly XNamespace XmlNs = XNamespace.Xml;

        private readonly List<PdfUaFailure> _failures = [];
        private readonly HashSet<(string, int?, int?)> _reported = [];
        private readonly Dictionary<(int, int), string> _annotationParents = [];
        private readonly Dictionary<(int, int), int> _formUses = [];
        private readonly HashSet<(int, int)> _formsWithMcids = [];
        private readonly HashSet<(string, int)> _fontsChecked = [];
        private Dictionary<string, PdfObject> _roleMap = [];
        private PdfPageTree _tree = null!;
        private Dictionary<(int, int), int> _pageIndexes = [];
        private bool _hasDocumentLanguage;
        private int _formDraws;

        internal int? DeclaredPart { get; private set; }
        internal IReadOnlyList<PdfUaFailure> Failures => _failures;

        internal void Run()
        {
            _tree = PdfPageTree.Read(document);
            _pageIndexes = _tree.Pages.ToDictionary(
                page => (page.Reference.ObjectNumber, page.Reference.Generation), page => page.Index);
            PdfDictionary catalog = _tree.Catalog;
            Guard("01-007", () => CheckMarkInfo(catalog));
            Guard("06-001", () => CheckMetadata(catalog));
            Guard("07-001", () => CheckViewerPreferences(catalog));
            Guard("02-001", () => CheckStructure(catalog));
            Guard("20-001", () => CheckOptionalContent(catalog));
            Guard("21-001", () => CheckEmbeddedFiles(catalog));
            Guard("25-001", () => CheckXfa(catalog));
            Guard("26-001", CheckSecurity);
            foreach (PdfPageTreeEntry page in _tree.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guard("01-005", () => CheckPageContent(page), page.Index);
                Guard("28-002", () => CheckAnnotations(page), page.Index);
            }
            foreach (((int number, int generation), int uses) in _formUses)
                if (uses > 1 && _formsWithMcids.Contains((number, generation)))
                    Fail("30-002", "A form XObject with marked-content identifiers is drawn more than once.",
                        objectNumber: number, objectGeneration: generation);
        }

        private void CheckMarkInfo(PdfDictionary catalog)
        {
            if (Get(catalog, "MarkInfo") is PdfDictionary markInfo
                && Get(markInfo, "Suspects") is PdfBoolean { Value: true })
                Fail("01-007", "The catalog MarkInfo dictionary sets Suspects to true.");
        }

        private void CheckMetadata(PdfDictionary catalog)
        {
            string? language = Get(catalog, "Lang") is PdfString lang
                ? PdfUnicodeEncoding.DecodeTextString(lang.Bytes.Span, "The document language").Trim()
                : null;
            _hasDocumentLanguage = !string.IsNullOrEmpty(language) && PdfLanguageTag.IsValid(language);
            if (!_hasDocumentLanguage)
                Fail("11-001", string.IsNullOrEmpty(language)
                    ? "The catalog has no Lang entry, so the language of the page content cannot be determined."
                    : "The catalog Lang entry is not a valid language tag.",
                    objectNumber: _tree.CatalogReference.ObjectNumber,
                    objectGeneration: _tree.CatalogReference.Generation);
            if (Get(catalog, "Metadata") is not PdfStream metadata)
            {
                Fail("06-001", "The catalog has no XMP metadata stream.");
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
            string? part = XmpValue(xmp, PdfUa + "part");
            DeclaredPart = int.TryParse(part, out int declared) ? declared : null;
            if (part is null)
                Fail("06-002", "The XMP metadata has no PDF/UA identifier (pdfuaid:part).");
            XElement? title = xmp.Descendants(Dc + "title").FirstOrDefault();
            if (title is null || string.IsNullOrWhiteSpace(title.Value))
                Fail("06-003", "The XMP metadata has no dc:title.");
            else if (!_hasDocumentLanguage && !title.Descendants().Any(item =>
                         item.Attribute(XmlNs + "lang")?.Value is string lang
                         && lang != "x-default" && PdfLanguageTag.IsValid(lang)))
                Fail("11-006", "The language of the document metadata cannot be determined.");
        }

        private void CheckViewerPreferences(PdfDictionary catalog)
        {
            PdfObject? value = Get(catalog, "ViewerPreferences") is PdfDictionary preferences
                ? Get(preferences, "DisplayDocTitle") : null;
            if (value is null)
                Fail("07-001", "The catalog ViewerPreferences dictionary has no DisplayDocTitle entry.");
            else if (value is not PdfBoolean { Value: true })
                Fail("07-002", "The catalog ViewerPreferences dictionary sets DisplayDocTitle to false.");
        }

        private void CheckStructure(PdfDictionary catalog)
        {
            if (Get(catalog, "StructTreeRoot") is not PdfDictionary root) return;
            if (Get(root, "RoleMap") is PdfDictionary roleMap)
                _roleMap = roleMap.ToDictionary(item => item.Key.ValueAsLatin1(), item => item.Value);
            foreach ((string type, PdfObject _) in _roleMap)
            {
                if (StandardTypes.Contains(type))
                    Fail("02-004", $"The standard structure type {type} is remapped in the RoleMap.");
                else
                    MapToStandard(type);
            }
            var walk = new StructureWalk();
            if (root.TryGetValue(Name("K"), out PdfObject? kids))
                Visit(kids, null, 0, walk, new HashSet<(int, int)>());
            if (walk.UsesNumberedHeadings && walk.UsesPlainHeadings)
                Fail("14-007", "The document uses both H and numbered H1 to H6 headings.");
        }

        private sealed class StructureWalk
        {
            internal int? PreviousHeadingLevel;
            internal bool UsesNumberedHeadings;
            internal bool UsesPlainHeadings;
            internal readonly Dictionary<string, int> NoteIds = [];
        }

        private void Visit(PdfObject value, string? parentType, int depth, StructureWalk walk,
            HashSet<(int, int)> visited)
        {
            if (depth >= MaximumDepth)
                throw new InvalidOperationException("The structure tree exceeds the supported nesting depth.");
            PdfIndirectReference? reference = value as PdfIndirectReference;
            if (reference is not null && !visited.Add((reference.ObjectNumber, reference.Generation))) return;
            value = Resolve(value);
            if (value is PdfArray array)
            {
                foreach (PdfObject item in array) Visit(item, parentType, depth + 1, walk, visited);
                return;
            }
            if (value is not PdfDictionary element) return;
            if (IsName(element, "Type", "OBJR"))
            {
                if (element.TryGetValue(Name("Obj"), out PdfObject? target)
                    && target is PdfIndirectReference annotation && parentType is not null)
                    _annotationParents[(annotation.ObjectNumber, annotation.Generation)] = parentType;
                return;
            }
            if (Get(element, "S") is not PdfName structureName)
            {
                if (element.TryGetValue(Name("K"), out PdfObject? orphanKids))
                    Visit(orphanKids, parentType, depth + 1, walk, visited);
                return;
            }
            string type = MapToStandard(structureName.ValueAsLatin1()) ?? structureName.ValueAsLatin1();
            int? page = PageOf(element);
            int? objectNumber = reference?.ObjectNumber;
            int? generation = reference?.Generation;
            if (type == "Figure" && !HasText(element, "Alt") && !HasText(element, "ActualText"))
                Fail("13-004", "A Figure structure element has neither Alt nor ActualText.",
                    page, objectNumber, generation);
            if (type == "Formula" && !HasText(element, "Alt"))
                Fail("17-002", "A Formula structure element has no Alt text.", page, objectNumber, generation);
            if (type == "Note")
            {
                if (Get(element, "ID") is not PdfString { Bytes.Length: > 0 } id)
                    Fail("19-003", "A Note structure element has no ID entry.", page, objectNumber, generation);
                else
                {
                    string key = Convert.ToHexString(id.Bytes.Span);
                    walk.NoteIds[key] = walk.NoteIds.GetValueOrDefault(key) + 1;
                    if (walk.NoteIds[key] == 2)
                        Fail("19-004", "Two Note structure elements share one ID.", page, objectNumber, generation);
                }
            }
            if (type == "H") walk.UsesPlainHeadings = true;
            if (type.Length == 2 && type[0] == 'H' && type[1] is >= '1' and <= '6')
            {
                int level = type[1] - '0';
                if (!walk.UsesNumberedHeadings && level != 1)
                    Fail("14-002", $"The first numbered heading is H{level}, not H1.", page, objectNumber, generation);
                else if (walk.PreviousHeadingLevel is int previous && level > previous + 1)
                    Fail("14-003", $"An H{level} heading follows H{previous}, skipping a level.",
                        page, objectNumber, generation);
                walk.UsesNumberedHeadings = true;
                walk.PreviousHeadingLevel = level;
            }
            if (type == "Table") CheckTable(element, page, objectNumber, generation, depth);
            CheckNesting(type, parentType, element, page, objectNumber, generation);
            if (element.TryGetValue(Name("K"), out PdfObject? children))
            {
                if (ChildTypes(children).Count(child => child == "H") > 1)
                    Fail("14-006", "A structure element contains more than one H heading.",
                        page, objectNumber, generation);
                Visit(children, type, depth + 1, walk, visited);
            }
        }

        // ISO 32000-1 tables 336 and 337 fix where list, table, TOC, Ruby and Warichu elements may
        // sit. Each rule names the parents an element needs, or the only children a group may hold.
        private static readonly (string Condition, string Group, string Type, string[] Parents)[] ParentRules =
        [
            ("09-004", "table", "TR", ["Table", "THead", "TBody", "TFoot"]),
            ("09-004", "table", "TH", ["TR"]),
            ("09-004", "table", "TD", ["TR"]),
            ("09-004", "table", "THead", ["Table"]),
            ("09-004", "table", "TBody", ["Table"]),
            ("09-004", "table", "TFoot", ["Table"]),
            ("09-005", "list", "LI", ["L"]),
            ("09-005", "list", "LBody", ["LI"]),
            ("09-006", "table of contents", "TOCI", ["TOC"]),
            ("09-007", "Ruby", "RB", ["Ruby"]),
            ("09-007", "Ruby", "RT", ["Ruby"]),
            ("09-007", "Ruby", "RP", ["Ruby"]),
            ("09-008", "Warichu", "WT", ["Warichu"]),
            ("09-008", "Warichu", "WP", ["Warichu"])
        ];

        private static readonly (string Condition, string Type, string[] Children)[] ChildRules =
        [
            ("09-004", "Table", ["TR", "THead", "TBody", "TFoot", "Caption"]),
            ("09-004", "THead", ["TR"]),
            ("09-004", "TBody", ["TR"]),
            ("09-004", "TFoot", ["TR"]),
            ("09-004", "TR", ["TH", "TD"]),
            ("09-005", "L", ["L", "LI", "Caption"]),
            ("09-005", "LI", ["Lbl", "LBody"]),
            ("09-006", "TOC", ["TOC", "TOCI", "Caption"]),
            ("09-007", "Ruby", ["RB", "RT", "RP"]),
            ("09-008", "Warichu", ["WT", "WP"])
        ];

        private void CheckNesting(string type, string? parentType, PdfDictionary element,
            int? page, int? objectNumber, int? generation)
        {
            foreach ((string condition, string group, string child, string[] parents) in ParentRules)
                if (child == type && (parentType is null || !parents.Contains(parentType)))
                    Fail(condition, $"A {type} {group} element sits in {parentType ?? "the structure tree root"}, "
                        + $"not in {string.Join(" or ", parents)}.", page, objectNumber, generation);
            if (!element.TryGetValue(Name("K"), out PdfObject? kids)) return;
            foreach ((string condition, string parent, string[] allowed) in ChildRules)
            {
                if (parent != type) continue;
                foreach (string kid in ChildTypes(kids).Distinct())
                    if (!allowed.Contains(kid))
                        Fail(condition, $"A {type} element contains {kid}, which it may not hold.",
                            page, objectNumber, generation);
            }
        }

        private List<string> ChildTypes(PdfObject children)
        {
            var types = new List<string>();
            PdfObject resolved = Resolve(children);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
            foreach (PdfObject item in items)
                if (Resolve(item) is PdfDictionary child && Get(child, "S") is PdfName name)
                    types.Add(MapToStandard(name.ValueAsLatin1()) ?? name.ValueAsLatin1());
            return types;
        }

        private void CheckTable(PdfDictionary table, int? page, int? objectNumber, int? generation, int depth)
        {
            var headers = new List<(PdfDictionary Cell, PdfIndirectReference? Reference)>();
            bool usesHeadersAttribute = false;
            var visited = new HashSet<(int, int)>();
            if (table.TryGetValue(Name("K"), out PdfObject? kids)) Scan(kids, depth + 1);
            if (usesHeadersAttribute) return;
            foreach ((PdfDictionary cell, PdfIndirectReference? reference) in headers)
                if (TableAttribute(cell, "Scope") is null)
                    Fail("15-003", "A TH cell has no Scope attribute in a table without Headers attributes.",
                        PageOf(cell) ?? page, reference?.ObjectNumber ?? objectNumber,
                        reference?.Generation ?? generation);

            void Scan(PdfObject value, int scanDepth)
            {
                if (scanDepth >= MaximumDepth)
                    throw new InvalidOperationException("The structure tree exceeds the supported nesting depth.");
                PdfIndirectReference? reference = value as PdfIndirectReference;
                if (reference is not null && !visited.Add((reference.ObjectNumber, reference.Generation))) return;
                value = Resolve(value);
                if (value is PdfArray array)
                {
                    foreach (PdfObject item in array) Scan(item, scanDepth + 1);
                    return;
                }
                if (value is not PdfDictionary cell || Get(cell, "S") is not PdfName name) return;
                string type = MapToStandard(name.ValueAsLatin1()) ?? name.ValueAsLatin1();
                if (type == "Table") return;
                if (type is "TH" or "TD" && TableAttribute(cell, "Headers") is not null)
                    usesHeadersAttribute = true;
                if (type == "TH") headers.Add((cell, reference));
                if (cell.TryGetValue(Name("K"), out PdfObject? children)) Scan(children, scanDepth + 1);
            }
        }

        private PdfObject? TableAttribute(PdfDictionary element, string key)
        {
            if (!element.TryGetValue(Name("A"), out PdfObject? attributes)) return null;
            PdfObject resolved = Resolve(attributes);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
            foreach (PdfObject item in items)
                if (Resolve(item) is PdfDictionary dictionary && IsName(dictionary, "O", "Table")
                    && Get(dictionary, key) is PdfObject found)
                    return found;
            return null;
        }

        // Follows the RoleMap to a standard type, reporting a cycle (02-003) or a chain that
        // never reaches one (02-001). Returns null when no standard type is reached.
        private string? MapToStandard(string type)
        {
            if (StandardTypes.Contains(type)) return type;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string current = type;
            while (seen.Add(current))
            {
                if (!_roleMap.TryGetValue(current, out PdfObject? mapped)
                    || Resolve(mapped) is not PdfName next)
                {
                    Fail("02-001", $"The structure type {type} does not map to a standard type.");
                    return null;
                }
                current = next.ValueAsLatin1();
                if (StandardTypes.Contains(current)) return current;
            }
            Fail("02-003", $"The RoleMap entry for {type} is circular.");
            return null;
        }

        private void CheckOptionalContent(PdfDictionary catalog)
        {
            if (Get(catalog, "OCProperties") is not PdfDictionary properties) return;
            if (Get(properties, "D") is PdfDictionary defaults)
            {
                if (!HasText(defaults, "Name"))
                    Fail("20-001", "The default optional content configuration has no Name.");
                if (defaults.ContainsKey(Name("AS")))
                    Fail("20-003", "The default optional content configuration has an AS entry.");
            }
            if (Get(properties, "Configs") is PdfArray configs)
                foreach (PdfObject item in configs)
                    if (Resolve(item) is PdfDictionary config)
                    {
                        if (!HasText(config, "Name"))
                            Fail("20-002", "An optional content configuration in Configs has no Name.");
                        if (config.ContainsKey(Name("AS")))
                            Fail("20-003", "An optional content configuration in Configs has an AS entry.");
                    }
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
                            && (!specification.ContainsKey(Name("F")) || !specification.ContainsKey(Name("UF"))))
                            Fail("21-001", "An embedded file specification lacks the F or UF entry.",
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

        private void CheckXfa(PdfDictionary catalog)
        {
            if (Get(catalog, "AcroForm") is not PdfDictionary form
                || !form.TryGetValue(Name("XFA"), out PdfObject? xfaValue)) return;
            PdfObject xfa = Resolve(xfaValue);
            IEnumerable<PdfObject> packets = xfa is PdfArray array ? array : [xfa];
            foreach (PdfObject packet in packets)
            {
                if (Resolve(packet) is not PdfStream stream) continue;
                string text = Encoding.UTF8.GetString(
                    PdfStreamDecoder.Decode(stream, document.Resolve, MaximumDecodedBytes));
                int index = text.IndexOf("<dynamicRender", StringComparison.Ordinal);
                if (index < 0) continue;
                int end = text.IndexOf("</dynamicRender>", index, StringComparison.Ordinal);
                if (end > index && text[index..end].Contains("required", StringComparison.Ordinal))
                {
                    Fail("25-001", "The XFA configuration sets dynamicRender to required.");
                    return;
                }
            }
        }

        private void CheckSecurity()
        {
            if (!document.Trailer.TryGetValue(Name("Encrypt"), out PdfObject? encryptValue)) return;
            if (Resolve(encryptValue) is not PdfDictionary encrypt) return;
            if (Get(encrypt, "P") is not PdfInteger permissions)
                Fail("26-001", "The document is encrypted but its encryption dictionary has no P entry.");
            else if ((permissions.Value & (1 << 9)) == 0)
                Fail("26-002", "The document is encrypted and its permissions withhold access for assistive technology (bit 10 of P).");
        }

        private void CheckPageContent(PdfPageTreeEntry page)
        {
            var reader = new PdfPageContentReader(document);
            IReadOnlyList<PdfContentInstruction> instructions =
                reader.ReadInstructions(page.Index, cancellationToken);
            PdfDictionary? resources = page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? value)
                ? Resolve(value) as PdfDictionary : null;
            var stack = new List<MarkKind>();
            _formDraws = 0;
            WalkContent(instructions, resources, page.Index, stack, new ContentState(), 0,
                new HashSet<(int, int)>());
        }

        private enum MarkKind { Tagged, Artifact, Other }

        private sealed class ContentState
        {
            internal PdfDictionary? Font;
            internal int RenderMode;
            internal readonly Stack<(PdfDictionary? Font, int RenderMode)> Saved = [];
        }

        private void WalkContent(IReadOnlyList<PdfContentInstruction> instructions, PdfDictionary? resources,
            int pageIndex, List<MarkKind> stack, ContentState state, int depth, HashSet<(int, int)> active)
        {
            if (depth >= 32) return;
            int baseDepth = stack.Count;
            bool foundMcid = false;
            foreach (PdfContentInstruction instruction in instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (instruction.Operator)
                {
                    case "BMC":
                    case "BDC":
                        MarkKind kind = MarkOf(instruction, resources);
                        if (kind == MarkKind.Artifact && stack.Contains(MarkKind.Tagged))
                            Fail("01-003", "Content marked as Artifact sits inside tagged content.", pageIndex);
                        if (kind == MarkKind.Tagged)
                        {
                            foundMcid = true;
                            if (stack.Contains(MarkKind.Artifact))
                                Fail("01-004", "Tagged content sits inside content marked as Artifact.", pageIndex);
                        }
                        stack.Add(kind);
                        break;
                    case "EMC":
                        if (stack.Count > baseDepth) stack.RemoveAt(stack.Count - 1);
                        break;
                    case "q":
                        state.Saved.Push((state.Font, state.RenderMode));
                        break;
                    case "Q":
                        if (state.Saved.Count > 0) (state.Font, state.RenderMode) = state.Saved.Pop();
                        break;
                    case "Tf":
                        state.Font = instruction.Operands.Count >= 1 && instruction.Operands[0] is PdfName fontName
                            ? Resolve(Get(Get(resources, "Font") as PdfDictionary, fontName) ?? PdfNull.Instance)
                                as PdfDictionary
                            : null;
                        break;
                    case "Tr":
                        if (instruction.Operands.Count >= 1 && instruction.Operands[0] is PdfInteger mode)
                            state.RenderMode = (int)mode.Value;
                        break;
                    case "Tj" or "TJ" or "'" or "\"":
                        RequireMarked(stack, pageIndex);
                        if (state.RenderMode != 3 && state.Font is not null) CheckFontEmbedded(state.Font, pageIndex);
                        break;
                    case "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "sh" or "BI":
                        RequireMarked(stack, pageIndex);
                        break;
                    case "Do":
                        if (instruction.Operands.Count >= 1 && instruction.Operands[0] is PdfName xobjectName)
                            DrawXObject(xobjectName, resources, pageIndex, stack, state, depth, active);
                        break;
                }
            }
            if (stack.Count > baseDepth) stack.RemoveRange(baseDepth, stack.Count - baseDepth);
            if (foundMcid && depth > 0 && active.Count > 0) _formsWithMcids.Add(active.Last());
        }

        private void DrawXObject(PdfName name, PdfDictionary? resources, int pageIndex, List<MarkKind> stack,
            ContentState state, int depth, HashSet<(int, int)> active)
        {
            PdfObject? entry = Get(Get(resources, "XObject") as PdfDictionary, name);
            if (entry is null || Resolve(entry) is not PdfStream xobject) return;
            if (IsName(xobject.Dictionary, "Subtype", "Image"))
            {
                RequireMarked(stack, pageIndex);
                return;
            }
            if (!IsName(xobject.Dictionary, "Subtype", "Form")) return;
            // The active set stops only true cycles, so a form that draws its child twice at every
            // level doubles the walk per level. A page budget bounds that.
            if (++_formDraws > MaximumFormDrawsPerPage)
                throw new InvalidOperationException(
                    $"The page draws form XObjects more than {MaximumFormDrawsPerPage} times.");
            (int, int)? key = entry is PdfIndirectReference reference
                ? (reference.ObjectNumber, reference.Generation) : null;
            if (xobject.Dictionary.ContainsKey(Name("Ref")))
                Fail("30-001", "A reference XObject is present.", pageIndex,
                    key?.Item1, key?.Item2);
            if (key is (int, int) formKey)
            {
                _formUses[formKey] = _formUses.GetValueOrDefault(formKey) + 1;
                if (!active.Add(formKey)) return;
            }
            try
            {
                IReadOnlyList<PdfContentInstruction> instructions = PdfContentStreamReader.Read(
                    PdfStreamDecoder.Decode(xobject, document.Resolve, MaximumDecodedBytes),
                    compatibilityRecovery: true, cancellationToken: cancellationToken);
                PdfDictionary? formResources = Get(xobject.Dictionary, "Resources") as PdfDictionary ?? resources;
                WalkContent(instructions, formResources, pageIndex, stack, state, depth + 1, active);
            }
            finally
            {
                if (key is (int, int) done) active.Remove(done);
            }
        }

        private MarkKind MarkOf(PdfContentInstruction instruction, PdfDictionary? resources)
        {
            if (instruction.Operands.Count >= 1 && instruction.Operands[0] is PdfName tag && tag.ValueAsLatin1() == "Artifact")
                return MarkKind.Artifact;
            if (instruction.Operator != "BDC" || instruction.Operands.Count < 2) return MarkKind.Other;
            PdfObject properties = instruction.Operands[1];
            if (properties is PdfName propertyName)
                properties = Get(Get(resources, "Properties") as PdfDictionary, propertyName) ?? PdfNull.Instance;
            return Resolve(properties) is PdfDictionary dictionary && Get(dictionary, "MCID") is PdfInteger
                ? MarkKind.Tagged : MarkKind.Other;
        }

        private void RequireMarked(List<MarkKind> stack, int pageIndex)
        {
            if (!stack.Contains(MarkKind.Tagged) && !stack.Contains(MarkKind.Artifact))
                Fail("01-005", "Page content is neither tagged nor marked as Artifact.", pageIndex);
        }

        private void CheckFontEmbedded(PdfDictionary font, int pageIndex)
        {
            PdfDictionary program = font;
            if (IsName(font, "Subtype", "Type3")) return;
            if (IsName(font, "Subtype", "Type0") && Get(font, "DescendantFonts") is PdfArray descendants
                && descendants.Count > 0 && Resolve(descendants[0]) is PdfDictionary descendant)
                program = descendant;
            if (Get(program, "FontDescriptor") is PdfDictionary descriptor
                && (descriptor.ContainsKey(Name("FontFile")) || descriptor.ContainsKey(Name("FontFile2"))
                    || descriptor.ContainsKey(Name("FontFile3"))))
                return;
            string fontName = Get(font, "BaseFont") is PdfName baseFont ? baseFont.ValueAsLatin1() : "unnamed";
            if (_fontsChecked.Add((fontName, pageIndex)))
                Fail("31-009", $"The font {fontName} is used for visible text but is not embedded.", pageIndex);
        }

        private void CheckAnnotations(PdfPageTreeEntry page)
        {
            if (Get(page.Dictionary, "Annots") is not PdfArray annotations) return;
            bool hasAnnotation = false;
            foreach (PdfObject item in annotations)
            {
                if (Resolve(item) is not PdfDictionary annotation) continue;
                string subtype = Get(annotation, "Subtype") is PdfName name ? name.ValueAsLatin1() : "";
                if (subtype == "Popup") continue;
                hasAnnotation = true;
                if (Get(annotation, "F") is PdfInteger flags && (flags.Value & 2) != 0) continue;
                PdfIndirectReference? reference = item as PdfIndirectReference;
                int? number = reference?.ObjectNumber;
                int? generation = reference?.Generation;
                string? parent = reference is null ? null
                    : _annotationParents.GetValueOrDefault((reference.ObjectNumber, reference.Generation));
                bool hasContents = HasText(annotation, "Contents");
                switch (subtype)
                {
                    case "TrapNet":
                        Fail("28-007", "A TrapNet annotation is present.", page.Index, number, generation);
                        break;
                    case "PrinterMark":
                        break;
                    case "Widget":
                        if (parent != "Form")
                            Fail("28-010", "A widget annotation is not nested within a Form structure element.",
                                page.Index, number, generation);
                        if (!HasText(annotation, "TU") && !HasText(FieldOf(annotation), "TU"))
                            Fail("28-005", "A form field has no TU description.", page.Index, number, generation);
                        break;
                    case "Link":
                        if (parent != "Link")
                            Fail("28-011", "A link annotation is not nested within a Link structure element.",
                                page.Index, number, generation);
                        if (!hasContents)
                            Fail("28-012", "A link annotation has no Contents description.",
                                page.Index, number, generation);
                        break;
                    default:
                        if (parent != "Annot")
                            Fail("28-002", $"A {subtype} annotation is not a direct child of an Annot structure element.",
                                page.Index, number, generation);
                        if (!hasContents)
                            Fail("28-004", $"A {subtype} annotation has no Contents description.",
                                page.Index, number, generation);
                        break;
                }
            }
            if (!hasAnnotation) return;
            PdfObject? tabs = Get(page.Dictionary, "Tabs");
            if (tabs is null)
                Fail("28-008", "A page with annotations has no Tabs entry.", page.Index,
                    page.Reference.ObjectNumber, page.Reference.Generation);
            else if (tabs is not PdfName { } order || order.ValueAsLatin1() != "S")
                Fail("28-009", "A page with annotations has a Tabs entry other than S.", page.Index,
                    page.Reference.ObjectNumber, page.Reference.Generation);
        }

        private PdfDictionary? FieldOf(PdfDictionary widget)
        {
            PdfDictionary? current = widget;
            for (int depth = 0; current is not null && depth < 32; depth++)
            {
                if (current.ContainsKey(Name("TU"))) return current;
                current = Get(current, "Parent") as PdfDictionary;
            }
            return null;
        }

        private void Guard(string condition, Action check, int? pageIndex = null)
        {
            try
            {
                check();
            }
            catch (Exception error) when (error is ArgumentException or FormatException
                or InvalidOperationException or NotSupportedException or OverflowException
                or XmlException or InvalidDataException)
            {
                Fail(condition, $"The check could not read the document: {error.Message}", pageIndex);
            }
        }

        private void Fail(string condition, string message, int? pageIndex = null,
            int? objectNumber = null, int? objectGeneration = null)
        {
            if (!_reported.Add((condition + "|" + message, pageIndex, objectNumber))) return;
            _failures.Add(new PdfUaFailure(condition, PdfUaMatterhorn.Clause(condition), message)
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

        private bool HasText(PdfDictionary? dictionary, string key) =>
            dictionary is not null && Get(dictionary, key) is PdfString text
            && !string.IsNullOrWhiteSpace(PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"The /{key} value"));

        private PdfObject? Get(PdfDictionary? dictionary, string key) =>
            dictionary is not null && dictionary.TryGetValue(Name(key), out PdfObject? value) ? Resolve(value) : null;

        private PdfObject? Get(PdfDictionary? dictionary, PdfName key) =>
            dictionary is not null && dictionary.TryGetValue(key, out PdfObject? value) ? Resolve(value) : null;

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

        private static string? XmpValue(XDocument xmp, XName name) =>
            xmp.Descendants(name).Select(element => element.Value.Trim()).FirstOrDefault(text => text.Length > 0)
            ?? xmp.Descendants().Attributes(name).Select(attribute => attribute.Value.Trim())
                .FirstOrDefault(text => text.Length > 0);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
