using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfAttachmentComparisonTests
{
    [Fact]
    public void CompareReportsUnregisteredPageAttachmentContentChanges()
    {
        PdfDocument registered = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("notes.txt", "original"u8.ToArray(), "text/plain", "Original")
            .AddFileAttachmentAnnotation(0, 20, 30, 24, "notes.txt").Build());
        PdfDocument replaced = PdfDocument.Open(new PdfIncrementalPageEditor(registered)
            .ReplaceAttachment("notes.txt", "revised"u8.ToArray()).Build());
        PdfDocument original = PdfDocument.Open(new PdfIncrementalPageEditor(registered)
            .RemoveAttachment("notes.txt").Build());
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(replaced)
            .RemoveAttachment("notes.txt").Build());
        Assert.Empty(PdfAttachmentReader.Read(original));
        Assert.Empty(PdfAttachmentReader.Read(changed));

        var changes = PdfAttachmentComparison.Compare(original, changed).Changes;
        Assert.Equal([
            new PdfAttachmentChange(PdfAttachmentChangeScope.PageAnnotation,
                PdfAttachmentChangeKind.Payload, "notes.txt", 0, 0),
            new PdfAttachmentChange(PdfAttachmentChangeScope.PageAnnotation,
                PdfAttachmentChangeKind.Metadata, "notes.txt", 0, 0)
        ], changes);
        Assert.False(PdfAttachmentComparison.Compare(changed, changed).HasChanges);
    }

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

        var changes = PdfAttachmentComparison.Compare(original, payloadChanged).Changes;
        PdfAttachmentChange payload = Assert.Single(changes,
            change => change.Kind == PdfAttachmentChangeKind.Payload
                && change.Scope == PdfAttachmentChangeScope.Document);
        PdfAttachmentChange pagePayload = Assert.Single(changes,
            change => change.Kind == PdfAttachmentChangeKind.Payload
                && change.Scope == PdfAttachmentChangeScope.PageAnnotation);
        Assert.Equal((0, annotationIndex), (pagePayload.PageIndex, pagePayload.AnnotationIndex));
        Assert.Contains(changes, change => change.Kind == PdfAttachmentChangeKind.Metadata
            && change.Scope == PdfAttachmentChangeScope.PageAnnotation);
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
