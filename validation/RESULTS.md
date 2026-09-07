# Validation results

## Release records

- **1.8.3:** [Corpus benchmark and release comparison](benchmarks/1.8.3/CORPUS.md),
  including five measured passes and the comparison with 1.8.2.
- **1.8.2:** [Corpus release record](https://github.com/SteveTheKiller/KillerPDF-Corpus/blob/main/benchmarks/killerpdf-v1.8.2.md),
  including complete per-file logs and five measured passes.
- **1.8.1:** The standards-conformance run is preserved below.

The 1.8.4 release tag contains the earlier records but no separate 1.8.4 corpus
benchmark. These open/save benchmarks and the standards checks below measure
different things; results must not be relabeled as a newer release's run.

## Standards-conformance validation: KillerPDF 1.8.1

Validation date: 2026-08-29

KillerPDF 1.8.1 rewrote 2,898 of 2,907 deliberately hostile conformance PDFs through
The KillerPDF.Engine. The release candidate completed with zero rewrite failures, zero new
veraPDF failures, and zero qpdf structural regressions.

Question under test: does saving a PDF through KillerPDF degrade its
standards conformance? Every file in a 2,907-file public corpus was validated, resaved through
KillerPDF's standard open/save pipeline, and validated again.

Result: **Zero** conformance regressions across every file KillerPDF saved. **74 files came
out more conformant than they went in.**

## Tools

| Tool | Version | Role |
|---|---|---|
| veraPDF | 1.30.2 | PDF/A + PDF/UA validation (the industry reference validator) |
| qpdf | 12.3.2 | Structural check (`--check` exit codes) |
| KillerPDF | 1.8.1 | `--batch-resave` through the standard open/save pipeline |
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
5. qpdf sweep: `qpdf --check` on original and resave of all 2,898 saved files; flag any file
   whose exit code worsened

## veraPDF results

| Outcome | Files |
|---|---|
| Corpus total | 2,907 |
| Resaved OK | 2,898 |
| Skipped (refused, source untouched) | 9 |
| Resave failures | 0 |
| Validation outcome unchanged | 2,824 |
| Improved (noncompliant before, fully compliant after) | 68 |
| Improved (fails fewer rules than before) | 6 |
| Regressed | 0 |

The nine skips are four encrypted files that batch mode deliberately does not decrypt or strip,
plus five parser-hostile files that do not provide enough reliable structure for a safe rewrite.
Each one is a SKIP row in `resave.csv`. No file went missing for any other reason.

The 74 improvements are a side effect, not a goal: many corpus files carry deliberately
malformed structure (bad trailers, broken xref, wrong stream lengths), and rewriting the file
through a clean serializer repairs that class of defect.

## qpdf structural sweep

`qpdf --check` on the original and the resave of all 2,898 saved files:

| Exit code before -> after | Files |
|---|---|
| 0 -> 0 (clean both sides) | 2,511 |
| 3 -> 0 (warnings before, clean after) | 374 |
| 3 -> 3 (kept preexisting warnings) | 12 |
| 2 -> 2 (kept preexisting error) | 1 |
| Worsened | 0 |

No file's structural health got worse; 374 files with qpdf warnings came out clean.

## Build under test

KillerPDF 1.8 replaces the legacy document pipeline with The KillerPDF.Engine. This gate
used the 1.8.1 Release candidate built from the current source tree.

| Gate | Result |
|---|---|
| Engine tests | 1,436 passed |
| Application tests | 272 passed |
| Release build | 0 warnings, 0 errors |
| veraPDF | 1.30.2 |
| qpdf | 12.3.2 |

## Reproducing this run

Everything needed ships in this folder or is a free download (veraPDF, qpdf, the public
corpora). On a tree containing the corpus:

```
verapdf --recurse --format json C:\pdf-corpus > baseline.json
Start-Process -Wait KillerPDF.exe -ArgumentList '--batch-resave','C:\pdf-corpus','C:\pdf-corpus-resaved','--log','resave.csv'
verapdf --recurse --format json C:\pdf-corpus-resaved > after.json
.\Compare-VeraPDF.ps1 -Baseline baseline.json -After after.json `
    -BaselineRoot C:\pdf-corpus -AfterRoot C:\pdf-corpus-resaved `
    -ResaveLog resave.csv -CsvOut compare.csv
.\QpdfSweep.ps1 -Corpus C:\pdf-corpus -Resaved C:\pdf-corpus-resaved `
    -ResaveLog resave.csv -CsvOut qpdf-results.csv
```

The resave step must wait for KillerPDF to exit before the after-scan starts. The comparison
uses `resave.csv` to distinguish the nine explicit skips from missing output. Any other missing
file, newly failed rule, or new veraPDF parse failure is a regression.
