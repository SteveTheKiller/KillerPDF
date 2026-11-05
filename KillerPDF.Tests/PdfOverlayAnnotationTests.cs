using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfOverlayAnnotationTests
{
    [Fact]
    public void WholeTextBoxSurvivesReopenWithItsEditingState()
    {
        var text = new TextAnnotation
        {
            Position = new Point(12, 18), Content = "First line\nSecond line", Width = 140,
            Height = 60, FontName = "Arial", Bold = true, Italic = true, Underline = true,
            Strike = true, LetterSpacing = 1.25, BlurRadius = 0.5, Rotation = 90,
            BgA = 150, PairId = "pair-1", GroupId = "group-1"
        };
        PdfDocument source = NativeText(text.Content);
        PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(source, true));
        byte[] saved = PdfOverlayAnnotations.Attach(source,
            [new(text, 400, 600, 0, comment.AnnotationIndex, comment.ObjectNumber!.Value)]);
        OverlayRecord reopened = Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(saved)));
        TextAnnotation actual = Assert.IsType<TextAnnotation>(reopened.Annotation);
        Assert.Equal("First line\nSecond line", actual.Content);
        Assert.Equal(new Point(12, 18), actual.Position);
        Assert.Equal(140, actual.Width);
        Assert.Equal(60, actual.Height);
        Assert.Equal("Arial", actual.FontName);
        Assert.True(actual.Bold && actual.Italic && actual.Underline && actual.Strike);
        Assert.Equal(1.25, actual.LetterSpacing);
        Assert.Equal(0.5, actual.BlurRadius);
        Assert.Equal(90, actual.Rotation);
        Assert.Equal(150, actual.BgA);
        Assert.Equal("pair-1", actual.PairId);
        Assert.Equal("group-1", actual.GroupId);
        Assert.Equal(text.HistoryId, actual.HistoryId);
        Assert.Equal(400, reopened.RenderWidth);
        Assert.Equal(600, reopened.RenderHeight);
    }

    [Fact]
    public void EditableTailRemovesNativeCopyAndReturnsWholeModel()
    {
        byte[] saved = SavedText();
        OverlayExtraction extraction = PdfOverlayAnnotations.TakeEditableTail(saved);
        Assert.Equal("Synthetic text", Assert.IsType<TextAnnotation>(Assert.Single(extraction.Records).Annotation).Content);
        Assert.Empty(PdfCommentReader.Read(PdfDocument.Open(extraction.Bytes), true));
        Assert.Equal("Synthetic text", Assert.Single(PdfCommentReader.Read(PdfDocument.Open(saved), true)).Contents);
        Assert.Equal(saved, extraction.Bytes[..saved.Length]);
    }

    [Fact]
    public void HiddenForeignPrefixDoesNotPreventAnIntactTailFromBeingEdited()
    {
        PdfDocument native = NativeText("Synthetic text");
        PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(native, true));
        byte[] withForeignPrefix = ReplaceAnnotation(native, comment.ObjectNumber!.Value,
            d => Set(d, "Contents", new PdfString([], PdfStringForm.Literal)));
        PdfDocument prefix = PdfDocument.Open(withForeignPrefix);
        byte[] appended = new PdfIncrementalAnnotationEditor(prefix).AddFreeText(0, 10, 20, 140, 60,
            "Appended", new PdfFormXObject(140, 60, new PdfContentStreamBuilder())).Build();
        PdfDocument both = PdfDocument.Open(appended);
        PdfCommentInfo tail = Assert.Single(PdfCommentReader.Read(both, true), item => item.Contents == "Appended");
        byte[] saved = PdfOverlayAnnotations.Attach(both,
            [new(new TextAnnotation { Content = "Appended" }, 400, 600, 0, tail.AnnotationIndex, tail.ObjectNumber!.Value)]);
        OverlayExtraction result = PdfOverlayAnnotations.TakeEditableTail(saved);
        Assert.Single(result.Records);
        Assert.Equal(1, PdfCommentReader.ReadPageAnnotationCount(PdfDocument.Open(result.Bytes), 0));
    }

    [Fact]
    public void IntendedRotationBecomesEditableOnlyWhenFinalPageRotationMatches()
    {
        PdfDocument native = NativeText("Rotated text");
        PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(native, true));
        byte[] pending = PdfOverlayAnnotations.Attach(native,
            [new(new TextAnnotation { Content = "Rotated text" }, 600, 400, 90, 0, comment.ObjectNumber!.Value)]);
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(pending)));
        Assert.Same(pending, PdfOverlayAnnotations.TakeEditableTail(pending).Bytes);
        byte[] final = new PdfIncrementalPageEditor(PdfDocument.Open(pending)).SetRotation(0, 90).Build();
        Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(final)));
        Assert.Single(PdfOverlayAnnotations.TakeEditableTail(final).Records);
    }

    [Theory]
    [InlineData("Cover")]
    [InlineData("Highlight")]
    [InlineData("Ink")]
    [InlineData("Signature")]
    [InlineData("Image")]
    public void OtherOverlayKindsRetainGeometryIdentityAndEditingState(string kind)
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=";
        PageAnnotation model = kind switch
        {
            "Cover" => new CoverAnnotation { Bounds = new Rect(5, 6, 70, 80), ColorR = 17 },
            "Highlight" => new HighlightAnnotation { Bounds = new Rect(5, 6, 70, 80), Erases = [new() { Radius = 3, Points = [new(7, 8), new(9, 10)] }] },
            "Ink" => new InkAnnotation { StrokeWidth = 4.5, Points = [new(7, 8), new(9, 10)], FillA = 17 },
            "Signature" => new SignatureAnnotation { Position = new(5, 6), Scale = 0.75, Strokes = [[new(7, 8), new(9, 10)]], Vectors = [new() { Data = "M 0 0 L 10 10", Stroke = 0xff000000 }] },
            "Image" => new ImageAnnotation { Position = new(5, 6), ImageData = png, Scale = 0.75 },
            _ => throw new ArgumentException(kind)
        };
        model.PairId = "linked";
        model.GroupId = "grouped";
        byte[] native = new PdfIncrementalAnnotationEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage(200, 300).Build()))
            .AddStamp(0, 10, 20, 140, 60, new PdfFormXObject(140, 60, new PdfContentStreamBuilder())).Build();
        PdfDocument document = PdfDocument.Open(native);
        PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(document, true));
        byte[] saved = PdfOverlayAnnotations.Attach(document,
            [new(model, 400, 600, 0, 0, comment.ObjectNumber!.Value)]);
        Assert.Equal(native, saved[..native.Length]);
        PageAnnotation actual = Assert.Single(PdfOverlayAnnotations.TakeEditableTail(saved).Records).Annotation;
        Assert.Equal(model.GetType(), actual.GetType());
        Assert.Equal(model.HistoryId, actual.HistoryId);
        Assert.Equal("linked", actual.PairId);
        Assert.Equal("grouped", actual.GroupId);
        switch (actual)
        {
            case CoverAnnotation cover: Assert.Equal(new Rect(5, 6, 70, 80), cover.Bounds); Assert.Equal(17, cover.ColorR); break;
            case HighlightAnnotation highlight: Assert.Equal(3, Assert.Single(highlight.Erases!).Radius); Assert.Equal(new Point(9, 10), highlight.Erases![0].Points[1]); break;
            case InkAnnotation ink: Assert.Equal(4.5, ink.StrokeWidth); Assert.Equal(17, ink.FillA); Assert.Equal(new Point(7, 8), ink.Points[0]); break;
            case SignatureAnnotation signature: Assert.Equal(0.75, signature.Scale); Assert.Equal("M 0 0 L 10 10", Assert.Single(signature.Vectors!).Data); Assert.Equal(new Point(9, 10), signature.Strokes[0][1]); break;
            case ImageAnnotation image: Assert.Equal(png, image.ImageData); Assert.Equal(new Point(5, 6), image.Position); break;
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("json")]
    [InlineData("contents")]
    [InlineData("missing-property")]
    [InlineData("number")]
    [InlineData("oversized")]
    [InlineData("reference")]
    public void UnsupportedOrCorruptMetadataStaysNativeAndLeavesBytesUnchanged(string corruption)
    {
        byte[] saved = SavedText();
        PdfDocument document = PdfDocument.Open(saved);
        PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(document, true));
        byte[] changed = ReplaceAnnotation(document, comment.ObjectNumber!.Value, annotation =>
        {
            var metadata = (PdfDictionary)annotation[Name("KillerPDFOverlay")];
            if (corruption == "version") return Set(annotation, "KillerPDFOverlay", Set(metadata, "V", new PdfInteger(2)));
            if (corruption == "contents") return Set(annotation, "Contents", new PdfString("Changed externally"u8, PdfStringForm.Literal));
            if (corruption == "reference") return Set(annotation, "AP", new PdfIndirectReference(int.MaxValue, 0));
            if (corruption == "json") return Set(annotation, "KillerPDFOverlay", Set(metadata, "Data", new PdfString("{"u8, PdfStringForm.Literal)));
            if (corruption == "oversized") return Set(annotation, "KillerPDFOverlay", Set(metadata, "Data", new PdfString(new byte[8 * 1024 * 1024 + 1], PdfStringForm.Hexadecimal)));
            JsonObject payload = JsonNode.Parse(((PdfString)metadata[Name("Data")]).Bytes.Span)!.AsObject();
            if (corruption == "kind") payload["Kind"] = "System.IO.FileInfo";
            if (corruption == "missing-property") payload["Model"]!.AsObject().Remove("FontName");
            if (corruption == "number") payload["Model"]!["Width"] = -1;
            return Set(annotation, "KillerPDFOverlay", Set(metadata, "Data",
                new PdfString(Encoding.UTF8.GetBytes(payload.ToJsonString()), PdfStringForm.Hexadecimal)));
        });
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(changed)));
        OverlayExtraction extraction = PdfOverlayAnnotations.TakeEditableTail(changed);
        Assert.Empty(extraction.Records);
        Assert.Same(changed, extraction.Bytes);
        Assert.Equal(1, PdfCommentReader.ReadPageAnnotationCount(PdfDocument.Open(changed), 0));
    }

    [Fact]
    public void AForeignAppearanceEditDisablesTheStoredFormatting()
    {
        byte[] saved = SavedText();
        PdfDocument document = PdfDocument.Open(saved);
        PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(document, true));
        PdfDictionary annotation = (PdfDictionary)document.Resolve(comment.ObjectNumber!.Value);
        PdfDictionary ap = (PdfDictionary)annotation[Name("AP")];
        PdfIndirectReference normal = Assert.IsType<PdfIndirectReference>(ap[Name("N")]);
        PdfStream appearance = Assert.IsType<PdfStream>(document.Resolve(normal));
        byte[] changed = new PdfIncrementalUpdateBuilder(document).ReplaceObject(normal.ObjectNumber,
            new PdfStream(appearance.Dictionary, "q 1 0 0 rg 0 0 100 40 re f Q"u8)).Build();
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(changed)));
        Assert.Same(changed, PdfOverlayAnnotations.TakeEditableTail(changed).Bytes);
    }

    [Fact]
    public void LaterForeignAnnotationPreventsEarlierOwnedBoxFromMovingAboveIt()
    {
        byte[] saved = SavedText();
        byte[] later = new PdfIncrementalAnnotationEditor(PdfDocument.Open(saved)).AddFreeText(
            0, 10, 20, 140, 60, "Foreign", new PdfFormXObject(140, 60, new PdfContentStreamBuilder())).Build();
        Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(later)));
        Assert.Empty(PdfOverlayAnnotations.TakeEditableTail(later).Records);
        PdfDocument doc = PdfDocument.Open(later);
        PdfCommentInfo tail = PdfCommentReader.Read(doc, true)[1];
        byte[] hidden = ReplaceAnnotation(doc, tail.ObjectNumber!.Value, d => Set(d, "Contents", new PdfString([], PdfStringForm.Literal)));
        Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(hidden)));
        Assert.Empty(PdfOverlayAnnotations.TakeEditableTail(hidden).Records);
        Assert.Same(hidden, PdfOverlayAnnotations.TakeEditableTail(hidden).Bytes);
    }

    [Fact]
    public void InvalidOwnedMemberPreservesItsPagesCompleteNativeStack()
    {
        byte[] first = SavedText();
        byte[] next = new PdfIncrementalAnnotationEditor(PdfDocument.Open(first)).AddFreeText(0, 10, 20, 140, 60,
            "Tail", new PdfFormXObject(140, 60, new PdfContentStreamBuilder())).Build();
        PdfDocument document = PdfDocument.Open(next);
        PdfCommentInfo tail = PdfCommentReader.Read(document, true)[1];
        byte[] saved = PdfOverlayAnnotations.Attach(document,
            [new(new TextAnnotation { Content = "Tail" }, 400, 600, 0, 1, tail.ObjectNumber!.Value)]);
        PdfDocument both = PdfDocument.Open(saved);
        int firstNumber = PdfCommentReader.Read(both, true)[0].ObjectNumber!.Value;
        byte[] changed = ReplaceAnnotation(both, firstNumber, d => Set(d, "Contents", new PdfString("Foreign edit"u8, PdfStringForm.Literal)));
        Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(changed)));
        Assert.Empty(PdfOverlayAnnotations.TakeEditableTail(changed).Records);
        Assert.Same(changed, PdfOverlayAnnotations.TakeEditableTail(changed).Bytes);
    }

    [Fact]
    public void MatchingNativeNameWithoutMetadataDoesNotClaimAnAnnotation()
    {
        PdfDocument native = NativeText("Foreign text");
        int number = Assert.Single(PdfCommentReader.Read(native, true)).ObjectNumber!.Value;
        byte[] forgedName = ReplaceAnnotation(native, number, d => Set(d, "NM", new PdfString("KillerPDFOverlay-pretend"u8, PdfStringForm.Literal)));
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(forgedName)));
        Assert.Same(forgedName, PdfOverlayAnnotations.TakeEditableTail(forgedName).Bytes);
    }

    [Fact]
    public void SharedCyclicAppearanceResourcesAreBoundedAndForeignResourceEditsStayNative()
    {
        PdfDocument native = NativeText("Resource text");
        int number = Assert.Single(PdfCommentReader.Read(native, true)).ObjectNumber!.Value;
        PdfDictionary annotation = (PdfDictionary)native.Resolve(number);
        PdfDictionary ap = (PdfDictionary)annotation[Name("AP")];
        PdfIndirectReference normal = Assert.IsType<PdfIndirectReference>(ap[Name("N")]);
        PdfStream appearance = (PdfStream)native.Resolve(normal);
        var update = new PdfIncrementalUpdateBuilder(native);
        PdfIndirectReference resource = update.ReserveObject();
        update.SetObject(resource, new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [Name("Self")] = resource, [Name("Opacity")] = new PdfReal(0.5)
        }));
        var resources = new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [Name("SyntheticA")] = resource, [Name("SyntheticB")] = resource
        });
        update.ReplaceObject(normal.ObjectNumber, new PdfStream(Set(appearance.Dictionary, "Resources", resources), appearance.EncodedData.Span));
        PdfDocument linked = PdfDocument.Open(update.Build());
        byte[] saved = PdfOverlayAnnotations.Attach(linked,
            [new(new TextAnnotation { Content = "Resource text" }, 400, 600, 0, 0, number)]);
        Assert.Single(PdfOverlayAnnotations.Read(PdfDocument.Open(saved)));
        PdfDocument reopened = PdfDocument.Open(saved);
        byte[] edited = new PdfIncrementalUpdateBuilder(reopened).ReplaceObject(resource.ObjectNumber,
            Set((PdfDictionary)reopened.Resolve(resource), "Opacity", new PdfReal(0.75))).Build();
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(edited)));
        Assert.Same(edited, PdfOverlayAnnotations.TakeEditableTail(edited).Bytes);
    }

    [Fact]
    public void PageFrameChangePreservesNativeRepresentation()
    {
        byte[] saved = SavedText();
        byte[] rotated = new PdfIncrementalPageEditor(PdfDocument.Open(saved)).SetRotation(0, 90).Build();
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(rotated)));
        Assert.Same(rotated, PdfOverlayAnnotations.TakeEditableTail(rotated).Bytes);
    }

    [Fact]
    public void ExcessivelyDeepAppearanceGraphCannotBeAttached()
    {
        PdfDocument native = NativeText("Deep graph");
        int number = Assert.Single(PdfCommentReader.Read(native, true)).ObjectNumber!.Value;
        PdfObject nested = new PdfInteger(1);
        for (int i = 0; i < 40; i++) nested = new PdfArray([nested]);
        byte[] deep = ReplaceAnnotation(native, number, d => Set(d, "SyntheticGraph", nested));
        Assert.Throws<NotSupportedException>(() => PdfOverlayAnnotations.Attach(PdfDocument.Open(deep),
            [new(new TextAnnotation { Content = "Deep graph" }, 400, 600, 0, 0, number)]));
        Assert.Same(deep, PdfOverlayAnnotations.TakeEditableTail(deep).Bytes);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary Set(PdfDictionary source, string key, PdfObject value) =>
        new(source.Where(p => !p.Key.Equals(Name(key))).Append(new(Name(key), value)));
    private static byte[] ReplaceAnnotation(PdfDocument document, int number, Func<PdfDictionary, PdfDictionary> change) =>
        new PdfIncrementalUpdateBuilder(document).ReplaceObject(number,
            change((PdfDictionary)document.Resolve(number))).Build();

    private static byte[] SavedText()
    {
        PdfDocument native = NativeText("Synthetic text");
        PdfCommentInfo comment = Assert.Single(PdfCommentReader.Read(native, true));
        return PdfOverlayAnnotations.Attach(native,
            [new(new TextAnnotation { Content = "Synthetic text" }, 400, 600, 0,
                comment.AnnotationIndex, comment.ObjectNumber!.Value)]);
    }

    private static PdfDocument NativeText(string content) => PdfDocument.Open(
        new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 300).Build()))
            .AddFreeText(0, 10, 20, 140, 60, content,
                new PdfFormXObject(140, 60, new PdfContentStreamBuilder())).Build());
}
