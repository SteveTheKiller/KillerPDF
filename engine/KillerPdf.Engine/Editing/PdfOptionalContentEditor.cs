using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits optional-content groups while preserving their existing configuration.</summary>
public static class PdfOptionalContentEditor
{
    private static readonly PdfName NameKey = new("Name"u8);
    private static readonly PdfName UsageKey = new("Usage"u8);
    private static readonly PdfName OptionalContentPropertiesKey = new("OCProperties"u8);
    private static readonly PdfName DefaultConfigurationKey = new("D"u8);
    private static readonly PdfName BaseStateKey = new("BaseState"u8);
    private static readonly PdfName OnKey = new("ON"u8);
    private static readonly PdfName OffKey = new("OFF"u8);
    private static readonly PdfName LockedKey = new("Locked"u8);
    private static readonly PdfName OrderKey = new("Order"u8);
    private static readonly PdfName CreatorKey = new("Creator"u8);
    private static readonly PdfName GroupsKey = new("OCGs"u8);
    private static readonly PdfName TypeKey = new("Type"u8);
    private static readonly PdfName AnnotationsKey = new("Annots"u8);
    private static readonly PdfName OptionalContentKey = new("OC"u8);
    private static readonly PdfName ContentsKey = new("Contents"u8);
    private static readonly PdfName ResourcesKey = new("Resources"u8);
    private static readonly PdfName PropertiesKey = new("Properties"u8);

    /// <summary>Assigns a page annotation to a registered layer, or clears its layer.</summary>
    public static byte[] SetAnnotationGroup(
        PdfDocument document, int annotationObjectNumber, int? groupObjectNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (annotationObjectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(annotationObjectNumber));
        PdfOptionalContentGroupInfo? group = groupObjectNumber.HasValue
            ? FindGroup(PdfOptionalContentReader.Read(document), groupObjectNumber.Value)
            : null;
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfIndirectReference? annotationReference = tree.Pages
            .Select(page => FindAnnotation(
                page.Dictionary.GetValueOrDefault(AnnotationsKey), annotationObjectNumber))
            .FirstOrDefault(reference => reference is not null);
        if (annotationReference is null)
            throw new KeyNotFoundException(
                $"Annotation {annotationObjectNumber} was not found on a page.");
        PdfDictionary annotation = document.Resolve(annotationReference) as PdfDictionary
            ?? throw new InvalidOperationException("The annotation is not a dictionary.");
        var entries = annotation.ToDictionary(entry => entry.Key, entry => entry.Value);
        if (group is null)
            entries.Remove(OptionalContentKey);
        else
            entries[OptionalContentKey] = new PdfIndirectReference(
                group.ObjectNumber, group.Generation);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(annotationObjectNumber, new PdfDictionary(entries)).Build();

        PdfIndirectReference? FindAnnotation(PdfObject? value, int objectNumber)
        {
            if (value is null) return null;
            PdfObject resolved = value is PdfIndirectReference arrayReference
                ? document.Resolve(arrayReference) : value;
            if (resolved is not PdfArray array)
                throw new InvalidOperationException("A page annotation list is not an array.");
            foreach (PdfObject item in array)
            {
                PdfObject current = item;
                var visited = new HashSet<(int, int)>();
                for (int depth = 0; current is PdfIndirectReference reference; depth++)
                {
                    if (reference.ObjectNumber == objectNumber) return reference;
                    if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                        throw new InvalidOperationException(
                            "A page annotation has an invalid reference chain.");
                    current = document.Resolve(reference);
                }
            }
            return null;
        }
    }

    /// <summary>Assigns all content on one page to a registered layer.</summary>
    public static byte[] SetPageContentGroup(
        PdfDocument document, int pageIndex, int groupObjectNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (groupObjectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(groupObjectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), groupObjectNumber);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = tree.Pages[pageIndex];
        PdfDictionary resources = page.InheritedValues.TryGetValue(
                ResourcesKey, out PdfObject? resourcesValue)
            ? ResolveDictionaryWithReference(document, resourcesValue,
                "The page resources").Dictionary
            : new PdfDictionary([]);
        var resourceEntries = resources.ToDictionary(item => item.Key, item => item.Value);
        Dictionary<PdfName, PdfObject> properties = resourceEntries.TryGetValue(
                PropertiesKey, out PdfObject? propertiesValue)
            ? ResolveDictionaryWithReference(document, propertiesValue,
                "The page optional-content resources").Dictionary
                .ToDictionary(item => item.Key, item => item.Value)
            : [];
        int suffix = 1;
        PdfName resourceName;
        do resourceName = new PdfName(Encoding.ASCII.GetBytes($"KPL{suffix++}"));
        while (properties.ContainsKey(resourceName));
        properties[resourceName] = new PdfIndirectReference(
            group.ObjectNumber, group.Generation);
        resourceEntries[PropertiesKey] = new PdfDictionary(properties);

        byte[] content = page.Dictionary.TryGetValue(ContentsKey, out PdfObject? contents)
            ? ReadContent(contents) : [];
        using var wrapped = new MemoryStream();
        wrapped.Write("/OC /"u8);
        wrapped.Write(resourceName.Bytes.Span);
        wrapped.Write(" BDC\n"u8);
        wrapped.Write(content);
        if (content.Length > 0 && content[^1] is not ((byte)'\n') and not ((byte)'\r'))
            wrapped.WriteByte((byte)'\n');
        wrapped.Write("EMC\n"u8);

        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference contentReference = update.AddObject(
            new PdfStream(new PdfDictionary([]), wrapped.ToArray()));
        var pageEntries = page.Dictionary.ToDictionary(item => item.Key, item => item.Value);
        pageEntries[ResourcesKey] = new PdfDictionary(resourceEntries);
        pageEntries[ContentsKey] = contentReference;
        return update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(pageEntries)).Build();

        byte[] ReadContent(PdfObject value)
        {
            PdfObject resolved = value is PdfIndirectReference reference
                ? document.Resolve(reference) : value;
            IEnumerable<PdfObject> values = resolved is PdfArray array ? array : [resolved];
            using var output = new MemoryStream();
            foreach (PdfObject item in values)
            {
                PdfObject current = item is PdfIndirectReference itemReference
                    ? document.Resolve(itemReference) : item;
                PdfStream stream = current as PdfStream
                    ?? throw new InvalidOperationException(
                        "A page content item is not a stream.");
                output.Write(PdfStreamDecoder.Decode(
                    stream, document.Resolve, 64 * 1024 * 1024));
                output.WriteByte((byte)'\n');
            }
            return output.ToArray();
        }
    }

    /// <summary>
    /// Assigns a complete top-level page instruction range to a registered layer.
    /// The range cannot split an existing marked-content sequence.
    /// </summary>
    public static byte[] SetPageInstructionRangeGroup(
        PdfDocument document, int pageIndex, int instructionIndex,
        int instructionCount, int groupObjectNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (groupObjectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(groupObjectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), groupObjectNumber);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        IReadOnlyList<PdfContentInstruction> instructions =
            new PdfPageContentReader(document).ReadInstructions(pageIndex);
        if (instructionIndex < 0 || instructionIndex > instructions.Count)
            throw new ArgumentOutOfRangeException(nameof(instructionIndex));
        if (instructionCount <= 0
            || instructionCount > instructions.Count - instructionIndex)
            throw new ArgumentOutOfRangeException(nameof(instructionCount));
        int selectionEnd = instructionIndex + instructionCount;
        int depth = 0;
        for (int index = 0; index <= instructions.Count; index++)
        {
            if ((index == instructionIndex || index == selectionEnd) && depth != 0)
                throw new ArgumentException(
                    "A layer assignment range cannot split a marked-content sequence.",
                    nameof(instructionIndex));
            if (index == instructions.Count) break;
            depth += instructions[index].Operator switch
            {
                "BMC" or "BDC" => 1,
                "EMC" => -1,
                _ => 0
            };
            if (depth < 0)
                throw new InvalidOperationException(
                    "The page has an unmatched marked-content terminator.");
        }
        if (depth != 0)
            throw new InvalidOperationException(
                "The page has an unterminated marked-content sequence.");

        PdfPageTreeEntry page = tree.Pages[pageIndex];
        PdfDictionary resources = page.InheritedValues.TryGetValue(
                ResourcesKey, out PdfObject? resourcesValue)
            ? ResolveDictionaryWithReference(document, resourcesValue,
                "The page resources").Dictionary
            : new PdfDictionary([]);
        var resourceEntries = resources.ToDictionary(item => item.Key, item => item.Value);
        Dictionary<PdfName, PdfObject> properties = resourceEntries.TryGetValue(
                PropertiesKey, out PdfObject? propertiesValue)
            ? ResolveDictionaryWithReference(document, propertiesValue,
                "The page optional-content resources").Dictionary
                .ToDictionary(item => item.Key, item => item.Value)
            : [];
        int suffix = 1;
        PdfName resourceName;
        do resourceName = new PdfName(Encoding.ASCII.GetBytes($"KPL{suffix++}"));
        while (properties.ContainsKey(resourceName));
        properties[resourceName] = new PdfIndirectReference(
            group.ObjectNumber, group.Generation);
        resourceEntries[PropertiesKey] = new PdfDictionary(properties);

        var rewritten = new List<PdfContentInstruction>(instructions.Count + 2);
        rewritten.AddRange(instructions.Take(instructionIndex));
        rewritten.Add(new PdfContentInstruction("BDC", 0,
            [new PdfName("OC"u8), resourceName]));
        rewritten.AddRange(instructions.Skip(instructionIndex).Take(instructionCount));
        rewritten.Add(new PdfContentInstruction("EMC", 0, []));
        rewritten.AddRange(instructions.Skip(selectionEnd));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference contentReference = update.AddObject(new PdfStream(
            new PdfDictionary([]), PdfContentStreamWriter.Write(rewritten)));
        var pageEntries = page.Dictionary.ToDictionary(item => item.Key, item => item.Value);
        pageEntries[ResourcesKey] = new PdfDictionary(resourceEntries);
        pageEntries[ContentsKey] = contentReference;
        return update.ReplaceObject(page.Reference.ObjectNumber,
            new PdfDictionary(pageEntries)).Build();
    }

    /// <summary>
    /// Flattens page-level optional content using the default state or an explicit visible set.
    /// Optional-content membership dictionaries, nested form content, annotations, and tagged
    /// documents are rejected until their semantics can be preserved completely.
    /// </summary>
    public static byte[] FlattenPageContent(
        PdfDocument document, IReadOnlyCollection<int>? visibleGroupObjectNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        if (info.Groups.Count == 0)
            throw new InvalidOperationException("The document has no optional content to flatten.");
        PdfPageTree tree = PdfPageTree.Read(document);
        if (tree.Catalog.ContainsKey(new PdfName("StructTreeRoot"u8)))
            throw new NotSupportedException(
                "Flattening tagged PDF content is not supported because hidden structure elements must be repaired with their content.");
        var registered = info.Groups.Select(group => group.ObjectNumber).ToHashSet();
        HashSet<int> visible = visibleGroupObjectNumbers is null
            ? info.Groups.Where(group => group.IsInitiallyVisible)
                .Select(group => group.ObjectNumber).ToHashSet()
            : visibleGroupObjectNumbers.ToHashSet();
        if (!visible.IsSubsetOf(registered))
            throw new ArgumentOutOfRangeException(nameof(visibleGroupObjectNumbers),
                "The visible set contains an unregistered optional-content group.");

        var editor = new PdfIncrementalPageEditor(document);
        bool changed = false;
        var reader = new PdfPageContentReader(document);
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            IReadOnlyList<PdfContentInstruction> source = reader.ReadInstructions(page.Index);
            var output = new List<PdfContentInstruction>(source.Count);
            var stack = new Stack<(bool ParentVisible, bool Optional)>();
            bool currentVisible = true;
            bool pageChanged = false;
            foreach (PdfContentInstruction instruction in source)
            {
                if (instruction.Operator == "DP" && IsOptionalContent(instruction))
                {
                    ResolveGroup(page, instruction.Operands[1]);
                    pageChanged = true;
                    continue;
                }
                if (instruction.Operator == "BDC" && IsOptionalContent(instruction))
                {
                    int groupObjectNumber = ResolveGroup(page, instruction.Operands[1]);
                    stack.Push((currentVisible, true));
                    currentVisible &= visible.Contains(groupObjectNumber);
                    pageChanged = true;
                    continue;
                }
                if (instruction.Operator is "BMC" or "BDC")
                {
                    if (currentVisible) output.Add(instruction);
                    stack.Push((currentVisible, false));
                    continue;
                }
                if (instruction.Operator == "EMC")
                {
                    if (stack.Count == 0)
                        throw new InvalidOperationException(
                            $"Page {page.Index + 1} has an unmatched marked-content terminator.");
                    (bool parentVisible, bool optional) = stack.Pop();
                    if (!optional && parentVisible) output.Add(instruction);
                    currentVisible = parentVisible;
                    continue;
                }
                if (currentVisible) output.Add(instruction);
            }
            if (stack.Count != 0)
                throw new InvalidOperationException(
                    $"Page {page.Index + 1} has an unterminated marked-content sequence.");
            if (!pageChanged) continue;
            editor.SetPageContentAndPruneResources(page.Index, output);
            changed = true;
        }

        PdfDocument flattened = changed ? PdfDocument.Open(editor.Build()) : document;
        flattened = FlattenAnnotations(flattened);
        foreach (PdfOptionalContentGroupInfo group in info.Groups)
            flattened = PdfDocument.Open(
                RemoveUnusedGroup(flattened, group.ObjectNumber));
        return flattened.Source.ToArray();

        static bool IsOptionalContent(PdfContentInstruction instruction) =>
            instruction.Operands.Count == 2
            && instruction.Operands[0] is PdfName tag
            && tag.ValueAsLatin1() == "OC";

        int ResolveGroup(PdfPageTreeEntry page, PdfObject operand)
        {
            PdfObject value = operand;
            if (value is PdfName propertyName)
            {
                if (!page.InheritedValues.TryGetValue(ResourcesKey,
                        out PdfObject? resourcesValue))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} has no resources for optional content.");
                PdfDictionary resources = ResolveObject(resourcesValue) as PdfDictionary
                    ?? throw new InvalidOperationException("Page resources are not a dictionary.");
                if (!resources.TryGetValue(PropertiesKey, out PdfObject? propertiesValue)
                    || ResolveObject(propertiesValue) is not PdfDictionary properties
                    || !properties.TryGetValue(propertyName, out value))
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} has no /{propertyName.ValueAsLatin1()} property resource.");
            }
            if (value is not PdfIndirectReference reference)
                throw new NotSupportedException(
                    "Direct optional-content properties cannot be flattened safely.");
            PdfDictionary dictionary = ResolveObject(reference) as PdfDictionary
                ?? throw new InvalidOperationException(
                    "An optional-content property is not a dictionary.");
            string type = dictionary.TryGetValue(TypeKey, out PdfObject? typeValue)
                && ResolveObject(typeValue) is PdfName typeName
                    ? typeName.ValueAsLatin1() : string.Empty;
            if (type == "OCMD")
                throw new NotSupportedException(
                    "Optional-content membership expressions cannot be flattened safely.");
            if (type != "OCG" || !registered.Contains(reference.ObjectNumber))
                throw new InvalidOperationException(
                    "An optional-content property does not reference a registered group.");
            return reference.ObjectNumber;
        }

        PdfObject ResolveObject(PdfObject value)
        {
            var visited = new HashSet<(int, int)>();
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                    throw new InvalidOperationException(
                        "An optional-content value has an invalid reference chain.");
                value = document.Resolve(reference);
            }
            return value;
        }

        PdfDocument FlattenAnnotations(PdfDocument input)
        {
            PdfPageTree inputTree = PdfPageTree.Read(input);
            var update = new PdfIncrementalUpdateBuilder(input);
            bool annotationChanges = false;
            foreach (PdfPageTreeEntry page in inputTree.Pages)
            {
                if (!page.Dictionary.TryGetValue(AnnotationsKey,
                        out PdfObject? annotationsValue))
                    continue;
                PdfObject resolved = ResolveInput(annotationsValue);
                PdfArray annotations = resolved as PdfArray
                    ?? throw new InvalidOperationException(
                        "A page annotation list is not an array.");
                var retained = new List<PdfObject>(annotations.Count);
                bool pageChanged = false;
                foreach (PdfObject item in annotations)
                {
                    PdfIndirectReference? reference = item as PdfIndirectReference;
                    PdfDictionary annotation = ResolveInput(item) as PdfDictionary
                        ?? throw new InvalidOperationException(
                            "A page annotation is not a dictionary.");
                    if (!annotation.TryGetValue(OptionalContentKey,
                            out PdfObject? optionalContent))
                    {
                        retained.Add(item);
                        continue;
                    }
                    if (optionalContent is not PdfIndirectReference groupReference)
                        throw new NotSupportedException(
                            "Direct annotation optional-content properties cannot be flattened safely.");
                    PdfDictionary groupDictionary = ResolveInput(groupReference) as PdfDictionary
                        ?? throw new InvalidOperationException(
                            "An annotation optional-content property is not a dictionary.");
                    string type = groupDictionary.TryGetValue(TypeKey, out PdfObject? typeValue)
                        && ResolveInput(typeValue) is PdfName typeName
                            ? typeName.ValueAsLatin1() : string.Empty;
                    if (type == "OCMD")
                        throw new NotSupportedException(
                            "Annotation optional-content membership expressions cannot be flattened safely.");
                    if (type != "OCG" || !registered.Contains(groupReference.ObjectNumber))
                        throw new InvalidOperationException(
                            "An annotation does not reference a registered optional-content group.");
                    annotationChanges = true;
                    if (!visible.Contains(groupReference.ObjectNumber))
                    {
                        pageChanged = true;
                        continue;
                    }
                    var entries = annotation.ToDictionary(
                        entry => entry.Key, entry => entry.Value);
                    entries.Remove(OptionalContentKey);
                    var replacement = new PdfDictionary(entries);
                    if (reference is not null)
                    {
                        update.ReplaceObject(reference.ObjectNumber, replacement);
                        retained.Add(reference);
                    }
                    else
                    {
                        retained.Add(replacement);
                        pageChanged = true;
                    }
                }
                if (!pageChanged) continue;
                var pageEntries = page.Dictionary.ToDictionary(
                    entry => entry.Key, entry => entry.Value);
                if (retained.Count == 0) pageEntries.Remove(AnnotationsKey);
                else pageEntries[AnnotationsKey] = new PdfArray(retained);
                update.ReplaceObject(page.Reference.ObjectNumber,
                    new PdfDictionary(pageEntries));
            }
            return annotationChanges ? PdfDocument.Open(update.Build()) : input;

            PdfObject ResolveInput(PdfObject value)
            {
                var visited = new HashSet<(int, int)>();
                for (int depth = 0; value is PdfIndirectReference reference; depth++)
                {
                    if (depth >= 32 || !visited.Add(
                            (reference.ObjectNumber, reference.Generation)))
                        throw new InvalidOperationException(
                            "An annotation has an invalid reference chain.");
                    value = input.Resolve(reference);
                }
                return value;
            }
        }
    }

    /// <summary>
    /// Reassigns supported page property resources and annotations from one layer to another,
    /// then unregisters the source layer. Unsupported active references stop the operation.
    /// </summary>
    public static byte[] MergeGroups(
        PdfDocument document, int sourceObjectNumber, int targetObjectNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (sourceObjectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceObjectNumber));
        if (targetObjectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetObjectNumber));
        if (sourceObjectNumber == targetObjectNumber)
            throw new ArgumentException("A layer cannot be merged into itself.",
                nameof(targetObjectNumber));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        PdfOptionalContentGroupInfo source = FindGroup(info, sourceObjectNumber);
        PdfOptionalContentGroupInfo target = FindGroup(info, targetObjectNumber);
        var sourceReference = new PdfIndirectReference(
            source.ObjectNumber, source.Generation);
        var targetReference = new PdfIndirectReference(
            target.ObjectNumber, target.Generation);
        PdfPageTree tree = PdfPageTree.Read(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var replacedAnnotations = new HashSet<int>();
        bool changed = false;

        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            if (page.InheritedValues.TryGetValue(ResourcesKey,
                    out PdfObject? resourcesValue))
            {
                PdfDictionary resources = ResolveDictionaryWithReference(
                    document, resourcesValue, "The page resources").Dictionary;
                var resourceEntries = resources.ToDictionary(
                    item => item.Key, item => item.Value);
                if (resourceEntries.TryGetValue(PropertiesKey,
                        out PdfObject? propertiesValue))
                {
                    PdfDictionary properties = ResolveDictionaryWithReference(
                        document, propertiesValue,
                        "The page optional-content resources").Dictionary;
                    var propertyEntries = properties.ToDictionary(
                        item => item.Key, item => item.Value);
                    bool resourceChanged = false;
                    foreach (PdfName name in propertyEntries.Keys.ToArray())
                    {
                        if (propertyEntries[name] is PdfIndirectReference reference
                            && SameReference(reference, sourceReference))
                        {
                            propertyEntries[name] = targetReference;
                            resourceChanged = true;
                        }
                    }
                    if (resourceChanged)
                    {
                        resourceEntries[PropertiesKey] = new PdfDictionary(propertyEntries);
                        var pageEntries = page.Dictionary.ToDictionary(
                            item => item.Key, item => item.Value);
                        pageEntries[ResourcesKey] = new PdfDictionary(resourceEntries);
                        update.ReplaceObject(page.Reference.ObjectNumber,
                            new PdfDictionary(pageEntries));
                        changed = true;
                    }
                }
            }

            if (!page.Dictionary.TryGetValue(AnnotationsKey,
                    out PdfObject? annotationsValue))
                continue;
            PdfObject resolvedAnnotations = annotationsValue is PdfIndirectReference arrayReference
                ? document.Resolve(arrayReference) : annotationsValue;
            PdfArray annotations = resolvedAnnotations as PdfArray
                ?? throw new InvalidOperationException(
                    "A page annotation list is not an array.");
            foreach (PdfObject annotationValue in annotations)
            {
                if (annotationValue is not PdfIndirectReference annotationReference)
                    throw new NotSupportedException(
                        "Direct page annotations cannot be merged between layers safely.");
                if (!replacedAnnotations.Add(annotationReference.ObjectNumber)) continue;
                PdfDictionary annotation = document.Resolve(annotationReference) as PdfDictionary
                    ?? throw new InvalidOperationException(
                        "A page annotation is not a dictionary.");
                if (!annotation.TryGetValue(OptionalContentKey, out PdfObject? layerValue)
                    || layerValue is not PdfIndirectReference layerReference
                    || !SameReference(layerReference, sourceReference))
                    continue;
                var annotationEntries = annotation.ToDictionary(
                    item => item.Key, item => item.Value);
                annotationEntries[OptionalContentKey] = targetReference;
                update.ReplaceObject(annotationReference.ObjectNumber,
                    new PdfDictionary(annotationEntries));
                changed = true;
            }
        }

        PdfDocument reassigned = changed ? PdfDocument.Open(update.Build()) : document;
        return RemoveUnusedGroup(reassigned, sourceObjectNumber);

        static bool SameReference(
            PdfIndirectReference left, PdfIndirectReference right) =>
            left.ObjectNumber == right.ObjectNumber
            && left.Generation == right.Generation;
    }

    /// <summary>Creates and registers a layer in the default configuration.</summary>
    public static byte[] AddGroup(PdfDocument document, string name,
        bool initiallyVisible = true, bool locked = false,
        bool? printVisible = null, bool? exportVisible = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A layer name is required.", nameof(name));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        if (info.Groups.Any(group => string.Equals(group.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Layer names must be unique.", nameof(name));

        PdfPageTree tree = PdfPageTree.Read(document);
        var update = new PdfIncrementalUpdateBuilder(document);
        var groupEntries = new Dictionary<PdfName, PdfObject>
        {
            [TypeKey] = new PdfName("OCG"u8),
            [NameKey] = UnicodeString(name)
        };
        var usageEntries = new Dictionary<PdfName, PdfObject>();
        AddUsage("Print", "PrintState", printVisible);
        AddUsage("Export", "ExportState", exportVisible);
        if (usageEntries.Count != 0)
            groupEntries[UsageKey] = new PdfDictionary(usageEntries);
        PdfIndirectReference groupReference = update.AddObject(new PdfDictionary(groupEntries));

        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
        {
            var configurationEntries = new Dictionary<PdfName, PdfObject>
            {
                [BaseStateKey] = new PdfName("ON"u8),
                [OrderKey] = new PdfArray([groupReference])
            };
            if (!initiallyVisible)
                configurationEntries[OffKey] = new PdfArray([groupReference]);
            if (locked)
                configurationEntries[LockedKey] = new PdfArray([groupReference]);
            var properties = new PdfDictionary(new Dictionary<PdfName, PdfObject>
            {
                [GroupsKey] = new PdfArray([groupReference]),
                [DefaultConfigurationKey] = new PdfDictionary(configurationEntries)
            });
            var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalogEntries[OptionalContentPropertiesKey] = properties;
            return update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries)).Build();
        }

        (PdfDictionary existingProperties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!existingProperties.TryGetValue(DefaultConfigurationKey,
                out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntriesExisting = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfOptionalContentConfigurationInfo defaultInfo = info.Configurations.Single(
            candidate => candidate.IsDefault);
        AddState(configurationEntriesExisting,
            initiallyVisible == (defaultInfo.BaseState != PdfOptionalContentBaseState.Off)
                ? null : initiallyVisible ? OnKey : OffKey);
        AddState(configurationEntriesExisting, locked ? LockedKey : null);
        PdfObject[] order = ResolvedArray(document,
            configurationEntriesExisting.GetValueOrDefault(OrderKey), "display order");
        configurationEntriesExisting[OrderKey] = new PdfArray([.. order, groupReference]);
        var replacementConfiguration = new PdfDictionary(configurationEntriesExisting);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);

        var propertiesEntries = existingProperties.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfObject[] groups = ResolvedArray(document,
            propertiesEntries.GetValueOrDefault(GroupsKey), "group list");
        propertiesEntries[GroupsKey] = new PdfArray([.. groups, groupReference]);
        if (configurationReference is null)
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
        var replacementProperties = new PdfDictionary(propertiesEntries);
        if (propertiesReference is not null)
            update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
        else
        {
            var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
            update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries));
        }
        return update.Build();

        void AddUsage(string category, string state, bool? visible)
        {
            if (!visible.HasValue) return;
            usageEntries[new PdfName(System.Text.Encoding.ASCII.GetBytes(category))] =
                new PdfDictionary(new Dictionary<PdfName, PdfObject>
                {
                    [new PdfName(System.Text.Encoding.ASCII.GetBytes(state))] =
                        new PdfName(visible.Value ? "ON"u8 : "OFF"u8)
                });
        }

        void AddState(Dictionary<PdfName, PdfObject> entries, PdfName? key)
        {
            if (key is null) return;
            PdfObject[] values = ResolvedArray(document, entries.GetValueOrDefault(key),
                "configuration state");
            entries[key] = new PdfArray([.. values, groupReference]);
        }
    }

    /// <summary>
    /// Duplicates a registered layer definition and its visibility settings under a new name.
    /// Existing page content and annotations remain assigned only to the source layer.
    /// </summary>
    public static byte[] DuplicateGroup(
        PdfDocument document, int objectNumber, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A layer name is required.", nameof(name));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        PdfOptionalContentGroupInfo sourceInfo = FindGroup(info, objectNumber);
        if (info.Groups.Any(group => string.Equals(
                group.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Layer names must be unique.", nameof(name));

        byte[] added = AddGroup(document, name,
            sourceInfo.IsInitiallyVisible, sourceInfo.IsLocked,
            sourceInfo.IsVisibleWhenPrinting, sourceInfo.IsVisibleWhenExporting);
        PdfDocument intermediate = PdfDocument.Open(added);
        PdfOptionalContentGroupInfo duplicateInfo = PdfOptionalContentReader.Read(intermediate)
            .Groups.Single(group => string.Equals(group.Name, name, StringComparison.Ordinal));
        PdfDictionary source = document.Resolve(new PdfIndirectReference(
                sourceInfo.ObjectNumber, sourceInfo.Generation)) as PdfDictionary
            ?? throw new InvalidOperationException(
                "The source optional-content group is not a dictionary.");
        var entries = source.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[NameKey] = UnicodeString(name);
        return new PdfIncrementalUpdateBuilder(intermediate)
            .ReplaceObject(duplicateInfo.ObjectNumber, new PdfDictionary(entries))
            .Build();
    }

    /// <summary>
    /// Unregisters a layer that is not referenced by page resources, annotations, or other
    /// document objects, and removes it from every optional-content configuration.
    /// </summary>
    public static byte[] RemoveUnusedGroup(PdfDocument document, int objectNumber)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        PdfOptionalContentGroupInfo group = FindGroup(info, objectNumber);
        var target = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfObject propertiesValue = tree.Catalog.TryGetValue(
                OptionalContentPropertiesKey, out PdfObject? properties)
            ? properties : throw new InvalidOperationException(
                "The document has no optional-content properties.");

        var propertyObjects = new HashSet<int>();
        CollectPropertyObjects(propertiesValue, 0);
        var catalogWithoutProperties = new PdfDictionary(tree.Catalog.Where(item =>
            !item.Key.Equals(OptionalContentPropertiesKey)));
        var visitedActiveObjects = new HashSet<(int, int)>();
        if (ContainsTarget(catalogWithoutProperties, 0))
            throw new InvalidOperationException(
                $"Layer {objectNumber} is still referenced outside its configurations.");

        var update = new PdfIncrementalUpdateBuilder(document);
        if (info.Groups.Count == 1)
        {
            var catalogEntries = tree.Catalog.ToDictionary(item => item.Key, item => item.Value);
            catalogEntries.Remove(OptionalContentPropertiesKey);
            return update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries)).Build();
        }

        var rewritten = new HashSet<int>();
        PdfObject replacementProperties = Rewrite(propertiesValue, false, 0)
            ?? throw new InvalidOperationException(
                "Removing the layer produced empty optional-content properties.");
        if (propertiesValue is not PdfIndirectReference)
        {
            var catalogEntries = tree.Catalog.ToDictionary(item => item.Key, item => item.Value);
            catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
            update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                new PdfDictionary(catalogEntries));
        }
        return update.Build();

        void CollectPropertyObjects(PdfObject value, int depth)
        {
            if (depth >= 128)
                throw new InvalidOperationException(
                    "The optional-content property graph is too deeply nested.");
            if (value is PdfIndirectReference reference)
            {
                if (!propertyObjects.Add(reference.ObjectNumber)) return;
                CollectPropertyObjects(document.Resolve(reference), depth + 1);
                return;
            }
            if (value is PdfDictionary dictionary)
                foreach (var item in dictionary)
                    CollectPropertyObjects(item.Value, depth + 1);
            else if (value is PdfArray array)
                foreach (PdfObject item in array)
                    CollectPropertyObjects(item, depth + 1);
        }

        bool ContainsTarget(PdfObject value, int depth)
        {
            if (depth >= 128)
                throw new InvalidOperationException("A document object is too deeply nested.");
            if (value is PdfIndirectReference reference)
            {
                if (SameReference(reference, target)) return true;
                if (!visitedActiveObjects.Add(
                        (reference.ObjectNumber, reference.Generation)))
                    return false;
                return ContainsTarget(document.Resolve(reference), depth + 1);
            }
            if (value is PdfStream stream)
                return ContainsTarget(stream.Dictionary, depth + 1);
            if (value is PdfDictionary dictionary)
                return dictionary.Any(item => ContainsTarget(item.Value, depth + 1));
            return value is PdfArray array
                && array.Any(item => ContainsTarget(item, depth + 1));
        }

        PdfObject? Rewrite(PdfObject value, bool order, int depth)
        {
            if (depth >= 128)
                throw new InvalidOperationException(
                    "The optional-content property graph is too deeply nested.");
            if (value is PdfIndirectReference reference)
            {
                if (SameReference(reference, target)) return null;
                if (!propertyObjects.Contains(reference.ObjectNumber)) return reference;
                if (rewritten.Add(reference.ObjectNumber))
                {
                    PdfObject replacement = Rewrite(
                            document.Resolve(reference), order, depth + 1)
                        ?? throw new InvalidOperationException(
                            "An optional-content configuration object became empty.");
                    update.ReplaceObject(reference.ObjectNumber, replacement);
                }
                return reference;
            }
            if (value is PdfArray array)
            {
                var items = new List<PdfObject>();
                foreach (PdfObject item in array)
                {
                    PdfObject? replacement = Rewrite(item, order, depth + 1);
                    if (replacement is not null) items.Add(replacement);
                }
                if (order && items.Count == 1 && items[0] is PdfString)
                    return null;
                return new PdfArray(items);
            }
            if (value is PdfDictionary dictionary)
            {
                var entries = new List<KeyValuePair<PdfName, PdfObject>>();
                foreach (var item in dictionary)
                {
                    PdfObject? replacement = Rewrite(item.Value,
                        item.Key.Equals(OrderKey), depth + 1);
                    if (replacement is not null)
                        entries.Add(new(item.Key, replacement));
                }
                return new PdfDictionary(entries);
            }
            if (value is PdfStream)
                throw new NotSupportedException(
                    "Optional-content properties stored in streams cannot be edited safely.");
            return value;
        }

        static bool SameReference(
            PdfIndirectReference left, PdfIndirectReference right) =>
            left.ObjectNumber == right.ObjectNumber
            && left.Generation == right.Generation;
    }

    /// <summary>Renames one registered layer by its source object number.</summary>
    public static byte[] RenameGroup(PdfDocument document, int objectNumber, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A layer name is required.", nameof(name));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        PdfOptionalContentGroupInfo group = FindGroup(info, objectNumber);
        if (info.Groups.Any(item => item.ObjectNumber != objectNumber
                && string.Equals(item.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Layer names must be unique.", nameof(name));
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The optional-content group is not a dictionary.");
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[NameKey] = UnicodeString(name);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(entries))
            .Build();
    }

    /// <summary>Sets or clears a layer's preferred print visibility.</summary>
    public static byte[] SetPrintVisibility(
        PdfDocument document, int objectNumber, bool? visible) =>
        SetUsageVisibility(document, objectNumber, "Print", "PrintState", visible);

    /// <summary>Sets or clears a layer's preferred export visibility.</summary>
    public static byte[] SetExportVisibility(
        PdfDocument document, int objectNumber, bool? visible) =>
        SetUsageVisibility(document, objectNumber, "Export", "ExportState", visible);

    /// <summary>Sets a layer's initial visibility in the default configuration.</summary>
    public static byte[] SetInitialVisibility(
        PdfDocument document, int objectNumber, bool visible)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), objectNumber);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException("The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfObject[] on = StateReferences(document, configurationEntries.GetValueOrDefault(OnKey), reference);
        PdfObject[] off = StateReferences(document, configurationEntries.GetValueOrDefault(OffKey), reference);
        if (visible)
            on = [.. on, reference];
        else
            off = [.. off, reference];
        SetArray(configurationEntries, OnKey, on);
        SetArray(configurationEntries, OffKey, off);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Sets whether the default configuration locks a layer's visibility.</summary>
    public static byte[] SetLocked(PdfDocument document, int objectNumber, bool locked)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), objectNumber);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException("The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        PdfObject[] values = StateReferences(
            document, configurationEntries.GetValueOrDefault(LockedKey), reference);
        if (locked) values = [.. values, reference];
        SetArray(configurationEntries, LockedKey, values);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Replaces the default configuration's flat layer display order.</summary>
    public static byte[] SetDisplayOrder(
        PdfDocument document, IReadOnlyList<int> objectNumbers)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(objectNumbers);
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        int[] registered = [.. info.Groups.Select(group => group.ObjectNumber).Order()];
        int[] requested = [.. objectNumbers.Order()];
        if (!registered.SequenceEqual(requested))
            throw new ArgumentException(
                "The display order must contain every registered layer exactly once.",
                nameof(objectNumbers));
        Dictionary<int, PdfIndirectReference> references = info.Groups.ToDictionary(
            group => group.ObjectNumber,
            group => new PdfIndirectReference(group.ObjectNumber, group.Generation));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException("The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        configurationEntries[OrderKey] = new PdfArray(objectNumbers.Select(
            objectNumber => (PdfObject)references[objectNumber]));
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Replaces the default configuration's nested layer display order.</summary>
    public static byte[] SetDisplayOrderTree(
        PdfDocument document, IReadOnlyList<PdfOptionalContentOrderItem> items)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(items);
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        Dictionary<int, PdfIndirectReference> references = info.Groups.ToDictionary(
            group => group.ObjectNumber,
            group => new PdfIndirectReference(group.ObjectNumber, group.Generation));
        var used = new HashSet<int>();
        PdfObject[] order = [.. items.Select(item => Build(item, 0))];
        if (!used.SetEquals(references.Keys))
            throw new ArgumentException(
                "The display order must contain every registered layer exactly once.",
                nameof(items));

        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        configurationEntries[OrderKey] = new PdfArray(order);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();

        PdfObject Build(PdfOptionalContentOrderItem item, int depth)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (depth > 64)
                throw new ArgumentException(
                    "The display order is too deeply nested.", nameof(items));
            if (item.GroupObjectNumber is int objectNumber)
            {
                if (item.Label is not null || item.Children.Count != 0
                    || !references.TryGetValue(objectNumber, out PdfIndirectReference? reference)
                    || !used.Add(objectNumber))
                    throw new ArgumentException(
                        "A layer entry must reference one unique registered layer.", nameof(items));
                return reference;
            }
            if (string.IsNullOrWhiteSpace(item.Label) || item.Children.Count == 0)
                throw new ArgumentException(
                    "A layer folder requires a name and at least one child.", nameof(items));
            return new PdfArray([UnicodeString(item.Label),
                .. item.Children.Select(child => Build(child, depth + 1))]);
        }
    }

    /// <summary>Sets or clears the default layer configuration name and creator.</summary>
    public static byte[] SetDefaultConfigurationMetadata(
        PdfDocument document, string? name, string? creator)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (name is not null && string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "A layer configuration name cannot be empty.", nameof(name));
        if (creator is not null && string.IsNullOrWhiteSpace(creator))
            throw new ArgumentException(
                "A layer configuration creator cannot be empty.", nameof(creator));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        SetOptionalText(configurationEntries, NameKey, name);
        SetOptionalText(configurationEntries, CreatorKey, creator);
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    /// <summary>Sets the default state applied before explicit layer overrides.</summary>
    public static byte[] SetDefaultBaseState(
        PdfDocument document, PdfOptionalContentBaseState baseState)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!Enum.IsDefined(baseState))
            throw new ArgumentOutOfRangeException(nameof(baseState));
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(OptionalContentPropertiesKey, out PdfObject? propertiesValue))
            throw new InvalidOperationException("The document has no optional-content properties.");
        (PdfDictionary properties, PdfIndirectReference? propertiesReference) =
            ResolveDictionaryWithReference(document, propertiesValue,
                "The optional-content properties");
        if (!properties.TryGetValue(DefaultConfigurationKey, out PdfObject? configurationValue))
            throw new InvalidOperationException(
                "The document has no default optional-content configuration.");
        (PdfDictionary configuration, PdfIndirectReference? configurationReference) =
            ResolveDictionaryWithReference(document, configurationValue,
                "The default optional-content configuration");
        var configurationEntries = configuration.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        configurationEntries[BaseStateKey] = baseState switch
        {
            PdfOptionalContentBaseState.On => new PdfName("ON"u8),
            PdfOptionalContentBaseState.Off => new PdfName("OFF"u8),
            PdfOptionalContentBaseState.Unchanged => new PdfName("Unchanged"u8),
            _ => throw new ArgumentOutOfRangeException(nameof(baseState))
        };
        var replacementConfiguration = new PdfDictionary(configurationEntries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (configurationReference is not null)
            update.ReplaceObject(configurationReference.ObjectNumber, replacementConfiguration);
        else
        {
            var propertiesEntries = properties.ToDictionary(entry => entry.Key, entry => entry.Value);
            propertiesEntries[DefaultConfigurationKey] = replacementConfiguration;
            var replacementProperties = new PdfDictionary(propertiesEntries);
            if (propertiesReference is not null)
                update.ReplaceObject(propertiesReference.ObjectNumber, replacementProperties);
            else
            {
                var catalogEntries = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
                catalogEntries[OptionalContentPropertiesKey] = replacementProperties;
                update.ReplaceObject(tree.CatalogReference.ObjectNumber,
                    new PdfDictionary(catalogEntries));
            }
        }
        return update.Build();
    }

    private static byte[] SetUsageVisibility(PdfDocument document, int objectNumber,
        string categoryName, string stateName, bool? visible)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        PdfOptionalContentGroupInfo group = FindGroup(
            PdfOptionalContentReader.Read(document), objectNumber);
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The optional-content group is not a dictionary.");
        var groupEntries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        var categoryKey = new PdfName(System.Text.Encoding.ASCII.GetBytes(categoryName));
        var stateKey = new PdfName(System.Text.Encoding.ASCII.GetBytes(stateName));
        var usageEntries = ResolveDictionary(document, groupEntries.GetValueOrDefault(UsageKey));
        var categoryEntries = ResolveDictionary(document, usageEntries.GetValueOrDefault(categoryKey));
        if (visible.HasValue)
            categoryEntries[stateKey] = new PdfName(visible.Value ? "ON"u8 : "OFF"u8);
        else
            categoryEntries.Remove(stateKey);
        if (categoryEntries.Count == 0)
            usageEntries.Remove(categoryKey);
        else
            usageEntries[categoryKey] = new PdfDictionary(categoryEntries);
        if (usageEntries.Count == 0)
            groupEntries.Remove(UsageKey);
        else
            groupEntries[UsageKey] = new PdfDictionary(usageEntries);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(groupEntries))
            .Build();
    }

    private static PdfOptionalContentGroupInfo FindGroup(
        PdfOptionalContentInfo info, int objectNumber) =>
        info.Groups.FirstOrDefault(item => item.ObjectNumber == objectNumber)
        ?? throw new KeyNotFoundException(
            $"Optional-content group {objectNumber} is not registered.");

    private static Dictionary<PdfName, PdfObject> ResolveDictionary(
        PdfDocument document, PdfObject? value) => value is null
        ? []
        : ((value is PdfIndirectReference reference ? document.Resolve(reference) : value)
            as PdfDictionary
            ?? throw new InvalidOperationException("Optional-content usage metadata is not a dictionary."))
        .ToDictionary(entry => entry.Key, entry => entry.Value);

    private static (PdfDictionary Dictionary, PdfIndirectReference? Reference)
        ResolveDictionaryWithReference(PdfDocument document, PdfObject value, string description)
    {
        PdfIndirectReference? lastReference = null;
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            lastReference = reference;
            value = document.Resolve(reference);
        }
        return (value as PdfDictionary
            ?? throw new InvalidOperationException($"{description} is not a dictionary."),
            lastReference);
    }

    private static PdfObject[] StateReferences(PdfDocument document, PdfObject? value,
        PdfIndirectReference excluded)
    {
        if (value is null) return [];
        PdfObject resolved = value is PdfIndirectReference reference
            ? document.Resolve(reference) : value;
        PdfArray array = resolved as PdfArray
            ?? throw new InvalidOperationException("An optional-content state value is not an array.");
        return [.. array.Where(item => item is not PdfIndirectReference candidate
            || candidate.ObjectNumber != excluded.ObjectNumber
            || candidate.Generation != excluded.Generation)];
    }

    private static PdfObject[] ResolvedArray(
        PdfDocument document, PdfObject? value, string description)
    {
        if (value is null) return [];
        PdfObject resolved = value is PdfIndirectReference reference
            ? document.Resolve(reference) : value;
        return resolved is PdfArray array ? [.. array] : throw new InvalidOperationException(
            $"The optional-content {description} is not an array.");
    }

    private static void SetArray(Dictionary<PdfName, PdfObject> entries,
        PdfName key, PdfObject[] values)
    {
        if (values.Length == 0) entries.Remove(key);
        else entries[key] = new PdfArray(values);
    }

    private static void SetOptionalText(Dictionary<PdfName, PdfObject> entries,
        PdfName key, string? value)
    {
        if (value is null) entries.Remove(key);
        else entries[key] = UnicodeString(value);
    }

    private static PdfString UnicodeString(string value)
    {
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(value);
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        text.CopyTo(bytes, 2);
        return new PdfString(bytes, PdfStringForm.Hexadecimal);
    }
}

/// <summary>One layer or named folder in an optional-content display-order tree.</summary>
public sealed record PdfOptionalContentOrderItem
{
    private PdfOptionalContentOrderItem(
        int? groupObjectNumber, string? label,
        IReadOnlyList<PdfOptionalContentOrderItem> children)
    {
        GroupObjectNumber = groupObjectNumber;
        Label = label;
        Children = children;
    }

    /// <summary>Gets the source object number for a layer entry.</summary>
    public int? GroupObjectNumber { get; }
    /// <summary>Gets the name for a folder entry.</summary>
    public string? Label { get; }
    /// <summary>Gets the folder's child entries.</summary>
    public IReadOnlyList<PdfOptionalContentOrderItem> Children { get; }

    /// <summary>Creates a layer entry.</summary>
    public static PdfOptionalContentOrderItem Layer(int objectNumber) =>
        new(objectNumber, null, []);

    /// <summary>Creates a named folder entry.</summary>
    public static PdfOptionalContentOrderItem Folder(
        string label, params PdfOptionalContentOrderItem[] children) =>
        new(null, label, Array.AsReadOnly(children.ToArray()));
}
