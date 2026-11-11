using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads review comments from every annotation subtype.</summary>
public static partial class PdfCommentReader
{
    private static readonly PdfCommentCompactJsonContext CompactJson = new(
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private static readonly PdfCommentIndentedJsonContext IndentedJson = new(
        new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

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
    public static string ExportJson(PdfDocument document, bool indented = true)
    {
        PdfCommentThread[] threads = [.. ReadThreads(document)];
        return JsonSerializer.Serialize(threads, indented
            ? IndentedJson.PdfCommentThreadArray
            : CompactJson.PdfCommentThreadArray);
    }

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

    /// <summary>
    /// Returns the raw page annotation-array count, including entries omitted by comment reading.
    /// A missing array has count zero; a malformed array is rejected.
    /// </summary>
    public static int ReadPageAnnotationCount(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before reading annotations.");
        IReadOnlyList<PdfPageTreeEntry> pages = PdfPageTree.Read(document).Pages;
        if ((uint)pageIndex >= (uint)pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (!pages[pageIndex].Dictionary.TryGetValue(Name("Annots"), out PdfObject? value))
            return 0;
        return Resolve(document, value) is PdfArray annotations
            ? annotations.Count
            : throw new InvalidOperationException(
                $"Page {pageIndex + 1} /Annots value is not an array.");
    }

    /// <summary>
    /// Returns every page's raw annotation-array count from one page-tree read, as
    /// <see cref="ReadPageAnnotationCount"/> counts a single page. A malformed array is rejected.
    /// </summary>
    public static IReadOnlyList<int> ReadPageAnnotationCounts(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before reading annotations.");
        IReadOnlyList<PdfPageTreeEntry> pages = PdfPageTree.Read(document).Pages;
        var counts = new int[pages.Count];
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            if (!pages[pageIndex].Dictionary.TryGetValue(Name("Annots"), out PdfObject? value)) continue;
            counts[pageIndex] = Resolve(document, value) is PdfArray annotations
                ? annotations.Count
                : throw new InvalidOperationException(
                    $"Page {pageIndex + 1} /Annots value is not an array.");
        }
        return Array.AsReadOnly(counts);
    }

    /// <summary>Returns annotations that contain review text, in page annotation order.</summary>
    public static IReadOnlyList<PdfCommentInfo> Read(PdfDocument document) => Read(document, includeMarkupWithoutText: false);

    /// <summary>
    /// Returns annotations that contain review text, plus, when <paramref name="includeMarkupWithoutText"/>
    /// is set, text markup, drawings and stamps that carry none (their <see cref="PdfCommentInfo.Contents"/>
    /// is empty). Screen readers announce those too.
    /// </summary>
    public static IReadOnlyList<PdfCommentInfo> Read(PdfDocument document, bool includeMarkupWithoutText) =>
        Read(document, includeMarkupWithoutText, null);

    /// <summary>
    /// Reads comments as <see cref="Read(PdfDocument, bool)"/> does, except that when
    /// <paramref name="unreadablePages"/> is given, a malformed annotation is skipped and its page
    /// recorded there instead of failing the whole read.
    /// </summary>
    public static IReadOnlyList<PdfCommentInfo> Read(PdfDocument document, bool includeMarkupWithoutText,
        ICollection<int>? unreadablePages)
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
            if (Resolve(document, annotationsValue) is not PdfArray annotations)
            {
                if (unreadablePages is null)
                    throw new InvalidOperationException(
                        $"Page {page.Index + 1} /Annots value is not an array.");
                unreadablePages.Add(page.Index);
                continue;
            }
            for (int index = 0; index < annotations.Count; index++)
            {
                PdfCommentInfo? comment;
                try { comment = ReadAnnotation(document, page.Index, annotations[index], index, includeMarkupWithoutText); }
                catch (Exception error) when (unreadablePages is not null
                    && error is InvalidOperationException or FormatException)
                {
                    unreadablePages.Add(page.Index);
                    continue;
                }
                if (comment is not null) comments.Add(comment);
            }
        }
        return Array.AsReadOnly(comments.ToArray());
    }

    private static PdfCommentInfo? ReadAnnotation(PdfDocument document, int pageIndex, PdfObject item,
        int index, bool includeMarkupWithoutText)
    {
        PdfIndirectReference? reference = item as PdfIndirectReference;
        PdfDictionary annotation = Resolve(document, item) as PdfDictionary
            ?? throw new InvalidOperationException(
                $"Page {pageIndex + 1} annotation {index + 1} is not a dictionary.");
        string? contents = Text(document, annotation, "Contents");
        if (string.IsNullOrEmpty(contents))
        {
            if (!includeMarkupWithoutText
                || !(annotation.TryGetValue(Name("Subtype"), out PdfObject? bareSubtype)
                    && Resolve(document, bareSubtype) is PdfName bareName
                    && IsMarkup(bareName.ValueAsLatin1()))) return null;
            contents = string.Empty;
        }
        string subtype = annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
            && Resolve(document, subtypeValue) is PdfName subtypeName
            ? subtypeName.ValueAsLatin1()
            : throw new InvalidOperationException(
                $"Page {pageIndex + 1} annotation {index + 1} has no subtype name.");
        return new PdfCommentInfo
        {
            PageIndex = pageIndex,
            AnnotationIndex = index,
            ObjectNumber = reference?.ObjectNumber,
            Subtype = subtype,
            Name = Text(document, annotation, "NM"),
            Contents = contents,
            Author = Text(document, annotation, "T"),
            Subject = Text(document, annotation, "Subj"),
            Bounds = Bounds(document, annotation, pageIndex, index),
            ReplyToObjectNumber = ReplyTarget(document, annotation)
        };
    }

    /// <summary>Annotation subtypes that mark up the page itself: text markup, drawings and stamps.</summary>
    public static bool IsMarkup(string subtype) => subtype is "Highlight" or "Underline" or "StrikeOut"
        or "Squiggly" or "Ink" or "Square" or "Circle" or "Line" or "Polygon" or "PolyLine" or "Stamp" or "FreeText";

    internal static PdfContentBounds? Bounds(PdfDocument document,
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
        // ISO 32000-1 7.9.5: a rectangle may name any two opposite corners.
        return new PdfContentBounds(Math.Min(left, right), Math.Min(bottom, top),
            Math.Max(left, right), Math.Max(bottom, top));
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

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(PdfCommentThread[]))]
    private sealed partial class PdfCommentCompactJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(PdfCommentThread[]))]
    private sealed partial class PdfCommentIndentedJsonContext : JsonSerializerContext;
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
