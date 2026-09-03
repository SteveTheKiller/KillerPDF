# PdfPig replacement for 1.9.0 Overkill

Status: content reader, explicit Unicode maps, and horizontal text placement implemented. Font-resource resolution and application migration remain pending.

## First implementation checkpoint

`PdfContentStreamReader` reuses the engine tokenizer and direct-object parser to read operators,
text strings, spacing arrays, and marked-content dictionaries. It retains byte offsets and applies
source-size, instruction, operand, and nesting limits. Inline images currently cause an explicit
unsupported-content error rather than allowing binary image bytes to be mistaken for text.

`PdfToUnicodeMap` decodes explicit character and range mappings, including ligatures and
supplementary Unicode characters. Missing mappings and inherited maps are reported explicitly.
`PdfTextContentReader` uses caller-supplied font maps and source-code widths to produce character
baselines in PDF coordinates. It handles text matrices, graphics transforms, text spacing,
line movement, horizontal scaling, rise, and saved graphics state. These baselines describe
advance widths, not glyph outlines or ready-to-use selection rectangles.

Next: resolve font dictionaries, embedded ToUnicode streams, and character/CID widths directly
from page resources. Then add fallback font encodings, nested forms, inline images, vertical
writing, glyph bounds, and reading-order grouping. No application feature has switched away
from PdfPig yet. Marked-content ActualText replacement and visibility/clipping interpretation
also remain pending; current placement results preserve source character order.

## Current uses

| Behavior | Implementation | Data to preserve |
| --- | --- | --- |
| Search | `Services/SearchService.cs` | Words, phrase matching, page order, bounding boxes |
| Flowing selection | `Services/TextRunService.cs` | Characters, ligatures, reading order, columns, right-to-left text, page dimensions |
| Region copy | `Controls/Viewer/PdfViewer.Selection.cs`, `MainWindow.xaml.cs` | Word bounds and ordered region text |
| Text editing | `Controls/Viewer/PdfViewer.TextEditing.cs` | Word and letter bounds, font name, effective font size, placeholder handling |
| Dark-mode image preservation | `Services/PdfImages.cs`, `Controls/Viewer/PdfViewer.Viewport.cs` | Image placement bounds, page dimensions, rotation and clipping |
| Viewer host boundary | `Features/Viewer/IViewerHost.cs`, `MainWindowViewerHost.cs`, `Controls/Viewer/PdfViewer.Bridge2.cs` | Replace exposed PdfPig word types with owned types |

The application references PdfPig 0.1.15. The app tests independently reference 0.1.14. Both references must be removed, including tests that currently use PdfPig to inspect engine output.

## Implementation order

1. Define owned extraction models and fixture expectations for text, glyph geometry, font size, and image placement. Preserve PDF-point coordinates and the existing conversion to display coordinates.
2. Implement content interpretation and font decoding in the portable engine, with bounded handling of nested forms and malformed inputs. The engine currently authors content streams but does not provide the extraction API these callers need.
3. Move search and selection to the owned extraction results, followed by region copy and text editing. Preserve current reading-order and cache-invalidation behavior.
4. Move image placement extraction and remove PdfPig types from the viewer host interfaces.
5. Replace PdfPig-based test assertions with independently checked fixture expectations. Remove both package references, unused aliases, and outdated dependency documentation.

## Verification before publishing the branch

- App and engine tests pass without PdfPig in either dependency graph.
- Extraction fixtures cover effective text size, ligatures, non-Latin text, columns, rotated pages, OCR text, nested image placements, and malformed content.
- Search, selection, text editing, and dark-mode image preservation work on representative documents.
- Compare extraction output and run corpus validation against the recorded baseline. Publish only measurements actually performed; opening and saving alone do not verify text or image geometry.

This branch stays local until dependency removal is complete. Publication requires separate authorization.
