using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
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
