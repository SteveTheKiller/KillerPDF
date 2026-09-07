# Editing and saving documents

Use `PdfIncrementalPageEditor` for typed page and document edits. It appends
revisions while preserving the original byte prefix. Use `PdfDocumentWriter`
when the intended output is a full rewrite. Both return new bytes; the host owns
file selection, validation, and replacement of a saved file.

Examples target the current 1.9 source. Authenticate encrypted inputs first and
check the operation's permission and signature requirements described in the
[security guide](security.md).

## Change page order and rotation

Indexes refer to the editor's current page order and start at zero. A move's
destination is the page's final position. Each subsequent operation observes
the order left by the previous operation.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] MoveFirstPageToEnd(PdfDocument document)
{
    var editor = new PdfIncrementalPageEditor(document);
    if (editor.PageCount < 2)
        throw new ArgumentException("At least two pages are required.", nameof(document));
    editor.MovePage(0, editor.PageCount - 1);
    editor.RotateClockwise(editor.PageCount - 1);
    return editor.Build();
}
```

`RotateClockwise` adds 90 degrees to the existing rotation. `SetRotation` sets an
absolute rotation. Rotation changes presentation rather than rewriting every
page instruction. `RemovePage`, `InsertBlankPage`, and `AddBlankPage` operate on
the same current order. Building an editor with no pending changes throws.

Page boxes and drawing coordinates use PDF points with a bottom-left origin.
Crop and media boxes can have nonzero origins. Refer to the
[geometry description](reading.md#page-geometry) before translating screen
coordinates to edits; a display bitmap also accounts for page rotation.

## Append content with its resources

Typed content carries its fonts, images, colors, and graphics state into an
isolated Form XObject. This example assumes an existing, unrotated US Letter page
with an origin of zero and no logical structure tree.

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] AddReviewLabel(PdfDocument document)
{
    var label = new PdfContentStreamBuilder()
        .SetFillRgb(0.2, 0.2, 0.2)
        .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
        .MoveText(36, 36).ShowLatin1Text("Reviewed copy").EndText();
    return new PdfIncrementalPageEditor(document)
        .AppendPageContent(0, 612, 792, label)
        .Build();
}
```

The width and height define the authored overlay's page area. The overload taking
`PdfContentBounds` supplies a destination rectangle. Typed overlays require an
existing destination page; build and reopen newly inserted pages before applying
these overlays to them.

`SetPageContent` replaces the selected page's content. Its raw-byte and instruction
overloads require the caller to maintain valid resource references. Raw
`AppendPageContent` does not import resources from another document.
`SetPageContentAndPruneResources` also removes unused font, XObject, and color-space
resources referenced by the supported rewritten instruction sequence.

For tagged PDFs, ordinary content changes retain structure guards.
`AppendPageArtifact` is an explicit path for decorative content that does not
belong in the logical reading order. It rejects marked-content identifiers;
do not use it to bypass structure requirements for meaningful text.

## Import a page from another document

Open and authenticate both documents before import. The page editor imports
dependent objects and resources rather than reusing source object numbers.

```csharp
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

static byte[] AppendFirstPage(PdfDocument destination, PdfDocument source)
{
    return new PdfIncrementalPageEditor(destination)
        .AddImportedPage(source, 0)
        .Build();
}
```

Use `AddImportedPages` or `InsertImportedPages` for a selected ordered set whose
cross-page references need to be remapped together. Dependencies on omitted
pages can remain unsupported. Whole-document imports have their own methods.
Imports enforce source-copy permissions and validate supported structures.

Tagged-page and form relationships require structural import handling.
`AllowUntaggedPageImports` is an explicit opt-in for intentionally untagged pages
in a tagged destination. It does not reconstruct missing accessibility data.
`AppendImportedPageContent` imports a page as transformed drawing content and
does not copy its annotations.

## Choose the appropriate editor

| Task | API |
| --- | --- |
| Pages, boxes, form values, metadata, and typed overlays | `PdfIncrementalPageEditor` |
| Notes, markup, links, and annotation updates | `PdfIncrementalAnnotationEditor` |
| Bookmark hierarchy | `PdfBookmarkEditor` |
| Optional-content layers | `PdfOptionalContentEditor` |
| Low-level indirect-object revisions | `PdfIncrementalUpdateBuilder` |
| Full rewrite and output serialization policy | `PdfDocumentWriter` |

For low-level updates, `AddObject` allocates an indirect reference, `ReserveObject`
and `SetObject` support forward references, and `ReplaceObject` supersedes an
existing object. These methods require valid PDF object graphs. Prefer typed
editors when they cover the operation.

## Validate the saved result

Reopen the returned bytes before replacing the source. Check the intended page
count, order, geometry, extracted content, forms, and annotations. Render changed
pages with the host's actual output settings and inspect diagnostics and pixels.
Successful parsing alone does not establish visual correctness or conformance.

For incremental output, compare the leading bytes with the source to confirm
preservation. Earlier revisions remain in the file, including superseded content.
Removing a page or covering text visually does not securely erase those bytes.
Use the engine's dedicated removal and verification workflows for sensitive data.

A full rewrite can prune unreachable objects or compress streams according to
`PdfDocumentWriteOptions`, but these options alone are not a redaction guarantee.
Encryption and signature preservation require separate policy choices. Reopen
each saved result as a new `PdfDocument` and create readers and renderers for that
instance so cached state continues to describe the correct bytes.
