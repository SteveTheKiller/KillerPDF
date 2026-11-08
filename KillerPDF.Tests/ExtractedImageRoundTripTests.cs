using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ExtractedImageRoundTripTests
{
    [Fact]
    public void AnExtractedTransparentImageStaysEditableAndKeepsItsHoleThroughSaveAndReopen()
    {
        // Two by two red pixels whose lower-right pixel is outside the selection.
        var frame = new PdfRenderedFrame(2, 2, [1,0,0,1, 1,0,0,1, 1,0,0,1, 1,0,0,1], PdfFrameColorEncoding.SrgbD65);
        var selection = PdfRasterSelection.SelectRectangle(2, 2, 0, 0, 2, 2)
            .Subtract(PdfRasterSelection.SelectRectangle(2, 2, 1, 1, 1, 1));
        byte[] asset = PdfRasterExtraction.Extract(frame, selection).Image.ToPdfBytes();
        var model = new ImageAnnotation
        {
            PageIndex = 0, Position = new Point(0, 0), Scale = 1, SourceWidth = 20, SourceHeight = 20,
            ImageData = Convert.ToBase64String(asset)
        };
        var blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(20, 20).Build());
        // The page content the save writes: the asset, rebuilt the way saving reads it, over the whole page.
        var content = new PdfContentStreamBuilder().DrawImage(PdfAnnotationImageSource.ReadPdfAsset(asset), 0, 0, 20, 20);
        byte[] saved = PdfOverlayAnnotations.AppendPageBatch(blank, 0, 20, 20, 0, [model], content, null);

        var reopened = PdfDocument.Open(saved);
        var restored = Assert.IsType<ImageAnnotation>(Assert.Single(PdfOverlayAnnotations.Read(reopened)).Annotation);
        Assert.Equal(asset, Convert.FromBase64String(restored.ImageData));

        var page = new PdfPageRenderer(reopened).RenderFrame(0, new PdfRenderOptions(20, 20));
        byte[] bgra = page.ToSrgbBgra32();
        (byte B, byte G, byte R) At(int x, int y) { int i = (y * 20 + x) * 4; return (bgra[i], bgra[i + 1], bgra[i + 2]); }
        Assert.Equal((0, 0, 255), At(5, 5));       // upper-left quarter: selected red
        Assert.Equal((0, 0, 255), At(5, 15));      // lower-left quarter: selected red
        Assert.Equal((255, 255, 255), At(15, 15)); // lower-right quarter: the hole shows the white page
    }
}
