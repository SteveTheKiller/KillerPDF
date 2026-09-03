# PdfPig replacement for 1.9.0 Overkill

The app now uses The KillerPDF.Engine for text and image placement extraction.
PdfPig is no longer a dependency of the app, engine, tests, or packaged payload.
PDFium remains the rendering backend.

## Application integration

`Services/PdfContentDocument.cs` owns the document and caches extracted pages.
It retains the app's one-based page numbering while the engine reader uses zero-based indices.

| Behavior | Implementation | Extraction data |
| --- | --- | --- |
| Search | `Services/SearchService.cs` | Words and bounding boxes |
| Flowing selection | `Services/TextRunService.cs` | Characters, baselines, font sizes, and page dimensions |
| Region copy | Viewer selection and `MainWindow.xaml.cs` | Word bounds and ordered region text |
| Text editing | Viewer text editing | Word and letter bounds, font names, and effective sizes |
| Dark-mode image preservation | `Services/PdfImages.cs` and viewer viewport | Image placements and clipping bounds |
| Viewer host | `IViewerHost` and its implementations | Engine-owned word types |

## Engine extraction

`PdfPageContentReader` resolves inherited resources, nested forms, fonts, and content streams.
`PdfTextContentReader` interprets text matrices, spacing, graphics transforms, horizontal and
vertical writing, and marked-content ActualText replacements. The output includes words,
letters, font names, effective sizes, baselines, and image placements.

Coordinates are unrotated PDF points relative to the crop box, with a bottom-left origin.
The app continues to apply page rotation and display-coordinate conversion. Existing text-run
logic handles selection order, columns, and right-to-left text.

Font decoding supports explicit ToUnicode maps, standard encodings, Differences arrays,
predefined CJK maps, CID widths, and vertical metrics. Standard and embedded font geometry
supplies glyph bounds where available; otherwise font metrics provide fallback rectangles.
Static font data is covered by `engine/THIRD-PARTY-NOTICES.txt`, which ships in the engine
NuGet package and app payload.

Inline images are skipped using their encoded boundaries rather than interpreting binary bytes
as text. Raw, ASCIIHex, ASCII85, RunLength, JPEG, Flate, LZW, and common CCITT streams are
supported. Expanded content, instruction counts, text size, recursion, and parser depth are
bounded; page extraction accepts cancellation.

## Scope and limits

- Extracted text preserves content order. Word grouping can differ from other extractors.
- Invisible OCR text remains available. Extraction does not reproduce rendering visibility,
  arbitrary clipping paths, or optional-content display decisions.
- Image clipping uses bounding rectangles. It does not decode image pixels.
- Unavailable glyph outlines use metric rectangles. These can be less precise than outlines.
  This includes CFF2, predefined Expert CFF charsets, unsupported CFF composite or randomized
  outlines, and unusual Type1 OtherSubrs. Unknown simple encodings require a ToUnicode map.
- Unsupported or malformed content produces an error rather than an unbounded recovery loop.
  Missing graphics-state resources and an unclosed saved graphics state are recoverable and
  recorded in page diagnostics.
- This migration does not relax document parsing or change the app's PDFium repair path.

## Verification

Focused fixtures cover Unicode decoding, ligatures, fonts, vertical text, nested forms, ActualText,
inline images, geometry, cancellation, and malformed content. App integration tests exercise
search, flowing selection, word bounds, and effective text size without PdfPig.

The [extraction validation report](../validation/extraction/1.9.0/README.md) records representative
comparisons and the 1.8.3 structural baseline separately from release benchmarks. An extraction comparison does not establish rendering accuracy,
standards conformance, or full-corpus save performance.

The Overkill branch remains local. Publishing the branch or a release requires separate authorization.
