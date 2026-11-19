# Editing and saving documents

Use `PdfIncrementalPageEditor` for typed page and document edits. It appends
revisions while preserving the original byte prefix. Use `PdfDocumentWriter`
when the intended output is a full rewrite. Both return new bytes; the host owns
file selection, validation, and replacement of a saved file.

Examples target the 1.9 source. Authenticate encrypted inputs first and
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
belong in the logical reading order. Tagged PDFs can use it for
explicit temporary artifact overlays while the structure and the ordinary
editing guards stay in place. It rejects marked-content identifiers;
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

## Edit a simple paragraph

`PdfParagraphEditor.FindParagraphs` identifies conservative paragraph candidates.
`Inspect` checks a manually selected source region. Coordinates are unrotated
points relative to the crop box, with a bottom-left origin.

Pass the fixed source region and a separate replacement box to `Plan`, together
with replacement text, an embeddable `TrueTypeFont`, size, baseline spacing and
`PdfParagraphAlignment`. The original source color is retained. The chosen font
is embedded; hosts should identify substitutions before the user accepts them.

`HasOverflow` blocks applying text whose advances or glyph ink exceed the box.
`GetPreviewDocument` returns the exact PDF bytes that `Apply` returns after
checking the source fingerprint. Changing text or geometry requires a new plan.
The host owns cancellation, Undo, saving and source-document lifetime checks.

The first slice supports horizontal, single-style Latin text in a single text
object on an unrotated page. Encryption, signatures, certification, tags, Form
XObjects, special graphics states, clipping and unsupported text transforms are
rejected. Combining marks, contextual scripts and missing replacement glyphs
are unsupported. It does not move neighboring objects or reflow across pages.

Replacement removes the selected source glyphs from the active page content;
it does not cover them with a drawing. This is an incremental edit, so previous
revisions still contain superseded text. Use redaction for sensitive content.

## Edit a line of text

`PdfTextLineEditor` rewrites text inside the page content itself. The old glyphs
leave the content stream and the new text is written in the same text object, so
it keeps the original font, size, color, clipping, and stacking order unless the
edit changes them. No cover is drawn over the page. `Inspect` reports, without
changing the document, how many glyphs lie in an area and whether a
`PdfTextLineBlocker` prevents the edit, such as invisible text, a zero font
size, or a font that cannot be decoded.

```csharp
using KillerPdf.Engine.Documents;

static byte[] ReplaceLine(PdfDocument document, PdfContentBounds region)
{
    var edit = new PdfTextLineEdit(0, region, "Revised text");
    return PdfTextLineEditor.Replace(document, edit).Document;
}
```

A `PdfTextLineEdit` can also set a new size, fill color, character spacing,
underline, or strike-through. When the original font cannot show the new text, or
when `UseSubstitute` asks for it, the edit writes the text in the embeddable
`Substitute` font and embeds it. `Replace` throws `NotSupportedException` when the
line cannot be rewritten or a needed substitute font was not supplied. The
result reports which font was used and how many old glyphs were removed.

The edit is incremental, so earlier revisions still contain the old text. Use
redaction for sensitive content.

## Replace a region with raster pixels

`PdfPartialRasterization.Plan` validates a region of a page and lists the text
runs, images, paths, and shadings it touches, with the exact pixel size needed
at a chosen resolution. The host renders those pixels and passes them to
`Apply`, which replaces the visual content of the region as an incremental
revision. Several non-overlapping regions can be applied together.

## Divide scanned spreads

`PdfSpreadDivider.Divide` splits each selected page at a displayed position from
the left or top edge, accounting for page rotation. Each selected page is
followed by its second half, and unselected pages keep their order.

## Choose the appropriate editor

| Task | API |
| --- | --- |
| Preview and replace a simple paragraph | `PdfParagraphEditor` |
| Rewrite one line of text in place | `PdfTextLineEditor` |
| Replace a region with raster pixels | `PdfPartialRasterization` |
| Split scanned two-page spreads | `PdfSpreadDivider` |
| Pages, boxes, form values, metadata, and typed overlays | `PdfIncrementalPageEditor` |
| Notes, markup, links, and annotation updates | `PdfIncrementalAnnotationEditor` |
| Bookmark hierarchy | `PdfBookmarkEditor` |
| Optional-content layers | `PdfOptionalContentEditor` |
| Low-level indirect-object revisions | `PdfIncrementalUpdateBuilder` |
| Full rewrite and output serialization policy | `PdfDocumentWriter` |

Form edits through `PdfIncrementalPageEditor` keep a comb field's `/MaxLen` and
`/Rect`, refuse a value longer than `/MaxLen`, and also reach page widgets that a
stale `/Fields` list no longer lists, under the names `PdfFormWidgetReader`
reports. `SetTextFieldTextStyle` sets a text field's font, color, underline, and
strikethrough. The [forms guide](forms.md) covers these rules.

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
