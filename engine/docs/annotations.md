# Annotations and review comments

The engine provides annotation authoring, incremental edits, and review-comment
data. The host supplies selection, popup windows, review decisions, and file
saving. Use the [rendering guide](rendering.md) for saved appearance output and
the [form guide](forms.md) for widgets and field values.

The examples below use an unencrypted, unsigned document. For existing protected
files, authenticate first and follow the [security guide](security.md).

## Create a review thread

This complete example creates a page, adds a note and a reply in one revision,
and returns bytes for the host to save:

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] CreateReviewExample()
{
    byte[] original = new PdfDocumentBuilder().AddBlankPage(612, 792).Build();
    PdfDocument document = PdfDocument.Open(original);
    return new PdfIncrementalAnnotationEditor(document)
        .AddTextNote(0, 72, 680, "Check the heading", name: "heading-review",
            annotationMetadata: new PdfAnnotationMetadata
            {
                Author = "Reviewer",
                Subject = "Heading"
            })
        .AddTextNote(0, 100, 680, "Suggested wording is ready",
            name: "heading-reply", inReplyTo: "heading-review")
        .Build();
}
```

Page indices are zero-based. These annotation coordinates use page user space
with a bottom-left origin. They are not screen pixels or crop-relative
extraction bounds. Apply the page's crop, rotation, and display transform in the
host before turning a pointer selection into annotation geometry.

Give authored annotations distinct names when later operations refer to them by
name. A reply target is an annotation name, not its text or author. The editor
resolves relationships and validates the resulting graph when building output.
An optional `PdfAnnotationPopup` records popup geometry and initial open state;
it does not create a host window.

## Read comments and identities

`PdfCommentReader.Read(document)` returns annotations with nonempty `/Contents`,
in page and annotation-array order. It includes review text on highlights and
other subtypes, not just text-note annotations. It is not a complete inventory
of annotations: an empty highlight, for example, is omitted.

| Result member | Contract |
| --- | --- |
| `PageIndex`, `AnnotationIndex` | Zero-based positions in the current source document. |
| `ObjectNumber` | Object identity when the page array directly references the annotation; can be null. |
| `Subtype`, `Name` | PDF annotation subtype and optional `/NM` identifier. |
| `Contents`, `Author`, `Subject` | Decoded review text and optional metadata. |
| `Bounds` | Optional raw annotation rectangle, without crop or rotation normalization. |
| `ReplyToObjectNumber` | Referenced parent annotation object, when present. |

Re-read comments after changing or reopening a document. Page and annotation
positions can move, and object numbers alone do not identify a document revision.
Keep selections associated with their source document rather than reusing them
against an unrelated file.

`ReadThreads(document)` returns roots with ordered `Replies`. A reply whose
parent has no returned review text appears as a root. Invalid relationships,
duplicate identities, or cyclic reply graphs can throw; they are not silently
flattened into a successful report. The reader also rejects malformed annotation
dictionaries, text values, and rectangles. These APIs have no cancellation-token
overload, so isolate large or untrusted batch work at the host process boundary.

## Export and edit a selected comment

The following functions can live in the same host service as the example above:

```csharp
using KillerPdf.Engine.Documents;

static string ExportReview(byte[] pdf)
{
    return PdfCommentReader.ExportJson(PdfDocument.Open(pdf), indented: true);
}

static byte[] ReplaceSelectedComment(
    PdfDocument document, PdfCommentInfo selected, string reviewedText)
{
    return PdfCommentEditor.SetContents(document, selected, reviewedText);
}

static byte[] RemoveSelectedComment(
    PdfDocument document, PdfCommentInfo selected)
{
    return PdfCommentEditor.Remove(document, selected);
}
```

JSON uses camel-case properties and nested `comment`/`replies` objects.
`ExportText(document)` produces a readable threaded report with one-based page
and annotation numbers. Its display formatting collapses line breaks in comment
text. Neither report is an import format or a complete annotation round trip.
For FDF/XFDF interchange, use the data APIs described in the [form guide](forms.md).

`PdfCommentEditor` re-reads the selected position and checks a supplied object
number before editing. Its text setter rejects empty or whitespace-only text.
For a deliberate clear operation, the lower-level
`PdfIncrementalAnnotationEditor.SetAnnotationContentsAt` accepts null. Clearing
contents leaves the annotation present, but removes it from comment-reader
results. Indexed edits require an indirect annotation dictionary even when the
reader can inspect a direct dictionary.

Removing a comment removes its annotation, including a highlight or other
visible markup. It does not merely clear the comment text. Popup removal goes
through its parent. Remaining replies cannot point to a removed annotation;
remove dependent replies in the same editor operation or before their parent.
Do not reuse old selection positions after an intermediate removal.

## Appearance and preservation boundaries

Content and metadata setters update annotation dictionary data. They do not
regenerate an existing appearance stream. Changing a FreeText comment's contents
therefore does not guarantee that its painted text changes. Reopen and render
the output when appearance matters. Adding a typed annotation creates its
associated appearance where that annotation type supports one.

The annotation editor also offers highlights, underline/strikeout/squiggly
markup, free text, lines, shapes, ink, image stamps, carets, redaction marks,
attachments, and links. Quadrilateral markup overloads support nonrectangular
text regions. See [navigation](navigation.md) for link targets and
[attachments](attachments.md) for embedded-file relationships.

A redaction mark is proposed markup, not removal of underlying content. Likewise,
removing an annotation through an incremental revision is not secure erasure:
the original bytes remain in the file. Use a deliberate content-removal and
sanitization workflow when data must be removed.

`Build()` validates pending changes and returns an incremental revision with
the original byte prefix preserved. It rejects an empty operation, forbidden
user-password annotation edits, incompatible certification restrictions, and
unsafe structural relationships. A successful build is not proof of signature
trust, complete appearance parity, or standards conformance. Retain the original,
reopen the result, inspect values and diagnostics, and render affected pages
before accepting output.
