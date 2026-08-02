# Standards-conformance validation results - KillerPDF 1.7.0

veraPDF run date: 2026-08-01, against the 1.7.0 release candidate. Numbers identical to the
1.6.4-1.6.6 runs (2026-07-17 through 2026-07-23): the 1.7.0 refactor moved the save pipeline
into `Services/` verbatim (verified by diff at the time), and this fresh run reproduces every
count exactly - same 2,236 resaves, same 671 refusals matching the SKIP rows one for one, same
63 improvements, and the same single documented PDF/A-4 header case as the only flagged file.
The qpdf sweep was also re-run fresh on 2026-08-01 (via `QpdfSweep.ps1`, new in this folder)
and reproduces its table exactly: 2,032 clean both sides, 195 improved, 9 kept preexisting
warnings, 0 worsened.

Question under test: does saving a PDF through KillerPDF degrade its
standards conformance? Every file in a 2,907-file public corpus was validated, resaved through
KillerPDF's standard open/save pipeline, and validated again.

Result: **Zero** conformance regressions across every file KillerPDF will save, with one documented engine limitation
(PDF/A-4's PDF 2.0 header). **63 files came out more conformant than they went in.**

## Tools

| Tool | Version | Role |
|---|---|---|
| veraPDF | 1.30.2 | PDF/A + PDF/UA validation (the industry reference validator) |
| qpdf | 12.3.2 | Structural check (`--check` exit codes) |
| KillerPDF | 1.7.0 | `--batch-resave` through the standard open/save pipeline |
| Compare-VeraPDF.ps1 | this folder | Diffs the two veraPDF reports file by file |
| QpdfSweep.ps1 | this folder | Structural before/after sweep (`qpdf --check` exit codes) |

## Corpus

2,907 PDFs from the public conformance suites: the veraPDF test corpus (PDF/A-1, PDF/A-2,
PDF/A-4, PDF/UA-1, PDF/UA-2), the Isartor PDF/A-1b test suite, and the TWG test files. These
are deliberately hostile files: most are constructed to violate exactly one clause of a
standard, so any structural damage a resave introduces shows up as a new failed rule.

## Method

1. Validate the pristine corpus: `verapdf --recurse --format json <corpus> > baseline.json`
2. Resave every file through KillerPDF: `KillerPDF.exe --batch-resave <corpus> <resaved> --log resave.csv`
3. Validate the resaved tree the same way into `after.json`
4. `Compare-VeraPDF.ps1` matches files by relative path and flags any file that fails a rule
   after the resave that it did not fail before
5. qpdf sweep: `qpdf --check` on original and resave of all 2,236 saved files; flag any file
   whose exit code worsened

## veraPDF results

| Outcome | Files |
|---|---|
| Corpus total | 2,907 |
| Resaved OK | 2,236 |
| Skipped (refused, source untouched) | 671 |
| Resave failures | 0 |
| Validation outcome unchanged | 2,172 |
| Improved (noncompliant before, fully compliant after) | 59 |
| Improved (fails fewer rules than before) | 4 |
| Regressed | 1 (the documented PDF/A-4 header case below) |

The 671 skips are encrypted files and files damaged beyond parsing. KillerPDF refuses to
resave what it cannot fully read rather than risk writing a damaged file; each one is a SKIP
row in `resave.csv`, and all 671 files absent from the after-report cross-check exactly
against those SKIP rows. No file went missing for any other reason.

The 63 improvements are a side effect, not a goal: many corpus files carry deliberately
malformed structure (bad trailers, broken xref, wrong stream lengths), and rewriting the file
through a clean serializer repairs that class of defect.

## The one known limitation: PDF/A-4

ISO 19005-4 (PDF/A-4) is built on PDF 2.0 and requires a `%PDF-2.0` header. KillerPDF's write
engine serializes PDF 1.7, so the single PDF/A-4 corpus file gains ISO 19005-4:2020 clause
6.1.3 tests 4 and 5 after a resave. This is a version-marker limitation, not structural
damage: qpdf reports the resaved file clean. PDF 2.0 serialization is future work; KillerPDF
does not claim PDF/A-4 output.

## qpdf structural sweep

`qpdf --check` on the original and the resave of all 2,236 saved files:

| Exit code before -> after | Files |
|---|---|
| 0 -> 0 (clean both sides) | 2,032 |
| 3 -> 0 (warnings before, clean after) | 195 |
| 3 -> 3 (kept preexisting warnings) | 9 |
| Worsened | 0 |

No file's structural health got worse; 195 files with qpdf warnings came out clean.

## What had to be fixed to get here

The write engine is PdfSharpCore 1.3.67 (MIT), vendored under `third_party/PdfSharpCore/`
with six patches, each marked `KillerPDF patch` in the source:

1. **No Producer/Creator stamping** into an imported document's Info dictionary. PDF/A
   (ISO 19005-1 clause 6.7.3) requires the Info dictionary to stay equivalent to the XMP
   metadata; silently rewriting Producer broke that on every save.
2. **No /ModDate rewrite at open.** Same clause: the reader stamped a new modification date
   into every document the moment it was opened for modification.
3. **No transparency /Group injected into pages.** The writer force-added
   `/Group << /S /Transparency >>` to every page; PDF/A-1 (clause 6.4) forbids transparency.
4. **Stream /Length always matches the spec's byte count** (clause 6.1.7), including
   zero-length streams, which were serialized with no EOL between `stream` and `endstream`.
5. **Debug verbose file layout removed.** Debug builds padded object tokens with extra
   spacing that violates the object syntax rules (clause 6.1.8).
6. **Booleans written as the PDF keywords `true` / `false`.** .NET's `Boolean.ToString()`
   leaked into indirect boolean objects as `True`, which is not a valid PDF token
   (ISO 32000-1 clause 7.3.2). This broke `/MarkInfo /Marked` in PDF/UA files.

On top of the library patches, every save runs three scrubs in KillerPDF itself:

- **Dangling /Outlines removal** - reading `doc.Outlines` plants an empty outline dictionary
  that becomes a dangling reference (the 1.6.3 corruption bug).
- **Degenerate /CropBox removal** - reading page boxes planted `[0 0 0 0]` boxes that Adobe
  rejects as out-of-range page dimensions (the other 1.6.3 corruption bug).
- **Dead signature values stripped** - a digital signature's digest must cover the entire
  file, so any resave invalidates it. Leaving the stale `/V` and `/Perms /DocMDP` entries in
  place fails strict validation; the save now removes the dead values and keeps the empty
  signature fields.

## Reproducing this run

Everything needed ships in this folder or is a free download (veraPDF, qpdf, the public
corpora). On a tree containing the corpus:

```
verapdf --recurse --format json C:\pdf-corpus > baseline.json
Start-Process -Wait KillerPDF.exe -ArgumentList '--batch-resave','C:\pdf-corpus','C:\pdf-corpus-resaved','--log','resave.csv'
verapdf --recurse --format json C:\pdf-corpus-resaved > after.json
.\Compare-VeraPDF.ps1 -Baseline baseline.json -After after.json `
    -BaselineRoot C:\pdf-corpus -AfterRoot C:\pdf-corpus-resaved -CsvOut compare.csv
.\QpdfSweep.ps1 -Corpus C:\pdf-corpus -Resaved C:\pdf-corpus-resaved `
    -ResaveLog resave.csv -CsvOut qpdf-results.csv
```

**The resave step must be `Start-Process -Wait`** (or otherwise blocked on): KillerPDF.exe is
a GUI-subsystem binary, so a bare invocation returns immediately and the after-scan then
validates a half-written tree - every not-yet-written file shows up as MISSING_AFTER (this
burned the 1.7.0 run, twice).

The compare script counts every MISSING_AFTER as a regression by design, so a run with skips
exits 1 even when clean. The release bar is: every MISSING_AFTER row cross-checks against a
SKIP row in `resave.csv` (encrypted/unparseable files KillerPDF refuses to touch), and the
only rule-level change is the documented PDF/A-4 header case. Anything beyond that is a real
regression.
