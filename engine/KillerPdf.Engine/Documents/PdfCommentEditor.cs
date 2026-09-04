using KillerPdf.Engine.Editing;

namespace KillerPdf.Engine.Documents;

/// <summary>Edits review comments through stable identities returned by the comment reader.</summary>
public static class PdfCommentEditor
{
    /// <summary>Changes one comment's review text without altering its annotation type or geometry.</summary>
    public static byte[] SetContents(
        PdfDocument document, PdfCommentInfo comment, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contents);
        PdfCommentInfo current = Resolve(document, comment);
        return new PdfIncrementalAnnotationEditor(document)
            .SetAnnotationContentsAt(current.PageIndex, current.AnnotationIndex, contents)
            .Build();
    }

    /// <summary>Removes one comment annotation while enforcing reply and popup integrity.</summary>
    public static byte[] Remove(PdfDocument document, PdfCommentInfo comment)
    {
        PdfCommentInfo current = Resolve(document, comment);
        return new PdfIncrementalAnnotationEditor(document)
            .RemoveAnnotationAt(current.PageIndex, current.AnnotationIndex)
            .Build();
    }

    private static PdfCommentInfo Resolve(PdfDocument document, PdfCommentInfo comment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(comment);
        PdfCommentInfo current = PdfCommentReader.Read(document).SingleOrDefault(item =>
            item.PageIndex == comment.PageIndex
            && item.AnnotationIndex == comment.AnnotationIndex)
            ?? throw new ArgumentException(
                "The selected comment no longer exists in the document.", nameof(comment));
        if (comment.ObjectNumber.HasValue
            && current.ObjectNumber != comment.ObjectNumber)
            throw new ArgumentException(
                "The selected comment identity no longer matches the document.", nameof(comment));
        return current;
    }
}
