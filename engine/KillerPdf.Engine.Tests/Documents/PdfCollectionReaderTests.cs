using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfCollectionReaderTests
{
    [Fact]
    public void ReadReturnsPortfolioViewFieldsAndSortRules()
    {
        PdfDictionary collection = new([
            new(Name("Type"), Name("Collection")),
            new(Name("View"), Name("T")),
            new(Name("D"), Text("cover.pdf")),
            new(Name("Schema"), new PdfDictionary([
                new(Name("Department"), new PdfDictionary([
                    new(Name("N"), Text("Department")),
                    new(Name("Subtype"), Name("S")),
                    new(Name("O"), new PdfInteger(2)),
                    new(Name("V"), new PdfBoolean(false)),
                    new(Name("E"), new PdfBoolean(true))
                ])),
                new(Name("Name"), new PdfDictionary([
                    new(Name("N"), Text("File name")),
                    new(Name("Subtype"), Name("F")),
                    new(Name("O"), new PdfInteger(1))
                ]))
            ])),
            new(Name("Sort"), new PdfDictionary([
                new(Name("S"), new PdfArray([Name("Department"), Name("Name")])) ,
                new(Name("A"), new PdfArray([new PdfBoolean(true), new PdfBoolean(false)]))
            ]))
        ]);

        PdfCollectionInfo info = Assert.IsType<PdfCollectionInfo>(
            PdfCollectionReader.Read(WithCollection(collection)));

        Assert.Equal(PdfCollectionView.Tile, info.View);
        Assert.Equal("T", info.RawViewName);
        Assert.Equal("cover.pdf", info.InitialDocument);
        Assert.Collection(info.Fields,
            field =>
            {
                Assert.Equal("Name", field.Key);
                Assert.Equal("File name", field.DisplayName);
                Assert.Equal("F", field.Subtype);
                Assert.True(field.IsVisible);
                Assert.False(field.IsEditable);
            },
            field =>
            {
                Assert.Equal("Department", field.Key);
                Assert.False(field.IsVisible);
                Assert.True(field.IsEditable);
            });
        Assert.Equal([
            new PdfCollectionSortInfo("Department", true),
            new PdfCollectionSortInfo("Name", false)
        ], info.Sort);
    }

    [Fact]
    public void ReadPreservesUnknownViewAndRejectsMismatchedSortDirections()
    {
        PdfCollectionInfo info = Assert.IsType<PdfCollectionInfo>(
            PdfCollectionReader.Read(WithCollection(new PdfDictionary([
                new(Name("View"), Name("CustomViewer"))
            ]))));
        Assert.Equal(PdfCollectionView.Unknown, info.View);
        Assert.Equal("CustomViewer", info.RawViewName);

        PdfDocument malformed = WithCollection(new PdfDictionary([
            new(Name("Sort"), new PdfDictionary([
                new(Name("S"), new PdfArray([Name("Name"), Name("Size")])) ,
                new(Name("A"), new PdfArray([new PdfBoolean(true)]))
            ]))
        ]));
        Assert.Throws<InvalidOperationException>(() => PdfCollectionReader.Read(malformed));
    }

    [Fact]
    public void EditorChangesAndClearsPresentationWithoutReplacingSchema()
    {
        PdfDocument document = WithCollection(new PdfDictionary([
            new(Name("View"), Name("T")),
            new(Name("Schema"), new PdfDictionary([
                new(Name("Name"), new PdfDictionary([
                    new(Name("N"), Text("File name")),
                    new(Name("Subtype"), Name("F"))
                ]))
            ]))
        ]));

        PdfDocument changed = PdfDocument.Open(PdfCollectionEditor.SetPresentation(
            document, PdfCollectionView.Hidden, "cover.pdf"));
        PdfCollectionInfo info = Assert.IsType<PdfCollectionInfo>(
            PdfCollectionReader.Read(changed));

        Assert.Equal(PdfCollectionView.Hidden, info.View);
        Assert.Equal("cover.pdf", info.InitialDocument);
        Assert.Single(info.Fields);
        Assert.Null(PdfCollectionReader.Read(PdfDocument.Open(
            PdfCollectionEditor.Clear(changed))));
    }

    [Fact]
    public void EditorReplacesSchemaAndSortWhilePreservingPresentation()
    {
        PdfDocument document = WithCollection(new PdfDictionary([
            new(Name("View"), Name("T")),
            new(Name("D"), Text("cover.pdf")),
            new(Name("Unknown"), new PdfInteger(42))
        ]));
        PdfCollectionFieldInfo[] fields = [
            new()
            {
                Key = "Name", DisplayName = "File name", Subtype = "F",
                Order = 1, IsVisible = true
            },
            new()
            {
                Key = "Department", DisplayName = "Department", Subtype = "S",
                Order = 2, IsVisible = false, IsEditable = true
            }];

        PdfDocument changed = PdfDocument.Open(PdfCollectionEditor.SetSchema(
            document, fields, [
                new PdfCollectionSortInfo("Department", true),
                new PdfCollectionSortInfo("Name", false)]));
        PdfCollectionInfo info = Assert.IsType<PdfCollectionInfo>(
            PdfCollectionReader.Read(changed));

        Assert.Equal(PdfCollectionView.Tile, info.View);
        Assert.Equal("cover.pdf", info.InitialDocument);
        Assert.Equal(fields, info.Fields);
        Assert.Equal([
            new PdfCollectionSortInfo("Department", true),
            new PdfCollectionSortInfo("Name", false)], info.Sort);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(changed.Resolve(
            Assert.IsType<PdfIndirectReference>(changed.Trailer[Name("Root")])));
        PdfDictionary collection = Assert.IsType<PdfDictionary>(catalog[Name("Collection")]);
        Assert.Equal(42, Assert.IsType<PdfInteger>(collection[Name("Unknown")]).Value);
    }

    [Fact]
    public void EditorRejectsUnknownFieldTypesAndSortKeys()
    {
        PdfDocument document = WithCollection(new PdfDictionary([]));
        PdfCollectionFieldInfo field = new()
        {
            Key = "Name", DisplayName = "Name", Subtype = "F", IsVisible = true
        };

        Assert.Throws<ArgumentException>(() => PdfCollectionEditor.SetSchema(document,
            [field with { Subtype = "Unknown" }]));
        Assert.Throws<ArgumentException>(() => PdfCollectionEditor.SetSchema(document,
            [field], [new PdfCollectionSortInfo("Missing", true)]));
    }

    [Fact]
    public void EditorReplacesAttachmentCollectionValuesWithoutChangingPayload()
    {
        PdfDocument attached = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("evidence.txt", "payload"u8.ToArray()).Build());
        PdfDocument portfolio = PdfDocument.Open(PdfCollectionEditor.SetSchema(attached, [
            new PdfCollectionFieldInfo
            {
                Key = "Department", DisplayName = "Department", Subtype = "S",
                IsVisible = true
            },
            new PdfCollectionFieldInfo
            {
                Key = "Score", DisplayName = "Score", Subtype = "N", IsVisible = true
            }]));

        PdfDocument changed = PdfDocument.Open(PdfCollectionEditor.SetItemValues(
            portfolio, "evidence.txt", [
                new PdfCollectionItemValue("Department", "Legal", null, "Team: "),
                new PdfCollectionItemValue("Score", null, 4.5, null)]));
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(changed));

        Assert.Equal("payload"u8.ToArray(), attachment.Data.ToArray());
        Assert.Equal([
            new PdfCollectionItemValue("Department", "Legal", null, "Team: "),
            new PdfCollectionItemValue("Score", null, 4.5, null)
        ], attachment.CollectionValues);
        Assert.Throws<ArgumentException>(() => PdfCollectionEditor.SetItemValues(
            portfolio, "evidence.txt", [
            new PdfCollectionItemValue("Missing", "value", null, null)]));
    }

    [Fact]
    public void ReadReturnsNestedPortfolioFoldersInDisplayOrder()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference root = update.ReserveObject();
        PdfIndirectReference child = update.ReserveObject();
        PdfIndirectReference sibling = update.ReserveObject();
        update.SetObject(root, new PdfDictionary([
            new(Name("Type"), Name("Folder")), new(Name("ID"), new PdfInteger(1)),
            new(Name("Name"), Text("Evidence")), new(Name("Desc"), Text("Case files")),
            new(Name("Child"), child)
        ]));
        update.SetObject(child, new PdfDictionary([
            new(Name("Type"), Name("Folder")), new(Name("ID"), new PdfInteger(2)),
            new(Name("Name"), Text("Photos")), new(Name("Parent"), root),
            new(Name("Next"), sibling)
        ]));
        update.SetObject(sibling, new PdfDictionary([
            new(Name("Type"), Name("Folder")), new(Name("ID"), new PdfInteger(3)),
            new(Name("Name"), Text("Reports")), new(Name("Parent"), root)
        ]));
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            source.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(catalogReference));
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Collection"), new PdfDictionary([
                new(Name("Type"), Name("Collection")), new(Name("Folders"), root)
            ])))));

        PdfCollectionInfo collection = Assert.IsType<PdfCollectionInfo>(
            PdfCollectionReader.Read(PdfDocument.Open(update.Build())));

        Assert.Equal(["Evidence", "Photos", "Reports"],
            collection.Folders.Select(folder => folder.Name));
        Assert.Equal([0, 1, 1], collection.Folders.Select(folder => folder.Depth));
        Assert.Equal([null, 1L, 1L], collection.Folders.Select(folder => folder.ParentId));
        Assert.Equal("Case files", collection.Folders[0].Description);
    }

    private static PdfDocument WithCollection(PdfDictionary collection)
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            source.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(catalogReference));
        var replacement = new PdfDictionary(catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(Name("Collection"), collection)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(catalogReference.ObjectNumber, replacement).Build());
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
    private static PdfString Text(string value) =>
        new(System.Text.Encoding.UTF8.GetBytes(value), PdfStringForm.Literal);
}
