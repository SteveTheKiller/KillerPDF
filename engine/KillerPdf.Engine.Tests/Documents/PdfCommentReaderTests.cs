using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using System.Text.Json;
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
                Assert.Equal(new PdfContentBounds(10, 20, 34, 44), comment.Bounds);
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

    [Fact]
    public void ReadThreadsGroupsNestedRepliesAndExportsStableJson()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] reviewed = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddTextNote(0, 10, 20, "Change this term", name: "note")
            .AddTextNote(0, 15, 25, "Suggested wording", name: "reply",
                inReplyTo: "note")
            .AddTextNote(0, 20, 30, "Applied", name: "confirmation",
                inReplyTo: "reply")
            .AddHighlight(0, 40, 50, 60, 12, "Check this separately")
            .Build();
        PdfDocument document = PdfDocument.Open(reviewed);

        IReadOnlyList<PdfCommentThread> threads = PdfCommentReader.ReadThreads(document);

        Assert.Equal(2, threads.Count);
        Assert.Equal("note", threads[0].Comment.Name);
        PdfCommentThread reply = Assert.Single(threads[0].Replies);
        Assert.Equal("reply", reply.Comment.Name);
        Assert.Equal("confirmation", Assert.Single(reply.Replies).Comment.Name);
        Assert.Equal("Highlight", threads[1].Comment.Subtype);
        using JsonDocument json = JsonDocument.Parse(PdfCommentReader.ExportJson(document));
        Assert.Equal("Change this term",
            json.RootElement[0].GetProperty("comment").GetProperty("contents").GetString());
        Assert.Equal("Applied", json.RootElement[0].GetProperty("replies")[0]
            .GetProperty("replies")[0].GetProperty("comment")
            .GetProperty("contents").GetString());
        string text = PdfCommentReader.ExportText(document);
        Assert.Contains("Comments: 4", text, StringComparison.Ordinal);
        Assert.Contains("Comment on page 1, annotation 1 [Text]", text,
            StringComparison.Ordinal);
        Assert.Contains("  Reply on page 1, annotation 2 [Text]", text,
            StringComparison.Ordinal);
        Assert.Contains("    Reply on page 1, annotation 3 [Text]", text,
            StringComparison.Ordinal);
        Assert.Contains("Check this separately", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCommentReportIsReadable()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Equal("Comments: 0", PdfCommentReader.ExportText(document));
    }

    [Fact]
    public void CommentEditorUpdatesAndRemovesReaderSelections()
    {
        PdfDocument original = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddHighlight(0, 10, 10, 40, 12, "Review this")
            .Build());
        PdfCommentInfo selected = Assert.Single(PdfCommentReader.Read(original));

        PdfDocument edited = PdfDocument.Open(
            PdfCommentEditor.SetContents(original, selected, "Translation updated"));
        PdfCommentInfo updated = Assert.Single(PdfCommentReader.Read(edited));

        Assert.Equal("Translation updated", updated.Contents);
        Assert.Equal(selected.ObjectNumber, updated.ObjectNumber);
        Assert.Equal(selected.Bounds, updated.Bounds);
        PdfDocument removed = PdfDocument.Open(PdfCommentEditor.Remove(edited, updated));
        Assert.Empty(PdfCommentReader.Read(removed));
        Assert.Throws<ArgumentException>(() =>
            PdfCommentEditor.SetContents(removed, updated, "Stale edit"));
    }
}
