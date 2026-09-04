using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads review comments from every annotation subtype.</summary>
public static class PdfCommentReader
{
    /// <summary>Returns annotations that contain review text, in page annotation order.</summary>
    public static IReadOnlyList<PdfCommentInfo> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before reading comments.");
        var comments = new List<PdfCommentInfo>();
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
        {
            if (!page.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue))
                continue;
            PdfArray annotations = Resolve(document, annotationsValue) as PdfArray
                ?? throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots value is not an array.");
            for (int index = 0; index < annotations.Count; index++)
            {
                PdfIndirectReference? reference = annotations[index] as PdfIndirectReference;
                PdfDictionary annotation = Resolve(document, annotations[index]) as PdfDictionary
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {index + 1} is not a dictionary.");
                string? contents = Text(document, annotation, "Contents");
                if (string.IsNullOrEmpty(contents)) continue;
                string subtype = annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
                    && Resolve(document, subtypeValue) is PdfName subtypeName
                    ? subtypeName.ValueAsLatin1()
                    : throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {index + 1} has no subtype name.");
                comments.Add(new PdfCommentInfo
                {
                    PageIndex = page.Index,
                    AnnotationIndex = index,
                    ObjectNumber = reference?.ObjectNumber,
                    Subtype = subtype,
                    Name = Text(document, annotation, "NM"),
                    Contents = contents,
                    Author = Text(document, annotation, "T"),
                    Subject = Text(document, annotation, "Subj"),
                    Bounds = Bounds(document, annotation, page.Index, index),
                    ReplyToObjectNumber = ReplyTarget(document, annotation)
                });
            }
        }
        return Array.AsReadOnly(comments.ToArray());
    }

    private static PdfContentBounds? Bounds(PdfDocument document,
        PdfDictionary annotation, int pageIndex, int annotationIndex)
    {
        if (!annotation.TryGetValue(Name("Rect"), out PdfObject? value)) return null;
        PdfArray rectangle = Resolve(document, value) as PdfArray
            ?? throw new InvalidOperationException(
                $"Page {pageIndex + 1} annotation {annotationIndex + 1} /Rect is not an array.");
        if (rectangle.Count != 4)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} annotation {annotationIndex + 1} /Rect has the wrong size.");
        double left = Number(document, rectangle[0]);
        double bottom = Number(document, rectangle[1]);
        double right = Number(document, rectangle[2]);
        double top = Number(document, rectangle[3]);
        if (right < left || top < bottom)
            throw new InvalidOperationException(
                $"Page {pageIndex + 1} annotation {annotationIndex + 1} /Rect is invalid.");
        return new PdfContentBounds(left, bottom, right - left, top - bottom);
    }

    private static int? ReplyTarget(PdfDocument document, PdfDictionary annotation)
    {
        if (!annotation.TryGetValue(Name("IRT"), out PdfObject? value)) return null;
        if (value is not PdfIndirectReference reference)
            throw new InvalidOperationException("An annotation /IRT value is not indirect.");
        _ = Resolve(document, reference) as PdfDictionary
            ?? throw new InvalidOperationException(
                "An annotation /IRT value does not reference a dictionary.");
        return reference.ObjectNumber;
    }

    private static string? Text(
        PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span,
                $"An annotation /{key} value")
            : throw new InvalidOperationException(
                $"An annotation /{key} value is not a string.");
    }

    private static double Number(PdfDocument document, PdfObject value) =>
        Resolve(document, value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when double.IsFinite(real.Value) => real.Value,
            _ => throw new InvalidOperationException(
                "An annotation rectangle contains a nonnumeric coordinate.")
        };

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An annotation reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

/// <summary>Review text and source identity for one annotation.</summary>
public sealed record PdfCommentInfo
{
    /// <summary>Gets the zero-based page index.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the zero-based position in the page annotation array.</summary>
    public required int AnnotationIndex { get; init; }
    /// <summary>Gets the source object number when the annotation is indirect.</summary>
    public int? ObjectNumber { get; init; }
    /// <summary>Gets the annotation subtype.</summary>
    public required string Subtype { get; init; }
    /// <summary>Gets the optional annotation name used for editing.</summary>
    public string? Name { get; init; }
    /// <summary>Gets the review text.</summary>
    public required string Contents { get; init; }
    /// <summary>Gets the optional author.</summary>
    public string? Author { get; init; }
    /// <summary>Gets the optional subject.</summary>
    public string? Subject { get; init; }
    /// <summary>Gets the annotation rectangle when supplied.</summary>
    public PdfContentBounds? Bounds { get; init; }
    /// <summary>Gets the object number of the comment this annotation replies to.</summary>
    public int? ReplyToObjectNumber { get; init; }
}
