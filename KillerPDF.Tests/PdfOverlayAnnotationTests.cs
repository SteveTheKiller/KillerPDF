using System.IO;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfOverlayAnnotationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HiddenReplacementAfterOrdinaryTextSurvivesAboveAnExistingComment(bool tagged, bool hideGroup)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-hidden-native-tail-{Guid.NewGuid():N}.pdf");
        try
        {
            var builder = new PdfDocumentBuilder();
            byte[] source = tagged ? builder
                .SetMetadata(new PdfDocumentMetadata { Title = "Hidden native tail", Language = "en-US" })
                .EnablePdfUa2Conformance()
                .AddPage(200, 100, new PdfContentStreamBuilder()
                    .BeginMarkedContent(PdfStructureType.Figure, 0)
                    .Rectangle(0, 0, 5, 5).Fill().EndMarkedContent())
                .AddStructureContainer(PdfStructureType.Document)
                .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original figure")
                .Build() : builder.AddBlankPage(200, 100).Build();
            var blank = PdfDocument.Open(source);
            File.WriteAllBytes(path, new PdfIncrementalAnnotationEditor(blank).AddRectangle(
                0, 0, 0, 200, 100, fillColor: new PdfRgbColor(1, 1, 1), contents: "Original comment").Build());
            var plain = new TextAnnotation { Content = "Ordinary", Position = new Point(10, 70), Width = 160, Height = 25 };
            var pair = new ReplacementPair(
                new TextAnnotation { PairId = "hidden-native-pair", Content = "Hidden replacement", Position = new Point(20, 20), Width = 150, Height = 40 },
                new CoverAnnotation { PairId = "hidden-native-pair", Bounds = new Rect(15, 15, 160, 50) });
            ReplacementLayers.SetLinked(pair, true);
            ReplacementLayers.SetVisibility(pair, hideGroup ? ReplacementLayer.Group : ReplacementLayer.Text, false);
            ReplacementLayers.SetMovementAllowed(pair, ReplacementLayer.Cover, false);
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [plain, pair.Cover, pair.Text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            byte[] saved = File.ReadAllBytes(path);
            Assert.Equal(3, PdfOverlayAnnotations.Read(PdfDocument.Open(saved)).Count);
            Assert.Equal(1, SearchService.Search(path, "Ordinary").TotalHits);
            Assert.Equal(0, SearchService.Search(path, "Hidden replacement").TotalHits);
            Assert.DoesNotContain(PdfCommentReader.Read(PdfDocument.Open(saved), true),
                comment => comment.Contents?.Contains("Hidden replacement", StringComparison.Ordinal) == true);
            if (tagged)
            {
                Assert.DoesNotContain(PdfAccessibilityReadingOrder.Read(PdfDocument.Open(saved)).Items,
                    item => item.AlternateDescription?.Contains("Hidden replacement", StringComparison.Ordinal) == true);
                if (hideGroup)
                    Assert.DoesNotContain(PdfAccessibilityReadingOrder.Read(PdfDocument.Open(saved)).Items,
                        item => item.AlternateDescription == "Markup");
            }
            OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(saved);
            Assert.Equal(3, extracted.Records.Count);
            CoverAnnotation restoredCover = Assert.IsType<CoverAnnotation>(extracted.Records[1].Annotation);
            Assert.False(ReplacementLayers.CanMove(restoredCover));
            TextAnnotation restored = Assert.IsType<TextAnnotation>(extracted.Records[2].Annotation);
            Assert.Equal("Hidden replacement", restored.Content);
            Assert.False(ReplacementLayers.IsVisible(restored));
            Assert.Equal(hideGroup, restored.IsVisible);
            Assert.True(restored.PairLinked);
            Assert.Equal(pair.Text.HistoryId, restored.HistoryId);
            Assert.Equal(pair.Cover.HistoryId, restoredCover.HistoryId);
            Assert.Equal("Original comment", Assert.Single(PdfCommentReader.Read(PdfDocument.Open(extracted.Bytes), true)).Contents);
            Assert.True(extracted.Bytes.AsSpan(0, saved.Length).SequenceEqual(saved));
            byte[] pixels = new PdfPageRenderer(PdfDocument.Open(saved))
                .Render(0, new PdfRenderOptions(200, 100)).Pixels.ToArray();
            File.WriteAllBytes(path, extracted.Bytes);
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>>
                { [0] = extracted.Records.Select(record => record.Annotation).ToList() },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            byte[] resaved = File.ReadAllBytes(path);
            Assert.Equal(pixels, new PdfPageRenderer(PdfDocument.Open(resaved))
                .Render(0, new PdfRenderOptions(200, 100)).Pixels.ToArray());
            Assert.Equal(3, PdfOverlayAnnotations.TakeEditableTail(resaved).Records.Count);
            Assert.Equal(0, SearchService.Search(path, "Hidden replacement").TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReplacementAddedAfterAnOrdinaryTextBoxRetainsHiddenLinkedEditingState()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-mixed-paired-overlay-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            var plain = new TextAnnotation { Content = "Ordinary", Position = new Point(10, 70), Width = 160, Height = 25 };
            var pair = new ReplacementPair(
                new TextAnnotation { PairId = "later-pair", Content = "Replacement", Position = new Point(20, 20), Width = 150, Height = 40 },
                new CoverAnnotation { PairId = "later-pair", Bounds = new Rect(15, 15, 160, 50) });
            ReplacementLayers.SetLinked(pair, true);
            ReplacementLayers.SetVisibility(pair, ReplacementLayer.Text, false);
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [plain, pair.Cover, pair.Text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path));
            Assert.Equal(3, extracted.Records.Count);
            Assert.Equal("Ordinary", Assert.IsType<TextAnnotation>(extracted.Records[0].Annotation).Content);
            Assert.IsType<CoverAnnotation>(extracted.Records[1].Annotation);
            TextAnnotation restored = Assert.IsType<TextAnnotation>(extracted.Records[2].Annotation);
            Assert.Equal("Replacement", restored.Content);
            Assert.False(restored.IsVisible);
            Assert.True(restored.PairLinked);
            Assert.Equal(1, SearchService.Search(path, "Ordinary").TotalHits);
            Assert.Equal(0, SearchService.Search(path, "Replacement").TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void HiddenReplacementRetainsItsEditingStateWithoutSavedSearchOrReadingOrderText()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-hidden-paired-overlay-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .SetMetadata(new PdfDocumentMetadata { Title = "Hidden pair", Language = "en-US" })
                .EnablePdfUa2Conformance()
                .AddPage(200, 100, new PdfContentStreamBuilder()
                    .BeginMarkedContent(PdfStructureType.Figure, 0)
                    .Rectangle(0, 0, 5, 5).Fill().EndMarkedContent())
                .AddStructureContainer(PdfStructureType.Document)
                .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original figure")
                .Build());
            var pair = new ReplacementPair(
                new TextAnnotation { PairId = "hidden-pair", Content = "Hidden replacement", Width = 150, Height = 40 },
                new CoverAnnotation { PairId = "hidden-pair", Bounds = new Rect(15, 15, 160, 50) });
            ReplacementLayers.SetLinked(pair, true);
            ReplacementLayers.SetVisibility(pair, ReplacementLayer.Group, false);
            ReplacementLayers.SetMovementAllowed(pair, ReplacementLayer.Text, false);
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [pair.Cover, pair.Text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            byte[] saved = File.ReadAllBytes(path);
            var document = PdfDocument.Open(saved);
            Assert.DoesNotContain(new PdfPageContentReader(document).Read(0).Words,
                word => word.Text.Contains("Hidden", StringComparison.Ordinal));
            Assert.Equal(0, SearchService.Search(path, "Hidden replacement").TotalHits);
            Assert.DoesNotContain(PdfAccessibilityReadingOrder.Read(document).Items,
                item => item.AlternateDescription?.Contains("Hidden replacement") == true);
            OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(saved);
            Assert.Equal(2, extracted.Records.Count);
            TextAnnotation text = Assert.IsType<TextAnnotation>(extracted.Records[1].Annotation);
            CoverAnnotation cover = Assert.IsType<CoverAnnotation>(extracted.Records[0].Annotation);
            Assert.False(ReplacementLayers.IsVisible(text));
            Assert.False(ReplacementLayers.IsVisible(cover));
            Assert.False(ReplacementLayers.CanMove(text));
            Assert.True(ReplacementLayers.CanMove(cover));
            Assert.True(text.PairLinked && cover.PairLinked);
            Assert.Equal("Hidden replacement", text.Content);
            Assert.True(extracted.Bytes.AsSpan(0, saved.Length).SequenceEqual(saved));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SavedReplacementRetainsItsEditableLinkedTextAndCover()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-paired-overlay-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            var pair = new ReplacementPair(
                new TextAnnotation { PairId = "saved-pair", Content = "Replacement", Position = new Point(20, 20),
                    Width = 150, Height = 40, FontSize = 18 },
                new CoverAnnotation { PairId = "saved-pair", Bounds = new Rect(15, 15, 160, 50) });
            ReplacementLayers.SetLinked(pair, true);
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [pair.Cover, pair.Text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });

            OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path));
            Assert.Equal(2, extracted.Records.Count);
            var text = Assert.IsType<TextAnnotation>(extracted.Records[1].Annotation);
            var cover = Assert.IsType<CoverAnnotation>(extracted.Records[0].Annotation);
            Assert.Equal("Replacement", text.Content);
            Assert.Equal(new Point(20, 20), text.Position);
            Assert.Equal(new Rect(15, 15, 160, 50), cover.Bounds);
            Assert.Equal("saved-pair", text.PairId);
            Assert.Equal(text.PairId, cover.PairId);
            Assert.True(text.PairLinked);
            Assert.True(cover.PairLinked);
            Assert.Equal(pair.Text.HistoryId, text.HistoryId);
            Assert.Equal(pair.Cover.HistoryId, cover.HistoryId);
            Assert.Equal(1, SearchService.Search(path, "Replacement").TotalHits);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

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

    // Save writes pending redaction marks after the overlay tail, so they sit above it. A page
    // with an unapplied mark must still reopen its text boxes as editable overlays.
    [Fact]
    public void APendingRedactionMarkAboveTheTailLeavesItsTextEditable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-redact-tail-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            var text = new TextAnnotation { Content = "Kept editable", Position = new Point(10, 10), Width = 160, Height = 25 };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            PdfEngineIntegration.WriteRedactionMarks(path, [new PdfEngineIntegration.RedactionMarkData(0, 20, 50, 80, 90)]);
            byte[] saved = File.ReadAllBytes(path);

            OverlayExtraction extraction = PdfOverlayAnnotations.TakeEditableTail(saved);

            Assert.Equal("Kept editable", Assert.IsType<TextAnnotation>(Assert.Single(extraction.Records).Annotation).Content);
            PdfDocument remaining = PdfDocument.Open(extraction.Bytes);
            Assert.Empty(PdfCommentReader.Read(remaining, true));
            Assert.Single(PdfRedactionMarkReader.Read(remaining));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Edited page text saves as a page-content editing layer. A pending redaction mark on that page is an
    // annotation too, and it must not keep the edit from reopening as editable.
    [Fact]
    public void APendingRedactionMarkLeavesAnEditedPagesTextEditable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-redact-batch-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
            var pair = new ReplacementPair(
                new TextAnnotation { PairId = "redact-batch", Content = "Edited line", Position = new Point(20, 20), Width = 150, Height = 30 },
                new CoverAnnotation { PairId = "redact-batch", Bounds = new Rect(15, 15, 160, 40) });
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [pair.Cover, pair.Text] },
                new Dictionary<int, (int w, int h)> { [0] = (200, 100) });
            Assert.Equal(2, PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path)).Records.Count);
            PdfEngineIntegration.WriteRedactionMarks(path, [new PdfEngineIntegration.RedactionMarkData(0, 20, 50, 80, 90)]);

            OverlayExtraction extraction = PdfOverlayAnnotations.TakeEditableTail(File.ReadAllBytes(path));

            Assert.Equal(2, extraction.Records.Count);
            Assert.Equal("Edited line", Assert.IsType<TextAnnotation>(extraction.Records[1].Annotation).Content);
            Assert.Single(PdfRedactionMarkReader.Read(PdfDocument.Open(extraction.Bytes)));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
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

    [Fact]
    public void SourcePdfImageDataRetainsItsAssetThroughEditableMetadata()
    {
        var originalImage = PdfImage.FromRgb(2, 1, new byte[] { 255, 0, 0, 0, 255, 0 });
        byte[] original = new PdfDocumentBuilder().AddPage(4, 1,
            new PdfContentStreamBuilder().DrawImage(originalImage, 0, 0, 2, 1)
                .SetFillRgb(0, 0, 1).Rectangle(3, 0, 1, 1).Fill()).Build();
        byte[] unchanged = original.ToArray();
        var source = PdfDocument.Open(original);
        var selection = Assert.Single(new PdfPageContentReader(source).ReadImagePlacements(0));
        var asset = PdfImagePlacementExtractor.Extract(source, selection);
        byte[] imagePdf = asset.ToPdfBytes();
        var model = new ImageAnnotation
        {
            Position = new Point(1, 1), SourceWidth = 2, SourceHeight = 1,
            Scale = 1, ImageData = Convert.ToBase64String(imagePdf)
        };
        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(4, 3).Build());
        var appearance = new PdfFormXObject(2, 1, new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 2, 1));
        var native = PdfDocument.Open(new PdfIncrementalAnnotationEditor(blank).AddStamp(0, 1, 1, 2, 1, appearance).Build());
        var comment = Assert.Single(PdfCommentReader.Read(native, true));
        byte[] saved = PdfOverlayAnnotations.Attach(native, [new(model, 4, 3, 0, 0, comment.ObjectNumber!.Value)]);
        var record = Assert.Single(PdfOverlayAnnotations.TakeEditableTail(saved).Records);
        var restored = Assert.IsType<ImageAnnotation>(record.Annotation);
        Assert.Equal(imagePdf, Convert.FromBase64String(restored.ImageData));
        Assert.Equal(new Point(1, 1), restored.Position);
        Assert.Equal(2, restored.SourceWidth);
        Assert.Equal(1, restored.SourceHeight);
        var restoredSource = PdfDocument.Open(Convert.FromBase64String(restored.ImageData));
        var content = new PdfPageContentReader(restoredSource).Read(0);
        Assert.Single(content.Images);
        Assert.Empty(content.Paths);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 255, 0, 255 },
            new PdfPageRenderer(restoredSource).Render(0, new PdfRenderOptions(2, 1)).Pixels.ToArray());
        Assert.Equal(unchanged, original);
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

public sealed class PdfOverlayPrefixTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void AttachingAnImageDoesNotAllocateAnotherWholeDecodedPayloadForClassification()
    {
        byte[] payload = new byte[2 * 1024 * 1024];
        "%PDF-"u8.CopyTo(payload);
        string data = Convert.ToBase64String(payload);
        var image = new ImageAnnotation { ImageData = data };
        var signature = new SignatureAnnotation { ImageData = data };
        PdfDocument native = NativeStamp();
        int number = Assert.Single(PdfCommentReader.Read(native, true)).ObjectNumber!.Value;
        OverlayWriteRecord[] imageRecords = [new(image, 400, 600, 0, 0, number)];
        OverlayWriteRecord[] signatureRecords = [new(signature, 400, 600, 0, 0, number)];
        // Both placed models validate and serialize the same data on the same native stamp.
        // Warm serializer metadata and buffer pools before comparing their allocation costs.
        for (int warm = 0; warm < 2; warm++)
        {
            _ = PdfOverlayAnnotations.Attach(native, imageRecords);
            _ = PdfOverlayAnnotations.Attach(native, signatureRecords);
        }
        long imageBytes = long.MaxValue, signatureBytes = long.MaxValue;
        // Five samples, not three: under the full suite a shared buffer pool can miss once and add one large rental to a
        // sample; the minimum of five absorbs that without loosening the allowance.
        for (int sample = 0; sample < 5; sample++)
        {
            imageBytes = Math.Min(imageBytes, Measure(imageRecords));
            signatureBytes = Math.Min(signatureBytes, Measure(signatureRecords));
        }
        output.WriteLine($"Decoded bytes: {payload.Length}; Base64 characters: {data.Length}; image allocation: {imageBytes}; signature allocation: {signatureBytes}; difference: {imageBytes - signatureBytes}.");
        Assert.True(imageBytes <= signatureBytes + 512 * 1024,
            $"Image allocated {imageBytes} bytes; the same signature payload allocated {signatureBytes} bytes. Classification exceeded the 512 KiB schema-overhead allowance.");

        long Measure(OverlayWriteRecord[] records)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            byte[] attached = PdfOverlayAnnotations.Attach(native, records);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            GC.KeepAlive(attached);
            return allocated;
        }
    }

    [Theory]
    [InlineData("JVBERi0=", 2)]
    [InlineData("JVBERi0w", 2)]
    [InlineData("JVBERi1B", 2)]
    [InlineData("JVBERi3/", 2)]
    [InlineData(" \tJ\rV\nBE\tRi 0=\r\n", 2)]
    [InlineData("JQ==", 1)]
    [InlineData("JVBE", 1)]
    [InlineData("JVBERg==", 1)]
    [InlineData("AAAA", 1)]
    [InlineData(" \t\r\n", 1)]
    [InlineData("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=", 1)]
    public void ImagePrefixClassificationAndBase64WhitespaceSurviveRoundtrip(string data, int schema)
    {
        byte[] saved = AttachImage(data);
        PdfDocument document = PdfDocument.Open(saved);
        int number = Assert.Single(PdfCommentReader.Read(document, true)).ObjectNumber!.Value;
        var annotation = Assert.IsType<PdfDictionary>(document.Resolve(number));
        var metadata = Assert.IsType<PdfDictionary>(annotation[Name("KillerPDFOverlay")]);
        Assert.Equal(schema, Assert.IsType<PdfInteger>(metadata[Name("V")]).Value);
        Assert.Equal(data, Assert.IsType<ImageAnnotation>(Assert.Single(PdfOverlayAnnotations.Read(document)).Annotation).ImageData);
        OverlayExtraction extraction = PdfOverlayAnnotations.TakeEditableTail(saved);
        Assert.Equal(data, Assert.IsType<ImageAnnotation>(Assert.Single(extraction.Records).Annotation).ImageData);
        Assert.Empty(PdfCommentReader.Read(PdfDocument.Open(extraction.Bytes), true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("JVBERi0=!")]
    [InlineData("JVBERi0===")]
    [InlineData("JVBERi0=\u00a0")]
    public void AttachingAnImageStillValidatesTheEntireBase64Payload(string data)
    {
        if (data.Length == 0) Assert.Throws<ArgumentException>(() => AttachImage(data));
        else Assert.Throws<FormatException>(() => AttachImage(data));
    }

    [Theory]
    [InlineData("")]
    [InlineData("JVBERi0=!")]
    [InlineData("JVBERi0===")]
    [InlineData("JVBERi0=\u00a0")]
    [InlineData("JVBE")]
    public void InvalidSourcePdfImageMetadataRemainsNativeAndLeavesBytesUnchanged(string data)
    {
        PdfDocument document = PdfDocument.Open(AttachImage("JVBERi0="));
        int number = Assert.Single(PdfCommentReader.Read(document, true)).ObjectNumber!.Value;
        var annotation = Assert.IsType<PdfDictionary>(document.Resolve(number));
        var metadata = Assert.IsType<PdfDictionary>(annotation[Name("KillerPDFOverlay")]);
        JsonObject payload = JsonNode.Parse(Assert.IsType<PdfString>(metadata[Name("Data")]).Bytes.Span)!.AsObject();
        payload["Model"]!["ImageData"] = data;
        var changedMetadata = new PdfDictionary(metadata.Where(pair => !pair.Key.Equals(Name("Data")))
            .Append(new(Name("Data"), new PdfString(Encoding.UTF8.GetBytes(payload.ToJsonString()), PdfStringForm.Hexadecimal))));
        byte[] changed = new PdfIncrementalUpdateBuilder(document).ReplaceObject(number,
            new PdfDictionary(annotation.Where(pair => !pair.Key.Equals(Name("KillerPDFOverlay")))
                .Append(new(Name("KillerPDFOverlay"), changedMetadata)))).Build();
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(changed)));
        OverlayExtraction extraction = PdfOverlayAnnotations.TakeEditableTail(changed);
        Assert.Empty(extraction.Records);
        Assert.Same(changed, extraction.Bytes);
        Assert.Single(PdfCommentReader.Read(PdfDocument.Open(changed), true));
    }

    private static byte[] AttachImage(string data)
    {
        PdfDocument native = NativeStamp();
        int number = Assert.Single(PdfCommentReader.Read(native, true)).ObjectNumber!.Value;
        return PdfOverlayAnnotations.Attach(native, [new(new ImageAnnotation { ImageData = data }, 400, 600, 0, 0, number)]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static PdfDocument NativeStamp() => PdfDocument.Open(
        new PdfIncrementalAnnotationEditor(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 300).Build()))
            .AddStamp(0, 10, 20, 140, 60, new PdfFormXObject(140, 60, new PdfContentStreamBuilder())).Build());
}
