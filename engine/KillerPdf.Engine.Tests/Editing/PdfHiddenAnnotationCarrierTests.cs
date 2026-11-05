using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfHiddenAnnotationCarrierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenTypedAnnotationWithEmptyContentsDoesNotEnterTaggedReadingOrder(bool stamp)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Hidden carrier", Language = "en-US" })
            .EnablePdfUa2Conformance()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginMarkedContent(PdfStructureType.Figure, 0)
                .Rectangle(0, 0, 5, 5).Fill().EndMarkedContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "Original figure")
            .Build());
        var editor = new PdfIncrementalAnnotationEditor(source);
        var appearance = new PdfFormXObject(50, 50, new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 50, 50).Fill());
        var hidden = new PdfAnnotationMetadata { Flags = PdfAnnotationFlags.Hidden };
        if (stamp) editor.AddStamp(0, 20, 20, 50, 50, appearance, string.Empty, hidden);
        else editor.AddFreeText(0, 20, 20, 50, 50, string.Empty, appearance, hidden);
        byte[] saved = editor.Build();
        var document = PdfDocument.Open(saved);
        Assert.Equal(1, PdfCommentReader.ReadPageAnnotationCount(document, 0));
        var order = PdfAccessibilityReadingOrder.Read(document);
        Assert.Single(order.Items, item => item.AlternateDescription == "Original figure");
        Assert.DoesNotContain(order.Items, item => item.Role == "Annot");
        int center = (50 * 100 + 50) * 4;
        foreach (bool printing in new[] { false, true })
        {
            var pixels = new PdfPageRenderer(document).Render(0,
                new PdfRenderOptions(100, 100) { ForPrinting = printing });
            Assert.Equal((byte)255, pixels.Pixels.Span[center]);
            Assert.Equal((byte)255, pixels.Pixels.Span[center + 1]);
            Assert.Equal((byte)255, pixels.Pixels.Span[center + 2]);
        }
        byte[] removed = new PdfIncrementalAnnotationEditor(document).RemoveAnnotationAt(0, 0).Build();
        Assert.Equal(0, PdfCommentReader.ReadPageAnnotationCount(PdfDocument.Open(removed), 0));
        Assert.Single(PdfAccessibilityReadingOrder.Read(PdfDocument.Open(removed)).Items,
            item => item.AlternateDescription == "Original figure");
    }

    [Theory]
    [InlineData(PdfAnnotationFlags.None)]
    [InlineData(PdfAnnotationFlags.Print)]
    [InlineData(PdfAnnotationFlags.NoView)]
    [InlineData(PdfAnnotationFlags.Invisible)]
    public void OtherVisibilityFlagsDoNotPermitUndescribedTaggedAnnotations(PdfAnnotationFlags flags)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Description required", Language = "en-US" })
            .EnablePdfUa2Conformance().AddBlankPage(100, 100)
            .AddStructureContainer(PdfStructureType.Document).Build());
        var editor = new PdfIncrementalAnnotationEditor(source).AddFreeText(0, 10, 10, 20, 20,
            string.Empty, new PdfFormXObject(20, 20, new PdfContentStreamBuilder()),
            new PdfAnnotationMetadata { Flags = flags });
        Assert.Throws<InvalidOperationException>(() => editor.Build());
    }
}
