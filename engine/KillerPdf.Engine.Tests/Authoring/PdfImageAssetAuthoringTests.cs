using System.Globalization;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfImageAssetAuthoringTests
{
    [Fact]
    public void DetachedImageCanBeReusedInsideAFormWithoutCopyingItsSourcePage()
    {
        var asset = Extract();
        Array.Clear(asset.ToPdfBytes());
        var form = new PdfFormXObject(4, 1, new PdfContentStreamBuilder()
            .DrawImage(asset, 0, 0, 2, 1).DrawImage(asset, 2, 0, 2, 1));
        var target = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 1,
            new PdfContentStreamBuilder().DrawForm(form, 0, 0, 4, 1)).Build());
        var content = new PdfPageContentReader(target).Read(0);
        Assert.Equal(2, content.Images.Count);
        Assert.Equal(new PdfContentBounds(0, 0, 2, 1), content.Images[0].BoundingBox);
        Assert.Equal(new PdfContentBounds(2, 0, 4, 1), content.Images[1].BoundingBox);
        Assert.Empty(content.Letters);
        Assert.Empty(content.Paths);
        var page = PdfPageTree.Read(target).Pages[0].Dictionary;
        var formStream = Assert.IsType<PdfStream>(Resolve(target, Assert.Single(XObjects(target, page)).Value));
        var image = Assert.IsType<PdfStream>(Resolve(target, Assert.Single(XObjects(target, formStream.Dictionary)).Value));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, image.EncodedData.ToArray());
        Assert.Equal("DeviceRGB", Assert.IsType<PdfName>(image.Dictionary[Name("ColorSpace")]).ValueAsLatin1());
        Assert.Equal(2, Assert.IsType<PdfInteger>(image.Dictionary[Name("Width")]).Value);
        Assert.Equal(1, Assert.IsType<PdfInteger>(image.Dictionary[Name("Height")]).Value);
        Assert.False(image.Dictionary.ContainsKey(Name("Filter")));
        Assert.False(formStream.Dictionary.ContainsKey(Name("Font")));
        var frame = new PdfPageRenderer(target).Render(0, new PdfRenderOptions(4, 1));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void DirectPagesAndNestedFormsShareOneAssetByIdentity()
    {
        var asset = Extract();
        var inner = new PdfFormXObject(2, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 2, 1));
        var outer = new PdfFormXObject(2, 1, new PdfContentStreamBuilder().DrawForm(inner, 0, 0));
        var document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 2, 1).DrawImage(asset, 0, 0, 2, 1))
            .AddPage(2, 1, new PdfContentStreamBuilder().DrawForm(outer, 0, 0)).Build());
        var pages = PdfPageTree.Read(document).Pages;
        var direct = Assert.IsType<PdfIndirectReference>(Assert.Single(XObjects(document, pages[0].Dictionary)).Value);
        var outerStream = Assert.IsType<PdfStream>(Resolve(document, Assert.Single(XObjects(document, pages[1].Dictionary)).Value));
        var innerStream = Assert.IsType<PdfStream>(Resolve(document, Assert.Single(XObjects(document, outerStream.Dictionary)).Value));
        var nested = Assert.IsType<PdfIndirectReference>(Assert.Single(XObjects(document, innerStream.Dictionary)).Value);
        Assert.Equal(direct.ObjectNumber, nested.ObjectNumber);
        Assert.Single(new PdfPageContentReader(document).Read(1).Images);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 },
            new PdfPageRenderer(document).Render(1, new PdfRenderOptions(2, 1)).Pixels.ToArray());
    }

    [Fact]
    public void DistinctEqualSizedAssetsRemainDistinctAndResourcePrefixesDoNotCollide()
    {
        var first = Extract();
        var second = Extract();
        var ordinary = PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 255 });
        var form = new PdfFormXObject(1, 1, new PdfContentStreamBuilder().DrawImage(ordinary, 0, 0, 1, 1));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(6, 1,
            new PdfContentStreamBuilder().DrawImage(first, 0, 0, 2, 1).DrawImage(second, 2, 0, 2, 1)
                .DrawImage(ordinary, 4, 0, 1, 1).DrawForm(form, 5, 0)).Build());
        var objects = XObjects(document, PdfPageTree.Read(document).Pages[0].Dictionary);
        Assert.Equal(new[] { "Ia1", "Ia2", "Im1", "Fm1" }.Order(), objects.Keys.Select(key => key.ValueAsLatin1()).Order());
        Assert.Equal(4, objects.Values.Select(value => Assert.IsType<PdfIndirectReference>(value).ObjectNumber).Distinct().Count());
        Assert.Equal(4, new PdfPageContentReader(document).Read(0).Images.Count);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 },
            new PdfPageRenderer(document).Render(0, new PdfRenderOptions(6, 1)).Pixels.ToArray());
    }

    [Theory]
    [InlineData(2, 0, -2, 1, true)]
    [InlineData(0, 1, 2, -1, false)]
    [InlineData(2, 1, -2, -1, true)]
    public void NegativeDimensionsFlipTheOriginalImage(double x, double y, double width, double height, bool reversed)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(Extract(), x, y, width, height)).Build());
        Assert.Equal(new PdfContentBounds(0, 0, 2, 1), Assert.Single(new PdfPageContentReader(document).Read(0).Images).BoundingBox);
        Assert.Equal(reversed ? new byte[] { 0, 255, 0, 255, 0, 0, 255, 255 } : new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 },
            new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 1)).Pixels.ToArray());
    }

    [Fact]
    public void ColoredPatternKeepsItsExactImageResourceAndRenderedPixels()
    {
        var pattern = new PdfTilingPattern(2, 1, new PdfContentStreamBuilder().DrawImage(Extract(), 0, 0, 2, 1));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 1,
            new PdfContentStreamBuilder().SetFillPattern(pattern).Rectangle(0, 0, 4, 1).Fill()).Build());
        var page = PdfPageTree.Read(document).Pages[0].Dictionary;
        var resources = Assert.IsType<PdfDictionary>(Resolve(document, page[Name("Resources")]));
        var patterns = Assert.IsType<PdfDictionary>(Resolve(document, resources[Name("Pattern")]));
        var stream = Assert.IsType<PdfStream>(Resolve(document, Assert.Single(patterns).Value));
        var image = Assert.IsType<PdfStream>(Resolve(document, Assert.Single(XObjects(document, stream.Dictionary)).Value));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, image.EncodedData.ToArray());
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(4, 1));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void TypedStampImportsOnlyTheAuthoredAssetAppearance()
    {
        var asset = Extract();
        var appearance = new PdfFormXObject(4, 1, new PdfContentStreamBuilder()
            .DrawImage(asset, 0, 0, 2, 1).DrawImage(asset, 2, 0, 2, 1));
        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(4, 1).Build());
        var document = PdfDocument.Open(new PdfIncrementalAnnotationEditor(blank).AddStamp(0, 0, 0, 4, 1, appearance).Build());
        var page = PdfPageTree.Read(document).Pages[0].Dictionary;
        var annotations = Assert.IsType<PdfArray>(Resolve(document, page[Name("Annots")]));
        var annotation = Assert.IsType<PdfDictionary>(Resolve(document, Assert.Single(annotations)));
        var ap = Assert.IsType<PdfDictionary>(Resolve(document, annotation[Name("AP")]));
        var normal = Assert.IsType<PdfStream>(Resolve(document, ap[Name("N")]));
        var image = Assert.IsType<PdfStream>(Resolve(document, Assert.Single(XObjects(document, normal.Dictionary)).Value));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, image.EncodedData.ToArray());
        var frame = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(4, 1));
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 255, 0, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void RawImageUsesPdf10AndFlateImageRequiresPdf12WithoutReencoding()
    {
        var raw = PdfDocument.Open(new PdfDocumentBuilder(PdfVersion.Pdf10).AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(Extract(), 0, 0, 2, 1)).Build());
        Assert.Equal(PdfVersion.Pdf10, raw.Header.Version);
        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(new byte[] { 255, 0, 0, 0, 255, 0 });
            compressed = output.ToArray();
        }
        var source = PdfDocument.Open(Fixture());
        var original = Assert.IsType<PdfStream>(source.Resolve(5));
        var entries = original.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        entries[Name("Filter")] = Name("FlateDecode");
        entries[Name("Interpolate")] = new PdfBoolean(false);
        source = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(5, new PdfStream(new PdfDictionary(entries), compressed)).Build());
        var asset = PdfImagePlacementExtractor.Extract(source, new PdfPageContentReader(source).ReadImagePlacements(0)[1]);
        Assert.Throws<InvalidOperationException>(() => new PdfDocumentBuilder(new PdfVersion(1, 1)).AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 2, 1)).Build());
        var target = PdfDocument.Open(new PdfDocumentBuilder(new PdfVersion(1, 2)).AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 2, 1)).Build());
        Assert.Equal(new PdfVersion(1, 2), target.Header.Version);
        var image = Assert.IsType<PdfStream>(Resolve(target, Assert.Single(XObjects(target, PdfPageTree.Read(target).Pages[0].Dictionary)).Value));
        Assert.Equal(compressed, image.EncodedData.ToArray());
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(image.Dictionary[Name("Filter")]).ValueAsLatin1());
        Assert.False(Assert.IsType<PdfBoolean>(image.Dictionary[Name("Interpolate")]).Value);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 },
            new PdfPageRenderer(target).Render(0, new PdfRenderOptions(2, 1)).Pixels.ToArray());
    }

    [Theory]
    [InlineData(double.NaN, 0, 2, 1)]
    [InlineData(0, double.PositiveInfinity, 2, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 2, 0)]
    [InlineData(0, 0, double.NaN, 1)]
    [InlineData(0, 0, 2, double.NegativeInfinity)]
    public void InvalidGeometryCannotRecordImageContent(double x, double y, double width, double height)
    {
        var content = new PdfContentStreamBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => content.DrawImage(Extract(), x, y, width, height));
        Assert.Empty(content.Build());
    }

    [Fact]
    public void NullAssetCannotRecordImageContent()
    {
        var content = new PdfContentStreamBuilder();
        Assert.Throws<ArgumentNullException>(() => content.DrawImage((PdfImageAsset)null!, 0, 0, 2, 1));
        Assert.Empty(content.Build());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncoloredPatternRejectsColoredAssetsIncludingNestedForms(bool nestedForm)
    {
        var content = new PdfContentStreamBuilder().DrawImage(Extract(), 0, 0, 2, 1);
        if (nestedForm)
            content = new PdfContentStreamBuilder().DrawForm(new PdfFormXObject(2, 1, content), 0, 0);
        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(2, 1, content,
            paintType: PdfTilingPatternPaintType.Uncolored));
    }

    [Fact]
    public void UncoloredPatternRejectsAnAssetInsideAColoredPatternInAForm()
    {
        var colored = new PdfTilingPattern(2, 1,
            new PdfContentStreamBuilder().DrawImage(Extract(), 0, 0, 2, 1));
        var form = new PdfFormXObject(2, 1, new PdfContentStreamBuilder()
            .SetFillPattern(colored).Rectangle(0, 0, 2, 1).Fill());
        var content = new PdfContentStreamBuilder().DrawForm(form, 0, 0);
        Assert.Throws<ArgumentException>(() => new PdfTilingPattern(2, 1, content,
            paintType: PdfTilingPatternPaintType.Uncolored));
    }

    private static PdfImageAsset Extract()
    {
        var source = PdfDocument.Open(Fixture());
        return PdfImagePlacementExtractor.Extract(source, new PdfPageContentReader(source).ReadImagePlacements(0)[1]);
    }

    private static PdfDictionary XObjects(PdfDocument document, PdfDictionary owner)
    {
        var resources = Assert.IsType<PdfDictionary>(Resolve(document, owner[Name("Resources")]));
        Assert.False(resources.ContainsKey(Name("Font")));
        return Assert.IsType<PdfDictionary>(Resolve(document, resources[Name("XObject")]));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] Fixture()
    {
        const string content = "q 20 0 0 20 10 10 cm /Im0 Do Q 0 0 1 rg 40 10 10 10 re f BT /F1 8 Tf 10 70 Td (Keep) Tj ET q 20 0 0 20 70 10 cm /Im0 Do Q";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> /Font << /F1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 6 >>\nstream\n" + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0]) + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = text.Length;
        text.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        text.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
