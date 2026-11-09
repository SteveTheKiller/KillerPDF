using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfFreeTextAppearanceTests
{
    [Fact]
    public void TypedAppearanceRendersAboveOriginalCommentAndPreservesLogicalText()
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        byte[] originalBytes = new PdfIncrementalAnnotationEditor(blank)
            .AddRectangle(0, 10, 10, 80, 80, fillColor: new PdfRgbColor(0, 0, 1),
                lineWidth: 1, contents: "Original opaque comment", name: "original")
            .Build();
        PdfDocument original = PdfDocument.Open(originalBytes);
        PdfDictionary originalPage = PdfPageTree.Read(original).Pages[0].Dictionary;
        PdfObject originalIdentity = Assert.Single(Assert.IsType<PdfArray>(originalPage[N("Annots")]));
        byte[] originalComment = PdfObjectWriter.Write(D(original, originalIdentity));
        var square = new PdfFormXObject(10, 10,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 0, 10, 10).Fill());
        var appearance = new PdfFormXObject(40, 40, new PdfContentStreamBuilder()
            .DrawForm(square, 0, 0)
            .SetFillRgb(0, 1, 0).Rectangle(20, 20, 10, 10).Fill()
            .SetFillRgb(0, 0, 0).BeginText().SetFont(PdfStandardFont.Helvetica, 10)
            .SetTextMatrix(0, 1, -1, 0, 18, 2).ShowLatin1Text("HI").EndText());
        const string contents = "Full logical text\nSecond line: \u03a9 \u4e2d";
        var editor = new PdfIncrementalAnnotationEditor(original);
        editor.AddFreeText(0, 30d, 30d, 40d, 40d, contents, appearance,
            new PdfAnnotationMetadata { Author = "Reviewer", Subject = "Text drawing" });
        byte[] saved = editor.Build();
        Assert.True(saved.AsSpan(0, originalBytes.Length).SequenceEqual(originalBytes));
        PdfDocument reopened = PdfDocument.Open(saved);
        PdfDictionary page = PdfPageTree.Read(reopened).Pages[0].Dictionary;
        var annotations = Assert.IsType<PdfArray>(page[N("Annots")]);
        Assert.Equal(2, annotations.Count);
        Assert.Equal(PdfObjectWriter.Write(originalIdentity), PdfObjectWriter.Write(annotations[0]));
        Assert.Equal(originalComment, PdfObjectWriter.Write(D(reopened, annotations[0])));
        PdfDictionary added = D(reopened, annotations[1]);
        Assert.Equal(N("FreeText"), added[N("Subtype")]);
        Assert.Equal(contents, Text(added[N("Contents")]));
        Assert.Equal("Reviewer", Text(added[N("T")]));
        Assert.Equal("Text drawing", Text(added[N("Subj")]));
        Assert.Equal("[30 30 70 70]", Encoding.ASCII.GetString(PdfObjectWriter.Write(added[N("Rect")])));
        var rendered = new PdfPageRenderer(reopened).Render(0, new PdfRenderOptions(100, 100));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        AssertPixel(pixels, 35, 65, 255, 0, 0);
        AssertPixel(pixels, 55, 45, 0, 255, 0);
        AssertPixel(pixels, 75, 25, 0, 0, 255);
        AssertPixel(pixels, 5, 5, 255, 255, 255);
        // Rotated HI is a narrow vertical band next to the red square.
        Assert.Contains(Enumerable.Range(54, 14).SelectMany(y => Enumerable.Range(40, 9)
            .Select(x => pixels.AsSpan((y * 100 + x) * 4, 3).ToArray())),
            pixel => pixel.All(component => component < 20));
    }

    [Fact]
    public void TypedAppearanceImportsEmbeddedFontsAndImageSoftMasks()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(
            format12: false, includeOutlines: true));
        var image = PdfImage.FromRgba(1, 1, new byte[] { 255, 0, 0, 128 });
        var appearance = new PdfFormXObject(40, 40, new PdfContentStreamBuilder()
            .DrawImage(image, 20, 20, 10, 10)
            .BeginText().SetFont(font, 20).MoveText(2, 2).ShowUnicodeText("A").EndText());
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfDocument saved = PdfDocument.Open(new PdfIncrementalAnnotationEditor(source)
            .AddFreeText(0, 30, 30, 40, 40, "A", appearance).Build());
        var rendered = new PdfPageRenderer(saved).Render(0, new PdfRenderOptions(100, 100));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        int imageOffset = (45 * 100 + 55) * 4;
        Assert.InRange(pixels[imageOffset], (byte)126, (byte)128);
        Assert.InRange(pixels[imageOffset + 1], (byte)126, (byte)128);
        Assert.Equal(255, pixels[imageOffset + 2]);
        Assert.Contains(Enumerable.Range(50, 18).SelectMany(y => Enumerable.Range(32, 14)
            .Select(x => pixels.AsSpan((y * 100 + x) * 4, 3).ToArray())),
            pixel => pixel.All(component => component < 20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedStampsAndFreeTextKeepInsertionOrderAboveOriginalComment(bool coverAbove)
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfDocument original = PdfDocument.Open(new PdfIncrementalAnnotationEditor(blank)
            .AddRectangle(0, 10, 10, 80, 80, fillColor: new PdfRgbColor(0, 0, 1),
                contents: "Original opaque comment", name: "original").Build());
        var originalPage = PdfPageTree.Read(original).Pages[0].Dictionary;
        PdfObject identity = Assert.Single(Assert.IsType<PdfArray>(originalPage[N("Annots")]));
        byte[] originalComment = PdfObjectWriter.Write(D(original, identity));
        var textAppearance = new PdfFormXObject(30, 30, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 30, 30).Fill());
        var coverAppearance = new PdfFormXObject(30, 30, new PdfContentStreamBuilder()
            .SetFillRgb(1, 1, 1).Rectangle(0, 0, 30, 30).Fill());
        var editor = new PdfIncrementalAnnotationEditor(original);
        if (coverAbove)
            editor.AddFreeText(0, 30d, 30d, 30d, 30d, "Logical drawing", textAppearance);
        editor.AddStamp(0, 30d, 30d, 30d, 30d, coverAppearance, "Opaque cover",
            new PdfAnnotationMetadata { Author = "Reviewer" });
        if (!coverAbove)
            editor.AddFreeText(0, 30d, 30d, 30d, 30d, "Logical drawing", textAppearance);
        PdfDocument reopened = PdfDocument.Open(editor.Build());
        var annotations = Assert.IsType<PdfArray>(PdfPageTree.Read(reopened).Pages[0].Dictionary[N("Annots")]);
        Assert.Equal(3, annotations.Count);
        Assert.Equal(PdfObjectWriter.Write(identity), PdfObjectWriter.Write(annotations[0]));
        Assert.Equal(originalComment, PdfObjectWriter.Write(D(reopened, annotations[0])));
        Assert.Equal(coverAbove ? N("FreeText") : N("Stamp"), D(reopened, annotations[1])[N("Subtype")]);
        Assert.Equal(coverAbove ? N("Stamp") : N("FreeText"), D(reopened, annotations[2])[N("Subtype")]);
        var stamp = D(reopened, annotations[coverAbove ? 2 : 1]);
        Assert.Equal("Opaque cover", Text(stamp[N("Contents")]));
        Assert.Equal("Reviewer", Text(stamp[N("T")]));
        var rendered = new PdfPageRenderer(reopened).Render(0, new PdfRenderOptions(100, 100));
        Assert.Empty(rendered.Diagnostics);
        byte[] pixels = rendered.Pixels.ToArray();
        AssertPixel(pixels, 45, 55, 255, coverAbove ? (byte)255 : (byte)0, coverAbove ? (byte)255 : (byte)0);
        AssertPixel(pixels, 75, 25, 0, 0, 255);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedAnnotationsHonorPasswordAndCertificationGuards(bool stamp)
    {
        var appearance = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .Rectangle(0, 0, 10, 10).Fill());
        var encrypted = PdfDocument.Open(new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "reader", OwnerPassword = "owner", AllowAnnotationModification = false
            }).AddBlankPage(100, 100).Build(), "reader");
        Assert.Throws<InvalidOperationException>(() => Add(encrypted).Build());

        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        var update = new PdfIncrementalUpdateBuilder(blank);
        var signature = update.AddObject(new PdfDictionary([
            new(N("Type"), N("Sig")), new(N("Reference"), new PdfArray([
                new PdfDictionary([
                    new(N("TransformMethod"), N("DocMDP")),
                    new(N("TransformParams"), new PdfDictionary([
                        new(N("Type"), N("TransformParams")), new(N("P"), new PdfInteger(2)),
                        new(N("V"), N("1.2"))]))])]))]));
        var catalogReference = Assert.IsType<PdfIndirectReference>(blank.Trailer[N("Root")]);
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(D(blank, catalogReference)
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Perms"), new PdfDictionary([
                new(N("DocMDP"), signature)])))));
        var certified = PdfDocument.Open(update.Build());
        Assert.Throws<InvalidOperationException>(() => Add(certified).Build());

        PdfIncrementalAnnotationEditor Add(PdfDocument document)
        {
            var editor = new PdfIncrementalAnnotationEditor(document);
            return stamp ? editor.AddStamp(0, 10, 10, 10, 10, appearance)
                : editor.AddFreeText(0, 10, 10, 10, 10, "Text", appearance);
        }
    }

    [Fact]
    public void TypedStampRejectsInvalidPageRectangleAndMissingAppearance()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        var editor = new PdfIncrementalAnnotationEditor(document);
        var appearance = new PdfFormXObject(10, 10, new PdfContentStreamBuilder());
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddStamp(1, 0, 0, 10, 10, appearance));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddStamp(0, double.NaN, 0, 10, 10, appearance));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.AddStamp(0, 0, 0, 0, 10, appearance));
        Assert.Throws<ArgumentNullException>(() => editor.AddStamp(0, 0, 0, 10, 10, null!));
        Assert.False(editor.HasChanges);
    }

    [Fact]
    public void RawPageAnnotationCountIncludesEntriesSkippedByCommentReading()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(100, 100).AddBlankPage(100, 100).Build());
        var page = PdfPageTree.Read(document).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(document);
        var link = update.AddObject(new PdfDictionary([new(N("Subtype"), N("Link"))]));
        var widget = update.AddObject(new PdfDictionary([new(N("Subtype"), N("Widget"))]));
        var unknown = update.AddObject(new PdfDictionary([new(N("Subtype"), N("ForeignDrawing"))]));
        var direct = new PdfDictionary([
            new(N("Subtype"), N("Text")),
            new(N("Contents"), new PdfString("Direct comment"u8.ToArray(), PdfStringForm.Literal))]);
        var annotations = update.AddObject(new PdfArray([link, widget, unknown, direct]));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(page.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(N("Annots"), annotations))));
        var reopened = PdfDocument.Open(update.Build());
        Assert.Single(PdfCommentReader.Read(reopened, true));
        Assert.Equal(4, PdfCommentReader.ReadPageAnnotationCount(reopened, 0));
        Assert.Equal(0, PdfCommentReader.ReadPageAnnotationCount(reopened, 1));
        Assert.Equal([4, 0], PdfCommentReader.ReadPageAnnotationCounts(reopened));
    }

    [Fact]
    public void RawPageAnnotationCountRejectsNullDocumentAndInvalidPages()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        Assert.Throws<ArgumentNullException>(() => PdfCommentReader.ReadPageAnnotationCount(null!, 0));
        Assert.Throws<ArgumentNullException>(() => PdfCommentReader.ReadPageAnnotationCounts(null!));
        Assert.Equal([0], PdfCommentReader.ReadPageAnnotationCounts(document));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfCommentReader.ReadPageAnnotationCount(document, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfCommentReader.ReadPageAnnotationCount(document, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RawPageAnnotationCountRejectsMalformedAnnotationArray(bool cyclic)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        var page = PdfPageTree.Read(document).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfObject value = new PdfInteger(7);
        if (cyclic)
        {
            var reference = update.ReserveObject();
            update.SetObject(reference, reference);
            value = reference;
        }
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(page.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(N("Annots"), value))));
        var reopened = PdfDocument.Open(update.Build());
        Assert.Throws<InvalidOperationException>(() => PdfCommentReader.ReadPageAnnotationCount(reopened, 0));
        Assert.Throws<InvalidOperationException>(() => PdfCommentReader.ReadPageAnnotationCounts(reopened));
    }

    private static void AssertPixel(byte[] pixels, int x, int y, byte r, byte g, byte b)
    {
        int offset = (y * 100 + x) * 4;
        Assert.Equal(new byte[] { b, g, r, 255 }, pixels.AsSpan(offset, 4).ToArray());
    }

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary D(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);
    private static string Text(PdfObject value) => PdfUnicodeEncoding.DecodeTextString(
        Assert.IsType<PdfString>(value).Bytes.Span, "annotation text");
}
