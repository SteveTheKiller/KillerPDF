# KillerPDF 1.9 Engine Testing Preview

This milestone opens the engine to early testing on `dev/1.9-overkill`.
It is not the final 1.9 release. The target remains accurate rendering of nearly
all recoverable PDFs and better performance than the 1.8.x PDFium path.

## Get the preview

The automated milestone record is [preview-1.9.0.json](records/preview-1.9.0.json),
covering product code through `a5e4c07`. The live-window checklist below remains
pending. No release or downloadable preview package is implied by this record.

Use Windows and install the .NET 10 SDK selected by the repository's
[`global.json`](../global.json). In a new folder, clone the development branch:

```powershell
git clone --branch dev/1.9-overkill https://github.com/SteveTheKiller/KillerPDF.git KillerPDF-preview
cd KillerPDF-preview
git rev-parse HEAD
dotnet build KillerPDF.csproj -c Release
```

Record the commit printed above with any report. Run
`bin/Release/net10.0-windows/KillerPDF.exe` from that checkout. The public branch
only contains changes that have been pushed; compare your commit with the
discussion's tested revision. An existing running app may receive file-open
requests, so close it before launching the preview.

## What to try

Use copies of documents and save edited results under a new name.

1. Open a normal document and a scanned document. Compare page count, text,
   images, colors, rotation and crop boundaries with 1.8.x or another viewer.
2. Scroll through a multipage document in continuous view. Switch pages and zoom
   repeatedly. Watch for missing tiles, stale pages, flicker or delayed sharpening.
3. Open a form, change a field, save a copy, and reopen it in both viewers.
   Check the field value, its appearance and surrounding artwork.
4. Add text, a highlight and ink. Save a copy and reopen it. Check position,
   color and rotation, including on a rotated page.
5. Run OCR on a scan. Search for visible words and copy several lines. Check
   accuracy, reading order and alignment of the searchable text.
6. Try a known slow or damaged PDF. Report missing content even if opening
   succeeds. Stop a stalled case and test other files separately.

## Report a problem

Include the preview commit, Windows version, page number, view mode and zoom,
steps to reproduce, expected result, and what actually happened. For slow files,
include the comparison viewer/version and distinguish first opening from repeat
rendering. Include document size and page count when a sample cannot be shared.

Attach a PDF you have permission to distribute, preferably a reduced example,
and matching screenshots when appearance differs. Report the original problem
before resaving the file in another viewer, since resaving can hide its cause.

## Known limits and evidence

- Render success does not establish complete or correct page content. Some
  recovered damaged files contain only fragments.
- The latest full PDFjs comparison covers page one at 1024 pixels and predates
  the most recent recovery changes. Later pages and other sizes remain in scope.
- Remaining work includes font and form appearance differences, color fidelity,
  codec costs, startup and memory use. Performance superiority is not established.
- Shipped OCR models have not been replaced by models retrained on the new
  antialiased renderer. Replacement requires held-out accuracy validation.
- See [performance results](PERFORMANCE.md), [rendering behavior](../engine/docs/rendering.md)
  and [source research](renderer-performance-notes.md) for measurements and limits.

The full engine and app suites and command-line checks provide automated evidence.
They do not replace the interactive checks above. A live-window smoke test must
be recorded separately before calling the preview fully checked.
