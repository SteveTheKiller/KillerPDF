using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Security;
using Xunit;
using System.Text;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfEditablePageOverlayTests
{
    [Fact]
    public void Lifecycle_ExistingNonzeroTaggedParentGenerationPreservesOriginalStructure()
    {
        const string paint = "/Figure <</MCID 0>> BDC 1 1 8 8 re f EMC\n";
        string[] objects = [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /StructParents 0 >>\nendobj\n",
            $"4 0 obj\n<< /Length {paint.Length} >>\nstream\n{paint}endstream\nendobj\n",
            "5 0 obj\n<< /Type /StructTreeRoot /K 6 1 R /ParentTree 8 0 R /ParentTreeNextKey 1 >>\nendobj\n",
            "6 1 obj\n<< /Type /StructElem /S /Document /P 5 0 R /K 7 0 R >>\nendobj\n",
            "7 0 obj\n<< /Type /StructElem /S /Figure /P 6 1 R /Pg 3 0 R /K 0 /Alt (Original figure) >>\nendobj\n",
            "8 0 obj\n<< /Nums [0 [7 0 R]] >>\nendobj\n"
        ];
        var fixture = new StringBuilder("%PDF-2.0\n");
        var offsets = new List<int>();
        foreach (string value in objects) { offsets.Add(fixture.Length); fixture.Append(value); }
        int xref = fixture.Length;
        fixture.Append("xref\n0 9\n0000000000 65535 f\n");
        for (int index = 0; index < offsets.Count; index++)
            fixture.Append($"{offsets[index]:D10} {(index == 5 ? 1 : 0):D5} n\n");
        fixture.Append($"trailer\n<< /Size 9 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        PdfDocument source = PdfDocument.Open(Encoding.ASCII.GetBytes(fixture.ToString()));
        Assert.Equal("Original figure", Assert.Single(PdfAccessibilityReadingOrder.Read(source).Items).AlternateDescription);
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(20, 20, 10, 10).Fill(), new byte[] { 8 }, "Owned figure"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(saved, [unit]));
        Assert.Equal("Original figure", Assert.Single(PdfAccessibilityReadingOrder.Read(removed).Items).AlternateDescription);
        Assert.Empty(PdfEditablePageOverlayEditor.Read(removed));
    }

    [Fact]
    public void Lifecycle_ExistingNonzeroPageGenerationPreservesEditableData()
    {
        string[] objects = [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 1 R] /Count 1 >>\nendobj\n",
            "3 1 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>\nendobj\n",
            "4 0 obj\n<< /Length 4 >>\nstream\nq Q\nendstream\nendobj\n"
        ];
        var fixture = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects) { offsets.Add(fixture.Length); fixture.Append(value); }
        int xref = fixture.Length;
        fixture.Append("xref\n0 5\n0000000000 65535 f\n");
        for (int index = 0; index < offsets.Count; index++)
            fixture.Append($"{offsets[index]:D10} {(index == 2 ? 1 : 0):D5} n\n");
        fixture.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        PdfDocument source = PdfDocument.Open(Encoding.ASCII.GetBytes(fixture.ToString()));
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 5, 3 }, "Owned"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        Assert.Equal(new byte[] { 5, 3 }, unit.EditingData.ToArray());
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(saved, [unit]));
        Assert.Empty(PdfEditablePageOverlayEditor.Read(removed));
        Assert.Equal(1, new PdfIncrementalPageEditor(removed).PageCount);
    }

    [Fact]
    public void Read_ReturnsOwnedBatchesInCurrentPageContentStackOrder()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfDocument first = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "First"));
        PdfDocument both = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(first, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(50, 50, 20, 20).Fill(), new byte[] { 2 }, "Second"));
        Assert.Equal(new byte[] { 1, 2 }, PdfEditablePageOverlayEditor.Read(both)
            .Select(unit => unit.EditingData.Span[0]).ToArray());
    }

    [Fact]
    public void Remove_RespectsUserPasswordModificationRestrictionsWhenCopyingIsAllowed()
    {
        byte[] original = new PdfDocumentBuilder().AddBlankPage(100, 100)
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "fixture-user", OwnerPassword = "fixture-owner", AllowContentCopying = true,
                AllowDocumentModification = false }).Build();
        PdfDocument owner = PdfDocument.Open(original, "fixture-owner");
        byte[] savedBytes = PdfEditablePageOverlayEditor.Append(owner, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 6 }, "Owned");
        PdfDocument user = PdfDocument.Open(savedBytes, "fixture-user");
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(user));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Remove(user, [unit]));
    }

    [Fact]
    public void Remove_OneOfTwoOwnedUnitsPreservesCurrentOtherUnitAndOriginalPixels()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().SetFillRgb(0, 1, 0).Rectangle(0, 0, 100, 100).Fill()).Build());
        PdfDocument first = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().SetFillRgb(0, 0, 1).Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "First"));
        PdfDocument both = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(first, 0, 100, 100,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(50, 50, 20, 20).Fill(), new byte[] { 2 }, "Second"));
        IReadOnlyList<PdfEditablePageOverlay> units = PdfEditablePageOverlayEditor.Read(both);
        Assert.Equal(2, units.Count);
        PdfEditablePageOverlay older = Assert.Single(units, unit => unit.EditingData.Span[0] == 1);
        PdfDocument retained = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(both, [older]));
        Assert.Equal(new byte[] { 2 }, Assert.Single(PdfEditablePageOverlayEditor.Read(retained)).EditingData.ToArray());
        PdfRenderedPage pixels = new PdfPageRenderer(retained).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels.Pixels.Slice((80 * 100 + 20) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.Pixels.Slice((40 * 100 + 60) * 4, 4).ToArray());
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(retained, PdfEditablePageOverlayEditor.Read(retained)));
        Assert.Empty(PdfEditablePageOverlayEditor.Read(removed));
        Assert.Equal(new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray(),
            new PdfPageRenderer(removed).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
    }

    [Fact]
    public void Read_UsesDeclaredFinalRotationAndRefusesDifferentCurrentFrame()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 80, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 80,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "Owned", expectedRotation: 90));
        Assert.Empty(PdfEditablePageOverlayEditor.Read(saved));
        PdfDocument rotated = PdfDocument.Open(new PdfIncrementalPageEditor(saved).RotateClockwise(0).Build());
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(rotated));
        PdfDocument different = PdfDocument.Open(new PdfIncrementalPageEditor(rotated).RotateClockwise(0).Build());
        Assert.Empty(PdfEditablePageOverlayEditor.Read(different));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Remove(different, [unit]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfEditablePageOverlayEditor.Append(source, 0, 100, 80,
            new PdfContentStreamBuilder(), new byte[] { 1 }, null, expectedRotation: 45));
    }

    [Fact]
    public void Remove_FailsClosedOnMalformedCertificationPermissions()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "Owned"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfPageTree tree = PdfPageTree.Read(saved);
        var catalog = tree.Catalog.ToDictionary(pair => pair.Key, pair => pair.Value);
        catalog[new PdfName("Perms"u8)] = new PdfDictionary([
            new(new PdfName("DocMDP"u8), new PdfDictionary([new(new PdfName("Type"u8), new PdfName("Sig"u8))]))
        ]);
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(saved)
            .ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog)).Build());
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Remove(changed, [unit]));
    }

    [Fact]
    public void Remove_PreservesLaterForeignTaggedWidgetAndOriginalFigure()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Tagged widget fixture", Language = "en-US" })
            .EnablePdfUa2Conformance()
            .AddPage(100, 100, new PdfContentStreamBuilder().BeginMarkedContent(PdfStructureType.Figure, 0)
                .Rectangle(1, 1, 8, 8).Fill().EndMarkedContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original figure").Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(20, 20, 10, 10).Fill(), new byte[] { 9 }, "Owned figure"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(saved)
            .AddCheckBox(0, "LaterField", 50, 50, 15, 15, isChecked: true).Build());
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(changed, [unit]));
        Assert.Equal(new[] { "Figure", "Form" }, PdfAccessibilityReadingOrder.Read(removed).Items.Select(item => item.Role));
        Assert.Equal("Original figure", PdfAccessibilityReadingOrder.Read(removed).Items[0].AlternateDescription);
        Assert.DoesNotContain(PdfAccessibilityReadingOrder.Read(removed).Items,
            item => item.AlternateDescription == "Owned figure");
        Assert.Equal("/Yes", Assert.Single(PdfFormWidgetReader.ReadPage(removed, 0)).Value);
        PdfDictionary root = Assert.IsType<PdfDictionary>(removed.Resolve(Assert.IsType<PdfIndirectReference>(
            PdfPageTree.Read(removed).Catalog[new PdfName("StructTreeRoot"u8)])));
        Assert.Equal(2, PdfNumberTree.Read(removed, root[new PdfName("ParentTree"u8)]).Count);
    }

    [Fact]
    public void Read_RespectsUserPasswordCopyRestrictions()
    {
        byte[] original = new PdfDocumentBuilder().AddBlankPage(100, 100)
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "fixture-user", OwnerPassword = "fixture-owner", AllowContentCopying = false,
                AllowDocumentModification = false }).Build();
        PdfDocument owner = PdfDocument.Open(original, "fixture-owner");
        byte[] savedBytes = PdfEditablePageOverlayEditor.Append(owner, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 6 }, "Owned");
        PdfDocument user = PdfDocument.Open(savedBytes, "fixture-user");
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(PdfDocument.Open(savedBytes, "fixture-owner")));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Read(user));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Remove(user, [unit]));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Append(user, 0, 100, 100,
            new PdfContentStreamBuilder(), new byte[] { 7 }, null));
    }

    [Fact]
    public void Lifecycle_AuthenticatedEncryptedDocumentRetainsEncryptionAndOwnedData()
    {
        byte[] original = new PdfDocumentBuilder().AddBlankPage(100, 100)
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "fixture-user", OwnerPassword = "fixture-owner" }).Build();
        PdfDocument source = PdfDocument.Open(original, "fixture-owner");
        byte[] savedBytes = PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 6, 2 }, "Owned");
        PdfDocument saved = PdfDocument.Open(savedBytes, "fixture-owner");
        Assert.True(saved.IsEncrypted);
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        Assert.Equal(new byte[] { 6, 2 }, unit.EditingData.ToArray());
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(saved, [unit]), "fixture-owner");
        Assert.True(removed.IsEncrypted);
        Assert.Empty(PdfEditablePageOverlayEditor.Read(removed));
    }

    [Fact]
    public void Read_MalformedOwnedPrefixFilterReturnsNoUnit()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "Owned"));
        PdfIndirectReference prefix = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(PdfPageTree.Read(saved).Pages[0].Dictionary[new PdfName("Contents"u8)])[0]);
        PdfStream stream = Assert.IsType<PdfStream>(saved.Resolve(prefix));
        var dictionary = stream.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        dictionary[new PdfName("Filter"u8)] = new PdfName("UnknownDecode"u8);
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(saved)
            .ReplaceObject(prefix.ObjectNumber, new PdfStream(new PdfDictionary(dictionary), stream.EncodedData.Span)).Build());
        Assert.Empty(PdfEditablePageOverlayEditor.Read(changed));
    }

    [Fact]
    public void Read_ForeignAppendedPageContentRefusesExtractionAndRemoval()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "Owned"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(saved)
            .AppendPageContent(0, "1 0 0 rg 15 15 20 20 re f\n"u8.ToArray()).Build());
        Assert.Empty(PdfEditablePageOverlayEditor.Read(changed));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Remove(changed, [unit]));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, new PdfPageRenderer(changed)
            .Render(0, new PdfRenderOptions(100, 100)).Pixels.Slice((80 * 100 + 20) * 4, 4).ToArray());
    }

    [Fact]
    public void Read_ModifiedOwnedFontResourceRefusesExtractionAndRemoval()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .SetTextMatrix(1, 0, 0, 1, 10, 40).ShowLatin1Text("Owned").EndText(), new byte[] { 1 }, "Owned"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfStream form = Assert.IsType<PdfStream>(saved.Resolve(new PdfIndirectReference(unit.FormObjectNumber, 0)));
        PdfDictionary resources = Assert.IsType<PdfDictionary>(form.Dictionary[new PdfName("Resources"u8)]);
        PdfDictionary fonts = Assert.IsType<PdfDictionary>(resources[new PdfName("Font"u8)]);
        PdfIndirectReference fontReference = Assert.IsType<PdfIndirectReference>(Assert.Single(fonts).Value);
        PdfDictionary font = Assert.IsType<PdfDictionary>(saved.Resolve(fontReference));
        var entries = font.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[new PdfName("BaseFont"u8)] = new PdfName("Courier"u8);
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(saved)
            .ReplaceObject(fontReference.ObjectNumber, new PdfDictionary(entries)).Build());
        Assert.Empty(PdfEditablePageOverlayEditor.Read(changed));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Remove(changed, [unit]));
    }

    [Fact]
    public void Remove_PreservesCurrentForeignFormValuesAnnotationsAndPageEntries()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray())
            .AddTextField(0, "Customer", 40, 40, 40, 15, "Before")
            .AddTextNote(0, 70, 70, "Original note").Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "Owned"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfDocument changedField = PdfDocument.Open(new PdfIncrementalPageEditor(saved).SetTextFieldValue("Customer", "After").Build());
        PdfDocument changedNote = PdfDocument.Open(new PdfIncrementalAnnotationEditor(changedField)
            .SetAnnotationContentsAt(0, 1, "Revised note").AddTextNote(0, 80, 20, "Later note").Build());
        PdfPageTreeEntry page = PdfPageTree.Read(changedNote).Pages[0];
        var entries = page.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[new PdfName("ForeignMarker"u8)] = new PdfInteger(23);
        PdfDocument current = PdfDocument.Open(new PdfIncrementalUpdateBuilder(changedNote)
            .ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries)).Build());
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(current, [unit]));
        Assert.Equal("After", Assert.Single(PdfFormWidgetReader.ReadPage(removed, 0)).Value);
        Assert.Equal(new[] { "Revised note", "Later note" }, PdfCommentReader.Read(removed).Select(note => note.Contents));
        Assert.Equal(23, Assert.IsType<PdfInteger>(PdfPageTree.Read(removed).Pages[0].Dictionary[new PdfName("ForeignMarker"u8)]).Value);
        Assert.Equal(3, PdfCommentReader.ReadPageAnnotationCount(removed, 0));
    }

    [Fact]
    public void Read_ModifiedOwnedInvocationDictionaryRefusesExtraction()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "Owned"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfStream invocation = Assert.IsType<PdfStream>(saved.Resolve(new PdfIndirectReference(unit.ContentsObjectNumber, 0)));
        var dictionary = invocation.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        dictionary[new PdfName("ForeignMarker"u8)] = new PdfInteger(5);
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(saved)
            .ReplaceObject(unit.ContentsObjectNumber, new PdfStream(new PdfDictionary(dictionary), invocation.EncodedData.Span)).Build());
        Assert.Empty(PdfEditablePageOverlayEditor.Read(changed));
    }

    [Fact]
    public void Read_ModifiedOpaquePayloadRefusesExtractionAndStaleRemoval()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 1 }, "Owned"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfStream invocation = Assert.IsType<PdfStream>(saved.Resolve(new PdfIndirectReference(unit.ContentsObjectNumber, 0)));
        PdfName metadataKey = new("KillerPDFEditableOverlay"u8);
        var metadata = Assert.IsType<PdfDictionary>(invocation.Dictionary[metadataKey])
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        metadata[new PdfName("Data"u8)] = new PdfString(new byte[] { 2 }, PdfStringForm.Hexadecimal);
        var dictionary = invocation.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        dictionary[metadataKey] = new PdfDictionary(metadata);
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalUpdateBuilder(saved)
            .ReplaceObject(unit.ContentsObjectNumber, new PdfStream(new PdfDictionary(dictionary), invocation.EncodedData.Span)).Build());
        Assert.Empty(PdfEditablePageOverlayEditor.Read(changed));
        Assert.Throws<InvalidOperationException>(() => PdfEditablePageOverlayEditor.Remove(changed, [unit]));
    }

    [Fact]
    public void Append_BlankPageRoundTripsOwnedContent()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(10, 10, 20, 20).Fill(), new byte[] { 2 }, "New content"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(saved, [unit]));
        Assert.Equal(new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray(),
            new PdfPageRenderer(removed).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
    }

    [Fact]
    public void Append_HiddenBatchRetainsOpaqueDataWithoutLogicalDescription()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, "q Q\n"u8.ToArray()).Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder(), new byte[] { 7, 8 }, null!));
        Assert.Equal(new byte[] { 7, 8 }, Assert.Single(PdfEditablePageOverlayEditor.Read(saved)).EditingData.ToArray());
        Assert.Empty(new PdfPageContentReader(saved).Read(0).Text);
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(saved,
            PdfEditablePageOverlayEditor.Read(saved)));
        Assert.Empty(PdfEditablePageOverlayEditor.Read(removed));
    }

    [Fact]
    public void Remove_TaggedOverlayRemovesOnlyItsLogicalDescriptionAndParentTreeEntry()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Tagged overlay fixture", Language = "en-US" })
            .EnablePdfUa2Conformance()
            .AddPage(100, 100, new PdfContentStreamBuilder().BeginMarkedContent(PdfStructureType.Figure, 0)
                .Rectangle(1, 1, 8, 8).Fill().EndMarkedContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original figure").Build());
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            new PdfContentStreamBuilder().Rectangle(20, 20, 10, 10).Fill(), new byte[] { 9 }, "Owned figure"));
        Assert.Equal(new[] { "Original figure", "Owned figure" },
            PdfAccessibilityReadingOrder.Read(saved).Items.Select(item => item.AlternateDescription));
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(saved,
            PdfEditablePageOverlayEditor.Read(saved)));
        Assert.Equal(new[] { "Original figure" },
            PdfAccessibilityReadingOrder.Read(removed).Items.Select(item => item.AlternateDescription));
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(removed.Resolve(
            Assert.IsType<PdfIndirectReference>(removed.Trailer[new PdfName("Root"u8)])));
        PdfDictionary root = Assert.IsType<PdfDictionary>(removed.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("StructTreeRoot"u8)])));
        Assert.Single(PdfNumberTree.Read(removed, root[new PdfName("ParentTree"u8)]));
    }

    [Fact]
    public void Lifecycle_PreservesSearchPixelsAndOpaqueData()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().SetFillRgb(0, 1, 0).Rectangle(0, 0, 100, 100).Fill()).Build());
        var content = new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(10, 10, 20, 20).Fill()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10).SetTextMatrix(1, 0, 0, 1, 40, 70)
            .ShowLatin1Text("Searchable overlay").EndText();
        PdfDocument saved = PdfDocument.Open(PdfEditablePageOverlayEditor.Append(source, 0, 100, 100,
            content, new byte[] { 3, 1, 4, 1, 5 }, "Editable square"));
        PdfEditablePageOverlay unit = Assert.Single(PdfEditablePageOverlayEditor.Read(saved));
        Assert.Equal(0, unit.PageIndex);
        Assert.Equal(new byte[] { 3, 1, 4, 1, 5 }, unit.EditingData.ToArray());
        Assert.Contains("Searchable overlay", new PdfPageContentReader(saved).Read(0).Text);
        PdfRenderedPage rendered = new PdfPageRenderer(saved).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((80 * 100 + 20) * 4, 4).ToArray());
        PdfDocument removed = PdfDocument.Open(PdfEditablePageOverlayEditor.Remove(saved, [unit]));
        Assert.Empty(PdfEditablePageOverlayEditor.Read(removed));
        Assert.DoesNotContain("Searchable overlay", new PdfPageContentReader(removed).Read(0).Text);
        Assert.Equal(new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray(),
            new PdfPageRenderer(removed).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
    }
}
