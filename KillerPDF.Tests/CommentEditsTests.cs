using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class CommentEditsTests
{
    [Fact]
    public void ApplyCommentEdits_AddsRepliesEditsTextAndRemovesThreads()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-comments-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(612, 792).AddBlankPage(612, 792).Build());

            PdfEngineIntegration.ApplyCommentEdits(path,
            [
                new CommentAddition(1, "Check this total", "c1", null),
                new CommentAddition(1, "Fixed in the next draft", "c2", "c1", 100, 700),
                new CommentAddition(0, "Cover page note", "c3", null)
            ], "Reviewer");

            IReadOnlyList<PdfCommentThread> threads = PdfEngineIntegration.ReadCommentThreads(path, null);
            Assert.Equal(2, threads.Count);
            PdfCommentThread total = Assert.Single(threads, thread => thread.Comment.Name == "c1");
            Assert.Equal(1, total.Comment.PageIndex);
            Assert.Equal("Reviewer", total.Comment.Author);
            PdfCommentThread reply = Assert.Single(total.Replies);
            Assert.Equal("Fixed in the next draft", reply.Comment.Contents);

            PdfCommentThread cover = Assert.Single(threads, thread => thread.Comment.Name == "c3");
            PdfEngineIntegration.ApplyCommentEdits(path,
            [
                new CommentTextEdit(total.Comment, "Check this total again"),
                new CommentRemoval(cover.Comment)
            ], "Reviewer");

            threads = PdfEngineIntegration.ReadCommentThreads(path, null);
            total = Assert.Single(threads);
            Assert.Equal("Check this total again", total.Comment.Contents);
            Assert.Single(total.Replies);

            PdfEngineIntegration.ApplyCommentEdits(path,
            [
                new CommentRemoval(total.Comment),
                new CommentRemoval(total.Replies[0].Comment)
            ], null);
            Assert.Empty(PdfEngineIntegration.ReadCommentThreads(path, null));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
