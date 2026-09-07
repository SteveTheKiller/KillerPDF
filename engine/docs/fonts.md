# Fonts, text codes, and outlines

The engine separates PDF character codes, extracted Unicode text, glyph outlines,
and text advance widths. A readable character does not prove that its outline is
available, and an outline does not prove that its Unicode mapping is correct.
Use the [rendering guide](rendering.md) for pixel output and the
[authoring guide](authoring.md) for placing text on new pages.

## Read a font resource

`PdfFontResourceReader.Read(document, fontDictionary, resolver)` resolves a PDF
font resource against its owning document. Pass the font dictionary, rather than
the page's entire `/Resources` or `/Font` dictionary. The document supplies any
referenced font programs, descriptors, widths, encodings, and Unicode maps.

This complete example inspects the standard Symbol font without an installed
font or an embedded program:

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;

static PdfExtractionFont ReadStandardSymbol()
{
    PdfDocument document = PdfDocument.Open(
        new PdfDocumentBuilder().AddBlankPage().Build());
    var dictionary = new PdfDictionary([
        new(new PdfName("Subtype"u8), new PdfName("Type1"u8)),
        new(new PdfName("BaseFont"u8), new PdfName("Symbol"u8))
    ]);
    return PdfFontResourceReader.Read(document, dictionary);
}
```

For this font, `font.Decode(new byte[] { 0x22 })` returns the universal-quantifier
character U+2200. `font.GetGlyphOutline(0x22)` reads its outline. The argument is
the PDF character code, not the Unicode scalar U+2200. Keep the document and its
backing input available while using a font that resolves document objects.

`PdfExtractionFont` exposes:

| Member | Meaning |
| --- | --- |
| `Decode` | Decoded characters with their original codes and byte lengths; unmapped characters use U+FFFD. |
| `Unicode` | The parsed Unicode map, which can be incomplete. |
| `GetWidth` | Advance width for a source character code, including the configured missing width. |
| `GetGlyphBounds` | Available outline or standard-metric bounds; null when unavailable. |
| `GetGlyphOutline` | Available embedded, bundled, or host-resolved contours; null when unavailable. |
| `GetVerticalMetrics` | Vertical advance and origin offsets for the source code. |
| `FontName`, `IsVertical`, `Diagnostics` | Font identity, writing direction, and recorded recovery conditions. |

Widths, bounds, ascent, descent, and contour coordinates use thousandths of text
space. They are not page coordinates. Apply text size and the text and graphics
transforms before comparing them with rendered pixels. Contours contain on-curve
points and quadratic or cubic control points; connecting every point with a
straight line does not reproduce the glyph.

## Standard fonts and substitutions

Unembedded ordinary standard fonts have bundled Liberation substitutes. The
Windows app can supply installed fonts through its resolver, including matching
Courier New faces for unavailable Courier. A standalone library host controls
its own installed-font policy.

Unembedded Symbol and ZapfDingbats use fixed bundled CFF programs before host
lookup. Their 189 and 202 standard encoding names, respectively, include space.
Encoding differences select glyph names from these programs. Embedded font
programs retain precedence. The source revision, font-data hashes, and license
are recorded in the [validation record](../../validation/records/portable-symbol-1.9.0.json)
and [third-party notices](../THIRD-PARTY-NOTICES.txt).

Substitution can preserve readable text while changing shape or spacing. Inspect
the rendered result when a document depends on a specific typeface. A missing
host glyph can fall through to bundled outlines; undefined simple-font codes
do not borrow unrelated installed glyphs with the same numeric position.

## Supply host fonts

Implement `IPdfFontResolver` and pass it to `PdfPageRenderer` or
`PdfFontResourceReader.Read`. Return standalone TrueType or supported OpenType
font bytes, or null when unavailable. The engine does not enumerate installed
fonts or fetch font files. Collection faces must first be exposed as standalone
font programs by the host.

This resolver uses exact requests and retains private copies of supplied bytes:

```csharp
using KillerPdf.Engine.Fonts;

sealed class MappedFontResolver : IPdfFontResolver
{
    private readonly Dictionary<PdfFontRequest, byte[]> _fonts;

    public MappedFontResolver(IReadOnlyDictionary<PdfFontRequest, byte[]> fonts)
    {
        _fonts = fonts.ToDictionary(entry => entry.Key,
            entry => entry.Value.ToArray());
    }

    public byte[]? Resolve(PdfFontRequest request) =>
        _fonts.GetValueOrDefault(request);
}
```

Each request includes `PostScriptName`, `Registry`, `Ordering`, and `IsVertical`.
Match style and CJK collection information deliberately when implementing a
broader fallback policy. Reuse resolver data and renderer instances when possible;
repeated filesystem scans and font decoding can dominate a small page's render
time. The example map is populated before use and does not mutate returned data.

For an unembedded Adobe-Identity CIDFontType2 resource without a `ToUnicode` map,
the engine applies `CIDToGIDMap` (or its identity default) to the supplied font.
These values are glyph IDs, not Unicode scalars. Return the requested font with
its original glyph ordering for this case. An unrelated substitute can place
different characters at the same IDs. Extraction uses the supplied font's reverse
character map when available; explicit PDF Unicode maps retain precedence.

## Embed text when authoring

Supply font bytes whose license and embedding flags permit the intended use and
whose glyphs cover the text. `TrueTypeFont.Load` validates the font. Use
`GetGlyphId(unicodeScalar)` to inspect character coverage; zero means unmapped.
The following function authors a small page with an embedded font and Unicode
mapping:

```csharp
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;

static byte[] CreateUnicodePage(byte[] fontBytes, string text)
{
    TrueTypeFont font = TrueTypeFont.Load(fontBytes);
    var content = new PdfContentStreamBuilder()
        .BeginText()
        .SetFont(font, 18)
        .MoveText(20, 60)
        .ShowUnicodeText(text)
        .EndText();
    return new PdfDocumentBuilder().AddPage(200, 100, content).Build();
}
```

This does not perform paragraph layout, line wrapping, or font fallback within a
string. Verify glyph coverage and page fit before authoring long or multilingual
text. `EmbeddingAllowed` and `SubsettingAllowed` describe font flags; they do not
replace the font's license terms.

## Limits and failure behavior

Malformed font tables, unsupported programs, invalid encodings, or incomplete
Unicode maps can fail strict reading. Compatibility recovery handles supported
malformations and reports diagnostics; it does not manufacture missing glyphs.
Type 3 fonts use PDF drawing procedures and must be evaluated through page
rendering for their appearance.

During page rendering only, compatibility recovery can supply a missing font
resource whose name exactly matches a standard PDF font, such as `/Helvetica`.
This restores text in appearances that omit the corresponding resource entry.
The page reports the recovery. Arbitrary resource names such as `/F1` are not
guessed, and existing font dictionaries retain precedence. This does not
regenerate stale form appearances from field values.

`PdfCffGlyphReader.TryRead` accepts standalone CFF1 or supported OpenType CFF
tables and returns null for unsupported or malformed data. Its name, CID, and
Unicode lookup methods return -1 when absent. A readable program can still have
an unsupported glyph operation, so check the outline result. The reader rejects
inputs larger than 64 MiB and bounds glyph interpretation.

Inspect font and page diagnostics together, retain source codes when auditing
text, and compare complete rendered pages against expected content. Neither
successful text extraction nor a non-null glyph outline is a conformance check.
