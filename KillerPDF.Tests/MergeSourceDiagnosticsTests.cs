using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class MergeSourceDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-merge-diagnostics-" + Guid.NewGuid().ToString("N"));

    // #495: merging an untagged PDF into an open tagged one stopped with "Untagged content cannot be
    // imported". The tagged pages keep their structure and the merged pages come in untagged.
    [Fact]
    public void InsertDocuments_MergesUntaggedPagesIntoATaggedDocument()
    {
        static PdfContentStreamBuilder TaggedPage() => new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill()
            .EndMarkedContent();
        string target = Write("tagged.pdf", new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Tagged merge target", Language = "en-US" })
            .EnablePdfUa2Conformance()
            .AddPage(100, 100, TaggedPage())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Square")
            .Build());
        string untagged = Write("untagged.pdf", new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build());

        // The command-line merge opens the first file as the target the same way.
        PdfDocument cliMerged = PdfDocument.Open(PdfEngineIntegration.MergeFiles([target, untagged]));
        Assert.Equal(3, PdfPageInformation.Read(cliMerged).Count);

        PdfEngineIntegration.InsertDocuments(target,
            [new PdfEngineIntegration.ImportedDocument(untagged, [0, 0])], int.MaxValue);

        PdfDocument merged = PdfDocument.Open(File.ReadAllBytes(target));
        Assert.Equal(3, PdfPageInformation.Read(merged).Count);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            merged.Resolve(Assert.IsType<PdfIndirectReference>(merged.Trailer[new PdfName("Root"u8)])));
        Assert.True(catalog.ContainsKey(new PdfName("StructTreeRoot"u8)));
    }

    // A folder or archive import keeps every readable entry. A PDF that opens but is rejected while
    // its pages are built used to abort the whole import instead of being skipped.
    [Fact]
    public void MergeReadableFiles_SkipsASourceRejectedWhileBuildingPages()
    {
        string first = Write("base.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        string rejected = Write("rejected.pdf", ReversedStitchingBounds());
        string last = Write("last.pdf", new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build());

        PdfDocument merged = PdfDocument.Open(PdfEngineIntegration.MergeReadableFiles([first, rejected, last]));

        Assert.Equal(3, PdfPageInformation.Read(merged).Count);
    }

    [Fact]
    public void MergeFiles_NamesTheRejectedSourceAndRetainsTheEngineReason()
    {
        string first = Write("base.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        byte[] rejected = ReversedStitchingBounds();
        string second = Write("report with spaces.pdf", rejected);
        string third = Write("last.pdf", new PdfDocumentBuilder().AddBlankPage().Build());

        var error = Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.MergeFiles([first, second, third]));

        Assert.Contains("/Bounds values are not ordered within /Domain", error.Message);
        Assert.StartsWith($"\"{second}\": ", error.Message);
        Assert.DoesNotContain(third, error.Message);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(rejected, File.ReadAllBytes(second));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void MergeFiles_NamesUnreadableInputs(bool firstInput, bool exists)
    {
        string good = Write("good.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        string bad = Path.Combine(_directory, "unreadable.pdf");
        if (exists) File.WriteAllText(bad, "This is not a PDF.");

        var error = Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.MergeFiles(
            firstInput ? [bad, good] : [good, bad]));

        Assert.StartsWith($"\"{bad}\": ", error.Message);
        Assert.NotNull(error.InnerException);
        Assert.EndsWith(error.InnerException.Message, error.Message);
    }

    [Fact]
    public void InsertDocuments_NamesTheRejectedSourceAndPreservesTheDestination()
    {
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        string target = Write("destination.pdf", original);
        string first = Write("good.pdf", original);
        byte[] rejected = ReversedStitchingBounds();
        string second = Write("rejected.pdf", rejected);

        var error = Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.InsertDocuments(
            target, [new(first, [0]), new(second, [0])], 1));

        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(original, File.ReadAllBytes(first));
        Assert.Equal(rejected, File.ReadAllBytes(second));
        Assert.Contains("rejected.pdf", error.Message);
        Assert.Contains("/Bounds values are not ordered within /Domain", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InsertDocuments_ReportsTheOriginalNameAfterRepair(bool readable)
    {
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        string target = Write("destination.pdf", original);
        string repaired = Write("repair-12345.pdf", readable ? ReversedStitchingBounds() : "not a PDF"u8.ToArray());
        string originalPath = Path.Combine(_directory, "customer report.pdf");

        var error = Assert.Throws<InvalidOperationException>(() => PdfEngineIntegration.InsertDocuments(
            target, [new(repaired, [0], originalPath)], 1));

        Assert.StartsWith($"\"{originalPath}\": ", error.Message);
        Assert.DoesNotContain(repaired, error.Message);
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CombinedFormFailure_DoesNotBlameAnIndividualSource(bool insert)
    {
        byte[] original = new PdfDocumentBuilder().AddBlankPage()
            .AddCheckBox(0, "target", 10, 10, 18, 18).Build();
        string target = Write("destination.pdf", original);
        string source = Write("xfa.pdf", XfaDocument());

        var error = Assert.Throws<NotSupportedException>(() =>
        {
            if (insert) PdfEngineIntegration.InsertDocuments(target, [new(source, [0])], 1);
            else PdfEngineIntegration.MergeFiles([target, source]);
        });

        Assert.Contains("XFA", error.Message);
        Assert.DoesNotContain(source, error.Message);
        Assert.DoesNotContain(target, error.Message);
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void InsertDocuments_OutputFailureDoesNotBlameTheSource()
    {
        byte[] original = new PdfDocumentBuilder().AddBlankPage().Build();
        string target = Write("destination.pdf", original);
        string source = Write("source.pdf", original);
        using var heldOpen = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);

        Exception? error = Record.Exception(() => PdfEngineIntegration.InsertDocuments(
            target, [new(source, [0])], 1));

        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.DoesNotContain(source, error.Message);
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Fact]
    public void Engine_ClearsFailedSourceWhenTheEditorIsReused()
    {
        PdfDocument target = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfDocument rejected = PdfDocument.Open(ReversedStitchingBounds());
        var editor = new PdfIncrementalPageEditor(target).AddImportedDocument(rejected);

        Assert.Throws<InvalidOperationException>(() => editor.Build());
        Assert.Same(rejected, editor.FailedImportSource);
        editor.RemovePage(1);
        Assert.Equal(1, new PdfIncrementalPageEditor(PdfDocument.Open(editor.Build())).PageCount);
        Assert.Null(editor.FailedImportSource);
    }

    private static byte[] XfaDocument()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddCheckBox(0, "source", 10, 10, 18, 18).Build());
        var catalogReference = (PdfIndirectReference)source.Trailer[Name("Root")];
        var catalog = (PdfDictionary)source.Resolve(catalogReference);
        PdfObject formValue = catalog[Name("AcroForm")];
        var form = (PdfDictionary)(formValue is PdfIndirectReference reference ? source.Resolve(reference) : formValue);
        var extended = new PdfDictionary(form.Append(Entry("XFA", new PdfArray([]))));
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog
            .Where(entry => !entry.Key.Equals(Name("AcroForm")))
            .Append(Entry("AcroForm", extended))));
        return update.Build();
    }

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ReversedStitchingBounds()
    {
        PdfDictionary Constant() => new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0)), Entry("C1", Numbers(1, 1, 1)), Entry("N", new PdfInteger(1))
        ]);
        var function = new PdfDictionary([
            Entry("FunctionType", new PdfInteger(3)), Entry("Domain", Numbers(0, 1)),
            Entry("Bounds", Numbers(.6, .4)), Entry("Encode", Numbers(0, 1, 0, 1, 0, 1)),
            Entry("Functions", new PdfArray([Constant(), Constant(), Constant()]))
        ]);
        var resources = new PdfDictionary([Entry("Pattern", new PdfDictionary([
            Entry("P", new PdfDictionary([
                Entry("Type", Name("Pattern")), Entry("PatternType", new PdfInteger(2)),
                Entry("Shading", new PdfDictionary([
                    Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", Name("DeviceRGB")),
                    Entry("Coords", Numbers(0, 0, 100, 0)), Entry("Function", function)
                ]))
            ]))
        ]))]);
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, "/Pattern cs /P scn 0 0 100 100 re f"u8.ToArray()).Build());
        PdfDictionary Resolve(PdfObject value) => (PdfDictionary)(value is PdfIndirectReference reference
            ? source.Resolve(reference) : value);
        PdfDictionary catalog = Resolve(source.Trailer[Name("Root")]);
        PdfDictionary pages = Resolve(catalog[Name("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        PdfDictionary page = Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        return update.Build();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfArray Numbers(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
