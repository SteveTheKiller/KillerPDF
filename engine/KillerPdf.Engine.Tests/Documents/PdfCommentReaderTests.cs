using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfCommentReaderTests
{
    [Fact]
    public void ReadFindsCommentsAcrossAnnotationTypesAndReplies()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] reviewed = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddTextNote(0, 10, 20, "Change this term", name: "note",
                annotationMetadata: new PdfAnnotationMetadata
                {
                    Author = "Reviewer",
                    Subject = "Translation"
                })
            .AddHighlight(0, 40, 50, 60, 12, "Use the approved translation")
            .AddTextNote(0, 15, 25, "Done", name: "reply", inReplyTo: "note")
            .Build();

        IReadOnlyList<PdfCommentInfo> comments = PdfCommentReader.Read(
            PdfDocument.Open(reviewed));

        Assert.Collection(comments,
            comment =>
            {
                Assert.Equal("Text", comment.Subtype);
                Assert.Equal("note", comment.Name);
                Assert.Equal("Change this term", comment.Contents);
                Assert.Equal("Reviewer", comment.Author);
                Assert.Equal("Translation", comment.Subject);
                Assert.Equal(new PdfContentBounds(10, 20, 24, 24), comment.Bounds);
                Assert.NotNull(comment.ObjectNumber);
            },
            comment =>
            {
                Assert.Equal("Highlight", comment.Subtype);
                Assert.Equal("Use the approved translation", comment.Contents);
            },
            comment =>
            {
                Assert.Equal("reply", comment.Name);
                Assert.Equal(comments[0].ObjectNumber, comment.ReplyToObjectNumber);
            });
    }

    [Fact]
    public void ReadSkipsAnnotationsWithoutReviewText()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] reviewed = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddHighlight(0, 10, 10, 20, 10)
            .Build();

        Assert.Empty(PdfCommentReader.Read(PdfDocument.Open(reviewed)));
    }
}
