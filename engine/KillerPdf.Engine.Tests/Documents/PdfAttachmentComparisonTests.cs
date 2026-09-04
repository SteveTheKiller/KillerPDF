using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfAttachmentComparisonTests
{
    [Fact]
    public void CompareReportsPayloadAndPlacementChanges()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("evidence.txt", "first"u8.ToArray(), "text/plain")
            .AddFileAttachmentAnnotation(0, 20, 30, 24, "evidence.txt",
                icon: PdfFileAttachmentIcon.Paperclip)
            .Build());
        PdfDocument payloadChanged = PdfDocument.Open(
            new PdfIncrementalPageEditor(original)
                .ReplaceAttachment("evidence.txt", "second"u8.ToArray())
                .Build());
        int annotationIndex = Assert.Single(
            PdfAttachmentReader.ReadPageAnnotations(original, 0)).AnnotationIndex;
        PdfDocument placementChanged = PdfDocument.Open(
            new PdfIncrementalAnnotationEditor(original)
                .SetFileAttachmentIconAt(0, annotationIndex, PdfFileAttachmentIcon.Tag)
                .Build());

        PdfAttachmentChange payload = Assert.Single(
            PdfAttachmentComparison.Compare(original, payloadChanged).Changes,
            change => change.Kind == PdfAttachmentChangeKind.Payload);
        PdfAttachmentChange placement = Assert.Single(
            PdfAttachmentComparison.Compare(original, placementChanged).Changes);

        Assert.Equal(PdfAttachmentChangeScope.Document, payload.Scope);
        Assert.Equal("evidence.txt", payload.FileName);
        Assert.Equal(PdfAttachmentChangeScope.PageAnnotation, placement.Scope);
        Assert.Equal(PdfAttachmentChangeKind.Placement, placement.Kind);
        Assert.Equal((0, annotationIndex),
            (placement.PageIndex, placement.AnnotationIndex));
        Assert.False(PdfAttachmentComparison.Compare(original, original).HasChanges);
    }
}
