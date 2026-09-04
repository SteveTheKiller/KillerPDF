using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfIncrementalAnnotationEditorTests
{
    [Fact]
    public void Build_RejectsExhaustedStructureParentKeySpace()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Exhausted parent keys",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddStructureContainer(PdfStructureType.Document)
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfIndirectReference rootReference = Assert.IsType<PdfIndirectReference>(
            catalog[Name("StructTreeRoot")]);
        PdfDictionary root = ResolveDictionary(source, rootReference);
        var setup = new PdfIncrementalUpdateBuilder(source);
        var rootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        rootEntries[Name("ParentTreeNextKey")] = new PdfInteger(long.MaxValue);
        setup.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(rootEntries));
        PdfDocument exhausted = PdfDocument.Open(setup.Build());

        Assert.Throws<OverflowException>(() =>
            new PdfIncrementalAnnotationEditor(exhausted)
                .AddTextNote(0, 20, 20, "Cannot allocate")
                .Build());
    }

    [Fact]
    public void Build_RejectsNegativeStructureParentTreeKey()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddStructureContainer(PdfStructureType.Document)
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfIndirectReference rootReference = Assert.IsType<PdfIndirectReference>(
            catalog[Name("StructTreeRoot")]);
        PdfDictionary root = ResolveDictionary(source, rootReference);
        var setup = new PdfIncrementalUpdateBuilder(source);
        var rootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        rootEntries[Name("ParentTree")] = new PdfDictionary([
            new(Name("Nums"), new PdfArray([new PdfInteger(-1), PdfNull.Instance]))
        ]);
        setup.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(rootEntries));
        PdfDocument malformed = PdfDocument.Open(setup.Build());

        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "Cannot allocate")
                .Build());

        var scalarSetup = new PdfIncrementalUpdateBuilder(source);
        var scalarRootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        scalarRootEntries[Name("ParentTree")] = new PdfDictionary([
            new(Name("Nums"), new PdfArray([
                new PdfInteger(0), new PdfInteger(17)
            ]))
        ]);
        scalarSetup.ReplaceObject(rootReference.ObjectNumber,
            new PdfDictionary(scalarRootEntries));
        PdfDocument scalarValue = PdfDocument.Open(scalarSetup.Build());
        InvalidOperationException scalarError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(scalarValue)
                .AddTextNote(0, 20, 20, "Cannot retain scalar mapping")
                .Build());
        Assert.Contains("is not a structure element or array",
            scalarError.Message, StringComparison.Ordinal);

        PdfObject documentValue = root[Name("K")];
        if (documentValue is PdfArray documentArray)
            documentValue = documentArray[0];
        PdfIndirectReference documentReference = Assert.IsType<PdfIndirectReference>(
            documentValue);
        PdfDictionary documentElement = ResolveDictionary(source, documentReference);
        var staleKidsEntries = documentElement.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        staleKidsEntries[Name("K")] = new PdfIndirectReference(999, 0);
        PdfDocument staleKids = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(documentReference.ObjectNumber,
                new PdfDictionary(staleKidsEntries))
            .Build());
        InvalidOperationException staleKidsError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(staleKids)
                .AddTextNote(0, 20, 20, "Cannot retain stale child")
                .Build());
        Assert.Contains("/K value contains a stale indirect reference",
            staleKidsError.Message, StringComparison.Ordinal);

        var emptyKidEntries = documentElement.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        emptyKidEntries[Name("K")] = new PdfDictionary([]);
        PdfDocument emptyKid = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(documentReference.ObjectNumber,
                new PdfDictionary(emptyKidEntries))
            .Build());
        InvalidOperationException emptyKidError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(emptyKid)
                .AddTextNote(0, 20, 20, "Cannot retain empty child")
                .Build());
        Assert.Contains("/K value contains an invalid child",
            emptyKidError.Message, StringComparison.Ordinal);
    }


    [Fact]
    public void Build_NormalizesDirectStructureTreeRootForTaggedAnnotation()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Direct structure root",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Existing note")
            .AddStructureContainer(PdfStructureType.Document)
            .Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            source.Trailer[Name("Root")]);
        PdfDictionary catalog = ResolveDictionary(source, catalogReference);
        PdfDictionary root = ResolveDictionary(source, catalog[Name("StructTreeRoot")]);
        PdfObject documentValue = Assert.IsType<PdfArray>(root[Name("K")])[0];
        PdfIndirectReference documentReference = Assert.IsType<PdfIndirectReference>(
            documentValue);
        PdfDictionary documentElement = ResolveDictionary(source, documentValue);
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference documentAlias = setup.AddObject(documentReference);
        PdfIndirectReference documentOuterAlias = setup.AddObject(documentAlias);
        PdfObject existingDocumentKids = documentElement[Name("K")];
        foreach (PdfObject childValue in existingDocumentKids is PdfArray childArray
                     ? childArray : new PdfArray([existingDocumentKids]))
        {
            PdfIndirectReference childReference = Assert.IsType<PdfIndirectReference>(childValue);
            PdfDictionary child = ResolveDictionary(source, childReference);
            setup.ReplaceObject(childReference.ObjectNumber, new PdfDictionary(child.Select(entry =>
                entry.Key.Equals(Name("P"))
                    ? new KeyValuePair<PdfName, PdfObject>(entry.Key, documentOuterAlias)
                    : entry)));
        }
        PdfIndirectReference documentKids = setup.AddObject(
            existingDocumentKids is PdfArray existingArray
                ? existingArray : new PdfArray([existingDocumentKids]));
        var documentEntries = documentElement.ToDictionary(
            entry => entry.Key, entry => entry.Value);
        documentEntries[Name("K")] = documentKids;
        documentElement = new PdfDictionary(documentEntries);
        PdfIndirectReference directKids = setup.AddObject(
            new PdfArray([documentElement]));
        var directRootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        directRootEntries[Name("K")] = directKids;
        root = new PdfDictionary(directRootEntries);
        setup.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog
            .Where(entry => !entry.Key.Equals(Name("StructTreeRoot")))
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("StructTreeRoot"), root))));
        PdfDocument direct = PdfDocument.Open(setup.Build());

        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(direct)
            .AddTextNote(0, 20, 20, "Accessible note")
            .Build());
        PdfDictionary reopenedCatalog = ResolveDictionary(
            reopened, reopened.Trailer[Name("Root")]);
        PdfIndirectReference reopenedRootReference = Assert.IsType<PdfIndirectReference>(
            reopenedCatalog[Name("StructTreeRoot")]);
        PdfDictionary reopenedRoot = ResolveDictionary(reopened, reopenedRootReference);
        PdfDictionary reopenedDocument = ResolveDictionary(
            reopened, reopenedRoot[Name("K")]);
        PdfIndirectReference reopenedDocumentReference = Assert.IsType<PdfIndirectReference>(
            reopenedRoot[Name("K")]);
        PdfArray parentNumbers = Assert.IsType<PdfArray>(ResolveDictionary(
            reopened, reopenedRoot[Name("ParentTree")])[Name("Nums")]);

        PdfArray reopenedDocumentKids = Assert.IsType<PdfArray>(
            reopenedDocument[Name("K")]);
        Assert.Equal(2, reopenedDocumentKids.Count);
        Assert.All(reopenedDocumentKids, child =>
        {
            PdfObject parent = ResolveDictionary(reopened, child)[Name("P")];
            PdfIndirectReference? finalParent = null;
            while (parent is PdfIndirectReference parentReference)
            {
                finalParent = parentReference;
                parent = reopened.Resolve(parentReference);
            }
            Assert.Equal(reopenedDocumentReference.ObjectNumber,
                Assert.IsType<PdfIndirectReference>(finalParent).ObjectNumber);
        });
        Assert.Equal(documentOuterAlias.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(ResolveDictionary(
                reopened, reopenedDocumentKids[0])[Name("P")]).ObjectNumber);
        Assert.Contains(parentNumbers, item => item is PdfInteger integer && integer.Value == 1);
        Assert.Equal(2, Assert.IsType<PdfInteger>(
            reopenedRoot[Name("ParentTreeNextKey")]).Value);
        Assert.Equal(documentReference.ObjectNumber,
            reopenedDocumentReference.ObjectNumber);
    }

    [Fact]
    public void Build_PreservesEncryptedPdfUaStructureWithoutLeakingText()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Encrypted accessible annotations",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowAccessibilityExtraction = true,
                AllowAnnotationModification = true
            })
            .AddBlankPage()
            .AddTextNote(0, 72, 700, "Existing encrypted note")
            .AddStructureContainer(PdfStructureType.Document)
            .Build();
        const string appendedText = "Confidential accessible highlight";
        byte[] output = new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(source, "owner"))
            .AddHighlight(0, 72, 650, 160, 20, appendedText)
            .Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressObjectStreams = true,
                CompressCrossReferenceStream = true
            });
        PdfDocument reopened = PdfDocument.Open(output, "owner");
        PdfDictionary catalog = ResolveDictionary(reopened, reopened.Trailer[Name("Root")]);
        PdfDictionary structureRoot = ResolveDictionary(reopened, catalog[Name("StructTreeRoot")]);
        PdfDictionary parentTree = ResolveDictionary(reopened, structureRoot[Name("ParentTree")]);

        Assert.Equal(4, Assert.IsType<PdfArray>(parentTree[Name("Nums")]).Count);
        Assert.Equal(2, Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")]).Count);
        Assert.True(output.AsSpan(0, source.Length).SequenceEqual(source));
        Assert.DoesNotContain(appendedText, Encoding.Latin1.GetString(output));
    }

    [Fact]
    public void Build_PreservesTaggedStructureAndAssociatesNewAnnotation()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Accessible incremental annotations",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddTextNote(0, 72, 700, "Existing review note")
            .AddStructureContainer(PdfStructureType.Document)
            .Build();
        PdfDocument initial = PdfDocument.Open(source);
        PdfDictionary initialCatalog = ResolveDictionary(initial, initial.Trailer[Name("Root")]);
        PdfIndirectReference initialRootReference = Assert.IsType<PdfIndirectReference>(
            initialCatalog[Name("StructTreeRoot")]);
        PdfDictionary initialRoot = ResolveDictionary(initial, initialRootReference);
        PdfArray initialNamespaces = Assert.IsType<PdfArray>(initialRoot[Name("Namespaces")]);
        var setup = new PdfIncrementalUpdateBuilder(initial);
        PdfIndirectReference pdf2NamespaceReference = Assert.IsType<PdfIndirectReference>(
            initialNamespaces[0]);
        PdfDictionary pdf2Namespace = ResolveDictionary(initial, pdf2NamespaceReference);
        PdfIndirectReference indirectPdf2Uri = setup.AddObject(new PdfString(
            [0xEF, 0xBB, 0xBF, .. "http://iso.org/pdf2/ssn"u8.ToArray()],
            PdfStringForm.Hexadecimal));
        PdfIndirectReference indirectPdf2UriAlias = setup.AddObject(indirectPdf2Uri);
        setup.ReplaceObject(pdf2NamespaceReference.ObjectNumber,
            new PdfDictionary(pdf2Namespace
                .Where(entry => !entry.Key.Equals(Name("NS")))
                .Append(new KeyValuePair<PdfName, PdfObject>(
                    Name("NS"), indirectPdf2UriAlias))));
        PdfIndirectReference customNamespace = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Namespace")),
            new(Name("NS"), new PdfString(
                Encoding.ASCII.GetBytes("https://example.test/custom-structure"),
                PdfStringForm.Literal))
        ]));
        var alteredRoot = initialRoot.ToDictionary(entry => entry.Key, entry => entry.Value);
        PdfIndirectReference namespaceArray = setup.AddObject(new PdfArray(
            [customNamespace, .. initialNamespaces]));
        PdfIndirectReference namespaceArrayAlias = setup.AddObject(namespaceArray);
        alteredRoot[Name("Namespaces")] = namespaceArrayAlias;
        PdfIndirectReference nextKey = setup.AddObject(new PdfInteger(0));
        alteredRoot[Name("ParentTreeNextKey")] = setup.AddObject(nextKey);
        PdfIndirectReference documentReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(initialRoot[Name("K")])[0]);
        PdfIndirectReference documentAlias = setup.AddObject(documentReference);
        PdfIndirectReference documentOuterAlias = setup.AddObject(documentAlias);
        alteredRoot[Name("K")] = new PdfArray([documentOuterAlias]);
        setup.ReplaceObject(initialRootReference.ObjectNumber, new PdfDictionary(alteredRoot));
        PdfIndirectReference rootAlias = setup.AddObject(initialRootReference);
        PdfIndirectReference rootOuterAlias = setup.AddObject(rootAlias);
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            initial.Trailer[Name("Root")]);
        setup.ReplaceObject(catalogReference.ObjectNumber,
            new PdfDictionary(initialCatalog.Select(entry =>
                entry.Key.Equals(Name("StructTreeRoot"))
                    ? new KeyValuePair<PdfName, PdfObject>(entry.Key, rootOuterAlias)
                    : entry)));
        source = setup.Build();
        byte[] output = new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(source))
            .AddHighlight(0, 72, 650, 160, 20, "New review highlight")
            .Build();
        PdfDocument document = PdfDocument.Open(output);
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary structureRoot = ResolveDictionary(document, catalog[Name("StructTreeRoot")]);
        PdfDictionary parentTree = ResolveDictionary(document, structureRoot[Name("ParentTree")]);
        PdfArray numbers = Assert.IsType<PdfArray>(parentTree[Name("Nums")]);
        PdfArray annotations = Assert.IsType<PdfArray>(Pages(document)[0].Page[Name("Annots")]);

        Assert.Equal(2, annotations.Count);
        Assert.All(annotations, value => Assert.True(
            ResolveDictionary(document, value).ContainsKey(Name("StructParent"))));
        Assert.Equal(4, numbers.Count);
        Assert.Equal(0, Assert.IsType<PdfInteger>(numbers[0]).Value);
        Assert.Equal(1, Assert.IsType<PdfInteger>(numbers[2]).Value);
        Assert.All([numbers[1], numbers[3]], value => Assert.Equal("Annot",
            Assert.IsType<PdfName>(ResolveDictionary(document, value)[Name("S")]).ValueAsLatin1()));
        PdfDictionary appendedElement = ResolveDictionary(document, numbers[3]);
        PdfDictionary selectedNamespace = ResolveDictionary(document, appendedElement[Name("NS")]);
        PdfObject selectedUriValue = selectedNamespace[Name("NS")];
        while (selectedUriValue is PdfIndirectReference selectedUriReference)
            selectedUriValue = document.Resolve(selectedUriReference);
        PdfString selectedUri = Assert.IsType<PdfString>(selectedUriValue);
        Assert.True(selectedUri.Bytes.Span.StartsWith(
            new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal("http://iso.org/pdf2/ssn", Encoding.UTF8.GetString(
            selectedUri.Bytes.Span[3..]));
        Assert.Equal(rootOuterAlias.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(catalog[Name("StructTreeRoot")]).ObjectNumber);
        Assert.Equal(documentOuterAlias.ObjectNumber, Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(structureRoot[Name("K")])[0]).ObjectNumber);
        Assert.True(output.AsSpan(0, source.Length).SequenceEqual(source));
    }

    [Fact]
    public void Build_RejectsDirectStructureNamespaceEntries()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Malformed namespace",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddStructureContainer(PdfStructureType.Document)
            .Build());
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfIndirectReference rootReference = Assert.IsType<PdfIndirectReference>(
            catalog[Name("StructTreeRoot")]);
        PdfDictionary root = ResolveDictionary(source, rootReference);
        var rootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        rootEntries[Name("Namespaces")] = new PdfArray([new PdfDictionary([
            new(Name("Type"), Name("Namespace")),
            new(Name("NS"), new PdfString(
                "http://iso.org/pdf2/ssn"u8, PdfStringForm.Literal))
        ])]);
        PdfDocument malformed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(rootEntries))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "Accessible note")
                .Build());

        Assert.Contains("structure namespace is not an indirect reference",
            error.Message, StringComparison.Ordinal);

        PdfArray originalNamespaces = Assert.IsType<PdfArray>(root[Name("Namespaces")]);
        PdfIndirectReference namespaceReference = Assert.IsType<PdfIndirectReference>(
            originalNamespaces[0]);
        PdfDocument untyped = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(namespaceReference.ObjectNumber, new PdfDictionary([
                new(Name("NS"), new PdfString(
                    "http://iso.org/pdf2/ssn"u8, PdfStringForm.Literal))
            ]))
            .Build());
        InvalidOperationException typeError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(untyped)
                .AddTextNote(0, 20, 20, "Accessible note")
                .Build());
        Assert.Contains("structure namespace has no /Type /Namespace value",
            typeError.Message, StringComparison.Ordinal);

        var duplicateSetup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference duplicateNamespace = duplicateSetup.AddObject(
            ResolveDictionary(source, namespaceReference));
        var duplicateRoot = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        duplicateRoot[Name("Namespaces")] = new PdfArray([
            namespaceReference, duplicateNamespace
        ]);
        duplicateSetup.ReplaceObject(rootReference.ObjectNumber,
            new PdfDictionary(duplicateRoot));
        PdfDocument duplicated = PdfDocument.Open(duplicateSetup.Build());
        InvalidOperationException duplicateError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(duplicated)
                .AddTextNote(0, 20, 20, "Accessible note")
                .Build());
        Assert.Contains("contains duplicate PDF 2.0 namespaces",
            duplicateError.Message, StringComparison.Ordinal);

        PdfObject topLevelValue = root[Name("K")];
        if (topLevelValue is PdfArray topLevelArray)
            topLevelValue = topLevelArray[0];
        PdfIndirectReference topLevelReference = Assert.IsType<PdfIndirectReference>(
            topLevelValue);
        PdfDictionary topLevel = ResolveDictionary(source, topLevelReference);
        PdfDocument mistypedRole = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(topLevelReference.ObjectNumber,
                new PdfDictionary(topLevel
                    .Where(entry => !entry.Key.Equals(Name("S")))
                    .Append(new KeyValuePair<PdfName, PdfObject>(
                        Name("S"), new PdfInteger(17)))))
            .Build());
        InvalidOperationException roleError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(mistypedRole)
                .AddTextNote(0, 20, 20, "Accessible note")
                .Build());
        Assert.Contains("top-level structure element /S value is not a name",
            roleError.Message, StringComparison.Ordinal);

        PdfDocument wrongParent = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(topLevelReference.ObjectNumber,
                new PdfDictionary(topLevel
                    .Where(entry => !entry.Key.Equals(Name("P")))
                    .Append(new KeyValuePair<PdfName, PdfObject>(
                        Name("P"), topLevelReference))))
            .Build());
        InvalidOperationException parentError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(wrongParent)
                .AddTextNote(0, 20, 20, "Accessible note")
                .Build());
        Assert.Contains("no reciprocal /P link to the structure-tree root",
            parentError.Message, StringComparison.Ordinal);

        var mistypedRootEntries = root.ToDictionary(entry => entry.Key, entry => entry.Value);
        mistypedRootEntries[Name("Type")] = Name("Unexpected");
        PdfDocument mistypedRoot = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(rootReference.ObjectNumber,
                new PdfDictionary(mistypedRootEntries))
            .Build());
        InvalidOperationException rootTypeError = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(mistypedRoot)
                .AddTextNote(0, 20, 20, "Accessible note")
                .Build());
        Assert.Contains("structure-tree root has an invalid /Type value",
            rootTypeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsUnpairedSurrogateInAnnotationText()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        var editor = new PdfIncrementalAnnotationEditor(document)
            .AddTextNote(0, 10, 10, "bad\uD800text");

        Assert.Throws<ArgumentException>(() => editor.Build());
    }

    [Fact]
    public void Build_RejectsStaleExistingPageAnnotations()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        PdfDictionary stalePage = new(Page.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                new PdfArray([new PdfIndirectReference(999, 0)]))));
        PdfDocument malformed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(Reference.ObjectNumber, stalePage)
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("/Annots contains a stale or non-dictionary entry",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsDirectExistingPageAnnotations()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var annotation = new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ]))
        ]);
        PdfDocument malformed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([annotation])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("/Annots contains a direct annotation entry",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsDuplicateExistingPageAnnotationReferences()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference annotation = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference)
        ]));
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([annotation, annotation])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("/Annots contains a duplicate annotation reference",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsDuplicateExistingPageAnnotationNames()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary Annotation() => new([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference),
            new(Name("NM"), new PdfString("duplicate"u8, PdfStringForm.Literal))
        ]);
        PdfIndirectReference first = setup.AddObject(Annotation());
        PdfIndirectReference second = setup.AddObject(Annotation());
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([first, second])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("/Annots contains duplicate /NM values",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsGeneratedAnnotationNameCollision()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference existing = setup.ReserveObject();
        string collidingName = $"KillerPDF-Note-{existing.ObjectNumber + 1}";
        setup.SetObject(existing, new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference),
            new(Name("NM"), new PdfString(
                Encoding.Latin1.GetBytes(collidingName), PdfStringForm.Literal))
        ]));
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([existing])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains($"already contains annotation /NM value '{collidingName}'",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsMalformedExistingAnnotationText()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference existing = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference),
            new(Name("Contents"), new PdfString(
                [0xEF, 0xBB, 0xBF, 0xC3, 0x28],
                PdfStringForm.Hexadecimal))
        ]));
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([existing])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("annotation /Contents value contains malformed UTF-8 text",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsMalformedExistingAnnotationCommonFields()
    {
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("M"),
                new PdfString("D:20250230"u8, PdfStringForm.Literal)),
            "annotation /M value is not a valid PDF date");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("F"),
                new PdfInteger(-1)),
            "annotation /F value is not a nonnegative integer");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("CA"),
                new PdfReal(1.1)),
            "annotation /CA value is not a number from 0 through 1");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("C"),
                new PdfArray([new PdfReal(1.1)])),
            "annotation /C value is not a valid color array");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("Border"),
                new PdfArray([
                    new PdfInteger(0), new PdfInteger(0), new PdfInteger(-1)
                ])),
            "annotation /Border value has invalid radii or width");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("BS"),
                new PdfDictionary([new(Name("S"), Name("Unexpected"))])),
            "annotation /BS /S value /Unexpected is not defined");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("QuadPoints"),
                new PdfArray([new PdfInteger(0), new PdfInteger(0)])),
            "annotation /QuadPoints value is not a nonempty sequence");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("Lang"),
                new PdfString("not_valid"u8, PdfStringForm.Literal)),
            "annotation /Lang value is not a valid BCP 47 language tag");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("RT"),
                Name("Unexpected")),
            "annotation /RT value /Unexpected is not defined");
        AssertInvalid(new KeyValuePair<PdfName, PdfObject>(Name("State"),
                new PdfString("Unexpected"u8, PdfStringForm.Literal)),
            "annotation /State and /StateModel must both be strings");

        static void AssertInvalid(
            KeyValuePair<PdfName, PdfObject> malformedEntry, string expectedMessage)
        {
            PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
                .AddBlankPage()
                .Build());
            (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
            var setup = new PdfIncrementalUpdateBuilder(source);
            PdfIndirectReference existing = setup.AddObject(new PdfDictionary(
            [
                new KeyValuePair<PdfName, PdfObject>(Name("Type"), Name("Annot")),
                new KeyValuePair<PdfName, PdfObject>(Name("Subtype"), Name("Text")),
                new KeyValuePair<PdfName, PdfObject>(Name("Rect"), new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    new PdfInteger(10), new PdfInteger(10)
                ])),
                new KeyValuePair<PdfName, PdfObject>(Name("P"), Reference),
                malformedEntry
            ]));
            PdfDocument malformed = PdfDocument.Open(setup
                .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                    new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                        new PdfArray([existing])))))
                .Build());

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                new PdfIncrementalAnnotationEditor(malformed)
                    .AddTextNote(0, 20, 20, "New note")
                    .Build());
            Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Build_RejectsExistingReplyTargetNotRegisteredOnPage()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference target = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text"))
        ]));
        PdfIndirectReference reply = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference),
            new(Name("IRT"), target)
        ]));
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([reply])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("annotation /IRT target is not registered on the page",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsExistingPopupWithMismatchedParent()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference other = setup.ReserveObject();
        PdfIndirectReference popup = setup.ReserveObject();
        PdfIndirectReference markup = setup.ReserveObject();
        PdfDictionary Markup(PdfObject? popupValue = null)
        {
            var entries = new List<KeyValuePair<PdfName, PdfObject>>
            {
                new(Name("Type"), Name("Annot")),
                new(Name("Subtype"), Name("Text")),
                new(Name("Rect"), new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    new PdfInteger(10), new PdfInteger(10)
                ])),
                new(Name("P"), Reference)
            };
            if (popupValue is not null)
                entries.Add(new KeyValuePair<PdfName, PdfObject>(Name("Popup"), popupValue));
            return new PdfDictionary(entries);
        }
        setup.SetObject(other, Markup());
        setup.SetObject(popup, new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Popup")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference),
            new(Name("Parent"), other)
        ]));
        setup.SetObject(markup, Markup(popup));
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([markup, popup, other])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("annotation /Popup target does not link back through /Parent",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_AcceptsAliasedAnnotationOwnershipAndPopupLinks()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference pageAlias = setup.AddObject(Reference);
        PdfIndirectReference popup = setup.ReserveObject();
        PdfIndirectReference markup = setup.ReserveObject();
        PdfIndirectReference popupAlias = setup.AddObject(popup);
        PdfIndirectReference markupAlias = setup.AddObject(markup);
        setup.SetObject(popup, new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Popup")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), pageAlias),
            new(Name("Parent"), markupAlias)
        ]));
        setup.SetObject(markup, new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), pageAlias),
            new(Name("Popup"), popupAlias),
            new(Name("IRT"), popupAlias)
        ]));
        byte[] aliasedBytes = setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([markupAlias, popupAlias])))))
            .Build();
        PdfDocument aliased = PdfDocument.Open(aliasedBytes);

        byte[] output = new PdfIncrementalAnnotationEditor(aliased)
            .AddTextNote(0, 20, 20, "New note")
            .Build();

        Assert.True(output.AsSpan(0, aliasedBytes.Length).SequenceEqual(aliasedBytes));
        Assert.Equal(3, Assert.IsType<PdfArray>(Pages(PdfDocument.Open(output))[0]
            .Page[Name("Annots")]).Count);
    }

    [Fact]
    public void Build_RejectsMalformedExistingAnnotationAppearanceState()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference appearance = setup.AddObject(new PdfStream(
            new PdfDictionary([
                new(Name("Type"), Name("XObject")),
                new(Name("Subtype"), Name("Form")),
                new(Name("BBox"), new PdfArray([
                    new PdfInteger(0), new PdfInteger(0),
                    new PdfInteger(10), new PdfInteger(10)
                ]))
            ]), []));
        PdfIndirectReference existing = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference),
            new(Name("AS"), Name("Off")),
            new(Name("AP"), new PdfDictionary([
                new(Name("N"), new PdfDictionary([
                    new(Name("On"), appearance)
                ]))
            ]))
        ]));
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([existing])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("/AS value has no matching normal appearance state",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsMalformedExistingAnnotationAppearanceStream()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .Build());
        (PdfIndirectReference Reference, PdfDictionary Page) = Pages(source)[0];
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference appearance = setup.AddObject(new PdfStream(
            new PdfDictionary([]), []));
        PdfIndirectReference existing = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), Reference),
            new(Name("AP"), new PdfDictionary([new(Name("N"), appearance)]))
        ]));
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(Reference.ObjectNumber, new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                    new PdfArray([existing])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("appearance has no /Subtype /Form value",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DetachesSharedPageAnnotationArraysBeforeAppending()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddBlankPage()
            .Build());
        IReadOnlyList<(PdfIndirectReference Reference, PdfDictionary Page)> pages = Pages(source);
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference sharedAnnotations = setup.AddObject(new PdfArray([]));
        foreach ((PdfIndirectReference reference, PdfDictionary page) in pages)
            setup.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Annots"), sharedAnnotations))));
        PdfDocument shared = PdfDocument.Open(setup.Build());

        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(shared)
            .AddTextNote(0, 20, 20, "Page one only")
            .Build());
        IReadOnlyList<(PdfIndirectReference Reference, PdfDictionary Page)> reopenedPages =
            Pages(reopened);
        PdfIndirectReference firstArrayReference = Assert.IsType<PdfIndirectReference>(
            reopenedPages[0].Page[Name("Annots")]);
        PdfIndirectReference secondArrayReference = Assert.IsType<PdfIndirectReference>(
            reopenedPages[1].Page[Name("Annots")]);

        Assert.NotEqual(firstArrayReference.ObjectNumber, secondArrayReference.ObjectNumber);
        Assert.Single(Assert.IsType<PdfArray>(reopened.Resolve(firstArrayReference)));
        Assert.Empty(Assert.IsType<PdfArray>(reopened.Resolve(secondArrayReference)));
    }

    [Fact]
    public void Build_RejectsExistingAnnotationOwnedByAnotherPage()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddBlankPage()
            .Build());
        IReadOnlyList<(PdfIndirectReference Reference, PdfDictionary Page)> pages = Pages(source);
        var annotation = new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Text")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)
            ])),
            new(Name("P"), pages[1].Reference)
        ]);
        var setup = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference annotationReference = setup.AddObject(annotation);
        PdfDocument malformed = PdfDocument.Open(setup
            .ReplaceObject(pages[0].Reference.ObjectNumber,
                new PdfDictionary(pages[0].Page.Append(
                    new KeyValuePair<PdfName, PdfObject>(Name("Annots"),
                        new PdfArray([annotationReference])))))
            .Build());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(malformed)
                .AddTextNote(0, 20, 20, "New note")
                .Build());

        Assert.Contains("/P value identifies another page",
            error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Build_HonorsAnnotationCertificationPermission(int permission, bool allowed)
    {
        var editor = new PdfIncrementalAnnotationEditor(
            PdfDocument.Open(CertifiedSource(permission)))
            .AddTextNote(0, 20, 700, "review");

        if (allowed)
            Assert.NotEmpty(editor.Build());
        else
            Assert.Throws<InvalidOperationException>(() => editor.Build());
    }

    [Fact]
    public void Build_CanEmitCompressedStructuralRevision()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(source))
            .AddTextNote(0, 72, 650, "Compressed note")
            .Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressObjectStreams = true,
                CompressCrossReferenceStream = true
            }));

        Assert.True(reopened.CrossReferences.Sections[0].IsStream);
        Assert.Contains(reopened.CrossReferences.Sections[0].Values,
            entry => entry.Type == PdfCrossReferenceEntryType.Compressed);
        Assert.Single(Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")]));
    }

    [Fact]
    public void Build_CanEmitEncryptedCompressedStructuralRevision()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user", OwnerPassword = "owner"
            })
            .AddBlankPage().Build();

        byte[] bytes = new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(source, "owner"))
            .AddTextNote(0, 72, 650, "Encrypted compressed note")
            .Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressObjectStreams = true,
                CompressCrossReferenceStream = true
            });
        PdfDocument reopened = PdfDocument.Open(bytes, "user");
        PdfDictionary note = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);

        PdfString contents = Assert.IsType<PdfString>(note[Name("Contents")]);
        Assert.Equal("Encrypted compressed note",
            Encoding.BigEndianUnicode.GetString(contents.Bytes.Span[2..]));
        Assert.Equal(-1, bytes.AsSpan().IndexOf("Encrypted compressed note"u8));
        Assert.Contains(reopened.CrossReferences.Sections[0].Values,
            entry => entry.Type == PdfCrossReferenceEntryType.Compressed);
    }

    [Fact]
    public void Build_AppendsAnnotationsToTheSelectedExistingPage()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build();
        PdfDocument original = PdfDocument.Open(source);
        var editor = new PdfIncrementalAnnotationEditor(original);

        byte[] result = editor
            .AddTextNote(1, 72, 650, "Review résumé", open: true)
            .AddHighlight(1, 72, 600, 200, 20, "Important", opacity: 0.4)
            .Build();
        PdfDocument reopened = PdfDocument.Open(result);
        IReadOnlyList<(PdfIndirectReference Reference, PdfDictionary Page)> pages = Pages(reopened);
        var annotations = Assert.IsType<PdfArray>(pages[1].Page[Name("Annots")]);
        PdfDictionary note = ResolveDictionary(reopened, annotations[0]);
        PdfDictionary highlight = ResolveDictionary(reopened, annotations[1]);

        Assert.Equal(2, editor.PageCount);
        Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
        Assert.False(pages[0].Page.ContainsKey(Name("Annots")));
        Assert.Equal("Text", Assert.IsType<PdfName>(note[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("Highlight", Assert.IsType<PdfName>(highlight[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal(pages[1].Reference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(note[Name("P")]).ObjectNumber);
        Assert.IsType<PdfStream>(reopened.Resolve(Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfDictionary>(highlight[Name("AP")])[Name("N")])));
    }

    [Fact]
    public void Build_PreservesExistingDirectAnnotationArray()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 20, 700, "Existing")
            .Build();
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddHighlight(0, 20, 650, 100, 15)
            .Build());
        PdfArray annotations = Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")]);

        Assert.Equal(2, annotations.Count);
        Assert.Equal("Text", Assert.IsType<PdfName>(
            ResolveDictionary(reopened, annotations[0])[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("Highlight", Assert.IsType<PdfName>(
            ResolveDictionary(reopened, annotations[1])[Name("Subtype")]).ValueAsLatin1());
    }

    [Fact]
    public void Build_UpdatesFinalIndirectAnnotationArrayWithoutReplacingAliases()
    {
        byte[] initial = new PdfDocumentBuilder().AddBlankPage().Build();
        PdfDocument firstDocument = PdfDocument.Open(initial);
        (PdfIndirectReference pageReference, PdfDictionary page) = Pages(firstDocument)[0];
        var setup = new PdfIncrementalUpdateBuilder(firstDocument);
        PdfIndirectReference arrayReference = setup.AddObject(new PdfArray([]));
        PdfIndirectReference arrayAlias = setup.AddObject(arrayReference);
        PdfIndirectReference arrayOuterAlias = setup.AddObject(arrayAlias);
        setup.ReplaceObject(pageReference.ObjectNumber, Replace(
            page, Name("Annots"), arrayOuterAlias));
        byte[] source = setup.Build();

        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddTextNote(0, 30, 700, "Indirect array")
            .Build());
        PdfDictionary reopenedPage = Pages(reopened)[0].Page;
        PdfIndirectReference reopenedArrayReference = Assert.IsType<PdfIndirectReference>(
            reopenedPage[Name("Annots")]);
        PdfObject resolvedArray = reopenedArrayReference;
        while (resolvedArray is PdfIndirectReference reference)
            resolvedArray = reopened.Resolve(reference);
        PdfArray annotations = Assert.IsType<PdfArray>(resolvedArray);

        Assert.Equal(arrayOuterAlias.ObjectNumber, reopenedArrayReference.ObjectNumber);
        Assert.Single(Assert.IsType<PdfArray>(reopened.Resolve(arrayReference)));
        Assert.Single(annotations);
        Assert.Equal(3, reopened.CrossReferences.Sections.Count);
    }

    [Fact]
    public void ArgumentsAndEmptyUpdates_AreRejected()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddTextNote(1, 0, 0, "bad"));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddHighlight(0, 0, 0, 10, 10, opacity: -1));
        Assert.Throws<InvalidOperationException>(() => editor.Build());
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        static byte[] Edit(PdfDocument document) => new PdfIncrementalAnnotationEditor(document)
            .AddTextNote(0, 20, 700, "Note")
            .AddHighlight(0, 20, 650, 100, 15)
            .Build();

        Assert.Equal(Edit(source), Edit(source));
    }

    [Theory]
    [InlineData("Underline")]
    [InlineData("StrikeOut")]
    [InlineData("Squiggly")]
    public void Build_AppendsEveryStandardTextMarkupType(string subtype)
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        _ = subtype switch
        {
            "Underline" => editor.AddUnderline(0, 20, 600, 100, 15),
            "StrikeOut" => editor.AddStrikeOut(0, 20, 600, 100, 15),
            "Squiggly" => editor.AddSquiggly(0, 20, 600, 100, 15),
            _ => throw new ArgumentOutOfRangeException(nameof(subtype))
        };
        PdfDocument reopened = PdfDocument.Open(editor.Build());
        PdfArray annotations = Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")]);
        PdfDictionary annotation = ResolveDictionary(reopened, annotations[0]);

        Assert.Equal(subtype, Assert.IsType<PdfName>(annotation[Name("Subtype")]).ValueAsLatin1());
        Assert.IsType<PdfStream>(reopened.Resolve(Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
    }

    [Fact]
    public void Build_AppendsEmbeddedFreeTextAndEveryVisualAnnotationType()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddFreeText(0, 20, 650, 160, 60, "A\nA", font, fillColor: PdfRgbColor.Yellow)
            .AddLine(0, new PdfPoint(20, 600), new PdfPoint(180, 570), lineWidth: 3)
            .AddRectangle(0, 20, 500, 70, 40, fillColor: PdfRgbColor.Yellow)
            .AddEllipse(0, 110, 500, 70, 40, fillColor: PdfRgbColor.Yellow)
            .AddInk(0,
            [
                [new PdfPoint(20, 450), new PdfPoint(50, 470)],
                [new PdfPoint(80, 450), new PdfPoint(110, 470)]
            ])
            .Build());
        PdfArray annotations = Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")]);
        string[] subtypes = [.. annotations.Select(value => Assert.IsType<PdfName>(
            ResolveDictionary(reopened, value)[Name("Subtype")]).ValueAsLatin1())];
        PdfDictionary freeText = ResolveDictionary(reopened, annotations[0]);
        PdfStream freeTextAppearance = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(freeText[Name("AP")])[Name("N")])));
        PdfDictionary fontResources = Assert.IsType<PdfDictionary>(
            Assert.IsType<PdfDictionary>(freeTextAppearance.Dictionary[Name("Resources")])[Name("Font")]);
        PdfDictionary type0 = ResolveDictionary(reopened, fontResources[Name("KpF1")]);
        PdfDictionary ink = ResolveDictionary(reopened, annotations[4]);

        Assert.Equal(["FreeText", "Line", "Square", "Circle", "Ink"], subtypes);
        Assert.Equal("Type0", Assert.IsType<PdfName>(type0[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal(2, Assert.IsType<PdfArray>(ink[Name("InkList")]).Count);
    }

    [Fact]
    public void FreeText_DistinguishesBaseAndVariationSequenceSharingOneGlyph()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, cmap: TrueTypeFontTests.Cmap14()));
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddFreeText(0, 20, 650, 160, 60, "AA\uFE0F", font)
            .Build());
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
        PdfDictionary type0 = ResolveDictionary(reopened,
            Assert.IsType<PdfDictionary>(
                Assert.IsType<PdfDictionary>(appearance.Dictionary[Name("Resources")])[Name("Font")])
                [Name("KpF1")]);
        PdfStream toUnicode = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(type0[Name("ToUnicode")])));

        Assert.Contains("<00010002> Tj", Encoding.ASCII.GetString(appearance.EncodedData.Span));
        Assert.Contains("<0001> <0041>", Encoding.ASCII.GetString(
            PdfStreamDecoder.Decode(toUnicode)));
        Assert.Contains("<0002> <0041FE0F>", Encoding.ASCII.GetString(
            PdfStreamDecoder.Decode(toUnicode)));
    }

    [Fact]
    public void VisualAnnotationArguments_AreRejectedBeforeWriting()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        Assert.Throws<ArgumentException>(() => editor.AddLine(
            0, new PdfPoint(1, 1), new PdfPoint(1, 1)));
        Assert.Throws<ArgumentException>(() => editor.AddInk(0, Array.Empty<PdfPoint>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddRectangle(
            0, 0, 0, 10, 10, lineWidth: 0));
    }

    [Fact]
    public void FreeTextAnnotations_ShareOneDeterministicEmbeddedFontSubset()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] Build() => new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddFreeText(0, 20, 650, 100, 40, "A", font)
            .AddFreeText(0, 140, 650, 100, 40, "AA", font)
            .Build();

        byte[] first = Build();
        PdfDocument reopened = PdfDocument.Open(first);
        PdfArray annotations = Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")]);
        PdfIndirectReference FontReference(PdfObject annotationReference)
        {
            PdfDictionary annotation = ResolveDictionary(reopened, annotationReference);
            PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
                Assert.IsType<PdfIndirectReference>(
                    Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
            PdfDictionary fonts = Assert.IsType<PdfDictionary>(
                Assert.IsType<PdfDictionary>(appearance.Dictionary[Name("Resources")])[Name("Font")]);
            return Assert.IsType<PdfIndirectReference>(fonts[Name("KpF1")]);
        }

        Assert.Equal(FontReference(annotations[0]).ObjectNumber, FontReference(annotations[1]).ObjectNumber);
        Assert.Equal(first, Build());
    }

    [Fact]
    public void ImageStamps_ShareImageAndSoftMaskObjects()
    {
        PdfImage image = PdfImage.FromRgba(1, 1, new byte[] { 20, 40, 60, 96 });
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddImageStamp(0, 20, 600, 100, 50, image)
            .AddImageStamp(0, 140, 600, 100, 50, image)
            .Build());
        PdfArray annotations = Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")]);
        PdfIndirectReference ImageReference(PdfObject annotationReference)
        {
            PdfDictionary annotation = ResolveDictionary(reopened, annotationReference);
            PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
                Assert.IsType<PdfIndirectReference>(
                    Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
            PdfDictionary xobjects = Assert.IsType<PdfDictionary>(
                Assert.IsType<PdfDictionary>(appearance.Dictionary[Name("Resources")])[Name("XObject")]);
            return Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")]);
        }
        PdfIndirectReference firstImage = ImageReference(annotations[0]);
        PdfIndirectReference secondImage = ImageReference(annotations[1]);
        PdfStream imageStream = Assert.IsType<PdfStream>(reopened.Resolve(firstImage));

        Assert.Equal(firstImage.ObjectNumber, secondImage.ObjectNumber);
        Assert.IsType<PdfIndirectReference>(imageStream.Dictionary[Name("SMask")]);
    }

    [Fact]
    public void Build_AppendsUriPageAndNamedDestinationLinks()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddNamedDestination("chapter", 1)
            .Build());
        var metadata = new PdfAnnotationMetadata
        {
            Author = "KillerPDF",
            Subject = "Navigation",
            Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom
        };
        var quad = new PdfTextQuad(
            new PdfPoint(20, 46), new PdfPoint(100, 46),
            new PdfPoint(20, 30), new PdfPoint(100, 30));
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(source)
                .AddUriLink(0, [quad], "https://killerpdf.net/docs",
                    new PdfLinkAppearance(
                        2, PdfLinkBorderStyle.Dashed, [4, 2],
                        new PdfRgbColor(0.1, 0.4, 0.9),
                        PdfLinkHighlightMode.Push), metadata, "Documentation")
                .AddPageLink(0, 20, 60, 80, 16, 1,
                    destination: PdfDestination.FitWidth(700))
                .AddNamedDestinationLink(0, 20, 90, 80, 16, "chapter")
                .Build());

        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        PdfDictionary uri = ResolveDictionary(reopened, annotations[0]);
        PdfDictionary action = Assert.IsType<PdfDictionary>(uri[Name("A")]);
        PdfDictionary border = Assert.IsType<PdfDictionary>(uri[Name("BS")]);
        PdfDictionary page = ResolveDictionary(reopened, annotations[1]);
        PdfArray pageDestination = Assert.IsType<PdfArray>(page[Name("Dest")]);
        PdfDictionary named = ResolveDictionary(reopened, annotations[2]);

        Assert.Equal(3, annotations.Count);
        Assert.Equal("Link", Assert.IsType<PdfName>(uri[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("URI", Assert.IsType<PdfName>(action[Name("S")]).ValueAsLatin1());
        Assert.Equal("D", Assert.IsType<PdfName>(border[Name("S")]).ValueAsLatin1());
        Assert.Equal("P", Assert.IsType<PdfName>(uri[Name("H")]).ValueAsLatin1());
        Assert.Equal(8, Assert.IsType<PdfArray>(uri[Name("QuadPoints")]).Count);
        Assert.Equal(12, Assert.IsType<PdfInteger>(uri[Name("F")]).Value);
        Assert.False(uri.ContainsKey(Name("AP")));
        PdfIndirectReference expectedPage = Pages(reopened)[1].Reference;
        PdfIndirectReference actualPage =
            Assert.IsType<PdfIndirectReference>(pageDestination[0]);
        Assert.Equal(expectedPage.ObjectNumber, actualPage.ObjectNumber);
        Assert.Equal(expectedPage.Generation, actualPage.Generation);
        Assert.Equal("FitH", Assert.IsType<PdfName>(pageDestination[1]).ValueAsLatin1());
        Assert.Equal(
            new byte[] { 0xFE, 0xFF, 0, 0x63, 0, 0x68, 0, 0x61,
                0, 0x70, 0, 0x74, 0, 0x65, 0, 0x72 },
            Assert.IsType<PdfString>(named[Name("Dest")]).Bytes.ToArray());
    }

    [Fact]
    public void LinkArguments_AreRejectedBeforeWriting()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        var editor = new PdfIncrementalAnnotationEditor(source);

        Assert.Throws<ArgumentException>(() => editor.AddUriLink(
            0, 0, 0, 10, 10, "javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() => editor.AddUriLink(
            0, [], "https://killerpdf.net"));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddPageLink(
            0, 0, 0, 10, 10, 1));
        Assert.Throws<ArgumentException>(() => editor.AddNamedDestinationLink(
            0, 0, 0, 10, 10, "missing"));
    }

    [Fact]
    public void Build_RetargetsExistingLinksWithoutChangingTheirGeometry()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddNamedDestination("chapter", 1)
            .AddUriLink(0, 20, 30, 80, 16, "https://old.example")
            .AddPageLink(0, 20, 60, 80, 16, 0)
            .AddNamedDestinationLink(0, 20, 90, 80, 16, "chapter")
            .Build());

        PdfDocument updated = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(source)
                .SetLinkDestinationAt(0, 0, 1, PdfDestination.FitWidth(700))
                .SetLinkNamedDestinationAt(0, 1, "chapter")
                .SetLinkUriAt(0, 2, "https://killerpdf.net")
                .Build());

        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(updated)[0].Page[Name("Annots")]);
        PdfDictionary pageLink = ResolveDictionary(updated, annotations[0]);
        PdfDictionary namedLink = ResolveDictionary(updated, annotations[1]);
        PdfDictionary uriLink = ResolveDictionary(updated, annotations[2]);
        PdfArray destination = Assert.IsType<PdfArray>(pageLink[Name("Dest")]);
        PdfDictionary action = Assert.IsType<PdfDictionary>(uriLink[Name("A")]);

        Assert.Equal("FitH", Assert.IsType<PdfName>(destination[1]).ValueAsLatin1());
        Assert.IsType<PdfString>(namedLink[Name("Dest")]);
        Assert.Equal("URI", Assert.IsType<PdfName>(action[Name("S")]).ValueAsLatin1());
        Assert.False(pageLink.ContainsKey(Name("A")));
        Assert.False(namedLink.ContainsKey(Name("A")));
        Assert.False(uriLink.ContainsKey(Name("Dest")));
        Assert.Equal(20, Assert.IsType<PdfInteger>(
            Assert.IsType<PdfArray>(pageLink[Name("Rect")])[0]).Value);
    }

    [Fact]
    public void Build_AppendsTaggedFileAttachmentUsingExistingFileSpecification()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Tagged attachment",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddAttachment("evidence.txt", "proof"u8.ToArray(),
                "text/plain", "Supporting evidence")
            .AddStructureContainer(PdfStructureType.Document)
            .Build());
        var metadata = new PdfAnnotationMetadata
        {
            Author = "KillerPDF",
            Subject = "Evidence",
            Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom
        };
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(source)
                .AddFileAttachmentAnnotation(0, 20, 30, 24, "EVIDENCE.TXT",
                    "Open the evidence", PdfFileAttachmentIcon.PushPin,
                    new PdfRgbColor(0.2, 0.5, 0.8), metadata)
                .Build());

        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        PdfIndirectReference fileReference =
            Assert.IsType<PdfIndirectReference>(annotation[Name("FS")]);
        PdfDictionary fileSpecification = ResolveDictionary(reopened, fileReference);

        Assert.Equal("FileAttachment", Assert.IsType<PdfName>(
            annotation[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("PushPin", Assert.IsType<PdfName>(
            annotation[Name("Name")]).ValueAsLatin1());
        Assert.Equal(12, Assert.IsType<PdfInteger>(annotation[Name("F")]).Value);
        Assert.IsType<PdfInteger>(annotation[Name("StructParent")]);
        Assert.Equal("Filespec", Assert.IsType<PdfName>(
            fileSpecification[Name("Type")]).ValueAsLatin1());
        Assert.IsType<PdfStream>(reopened.Resolve(Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
    }

    [Fact]
    public void FileAttachmentArguments_RejectMissingFilesAndInvalidGeometry()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("present.txt", "data"u8.ToArray(), "text/plain")
            .Build());
        var editor = new PdfIncrementalAnnotationEditor(source);

        Assert.Throws<ArgumentException>(() => editor.AddFileAttachmentAnnotation(
            0, 0, 0, 24, "missing.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddFileAttachmentAnnotation(
            0, 0, 0, 0, "present.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddFileAttachmentAnnotation(
            0, 0, 0, 24, "present.txt", icon: (PdfFileAttachmentIcon)99));
    }

    [Fact]
    public void SetFileAttachmentIconAt_ChangesOnlyFileAttachmentIcons()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("evidence.txt", "proof"u8.ToArray(), "text/plain")
            .AddFileAttachmentAnnotation(0, 20, 30, 24, "evidence.txt",
                icon: PdfFileAttachmentIcon.Paperclip)
            .AddUriLink(0, 60, 30, 40, 20, "https://example.test")
            .Build());
        int attachmentIndex = Assert.Single(
            PdfAttachmentReader.ReadPageAnnotations(source, 0)).AnnotationIndex;
        int linkIndex = attachmentIndex == 0 ? 1 : 0;

        PdfDocument updated = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(source)
                .SetFileAttachmentIconAt(0, attachmentIndex, PdfFileAttachmentIcon.Tag)
                .Build());
        PdfAttachmentAnnotationInfo attachment = Assert.Single(
            PdfAttachmentReader.ReadPageAnnotations(updated, 0));
        PdfDictionary annotation = ResolveDictionary(updated,
            Assert.IsType<PdfArray>(Pages(updated)[0].Page[Name("Annots")])[attachmentIndex]);

        Assert.Equal("Tag", attachment.Icon);
        Assert.False(annotation.ContainsKey(Name("AP")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfIncrementalAnnotationEditor(updated)
                .SetFileAttachmentIconAt(0, attachmentIndex, (PdfFileAttachmentIcon)99));
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(updated)
                .SetFileAttachmentIconAt(0, linkIndex, PdfFileAttachmentIcon.Graph));
    }

    [Fact]
    public void Build_WritesLifecycleMetadataForEveryVisualAnnotationFamily()
    {
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 10, 20, 30 });
        var metadata = new PdfAnnotationMetadata
        {
            Author = "Editor",
            Subject = "Review",
            CreationDate = new DateTimeOffset(2026, 8, 24, 10, 11, 12,
                TimeSpan.FromHours(-7)),
            ModificationDate = new DateTimeOffset(2026, 8, 24, 11, 12, 13,
                TimeSpan.FromHours(-7)),
            Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked
        };
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddTextNote(0, 10, 10, "Note",
                    annotationMetadata: metadata)
                .AddHighlight(0, 10, 40, 50, 12, "Highlight",
                    annotationMetadata: metadata)
                .AddFreeText(0, 10, 70, 80, 30, "A", font,
                    annotationMetadata: metadata)
                .AddLine(0, new PdfPoint(10, 120), new PdfPoint(80, 125),
                    contents: "Line", annotationMetadata: metadata)
                .AddRectangle(0, 10, 150, 50, 25, contents: "Rectangle",
                    annotationMetadata: metadata)
                .AddInk(0, [new PdfPoint(10, 200), new PdfPoint(30, 210)],
                    contents: "Ink", annotationMetadata: metadata)
                .AddImageStamp(0, 10, 240, 30, 30, image, "Image", metadata)
                .Build());

        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        Assert.Equal(7, annotations.Count);
        foreach (PdfObject reference in annotations)
        {
            PdfDictionary annotation = ResolveDictionary(reopened, reference);
            Assert.Equal(132, Assert.IsType<PdfInteger>(
                annotation[Name("F")]).Value);
            Assert.IsType<PdfString>(annotation[Name("T")]);
            Assert.IsType<PdfString>(annotation[Name("Subj")]);
            Assert.IsType<PdfString>(annotation[Name("CreationDate")]);
            Assert.IsType<PdfString>(annotation[Name("M")]);
        }
    }

    [Fact]
    public void Build_AppendsEveryMultiQuadTextMarkupTypeWithTightGeometry()
    {
        PdfTextQuad first = new(
            new PdfPoint(10, 30), new PdfPoint(60, 35),
            new PdfPoint(11, 20), new PdfPoint(61, 25));
        PdfTextQuad second = new(
            new PdfPoint(15, 55), new PdfPoint(65, 60),
            new PdfPoint(16, 45), new PdfPoint(66, 50));
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddHighlight(0, [first, second], "Highlight")
                .AddUnderline(0, [first, second], "Underline")
                .AddStrikeOut(0, [first, second], "Strikeout")
                .AddSquiggly(0, [first, second], "Squiggly")
                .Build());

        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        Assert.Equal(4, annotations.Count);
        Assert.Equal(["Highlight", "Underline", "StrikeOut", "Squiggly"],
            [.. annotations.Select(reference => Assert.IsType<PdfName>(
                ResolveDictionary(reopened, reference)[Name("Subtype")])
                .ValueAsLatin1())]);
        foreach (PdfObject reference in annotations)
        {
            PdfDictionary annotation = ResolveDictionary(reopened, reference);
            Assert.Equal(16, Assert.IsType<PdfArray>(
                annotation[Name("QuadPoints")]).Count);
            PdfArray rectangle = Assert.IsType<PdfArray>(annotation[Name("Rect")]);
            Assert.Equal(10, Assert.IsType<PdfInteger>(rectangle[0]).Value);
            Assert.Equal(20, Assert.IsType<PdfInteger>(rectangle[1]).Value);
            Assert.Equal(66, Assert.IsType<PdfInteger>(rectangle[2]).Value);
            Assert.Equal(60, Assert.IsType<PdfInteger>(rectangle[3]).Value);
            PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
                Assert.IsType<PdfIndirectReference>(
                    Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
            Assert.NotEmpty(appearance.EncodedData.ToArray());
        }
    }

    [Fact]
    public void MultiQuadTextMarkupRejectsEmptyGeometry()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        Assert.Throws<ArgumentException>(() => editor.AddHighlight(
            0, []));
        Assert.Throws<ArgumentException>(() => editor.AddSquiggly(
            0, []));
    }

    [Fact]
    public void Build_WritesDashedLineShapeAndInkStylesIntoDictionariesAndAppearances()
    {
        double[] dash = [3, 2];
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddLine(0, new PdfPoint(10, 20), new PdfPoint(80, 30),
                    dashPattern: dash)
                .AddRectangle(0, 10, 50, 70, 30, dashPattern: dash)
                .AddEllipse(0, 10, 100, 70, 30, dashPattern: dash)
                .AddInk(0, [new PdfPoint(10, 150), new PdfPoint(80, 160)],
                    dashPattern: dash)
                .Build());

        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        Assert.Equal(4, annotations.Count);
        foreach (PdfObject reference in annotations)
        {
            PdfDictionary annotation = ResolveDictionary(reopened, reference);
            PdfDictionary border = Assert.IsType<PdfDictionary>(annotation[Name("BS")]);
            Assert.Equal("D", Assert.IsType<PdfName>(
                border[Name("S")]).ValueAsLatin1());
            PdfArray dictionaryDash = Assert.IsType<PdfArray>(border[Name("D")]);
            Assert.Equal([3L, 2L], [.. dictionaryDash.Select(value =>
                Assert.IsType<PdfInteger>(value).Value)]);
            PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
                Assert.IsType<PdfIndirectReference>(
                    Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
            Assert.Contains("[3 2] 0 d", Encoding.ASCII.GetString(
                appearance.EncodedData.Span));
        }
    }

    [Fact]
    public void DashedAnnotationsRejectEmptyNegativeAndAllZeroPatterns()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddLine(
            0, new PdfPoint(0, 0), new PdfPoint(10, 10), dashPattern: []));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddRectangle(
            0, 0, 0, 10, 10, dashPattern: [1, -1]));
        Assert.Throws<ArgumentException>(() => editor.AddInk(
            0, [new PdfPoint(0, 0), new PdfPoint(10, 10)], dashPattern: [0, 0]));
    }

    [Fact]
    public void Build_WritesPolylineAndPolygonWithAuthoredEquivalentGeometryAndStyling()
    {
        var metadata = new PdfAnnotationMetadata { Author = "Editor" };
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddPolyline(0,
                    [new PdfPoint(10, 20), new PdfPoint(70, 30), new PdfPoint(90, 50)],
                    lineWidth: 2, contents: "Route",
                    startEnding: PdfLineEndingStyle.Circle,
                    endEnding: PdfLineEndingStyle.ClosedArrow,
                    dashPattern: [4, 2], interiorColor: PdfRgbColor.Yellow,
                    annotationMetadata: metadata,
                    intent: PdfVertexAnnotationIntent.Dimension)
                .AddPolygon(0,
                    [new PdfPoint(20, 100), new PdfPoint(80, 110), new PdfPoint(50, 150)],
                    fillColor: PdfRgbColor.Yellow, lineWidth: 3,
                    contents: "Area", dashPattern: [5, 1],
                    annotationMetadata: metadata,
                    intent: PdfVertexAnnotationIntent.Cloud)
                .Build());

        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        PdfDictionary polyline = ResolveDictionary(reopened, annotations[0]);
        PdfDictionary polygon = ResolveDictionary(reopened, annotations[1]);
        Assert.Equal("PolyLine", Assert.IsType<PdfName>(
            polyline[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("Polygon", Assert.IsType<PdfName>(
            polygon[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal(6, Assert.IsType<PdfArray>(
            polyline[Name("Vertices")]).Count);
        Assert.Equal(6, Assert.IsType<PdfArray>(
            polygon[Name("Vertices")]).Count);
        Assert.Equal(["Circle", "ClosedArrow"],
            [.. Assert.IsType<PdfArray>(polyline[Name("LE")]).Select(value =>
                Assert.IsType<PdfName>(value).ValueAsLatin1())]);
        Assert.Equal("PolyLineDimension", Assert.IsType<PdfName>(
            polyline[Name("IT")]).ValueAsLatin1());
        Assert.Equal("PolygonCloud", Assert.IsType<PdfName>(
            polygon[Name("IT")]).ValueAsLatin1());
        Assert.IsType<PdfArray>(polyline[Name("IC")]);
        Assert.IsType<PdfArray>(polygon[Name("IC")]);
        Assert.IsType<PdfString>(polyline[Name("T")]);
        Assert.IsType<PdfString>(polygon[Name("T")]);
        foreach (PdfDictionary annotation in new[] { polyline, polygon })
        {
            PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
                Assert.IsType<PdfIndirectReference>(
                    Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
            Assert.Contains(" d\n", Encoding.ASCII.GetString(
                appearance.EncodedData.Span));
        }
    }

    [Fact]
    public void VertexAnnotationsRejectMalformedGeometryAndIntent()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));

        Assert.Throws<ArgumentException>(() => editor.AddPolyline(
            0, [new PdfPoint(0, 0)]));
        Assert.Throws<ArgumentException>(() => editor.AddPolygon(
            0, [new PdfPoint(0, 0), new PdfPoint(10, 10)]));
        Assert.Throws<ArgumentException>(() => editor.AddPolyline(0,
            [new PdfPoint(0, 0), new PdfPoint(10, 10)],
            intent: PdfVertexAnnotationIntent.Cloud));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddPolygon(0,
            [new PdfPoint(0, 0), new PdfPoint(10, 10), new PdfPoint(20, 0)],
            intent: (PdfVertexAnnotationIntent)99));
    }

    [Fact]
    public void Build_WritesCaretSymbolMetadataTaggingAndAppearance()
    {
        var metadata = new PdfAnnotationMetadata
        {
            Author = "Editor",
            Subject = "Insertion"
        };
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddCaretAnnotation(0, 20, 30, 24, 30, "Insert here",
                    new PdfRgbColor(0.1, 0.35, 0.9), 0.75,
                    PdfCaretSymbol.Paragraph, metadata)
                .Build());

        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        Assert.Equal("Caret", Assert.IsType<PdfName>(
            annotation[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("P", Assert.IsType<PdfName>(
            annotation[Name("Sy")]).ValueAsLatin1());
        Assert.IsType<PdfString>(annotation[Name("T")]);
        Assert.IsType<PdfString>(annotation[Name("Subj")]);
        PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
        Assert.Contains(" m\n", Encoding.ASCII.GetString(
            appearance.EncodedData.Span));
    }

    [Fact]
    public void CaretRejectsInvalidGeometryOpacityAndSymbol()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddCaret(
            0, 0, 0, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddCaret(
            0, 0, 0, 10, 10, opacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddCaret(
            0, 0, 0, 10, 10, symbol: (PdfCaretSymbol)99));
    }

    [Fact]
    public void Build_WritesSelectedImageStampIconAndRejectsUnknownValues()
    {
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 10, 20, 30 });
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(source)
                .AddImageStamp(0, 10, 10, 30, 30, image,
                    icon: PdfStampIcon.Confidential)
                .Build());
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);

        Assert.Equal("Confidential", Assert.IsType<PdfName>(
            annotation[Name("Name")]).ValueAsLatin1());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfIncrementalAnnotationEditor(source).AddImageStamp(
                0, 10, 10, 30, 30, image, icon: (PdfStampIcon)99));
    }

    [Fact]
    public void Build_AuthorizesExactMinimumVersionsForOpacityAndVertexAnnotations()
    {
        byte[] source = new PdfDocumentBuilder(PdfVersion.Pdf10)
            .AddBlankPage().Build();
        PdfDocument opacity = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
                .AddHighlight(0, 10, 10, 30, 10)
                .Build());
        PdfDocument vertex = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
                .AddPolyline(0,
                    [new PdfPoint(10, 10), new PdfPoint(30, 30)])
                .Build());

        Assert.Equal("1.4", Assert.IsType<PdfName>(ResolveDictionary(
            opacity, opacity.Trailer[Name("Root")])[Name("Version")]).ValueAsLatin1());
        Assert.Equal("1.5", Assert.IsType<PdfName>(ResolveDictionary(
            vertex, vertex.Trailer[Name("Root")])[Name("Version")]).ValueAsLatin1());
        Assert.Equal(PdfVersion.Pdf10, opacity.Header.Version);
        Assert.Equal(PdfVersion.Pdf10, vertex.Header.Version);
    }

    [Fact]
    public void Build_RejectsMalformedExistingCatalogVersionBeforeAnnotationUpgrade()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            source.Trailer[Name("Root")]);
        var setup = new PdfIncrementalUpdateBuilder(source);
        setup.ReplaceObject(catalogReference.ObjectNumber,
            Replace(ResolveDictionary(source, catalogReference), Name("Version"),
                Name("1.9")));

        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(setup.Build()))
                .AddHighlight(0, 10, 10, 30, 10)
                .Build());
    }

    [Fact]
    public void Build_WritesMultiQuadRedactionWithRepeatedBaselineOverlayAndPdf17Upgrade()
    {
        PdfTextQuad first = Quad(10, 20, 80, 14);
        PdfTextQuad second = Quad(10, 50, 100, 14);
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder(PdfVersion.Pdf10).AddBlankPage().Build()))
                .AddRedactionMark(0, [first, second], "Remove account number",
                    overlayText: "REDACTED", repeatOverlayText: true,
                    overlayAlignment: PdfTextAlignment.Right)
                .Build());

        PdfDictionary catalog = ResolveDictionary(reopened,
            reopened.Trailer[Name("Root")]);
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        Assert.Equal("1.7", Assert.IsType<PdfName>(
            catalog[Name("Version")]).ValueAsLatin1());
        Assert.Equal("Redact", Assert.IsType<PdfName>(
            annotation[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal(16, Assert.IsType<PdfArray>(
            annotation[Name("QuadPoints")]).Count);
        Assert.True(Assert.IsType<PdfBoolean>(annotation[Name("Repeat")]).Value);
        Assert.Equal(2, Assert.IsType<PdfInteger>(annotation[Name("Q")]).Value);
        Assert.IsType<PdfString>(annotation[Name("DA")]);
        PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
        string content = Encoding.ASCII.GetString(appearance.EncodedData.Span);
        Assert.Equal(2, content.Split("(REDACTED) Tj", StringSplitOptions.None).Length - 1);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(
            Assert.IsType<PdfDictionary>(appearance.Dictionary[Name("Resources")])
                [Name("Font")]);
        Assert.Equal("Type1", Assert.IsType<PdfName>(ResolveDictionary(
            reopened, fonts[Name("Helv")])[Name("Subtype")]).ValueAsLatin1());
    }

    [Fact]
    public void Build_EmbedsTrueTypeFontForUnicodeRedactionOverlay()
    {
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddRedactionMark(0,
                    [Quad(10, 20, 80, 14)],
                    overlayText: "AA", overlayFont: font)
                .Build());
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(
            Assert.IsType<PdfDictionary>(appearance.Dictionary[Name("Resources")])
                [Name("Font")]);
        PdfDictionary type0 = ResolveDictionary(reopened, fonts.Single().Value);
        Assert.Equal("Type0", Assert.IsType<PdfName>(
            type0[Name("Subtype")]).ValueAsLatin1());
        Assert.Contains("<00010001> Tj", Encoding.ASCII.GetString(
            appearance.EncodedData.Span));
    }

    [Fact]
    public void RedactionOverlayRequiresEmbeddedFontWhenExistingXmpClaimsPdfA4()
    {
        const string xmp = """
            <?xml version="1.0" encoding="utf-8"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">
                  <pdfaid:part>4</pdfaid:part>
                  <pdfaid:rev>2020</pdfaid:rev>
                </rdf:Description>
              </rdf:RDF>
            </x:xmpmeta>
            """;
        PdfDocument initial = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            initial.Trailer[Name("Root")]);
        var setup = new PdfIncrementalUpdateBuilder(initial);
        PdfIndirectReference metadata = setup.AddObject(new PdfStream(
            new PdfDictionary([
                new(Name("Type"), Name("Metadata")),
                new(Name("Subtype"), Name("XML"))
            ]), Encoding.UTF8.GetBytes(xmp)));
        setup.ReplaceObject(catalogReference.ObjectNumber,
            Replace(ResolveDictionary(initial, catalogReference),
                Name("Metadata"), metadata));
        PdfDocument pdfA4 = PdfDocument.Open(setup.Build());
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));

        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(pdfA4).AddRedactionMark(0,
                [Quad(10, 20, 80, 14)], overlayText: "REDACTED"));
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(pdfA4).AddRedactionMark(0,
                [Quad(10, 20, 80, 14)], overlayText: "AA", overlayFont: font)
                .Build());
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        Assert.Equal("Redact", Assert.IsType<PdfName>(
            annotation[Name("Subtype")]).ValueAsLatin1());
    }

    [Fact]
    public void RedactionRejectsInvalidGeometryOverlayAndPresentationArguments()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        Assert.Throws<ArgumentException>(() => editor.AddRedactionMark(
            0, []));
        Assert.Throws<ArgumentException>(() => editor.AddRedactionMark(0,
            [Quad(0, 0, 10, 10)], overlayText: "é"));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddRedactionMark(0,
            [Quad(0, 0, 10, 10)], opacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddRedactionMark(0,
            [Quad(0, 0, 10, 10)], overlayFontSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddRedactionMark(0,
            [Quad(0, 0, 10, 10)],
            overlayAlignment: (PdfTextAlignment)99));
    }

    [Fact]
    public void Build_WritesLineEndingsInteriorColorIntentAndExpandedAppearanceBounds()
    {
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddLine(0, new PdfPoint(20, 30), new PdfPoint(120, 80),
                    lineWidth: 3, dashPattern: [4, 2],
                    startEnding: PdfLineEndingStyle.Circle,
                    endEnding: PdfLineEndingStyle.ClosedArrow,
                    interiorColor: PdfRgbColor.Yellow,
                    intent: PdfLineAnnotationIntent.Arrow)
                .Build());
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        Assert.Equal(["Circle", "ClosedArrow"],
            [.. Assert.IsType<PdfArray>(annotation[Name("LE")]).Select(value =>
                Assert.IsType<PdfName>(value).ValueAsLatin1())]);
        Assert.Equal("LineArrow", Assert.IsType<PdfName>(
            annotation[Name("IT")]).ValueAsLatin1());
        Assert.IsType<PdfArray>(annotation[Name("IC")]);
        PdfArray rectangle = Assert.IsType<PdfArray>(annotation[Name("Rect")]);
        Assert.True(Assert.IsType<PdfInteger>(rectangle[0]).Value < 20);
        Assert.True(Assert.IsType<PdfInteger>(rectangle[1]).Value < 30);
        PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
        string content = Encoding.ASCII.GetString(appearance.EncodedData.Span);
        Assert.Contains(" c\n", content);
        Assert.Contains("h\nB\n", content);
    }

    [Fact]
    public void LineRejectsUndefinedEndingsAndIntent()
    {
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddLine(
            0, new PdfPoint(0, 0), new PdfPoint(10, 10),
            startEnding: (PdfLineEndingStyle)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddLine(
            0, new PdfPoint(0, 0), new PdfPoint(10, 10),
            intent: (PdfLineAnnotationIntent)99));
    }

    [Fact]
    public void Build_WritesEditableCalibratedLineMeasurement()
    {
        var profile = new PdfMeasurementProfile("Site plan", 0.125, "ft", 3);
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddLine(0, new PdfPoint(20, 30), new PdfPoint(120, 30),
                    contents: "12.500 ft", measurement: profile)
                .Build());

        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        Assert.Equal("LineDimension", Assert.IsType<PdfName>(
            annotation[Name("IT")]).ValueAsLatin1());
        PdfDictionary measure = Assert.IsType<PdfDictionary>(annotation[Name("Measure")]);
        Assert.Equal("RL", Assert.IsType<PdfName>(measure[Name("Subtype")]).ValueAsLatin1());
        PdfDictionary distance = Assert.IsType<PdfDictionary>(
            Assert.IsType<PdfArray>(measure[Name("D")])[0]);
        Assert.Equal(0.125, Assert.IsType<PdfReal>(distance[Name("C")]).Value);
        Assert.Equal(1000, Assert.IsType<PdfInteger>(distance[Name("D")]).Value);
        ReadOnlySpan<byte> units = Assert.IsType<PdfString>(distance[Name("U")]).Bytes.Span;
        Assert.Equal("ft", Encoding.BigEndianUnicode.GetString(units[2..]));
        _ = new PdfIncrementalPageEditor(reopened);

        var editor = new PdfIncrementalAnnotationEditor(reopened);
        Assert.Throws<ArgumentException>(() => editor.AddLine(
            0, new PdfPoint(0, 0), new PdfPoint(10, 10),
            intent: PdfLineAnnotationIntent.Arrow, measurement: profile));
    }

    [Fact]
    public void Build_WritesCalibratedPolylineAndPolygonMeasurements()
    {
        var profile = new PdfMeasurementProfile("Plan", 0.5, "m", 2);
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 200).Build()))
            .AddPolyline(0, [new PdfPoint(10, 10), new PdfPoint(50, 10),
                new PdfPoint(50, 60)], measurement: profile)
            .AddPolygon(0, [new PdfPoint(80, 10), new PdfPoint(120, 10),
                new PdfPoint(120, 60)], measurement: profile)
            .Build());
        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        PdfDictionary polyline = ResolveDictionary(reopened, annotations[0]);
        PdfDictionary polygon = ResolveDictionary(reopened, annotations[1]);

        Assert.Equal("PolyLineDimension", Assert.IsType<PdfName>(
            polyline[Name("IT")]).ValueAsLatin1());
        Assert.False(Assert.IsType<PdfDictionary>(polyline[Name("Measure")])
            .ContainsKey(Name("A")));
        PdfDictionary measure = Assert.IsType<PdfDictionary>(polygon[Name("Measure")]);
        PdfDictionary area = Assert.IsType<PdfDictionary>(
            Assert.IsType<PdfArray>(measure[Name("A")])[0]);
        Assert.Equal(0.25, Assert.IsType<PdfReal>(area[Name("C")]).Value);
    }

    [Fact]
    public void Build_WritesEditableAngleMeasurementWithCalculatedLabel()
    {
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 200).Build()))
            .AddAngleMeasurement(0, new PdfPoint(50, 100), new PdfPoint(50, 50),
                new PdfPoint(100, 50), precision: 2)
            .Build());

        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        Assert.Equal("PolyLine", Assert.IsType<PdfName>(
            annotation[Name("Subtype")]).ValueAsLatin1());
        Assert.Equal("PolyLineDimension", Assert.IsType<PdfName>(
            annotation[Name("IT")]).ValueAsLatin1());
        Assert.Equal(6, Assert.IsType<PdfArray>(annotation[Name("Vertices")]).Count);
        ReadOnlySpan<byte> contents = Assert.IsType<PdfString>(
            annotation[Name("Contents")]).Bytes.Span;
        Assert.Equal("90.00 deg", Encoding.BigEndianUnicode.GetString(contents[2..]));
        Assert.False(annotation.ContainsKey(Name("Measure")));

        var editor = new PdfIncrementalAnnotationEditor(reopened);
        Assert.Throws<ArgumentException>(() => editor.AddAngleMeasurement(0,
            new PdfPoint(0, 0), new PdfPoint(0, 0), new PdfPoint(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddAngleMeasurement(0,
            new PdfPoint(0, 1), new PdfPoint(0, 0), new PdfPoint(1, 0), precision: 11));
    }

    [Fact]
    public void Build_WritesCalculatedPerimeterAndAreaMeasurements()
    {
        var profile = new PdfMeasurementProfile("Plan", 0.5, "m", 2);
        PdfPoint[] triangle = [new(10, 10), new(13, 10), new(13, 14)];
        PdfDocument reopened = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 200).Build()))
            .AddPerimeterMeasurement(0, triangle, profile)
            .AddAreaMeasurement(0, triangle, profile)
            .Build());
        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        PdfDictionary perimeter = ResolveDictionary(reopened, annotations[0]);
        PdfDictionary area = ResolveDictionary(reopened, annotations[1]);

        Assert.Equal(8, Assert.IsType<PdfArray>(perimeter[Name("Vertices")]).Count);
        Assert.Equal("6.00 m", Encoding.BigEndianUnicode.GetString(
            Assert.IsType<PdfString>(perimeter[Name("Contents")]).Bytes.Span[2..]));
        Assert.Equal("1.50 m^2", Encoding.BigEndianUnicode.GetString(
            Assert.IsType<PdfString>(area[Name("Contents")]).Bytes.Span[2..]));
        Assert.True(Assert.IsType<PdfDictionary>(area[Name("Measure")])
            .ContainsKey(Name("A")));
    }

    [Fact]
    public void Build_WritesAlignedDashedCalloutFreeTextWithExpandedBounds()
    {
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
                .AddFreeText(0, 100, 100, 120, 50, "AA", font,
                    alignment: PdfTextAlignment.Right,
                    dashPattern: [3, 2], intent: PdfFreeTextIntent.Callout,
                    calloutLine:
                    [
                        new PdfPoint(20, 40),
                        new PdfPoint(60, 70),
                        new PdfPoint(100, 100)
                    ],
                    calloutEnding: PdfLineEndingStyle.ClosedArrow)
                .Build());
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.IsType<PdfArray>(Pages(reopened)[0].Page[Name("Annots")])[0]);
        Assert.Equal(2, Assert.IsType<PdfInteger>(annotation[Name("Q")]).Value);
        Assert.Equal("FreeTextCallout", Assert.IsType<PdfName>(
            annotation[Name("IT")]).ValueAsLatin1());
        Assert.Equal("ClosedArrow", Assert.IsType<PdfName>(
            annotation[Name("LE")]).ValueAsLatin1());
        Assert.Equal(6, Assert.IsType<PdfArray>(annotation[Name("CL")]).Count);
        Assert.Equal("D", Assert.IsType<PdfName>(Assert.IsType<PdfDictionary>(
            annotation[Name("BS")])[Name("S")]).ValueAsLatin1());
        PdfArray rectangle = Assert.IsType<PdfArray>(annotation[Name("Rect")]);
        Assert.True(Assert.IsType<PdfInteger>(rectangle[0]).Value < 20);
        Assert.True(Assert.IsType<PdfInteger>(rectangle[1]).Value < 40);
        PdfStream appearance = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(
                Assert.IsType<PdfDictionary>(annotation[Name("AP")])[Name("N")])));
        string content = Encoding.ASCII.GetString(appearance.EncodedData.Span);
        Assert.Contains("[3 2] 0 d", content);
        Assert.Contains("h\nS\n", content);
        Assert.Contains(" Tm\n<00010001> Tj", content);
    }

    [Fact]
    public void FreeTextRejectsInvalidAlignmentIntentEndingAndCalloutCombinations()
    {
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        var editor = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build()));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddFreeText(
            0, 0, 0, 50, 20, "A", font,
            alignment: (PdfTextAlignment)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddFreeText(
            0, 0, 0, 50, 20, "A", font,
            intent: (PdfFreeTextIntent)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddFreeText(
            0, 0, 0, 50, 20, "A", font,
            calloutEnding: (PdfLineEndingStyle)99));
        Assert.Throws<ArgumentException>(() => editor.AddFreeText(
            0, 0, 0, 50, 20, "A", font,
            intent: PdfFreeTextIntent.Callout));
        Assert.Throws<ArgumentException>(() => editor.AddFreeText(
            0, 0, 0, 50, 20, "A", font,
            calloutLine: [new PdfPoint(0, 0), new PdfPoint(10, 10)]));
    }

    [Fact]
    public void Build_WritesNamedStatefulRepliesAndReciprocalPopup()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Existing", name: "existing")
            .Build());
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(source)
                .AddTextNote(0, 40, 40, "First reply",
                    icon: PdfTextNoteIcon.Comment,
                    state: PdfTextNoteState.Accepted,
                    name: "new-note", inReplyTo: "existing")
                .AddTextNote(0, 70, 70, "Grouped reply",
                    icon: PdfTextNoteIcon.Key,
                    name: "grouped", inReplyTo: "new-note",
                    replyType: PdfAnnotationReplyType.Group,
                    popup: new PdfAnnotationPopup(100, 100, 180, 90, open: true))
                .Build());

        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        Assert.Equal(4, annotations.Count);
        PdfIndirectReference existingReference = Assert.IsType<PdfIndirectReference>(annotations[0]);
        PdfIndirectReference firstReference = Assert.IsType<PdfIndirectReference>(annotations[1]);
        PdfIndirectReference groupedReference = Assert.IsType<PdfIndirectReference>(annotations[2]);
        PdfIndirectReference popupReference = Assert.IsType<PdfIndirectReference>(annotations[3]);
        PdfDictionary first = ResolveDictionary(reopened, firstReference);
        PdfDictionary grouped = ResolveDictionary(reopened, groupedReference);
        PdfDictionary popup = ResolveDictionary(reopened, popupReference);
        Assert.Equal("Comment", Assert.IsType<PdfName>(
            first[Name("Name")]).ValueAsLatin1());
        Assert.Equal("Accepted", Assert.IsType<PdfName>(
            first[Name("State")]).ValueAsLatin1());
        Assert.Equal("Review", Assert.IsType<PdfName>(
            first[Name("StateModel")]).ValueAsLatin1());
        Assert.Equal(existingReference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(first[Name("IRT")]).ObjectNumber);
        Assert.Equal(firstReference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(grouped[Name("IRT")]).ObjectNumber);
        Assert.Equal("Group", Assert.IsType<PdfName>(
            grouped[Name("RT")]).ValueAsLatin1());
        Assert.Equal(popupReference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(grouped[Name("Popup")]).ObjectNumber);
        Assert.Equal(groupedReference.ObjectNumber,
            Assert.IsType<PdfIndirectReference>(popup[Name("Parent")]).ObjectNumber);
        Assert.True(Assert.IsType<PdfBoolean>(popup[Name("Open")]).Value);
    }

    [Fact]
    public void TextNoteRejectsDuplicateNamesMissingTargetsAndInvalidWorkflowValues()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddTextNote(0, 0, 0, "Existing", name: "existing")
            .Build());
        var editor = new PdfIncrementalAnnotationEditor(source);
        Assert.Throws<ArgumentException>(() => editor.AddTextNote(
            0, 10, 10, "Duplicate", name: "existing"));
        Assert.Throws<ArgumentException>(() => editor.AddTextNote(
            0, 10, 10, "Missing", inReplyTo: "missing"));
        Assert.Throws<ArgumentException>(() => editor.AddTextNote(
            0, 10, 10, "Group", replyType: PdfAnnotationReplyType.Group));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddTextNote(
            0, 10, 10, "Icon", icon: (PdfTextNoteIcon)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddTextNote(
            0, 10, 10, "State", state: (PdfTextNoteState)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddTextNote(
            0, 10, 10, "Reply", replyType: (PdfAnnotationReplyType)99));
    }

    [Fact]
    public void RemoveAnnotation_RemovesNamedNoteAndReciprocalPopupOnly()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Keep", name: "keep")
            .AddTextNote(0, 40, 40, "Remove", name: "remove",
                popup: new PdfAnnotationPopup(100, 100, 180, 90, open: true))
            .Build();
        byte[] output = new PdfIncrementalAnnotationEditor(PdfDocument.Open(sourceBytes))
                .RemoveAnnotation(0, "remove")
                .Build();
        PdfDocument reopened = PdfDocument.Open(output);
        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);

        Assert.Single(annotations);
        PdfDictionary retained = ResolveDictionary(reopened, annotations[0]);
        ReadOnlySpan<byte> retainedName = Assert.IsType<PdfString>(
            retained[Name("NM")]).Bytes.Span;
        Assert.Equal("keep", Encoding.BigEndianUnicode.GetString(
            retainedName[2..]));
        Assert.True(output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
    }

    [Fact]
    public void RemoveAnnotation_RejectsMissingDuplicateAndOrphaningReplyTargets()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Parent", name: "parent")
            .AddTextNote(0, 40, 40, "Reply", name: "reply",
                inReplyTo: "parent")
            .Build());
        var editor = new PdfIncrementalAnnotationEditor(source);
        Assert.Throws<ArgumentException>(() => editor.RemoveAnnotation(0, "missing"));
        editor.RemoveAnnotation(0, "parent");
        Assert.Throws<ArgumentException>(() => editor.RemoveAnnotation(0, "parent"));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => editor.Build());
        Assert.Contains("would orphan a retained /IRT relationship",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveAnnotation_PrunesTaggedParentTreeAndDocumentChild()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Accessible note", name: "remove")
            .AddStructureContainer(PdfStructureType.Document)
            .Build();
        byte[] output = new PdfIncrementalAnnotationEditor(PdfDocument.Open(sourceBytes))
                .RemoveAnnotation(0, "remove")
                .Build();
        PdfDocument reopened = PdfDocument.Open(output);
        PdfDictionary catalog = ResolveDictionary(reopened,
            reopened.Trailer[Name("Root")]);
        PdfDictionary root = ResolveDictionary(reopened,
            catalog[Name("StructTreeRoot")]);
        PdfDictionary parentTree = ResolveDictionary(reopened,
            root[Name("ParentTree")]);
        PdfObject documentValue = root[Name("K")];
        if (documentValue is PdfArray documentKids)
            documentValue = documentKids[0];
        PdfDictionary document = ResolveDictionary(reopened, documentValue);

        Assert.Empty(Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]));
        Assert.Empty(Assert.IsType<PdfArray>(parentTree[Name("Nums")]));
        Assert.False(document.ContainsKey(Name("K")));
        Assert.True(output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
    }

    [Fact]
    public void RemoveAnnotation_PrunesPdfUaStructParentMappingElementAndObjr()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Remove accessible annotation",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Accessible note", name: "remove")
            .AddStructureContainer(PdfStructureType.Document)
            .Build();
        PdfDocument initial = PdfDocument.Open(sourceBytes);
        PdfDictionary initialAnnotation = ResolveDictionary(initial,
            Assert.IsType<PdfArray>(Pages(initial)[0].Page[Name("Annots")])[0]);
        long removedKey = Assert.IsType<PdfInteger>(
            initialAnnotation[Name("StructParent")]).Value;
        PdfDictionary initialCatalog = ResolveDictionary(initial,
            initial.Trailer[Name("Root")]);
        PdfDictionary initialRoot = ResolveDictionary(initial,
            initialCatalog[Name("StructTreeRoot")]);
        PdfArray initialNumbers = Assert.IsType<PdfArray>(ResolveDictionary(
            initial, initialRoot[Name("ParentTree")])[Name("Nums")]);
        int keyIndex = Enumerable.Range(0, initialNumbers.Count / 2)
            .Select(index => index * 2)
            .Single(index => Assert.IsType<PdfInteger>(
                initialNumbers[index]).Value == removedKey);
        PdfIndirectReference removedElement = Assert.IsType<PdfIndirectReference>(
            initialNumbers[keyIndex + 1]);

        byte[] output = new PdfIncrementalAnnotationEditor(initial)
            .RemoveAnnotation(0, "remove").Build();
        PdfDocument reopened = PdfDocument.Open(output);
        PdfDictionary catalog = ResolveDictionary(reopened,
            reopened.Trailer[Name("Root")]);
        PdfDictionary root = ResolveDictionary(reopened,
            catalog[Name("StructTreeRoot")]);
        PdfArray numbers = Assert.IsType<PdfArray>(ResolveDictionary(
            reopened, root[Name("ParentTree")])[Name("Nums")]);
        PdfObject documentValue = root[Name("K")];
        if (documentValue is PdfArray rootKids) documentValue = rootKids[0];
        PdfDictionary document = ResolveDictionary(reopened, documentValue);
        IEnumerable<PdfObject> documentKids = document.TryGetValue(
                Name("K"), out PdfObject? kidsValue)
            ? kidsValue is PdfArray kids ? kids : [kidsValue]
            : [];

        Assert.Empty(Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]));
        Assert.DoesNotContain(Enumerable.Range(0, numbers.Count / 2)
            .Select(index => Assert.IsType<PdfInteger>(numbers[index * 2]).Value),
            key => key == removedKey);
        Assert.DoesNotContain(documentKids, kid => kid is PdfIndirectReference reference
            && reference.ObjectNumber == removedElement.ObjectNumber
            && reference.Generation == removedElement.Generation);
        Assert.True(output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
    }

    [Fact]
    public void Build_ComposesTaggedRemovalAndSameNameReplacementInOneRevision()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Replace accessible annotation",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Old accessible note", name: "review")
            .AddStructureContainer(PdfStructureType.Document)
            .Build();
        PdfDocument initial = PdfDocument.Open(sourceBytes);
        PdfDictionary oldAnnotation = ResolveDictionary(initial,
            Assert.IsType<PdfArray>(Pages(initial)[0].Page[Name("Annots")])[0]);
        long oldKey = Assert.IsType<PdfInteger>(
            oldAnnotation[Name("StructParent")]).Value;

        byte[] output = new PdfIncrementalAnnotationEditor(initial)
            .RemoveAnnotation(0, "review")
            .AddTextNote(0, 40, 40, "New accessible note", name: "review")
            .Build();
        PdfDocument reopened = PdfDocument.Open(output);
        PdfArray annotations = Assert.IsType<PdfArray>(
            Pages(reopened)[0].Page[Name("Annots")]);
        PdfDictionary replacement = ResolveDictionary(reopened,
            Assert.Single(annotations));
        long newKey = Assert.IsType<PdfInteger>(
            replacement[Name("StructParent")]).Value;
        PdfDictionary catalog = ResolveDictionary(reopened,
            reopened.Trailer[Name("Root")]);
        PdfDictionary root = ResolveDictionary(reopened,
            catalog[Name("StructTreeRoot")]);
        PdfArray numbers = Assert.IsType<PdfArray>(ResolveDictionary(
            reopened, root[Name("ParentTree")])[Name("Nums")]);
        long[] keys = [.. Enumerable.Range(0, numbers.Count / 2).Select(index => Assert.IsType<PdfInteger>(numbers[index * 2]).Value)];

        Assert.NotEqual(oldKey, newKey);
        Assert.DoesNotContain(oldKey, keys);
        Assert.Contains(newKey, keys);
        Assert.Equal("review", Encoding.BigEndianUnicode.GetString(
            Assert.IsType<PdfString>(replacement[Name("NM")]).Bytes.Span[2..]));
        Assert.True(output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
    }

    [Fact]
    public void RemoveAnnotationAt_RemovesUnnamedAnnotationAndRejectsPopupIndex()
    {
        PdfDocument baseDocument = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        var setup = new PdfIncrementalUpdateBuilder(baseDocument);
        var (Reference, Page) = Pages(baseDocument)[0];
        PdfIndirectReference unnamed = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Square")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(10), new PdfInteger(10),
                new PdfInteger(40), new PdfInteger(40)
            ])),
            new(Name("P"), Reference),
            new(Name("F"), new PdfInteger(4))
        ]));
        setup.ReplaceObject(Reference.ObjectNumber,
            new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(
                    Name("Annots"), new PdfArray([unnamed])))));
        PdfDocument unnamedSource = PdfDocument.Open(setup.Build());
        PdfDocument removed = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(unnamedSource)
                .RemoveAnnotationAt(0, 0).Build());
        Assert.Empty(Assert.IsType<PdfArray>(
            Pages(removed)[0].Page[Name("Annots")]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PdfIncrementalAnnotationEditor(unnamedSource)
                .RemoveAnnotationAt(0, 1));

        PdfDocument popupSource = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Popup parent", name: "parent",
                popup: new PdfAnnotationPopup(50, 50, 100, 80))
            .Build());
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(popupSource)
                .RemoveAnnotationAt(0, 1));
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(popupSource)
                .SetAnnotationContentsAt(0, 1, "Invalid popup contents"));
    }

    [Fact]
    public void SetAnnotationContentsAndMetadata_ReplaceOnlyLifecycleFields()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Before", name: "review",
                annotationMetadata: new PdfAnnotationMetadata
                {
                    Author = "Before author",
                    Subject = "Before subject",
                    Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom
                })
            .Build();
        byte[] output = new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(sourceBytes))
            .SetAnnotationContents(0, "review", "After")
            .SetAnnotationMetadata(0, "review", new PdfAnnotationMetadata
            {
                Author = "After author",
                Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked
            })
            .Build();
        PdfDocument reopened = PdfDocument.Open(output);
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.Single(Assert.IsType<PdfArray>(
                Pages(reopened)[0].Page[Name("Annots")])));

        Assert.Equal("After", Encoding.BigEndianUnicode.GetString(
            Assert.IsType<PdfString>(annotation[Name("Contents")]).Bytes.Span[2..]));
        Assert.Equal("After author", Encoding.BigEndianUnicode.GetString(
            Assert.IsType<PdfString>(annotation[Name("T")]).Bytes.Span[2..]));
        Assert.False(annotation.ContainsKey(Name("Subj")));
        Assert.Equal((long)(PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked),
            Assert.IsType<PdfInteger>(annotation[Name("F")]).Value);
        Assert.True(annotation.ContainsKey(Name("AP")));
        Assert.True(output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
    }

    [Fact]
    public void SetAnnotationContents_AllowsRemovalAndRejectsRemovedTarget()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Before", name: "review")
            .Build());
        PdfDocument reopened = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(source)
                .SetAnnotationContents(0, "review", null)
                .Build());
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.Single(Assert.IsType<PdfArray>(
                Pages(reopened)[0].Page[Name("Annots")])));
        Assert.False(annotation.ContainsKey(Name("Contents")));

        var editor = new PdfIncrementalAnnotationEditor(source)
            .RemoveAnnotation(0, "review");
        Assert.Throws<InvalidOperationException>(() =>
            editor.SetAnnotationContents(0, "review", "After"));
        var reverse = new PdfIncrementalAnnotationEditor(source)
            .SetAnnotationContents(0, "review", "After");
        Assert.Throws<InvalidOperationException>(() =>
            reverse.RemoveAnnotation(0, "review"));
    }

    [Fact]
    public void SetAnnotationContentsAtAndMetadataAt_UpdateUnnamedAnnotation()
    {
        PdfDocument baseDocument = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        var setup = new PdfIncrementalUpdateBuilder(baseDocument);
        var (Reference, Page) = Pages(baseDocument)[0];
        PdfIndirectReference unnamed = setup.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Annot")),
            new(Name("Subtype"), Name("Square")),
            new(Name("Rect"), new PdfArray([
                new PdfInteger(10), new PdfInteger(10),
                new PdfInteger(40), new PdfInteger(40)
            ])),
            new(Name("P"), Reference),
            new(Name("F"), new PdfInteger(4))
        ]));
        setup.ReplaceObject(Reference.ObjectNumber,
            new PdfDictionary(Page.Append(
                new KeyValuePair<PdfName, PdfObject>(
                    Name("Annots"), new PdfArray([unnamed])))));
        byte[] sourceBytes = setup.Build();
        byte[] output = new PdfIncrementalAnnotationEditor(
                PdfDocument.Open(sourceBytes))
            .SetAnnotationContentsAt(0, 0, "Unnamed contents")
            .SetAnnotationMetadataAt(0, 0, new PdfAnnotationMetadata
            {
                Author = "Editor",
                Flags = PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked
            })
            .Build();
        PdfDocument reopened = PdfDocument.Open(output);
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.Single(Assert.IsType<PdfArray>(
                Pages(reopened)[0].Page[Name("Annots")])));

        Assert.Equal("Unnamed contents", Encoding.BigEndianUnicode.GetString(
            Assert.IsType<PdfString>(annotation[Name("Contents")]).Bytes.Span[2..]));
        Assert.Equal("Editor", Encoding.BigEndianUnicode.GetString(
            Assert.IsType<PdfString>(annotation[Name("T")]).Bytes.Span[2..]));
        Assert.True(output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
    }

    [Fact]
    public void SetAnnotationContents_SynchronizesTaggedAlternateDescription()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Update accessible annotation",
                Language = "en-US"
            })
            .EnablePdfUa2Conformance()
            .AddBlankPage()
            .AddTextNote(0, 10, 10, "Before", name: "review")
            .AddStructureContainer(PdfStructureType.Document)
            .Build();
        PdfDocument initial = PdfDocument.Open(sourceBytes);
        byte[] output = new PdfIncrementalAnnotationEditor(initial)
            .SetAnnotationContents(0, "review", "After accessible description")
            .Build();
        PdfDocument reopened = PdfDocument.Open(output);
        PdfDictionary annotation = ResolveDictionary(reopened,
            Assert.Single(Assert.IsType<PdfArray>(
                Pages(reopened)[0].Page[Name("Annots")])));
        long key = Assert.IsType<PdfInteger>(
            annotation[Name("StructParent")]).Value;
        PdfDictionary catalog = ResolveDictionary(reopened,
            reopened.Trailer[Name("Root")]);
        PdfDictionary root = ResolveDictionary(reopened,
            catalog[Name("StructTreeRoot")]);
        PdfArray numbers = Assert.IsType<PdfArray>(ResolveDictionary(
            reopened, root[Name("ParentTree")])[Name("Nums")]);
        int index = Enumerable.Range(0, numbers.Count / 2)
            .Select(value => value * 2)
            .Single(value => Assert.IsType<PdfInteger>(numbers[value]).Value == key);
        PdfDictionary element = ResolveDictionary(reopened, numbers[index + 1]);

        Assert.Equal("After accessible description",
            Encoding.BigEndianUnicode.GetString(Assert.IsType<PdfString>(
                annotation[Name("Contents")]).Bytes.Span[2..]));
        Assert.Equal("After accessible description",
            Encoding.BigEndianUnicode.GetString(Assert.IsType<PdfString>(
                element[Name("Alt")]).Bytes.Span[2..]));
        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalAnnotationEditor(initial)
                .SetAnnotationContents(0, "review", null).Build());
        Assert.True(output.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
    }

    private static IReadOnlyList<(PdfIndirectReference Reference, PdfDictionary Page)> Pages(
        PdfDocument document)
    {
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        return [.. Assert.IsType<PdfArray>(pages[Name("Kids")]).Select(value =>
        {
            var reference = Assert.IsType<PdfIndirectReference>(value);
            return (reference, ResolveDictionary(document, reference));
        })];
    }

    private static PdfTextQuad Quad(
        double x, double y, double width, double height) =>
        new(new PdfPoint(x, y + height), new PdfPoint(x + width, y + height),
            new PdfPoint(x, y), new PdfPoint(x + width, y));

    private static PdfDictionary Replace(PdfDictionary source, PdfName name, PdfObject value) =>
        new(source.Where(entry => !entry.Key.Equals(name))
            .Append(new KeyValuePair<PdfName, PdfObject>(name, value)));
    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value)
    {
        while (value is PdfIndirectReference reference)
            value = document.Resolve(reference);
        return Assert.IsType<PdfDictionary>(value);
    }
    private static byte[] CertifiedSource(int permission)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Root")]);
        PdfDictionary catalog = ResolveDictionary(document, catalogReference);
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference parameters = update.AddObject(new PdfDictionary([
            new(Name("Type"), Name("TransformParams")),
            new(Name("P"), new PdfInteger(permission)),
            new(Name("V"), Name("1.2"))
        ]));
        PdfIndirectReference transform = update.AddObject(new PdfDictionary([
            new(Name("TransformMethod"), Name("DocMDP")),
            new(Name("TransformParams"), parameters)
        ]));
        PdfIndirectReference signature = update.AddObject(new PdfDictionary([
            new(Name("Type"), Name("Sig")),
            new(Name("Reference"), new PdfArray([transform]))
        ]));
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Perms"), new PdfDictionary([
                new(Name("DocMDP"), signature)
            ])))));
        return update.Build();
    }
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
