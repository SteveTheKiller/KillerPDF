using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfAttachmentComparisonTests
{
    // #455: files that share a name are compared in order, one with the next, so a change to one of three
    // "Content Credentials" files is reported for that one file and an unchanged document reports nothing.
    [Fact]
    public void CompareMatchesFilesThatShareANameInOrder()
    {
        byte[] source = RawPdfFixtures.RepeatedContentCredentials();
        PdfDocument original = PdfDocument.Open(source);
        PdfAttachmentInfo[] files = [.. PdfAttachmentReader.Read(original)];
        PdfDocument replaced = PdfDocument.Open(new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .ReplaceAttachmentByKey(files[1].AttachmentKey!, "changed"u8.ToArray()).Build());
        PdfDocument removed = PdfDocument.Open(new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RemoveAttachmentByKey(files[2].AttachmentKey!).Build());

        Assert.False(PdfAttachmentComparison.Compare(original, original).HasChanges);
        Assert.Equal([
                new PdfAttachmentChange(PdfAttachmentChangeScope.Document,
                    PdfAttachmentChangeKind.Payload, "Content Credentials"),
                new PdfAttachmentChange(PdfAttachmentChangeScope.Document,
                    PdfAttachmentChangeKind.Metadata, "Content Credentials")],
            PdfAttachmentComparison.Compare(original, replaced).Changes);
        Assert.Equal([new PdfAttachmentChange(PdfAttachmentChangeScope.Document,
                PdfAttachmentChangeKind.Removed, "Content Credentials")],
            PdfAttachmentComparison.Compare(original, removed).Changes);
    }

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
