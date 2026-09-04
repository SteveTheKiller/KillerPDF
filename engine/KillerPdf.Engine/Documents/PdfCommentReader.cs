using System.Globalization;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads review comments from every annotation subtype.</summary>
public static class PdfCommentReader
{
    /// <summary>Exports threaded review comments as readable text.</summary>
    public static string ExportText(PdfDocument document)
    {
        IReadOnlyList<PdfCommentThread> threads = ReadThreads(document);
        int count = threads.Sum(Count);
        var output = new StringBuilder();
        output.Append("Comments: ").AppendLine(count.ToString(CultureInfo.InvariantCulture));
        foreach (PdfCommentThread thread in threads) Append(thread, 0);
        return output.ToString().TrimEnd();

        void Append(PdfCommentThread thread, int depth)
        {
            PdfCommentInfo comment = thread.Comment;
            output.Append(' ', depth * 2).Append(depth == 0 ? "Comment" : "Reply")
                .Append(" on page ")
                .Append((comment.PageIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(", annotation ")
                .Append((comment.AnnotationIndex + 1).ToString(CultureInfo.InvariantCulture))
                .Append(" [").Append(comment.Subtype).Append(']');
            if (!string.IsNullOrWhiteSpace(comment.Author))
                output.Append(" by ").Append(SingleLine(comment.Author));
            if (!string.IsNullOrWhiteSpace(comment.Subject))
                output.Append(" (subject: ").Append(SingleLine(comment.Subject)).Append(')');
            output.AppendLine();
            output.Append(' ', depth * 2 + 2)
                .AppendLine(SingleLine(comment.Contents));
            foreach (PdfCommentThread reply in thread.Replies) Append(reply, depth + 1);
        }

        static int Count(PdfCommentThread thread) =>
            1 + thread.Replies.Sum(Count);
    }

    /// <summary>Exports threaded review comments as stable machine-readable JSON.</summary>
    public static string ExportJson(PdfDocument document, bool indented = true) =>
        JsonSerializer.Serialize(ReadThreads(document), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        });

    /// <summary>Returns comments grouped into reply threads in page annotation order.</summary>
    public static IReadOnlyList<PdfCommentThread> ReadThreads(PdfDocument document)
    {
        IReadOnlyList<PdfCommentInfo> comments = Read(document);
        Dictionary<int, PdfCommentInfo> byObject = comments
            .Where(comment => comment.ObjectNumber.HasValue)
            .ToDictionary(comment => comment.ObjectNumber!.Value);
        var replies = new Dictionary<int, List<PdfCommentInfo>>();
        foreach (PdfCommentInfo comment in comments)
        {
            if (comment.ReplyToObjectNumber is not int parent || !byObject.ContainsKey(parent))
                continue;
            if (!replies.TryGetValue(parent, out List<PdfCommentInfo>? children))
                replies.Add(parent, children = []);
            children.Add(comment);
        }
        var active = new HashSet<int>();
        var included = new HashSet<PdfCommentInfo>(ReferenceEqualityComparer.Instance);
        var roots = new List<PdfCommentThread>();
        foreach (PdfCommentInfo comment in comments)
            if (comment.ReplyToObjectNumber is not int parent || !byObject.ContainsKey(parent))
                roots.Add(Build(comment));
        foreach (PdfCommentInfo comment in comments)
            if (!included.Contains(comment)) roots.Add(Build(comment));
        return Array.AsReadOnly(roots.ToArray());

        PdfCommentThread Build(PdfCommentInfo comment)
        {
            if (!included.Add(comment))
                throw new InvalidOperationException(
                    "A comment appears more than once in the reply graph.");
            if (comment.ObjectNumber is not int objectNumber)
                return new PdfCommentThread(comment, []);
            if (!active.Add(objectNumber))
                throw new InvalidOperationException("The comment reply graph contains a cycle.");
            PdfCommentThread[] children = replies.TryGetValue(objectNumber, out var replyList)
                ? [.. replyList.Select(Build)] : [];
            active.Remove(objectNumber);
            return new PdfCommentThread(comment, Array.AsReadOnly(children));
        }
    }

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
        return new PdfContentBounds(left, bottom, right, top);
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

    private static string SingleLine(string value) => value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}

/// <summary>One review comment and its ordered replies.</summary>
public sealed record PdfCommentThread(
    PdfCommentInfo Comment, IReadOnlyList<PdfCommentThread> Replies);

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
