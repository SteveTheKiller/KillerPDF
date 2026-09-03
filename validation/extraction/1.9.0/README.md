# KillerPDF 1.9.0 extraction validation

Validation date: September 2, 2026 (Pacific time). Development branch: `dev/1.9-overkill`.

The app and engine no longer depend on PdfPig. The replacement passes 1,572 engine tests
and 291 app tests. The Release app payload and engine NuGet package build successfully;
neither contains a PdfPig assembly or package dependency. Required font-data notices ship in both.

## Representative extraction comparison

Compared the engine with PdfPig 0.1.15 in a separate scratch tool outside the application.
The sample contains 40 conformance, 80 regression, 38 stress, two fuzz, and two private PDFs.
Public inputs were selected by deterministic path hashing and directory round-robin sampling,
excluding inputs over 100 MB. This is a diagnostic sample, not a random failure-rate estimate.

The first two pages of each public input were checked. All 86 pages of the two private
regression documents were checked. Each reader/file ran in a separate process with a
25-second timeout; engine page reads also had an 18-second cancellation deadline.
Empty-password documents were authenticated before engine extraction.

| Measurement | Result |
| --- | ---: |
| Files sampled | 162 |
| Files completed by both readers | 140 |
| Paired pages compared | 269 |
| Exact normalized text | 264 pages |
| Exact word sequences | 102 pages |
| Engine errors | 22 files |
| PdfPig errors, including timeout | 9 files |
| Engine-only errors | 13 files |
| Engine timeouts | 0 |
| PdfPig timeouts | 1 |
| Invalid engine glyph bounds | 0 |

The 13 engine-only errors are existing document restrictions: 12 occur during document or
page-tree reading, and one input lacks a MediaBox. The app already routes structurally invalid
documents through its repair path; this comparison exercises the strict engine directly.
There are no remaining font-decoding errors in the sample.

All 86 private pages match normalized text and have no zero-area glyph boxes. Their minimum
word-comparison Dice score is 0.9834. Punctuation attachment and ordering account for many
word-sequence differences, so those counts are not extraction failure rates.

Both arxiv glyph-geometry regressions now have median height and area ratios of 1.0 against
PdfPig. All 4,857 paired glyph rectangles in `2501.05289v1.pdf` match within 0.01 point.
The second sample has a 0.041-point 95th-percentile coordinate difference; six glyphs differ
by more than one point, with a maximum of 1.20 points.
The formerly missing ligatures and Greek letters in `2102.02280v5.pdf` now match.

Five normalized-text differences remain: the Ghent GWG080 DeviceN ReadMe, two
laravel-snappy pages, and two OCRmyPDF pages. Comparator output includes duplicate headings
and shifted-code text in some cases; PdfPig is a comparison tool, not ground truth.
One nonspace zero-area engine glyph remains in `FOP-2737-5.pdf`.

## Structural comparison with 1.8.3

The same incremental structural check was run against 2,907 veraPDF-suite inputs using the
released 1.8.3 engine and the development engine. Both produced **2,894 passes and the same
13 failures with identical diagnostics**. No previously passing input failed this check.

The check writes an incremental update and verifies source-byte preservation and readable
output. It is separate from the application's full rewrite pipeline. The recorded refusal count
must not be compared directly with the 1.8.3 full-corpus save benchmark.

Replay the structural check against a local corpus directory with:

```powershell
dotnet run --project engine/KillerPdf.Engine.Corpus -c Release -- --incremental-structural <corpus-directory>
```

## Evidence and limits

- `manifest.json`: corpus-relative names and public file hashes; anonymous private IDs.
- `results.json`: per-file outcomes and numeric page/text/geometry comparisons.
- `geometry.json`: paired glyph comparisons for the two arxiv regressions.
- `summary.json`: totals and the tested engine assembly SHA-256.
- `structural-1.8.3.txt` and `structural-1.9.0.txt`: original structural check output.

No private filenames, paths, document hashes, or extracted text are included.
Text normalization uses FormKC, letters/digits only, and lowercase. Word comparison preserves
case and punctuation after FormKC and whitespace collapse. Dice scores compare bigram
multisets; they are not edit-distance accuracy scores.

These checks do not establish rendering accuracy, PDF conformance, or full-corpus save
performance. Existing 1.8.3 release benchmark totals remain unchanged. Unsupported exotic
font outlines retain metric fallback rectangles as documented in the migration notes.
