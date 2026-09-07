# Authoring documents

Use `PdfDocumentBuilder` to create a new PDF and `PdfContentStreamBuilder` to
describe page drawing operations and their resources. These examples target the
1.9 development API. See the [engine README](../README.md) for the project reference.

## Create a page with text and graphics

Page dimensions and drawing coordinates use PDF points. With the default user
space, 72 points equal one inch; the origin is at the bottom left. A US Letter
page is 612 by 792 points. Colors below use normalized components from zero to one.

```csharp
using KillerPdf.Engine.Authoring;

var content = new PdfContentStreamBuilder()
    .SaveState()
    .SetFillRgb(0.90, 0.94, 0.98)
    .Rectangle(54, 684, 504, 72).Fill()
    .RestoreState()
    .SetFillRgb(0.08, 0.10, 0.15)
    .BeginText()
    .SetFont(PdfStandardFont.HelveticaBold, 20)
    .MoveText(72, 712)
    .ShowLatin1Text("Engine example")
    .EndText();

byte[] pdf = new PdfDocumentBuilder()
    .SetMetadata(new PdfDocumentMetadata
    {
        Title = "Engine example",
        Author = "Example application",
        Language = "en-US"
    })
    .AddPage(612, 792, content)
    .Build();
File.WriteAllBytes("example.pdf", pdf);
```

The document builder defaults to PDF 2.0. Its constructor accepts a defined
`PdfVersion` when a different output version is required. Features have minimum
version checks; selecting an older header does not make newer features compatible.
`SetMetadata` validates a supplied language tag. Avoid putting credentials or
other unintended data into metadata, attachments, or page text.

Add pages before adding bookmarks, annotations, form widgets, or other entries
that refer to a page index. Those indices are zero-based.

## Keep content and resources together

Pass the typed content builder directly to `AddPage`. That overload transfers its
fonts, images, graphics states, and other resource definitions to the document.
Calling `content.Build()` first and passing only those bytes uses the raw-content
overload, which does not transfer the resource graph.

`Build` checks balanced graphics, text, and marked-content state. Pair each
`SaveState` with `RestoreState`, `BeginText` with `EndText`, and marked-content
opening with `EndMarkedContent`. Use a saved graphics state around temporary
transforms, clipping, colors, or opacity so they do not affect later content.

Paths are built with methods such as `MoveTo`, `LineTo`, `CurveTo`, `Rectangle`,
and `ClosePath`, then painted with `Stroke`, `Fill`, or `FillAndStroke`. Even-odd
variants use the corresponding alternate fill rule. `Clip` and `ClipEvenOdd`
consume the path as a clipping boundary without painting it.

`Transform(a, b, c, d, e, f)` concatenates an affine transform. It affects later
coordinates until restored. The content builder writes drawing instructions; it
does not automatically lay out paragraphs, tables, or page breaks.

## Fonts and Unicode

`PdfStandardFont` selects one of the standard Type 1 font names. These fonts are
not embedded replacements for a chosen typeface. `ShowLatin1Text` accepts the
supported single-byte text range and rejects characters outside it.

For Unicode text, first supply a font file whose license permits embedding and
whose glyphs cover the required text. Place it at `fonts/Example.ttf` for this
example, then load it through `TrueTypeFont.Load`:

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

TrueTypeFont font = TrueTypeFont.Load(File.ReadAllBytes("fonts/Example.ttf"));
var content = new PdfContentStreamBuilder()
    .BeginText()
    .SetFont(font, 18)
    .MoveText(72, 720)
    .ShowUnicodeText("Temperature: 23 °C")
    .EndText();

byte[] pdf = new PdfDocumentBuilder().AddPage(612, 792, content).Build();
File.WriteAllBytes("unicode.pdf", pdf);
```

The loader accepts supported OpenType fonts with TrueType or CFF outlines and
validates their tables. `EmbeddingAllowed` and `SubsettingAllowed` reflect the
font's embedding flags. Font selection rejects prohibited embedding; applications
still need to comply with the font license.

`ShowUnicodeText` requires an embedded font and records Unicode mappings for the
glyphs it writes. Choose text positioning explicitly with `MoveText`,
`SetTextMatrix`, leading, spacing, and positioned-text methods. Do not assume
that a font's Unicode coverage provides paragraph layout or language shaping.
Check extracted text and rendered glyphs when validating multilingual output.

## Images and alpha

Construct a `PdfImage` from encoded JPEG data or tightly packed sample data, then
place it with `DrawImage`. Image pixel dimensions and placement dimensions are
different: the former describes samples, while the latter uses PDF points.

```csharp
using KillerPdf.Engine.Authoring;

byte[] rgba =
[
    255, 0, 0, 255,
    0, 0, 255, 128
];
PdfImage image = PdfImage.FromRgba(2, 1, rgba);
var content = new PdfContentStreamBuilder().DrawImage(image, 72, 600, 144, 72);
byte[] pdf = new PdfDocumentBuilder().AddPage(612, 792, content).Build();
File.WriteAllBytes("image.pdf", pdf);
```

| Factory | Input |
| --- | --- |
| `FromJpeg` | A supported complete JPEG stream, retained in encoded form. |
| `FromRgb` | Three interleaved eight-bit components per pixel. |
| `FromGray` | One eight-bit grayscale component per pixel. |
| `FromCmyk` | Four interleaved eight-bit color components per pixel. |
| `FromRgba` | RGB plus alpha, split into color and PDF soft-mask images. |
| `FromGrayAlpha` and `FromCmyka` | Grayscale or CMYK components followed by alpha. |
| `FromBitonal` | One eight-bit sample per pixel, thresholded at 128 and packed into a one-bit image. |

The sample factories validate dimensions and exact input length. Supply RGBA,
not the renderer's BGRA byte order, to `FromRgba`. Alpha is a separate opacity
sample; do not premultiply the color components before passing them. Reuse a
`PdfImage` object when placing the same image repeatedly in a content builder.

`SetOpacity` sets paint opacity, while `SetBlendMode` controls compositing.
Neither changes the encoded image samples. For advanced reusable graphics, use
`PdfFormXObject`, `PdfTilingPattern`, and typed shading/color-space models.

## Navigation, notes, attachments, and fields

Create the target page first. The builder can then add interactive document
objects with page-relative coordinates and generate their appearances:

```csharp
using KillerPdf.Engine.Authoring;
using System.Text;

byte[] pdf = new PdfDocumentBuilder()
    .AddBlankPage(612, 792)
    .AddBookmark("Summary", 0)
    .AddTextNote(0, 72, 720, "Review this page.")
    .AddTextField(0, "CustomerName", 72, 650, 240, 24, value: "Example customer")
    .AddAttachment("notes.txt", Encoding.UTF8.GetBytes("Supporting notes"), "text/plain")
    .Build();
File.WriteAllBytes("interactive.pdf", pdf);
```

Bookmark levels describe a hierarchy: start at level zero and do not skip a
parent level. Field names must be unique. Text fields with Unicode values require
an embedded font; multiline values require the matching field options. Attachment
file names must be unique under the builder's case-insensitive comparison.

Viewer interaction depends on the host. The engine's renderer can include field
and annotation appearances, but it does not provide editable UI controls. A note,
widget, or attachment is a document object, not text painted into page content.

Additional typed APIs cover links, markup, shapes, stamps, checkboxes, radio
groups, choice fields, buttons, signature fields, named destinations, page labels,
transitions, initial view, and viewer preferences. Refer to their source summaries
and options rather than fabricating raw dictionaries for ordinary authoring.

## Conformance and output validation

PDF/A-4, PDF/A-4f, PDF/A-4e, and PDF/UA-2 authoring are explicit modes enabled on
the builder. They impose additional checks and require appropriate inputs.
For example, PDF/A forbids encryption, and accessibility requires meaningful
structure rather than only a document title or language tag. An output intent
needs a valid profile matching the intended use.

Choose conformance requirements before authoring content. These modes are not
general-purpose converters for arbitrary existing files, and `Build()` is not a
substitute for independent conformance validation. Use a validator appropriate
to the target standard and inspect the final rendered pages and extracted text.

The builder returns bytes and does not select a destination file or replace an
existing file on its own. For an application save workflow, validate the result
before replacing the original and follow the host's existing atomic-save policy.
Full rewrites and incremental editing of existing documents use the separate
writing and editing APIs.

Source entry points:
[PdfDocumentBuilder](../KillerPdf.Engine/Authoring/PdfDocumentBuilder.cs),
[PdfContentStreamBuilder](../KillerPdf.Engine/Authoring/PdfContentStreamBuilder.cs),
[PdfImage](../KillerPdf.Engine/Authoring/PdfImage.cs), and
[TrueTypeFont](../KillerPdf.Engine/Fonts/TrueTypeFont.cs).
See [reading](reading.md) and [rendering](rendering.md) for inspecting the result.
