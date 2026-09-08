using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfDescribedOverlayTests
{
    private static PdfName N(string name) => new(Encoding.ASCII.GetBytes(name));
    private static PdfDictionary D(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);
    private static (PdfDictionary Catalog, PdfDictionary Page, PdfObject Reference) Tree(PdfDocument document)
    {
        var catalog = D(document, document.Trailer[N("Root")]);
        var pages = D(document, catalog[N("Pages")]);
        var reference = Assert.Single(Assert.IsType<PdfArray>(pages[N("Kids")]));
        return (catalog, D(document, reference), reference);
    }

    private static PdfDocument Source() => PdfDocument.Open(new PdfDocumentBuilder()
        .SetMetadata(new PdfDocumentMetadata { Title = "Tagged source", Language = "en-US" })
        .EnablePdfUa2Conformance()
        .AddPage(100, 100, new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill().EndMarkedContent())
        .AddStructureContainer(PdfStructureType.Document)
        .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original square")
        .Build());

    [Fact]
    public void RepeatedSavesPreserveOriginalStructureAndLinkEveryOverlay()
    {
        PdfDocument document = Source();
        var sourceTree = Tree(document);
        PdfObject originalContents = sourceTree.Page[N("Contents")];
        PdfDictionary root = D(document, sourceTree.Catalog[N("StructTreeRoot")]);
        PdfDictionary originalParentTree = D(document, root[N("ParentTree")]);
        var originalNumbers = Assert.IsType<PdfArray>(originalParentTree[N("Nums")]);
        byte[] originalEntry = PdfObjectWriter.Write(originalNumbers[1]);
        for (int save = 0; save < 3; save++)
        {
            document = PdfDocument.Open(new PdfIncrementalPageEditor(document)
                .AppendPageDescribedContent(0, 100, 100,
                    new PdfContentStreamBuilder().Rectangle(40, 40, 20, 20).Fill(), "Signed and dated")
                .Build());
            var tree = Tree(document);
            var page = tree.Page;
            root = D(document, tree.Catalog[N("StructTreeRoot")]);
            var numbers = Assert.IsType<PdfArray>(D(document, root[N("ParentTree")])[N("Nums")]);
            Assert.Equal((save + 2) * 2, numbers.Count);
            Assert.Equal(originalEntry, PdfObjectWriter.Write(numbers[1]));
            var streams = Assert.IsType<PdfArray>(page[N("Contents")]);
            Assert.Equal(PdfObjectWriter.Write(originalContents), PdfObjectWriter.Write(streams[0]));
            var resources = D(document, page[N("Resources")]);
            var forms = D(document, resources[N("XObject")]);
            Assert.Equal(save + 1, forms.Count);
            foreach (var formEntry in forms)
            {
                var formReference = Assert.IsType<PdfIndirectReference>(formEntry.Value);
                var form = Assert.IsType<PdfStream>(document.Resolve(formReference));
                long key = Assert.IsType<PdfInteger>(form.Dictionary[N("StructParents")]).Value;
                int index = Enumerable.Range(0, numbers.Count / 2)
                    .Single(i => Assert.IsType<PdfInteger>(numbers[i * 2]).Value == key) * 2;
                var parents = Assert.IsType<PdfArray>(numbers[index + 1]);
                var element = D(document, Assert.Single(parents));
                Assert.Equal(N("Figure"), element[N("S")]);
                var mcr = D(document, element[N("K")]);
                Assert.Equal(N("MCR"), mcr[N("Type")]);
                Assert.Equal(PdfObjectWriter.Write(formReference), PdfObjectWriter.Write(mcr[N("Stm")]));
                Assert.Equal(PdfObjectWriter.Write(tree.Reference), PdfObjectWriter.Write(mcr[N("Pg")]));
                Assert.Equal(0, Assert.IsType<PdfInteger>(mcr[N("MCID")]).Value);
                Assert.Contains("/Figure <</MCID 0>> BDC", Encoding.ASCII.GetString(PdfStreamDecoder.Decode(form)));
                var parent = D(document, element[N("P")]);
                var kids = Assert.IsType<PdfArray>(parent[N("K")]);
                Assert.Contains(kids, kid => PdfObjectWriter.Write(kid).SequenceEqual(PdfObjectWriter.Write(parents[0])));
            }
        }
    }

    [Fact]
    public void OrdinaryContentGuardRemainsInForce()
    {
        Assert.Throws<NotSupportedException>(() => new PdfIncrementalPageEditor(Source())
            .AppendPageDescribedContent(0, 100, 100, new PdfContentStreamBuilder().Rectangle(1, 1, 2, 2).Fill(), "Square")
            .AppendPageContent(0, "q Q"u8.ToArray()).Build());
    }

    [Fact]
    public void DescribedContentRejectsNestedLogicalIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new PdfIncrementalPageEditor(Source())
            .AppendPageDescribedContent(0, 100, 100, new PdfContentStreamBuilder()
                .BeginMarkedContent(PdfStructureType.Figure, 0).EndMarkedContent(), "Square"));
    }
}
