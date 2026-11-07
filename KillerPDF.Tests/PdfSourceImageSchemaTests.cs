using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfSourceImageSchemaTests
{
    [Fact]
    public void SourcePdfImageUsesVersionTwoAndRetainsTheNativeAppearanceAndExactAsset()
    {
        var (nativeBytes, imagePdf, model) = CreateSourceImage();
        byte[] saved = Attach(nativeBytes, model);
        var document = PdfDocument.Open(saved);
        Assert.Equal(2, Version(document));
        Assert.True(saved.AsSpan(0, nativeBytes.Length).SequenceEqual(nativeBytes));
        Assert.Single(PdfCommentReader.Read(document, true));
        var read = Assert.IsType<ImageAnnotation>(Assert.Single(PdfOverlayAnnotations.Read(document)).Annotation);
        Assert.Equal(imagePdf, Convert.FromBase64String(read.ImageData));
        OverlayExtraction extracted = PdfOverlayAnnotations.TakeEditableTail(saved);
        var restored = Assert.IsType<ImageAnnotation>(Assert.Single(extracted.Records).Annotation);
        Assert.Equal(imagePdf, Convert.FromBase64String(restored.ImageData));
        Assert.True(extracted.Bytes.AsSpan(0, saved.Length).SequenceEqual(saved));
        Assert.Empty(PdfCommentReader.Read(PdfDocument.Open(extracted.Bytes), true));
    }

    [Fact]
    public void OrdinaryPngImageRemainsVersionOneAndEditable()
    {
        var (native, _, model) = CreateSourceImage();
        model.ImageData = Png;
        byte[] saved = Attach(native, model);
        Assert.Equal(1, Version(PdfDocument.Open(saved)));
        var restored = Assert.IsType<ImageAnnotation>(Assert.Single(PdfOverlayAnnotations.TakeEditableTail(saved).Records).Annotation);
        Assert.Equal(Png, restored.ImageData);
    }

    [Fact]
    public void WhitespaceInBase64DoesNotHideTheSourcePdfRepresentation()
    {
        var (native, imagePdf, model) = CreateSourceImage();
        model.ImageData = " \r\n" + model.ImageData.Insert(3, " \t\n");
        byte[] saved = Attach(native, model);
        Assert.Equal(2, Version(PdfDocument.Open(saved)));
        var restored = Assert.IsType<ImageAnnotation>(Assert.Single(PdfOverlayAnnotations.TakeEditableTail(saved).Records).Annotation);
        Assert.Equal(imagePdf, Convert.FromBase64String(restored.ImageData));
    }

    [Fact]
    public void ExistingVersionOneSourcePdfPrototypeRemainsReadable()
    {
        var (native, imagePdf, model) = CreateSourceImage();
        byte[] saved = RewriteMetadata(Attach(native, model), 1);
        var restored = Assert.IsType<ImageAnnotation>(Assert.Single(PdfOverlayAnnotations.TakeEditableTail(saved).Records).Annotation);
        Assert.Equal(imagePdf, Convert.FromBase64String(restored.ImageData));
    }

    [Theory]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    public void UnsupportedMetadataPreservesTheWholeNativeSource(int version, bool ordinaryImage, bool unknownKind)
    {
        var (native, _, model) = CreateSourceImage();
        if (ordinaryImage) model.ImageData = Png;
        byte[] saved = RewriteMetadata(Attach(native, model), version, unknownKind);
        Assert.Empty(PdfOverlayAnnotations.Read(PdfDocument.Open(saved)));
        OverlayExtraction extraction = PdfOverlayAnnotations.TakeEditableTail(saved);
        Assert.Empty(extraction.Records);
        Assert.Equal(saved, extraction.Bytes);
        Assert.Single(PdfCommentReader.Read(PdfDocument.Open(extraction.Bytes), true));
    }

    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    private static byte[] RewriteMetadata(byte[] saved, int version, bool unknownKind = false)
    {
        var document = PdfDocument.Open(saved);
        var comment = Assert.Single(PdfCommentReader.Read(document, true));
        var annotation = Assert.IsType<PdfDictionary>(document.Resolve(comment.ObjectNumber!.Value));
        var metadata = Assert.IsType<PdfDictionary>(annotation[Name("KillerPDFOverlay")]);
        var data = Assert.IsType<PdfString>(metadata[Name("Data")]);
        JsonObject payload = JsonNode.Parse(data.Bytes.Span)!.AsObject();
        if (unknownKind) payload["Kind"] = "FutureImage";
        var replacement = new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [Name("V")] = new PdfInteger(version),
            [Name("Data")] = new PdfString(Encoding.UTF8.GetBytes(payload.ToJsonString()), PdfStringForm.Hexadecimal)
        });
        return new PdfIncrementalUpdateBuilder(document).ReplaceObject(comment.ObjectNumber.Value,
            new PdfDictionary(annotation.Select(pair => pair.Key.Equals(Name("KillerPDFOverlay"))
                ? new KeyValuePair<PdfName, PdfObject>(pair.Key, replacement) : pair))).Build();
    }

    private static (byte[] Native, byte[] ImagePdf, ImageAnnotation Model) CreateSourceImage()
    {
        var image = PdfImage.FromRgb(2, 1, new byte[] { 255, 0, 0, 0, 255, 0 });
        byte[] source = new PdfDocumentBuilder().AddPage(2, 1,
            new PdfContentStreamBuilder().DrawImage(image, 0, 0, 2, 1)).Build();
        var document = PdfDocument.Open(source);
        var placement = Assert.Single(new PdfPageContentReader(document).ReadImagePlacements(0));
        var asset = PdfImagePlacementExtractor.Extract(document, placement);
        byte[] imagePdf = asset.ToPdfBytes();
        var model = new ImageAnnotation
        {
            Position = new Point(1, 1), SourceWidth = 2, SourceHeight = 1,
            Scale = 1, ImageData = Convert.ToBase64String(imagePdf)
        };
        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(4, 3).Build());
        var appearance = new PdfFormXObject(2, 1,
            new PdfContentStreamBuilder().DrawImage(asset, 0, 0, 2, 1));
        byte[] native = new PdfIncrementalAnnotationEditor(blank).AddStamp(0, 1, 1, 2, 1, appearance).Build();
        return (native, imagePdf, model);
    }

    private static byte[] Attach(byte[] native, ImageAnnotation model)
    {
        var document = PdfDocument.Open(native);
        var comment = Assert.Single(PdfCommentReader.Read(document, true));
        return PdfOverlayAnnotations.Attach(document, [new(model, 4, 3, 0, 0, comment.ObjectNumber!.Value)]);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static long Version(PdfDocument document)
    {
        var comment = Assert.Single(PdfCommentReader.Read(document, true));
        var annotation = Assert.IsType<PdfDictionary>(document.Resolve(comment.ObjectNumber!.Value));
        var metadata = Assert.IsType<PdfDictionary>(annotation[Name("KillerPDFOverlay")]);
        return Assert.IsType<PdfInteger>(metadata[Name("V")]).Value;
    }
}
