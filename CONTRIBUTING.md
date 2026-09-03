# Contributing to KillerPDF

KillerPDF welcomes features, fixes, experiments, tests, translations, documentation, and interface improvements.

## 1.9.0 Overkill development

The public [`dev/1.9-overkill`](https://github.com/SteveTheKiller/KillerPDF/tree/dev/1.9-overkill) branch is open to pull requests across just about every part of KillerPDF, including the engine. New tools, larger features, workflow improvements, performance work, and experimental ideas are welcome alongside smaller fixes.

Base 1.9 work on this branch and target `dev/1.9-overkill` when opening the pull request. `main` remains the 1.8.x maintenance line.

You do not need an assigned issue or advance permission to open a PR. Early draft PRs are welcome, especially when you want feedback on an approach before finishing it. For architectural changes, new dependencies, or work that could take weeks, start a discussion early so we can coordinate. That is an invitation to collaborate, not a prerequisite for submitting work.

Keep each PR focused on one coherent change, explain what it does, and state what you tested. Reuse the existing interface styles and preserve the engine's platform-independent boundary. Contributions still receive review; opening a PR does not guarantee inclusion in 1.9.

The project moves quickly. Small, reviewable contributions are much easier to integrate than large speculative rewrites. If you find a bug and understand the fix, I would rather see a focused pull request while the issue is current than hold a release open waiting for a larger redesign.

## Before you start

Check the existing [issues](https://github.com/SteveTheKiller/KillerPDF/issues) and [discussions](https://github.com/SteveTheKiller/KillerPDF/discussions) first.

You do not need permission to submit:

- A focused bug fix
- A regression test
- Any translation work
- A documentation correction
- A small theme or layout fix that preserves the established design

For maintenance work targeting `main`, please open an issue or discussion before starting:

- A large feature
- An architectural change
- A new external dependency
- A substantial UI rewrite
- A change to PDF preservation or compatibility policy
- Work intended for a future milestone
- Anything that would take weeks to complete

This is not bureaucracy. It prevents someone from spending a month on an approach that conflicts with work already underway.

## Keep changes focused

A pull request should address one coherent problem.

Do not combine a functional change with unrelated formatting, cleanup, file moves, dependency updates, or refactoring. A smaller pull request is easier to review, test, and release safely.

Preserve existing behavior outside the area you are changing. If a layout has a deliberate width, gutter, margin, or minimum size, do not change it simply because another arrangement looks better on your machine. Explain the specific problem your change solves.

Do not bump the application version, edit release dates, prepare packages, or rewrite release notes unless the pull request is specifically for release work.

## Repository structure

KillerPDF contains two major parts:

- The Windows desktop application targets `net10.0-windows` and uses WPF.
- [`KillerPdf.Engine`](engine/) is an independent, UI-free .NET 10 library for reading, validating, editing, authoring, signing, encrypting, and writing PDFs.

The engine boundary is deliberate. `KillerPdf.Engine` must not reference:

- WPF
- KillerPDF application code
- PDFium
- PdfPig
- Tesseract
- Other platform-specific UI or native libraries

Code that interprets or writes PDF structure generally belongs in the engine. Window state, interaction behavior, rendering integration, and desktop workflow belong in the application.

Read the [architecture decision records](engine/docs/architecture/) before changing this boundary. The [engine README](engine/README.md) is the developer entry point for its API, project structure, build commands, validation, and design principles.

## Development setup

KillerPDF requires the .NET 10 SDK version selected by [`global.json`](global.json).

Clone and publish the Windows application:

```powershell
git clone https://github.com/SteveTheKiller/KillerPDF.git
cd KillerPDF
dotnet publish -c Release
```

The published application appears under:

```text
bin/Release/net10.0-windows/publish/
```

The WPF application must be built and tested on Windows. Additional build information is maintained in the main [README](README.md#build-from-source).

Build and test the engine from the repository root:

```powershell
dotnet build engine\KillerPdf.Engine\KillerPdf.Engine.csproj -c Release
dotnet test engine\KillerPdf.Engine.Tests\KillerPdf.Engine.Tests.csproj -c Release
```

Run the application tests with:

```powershell
dotnet test KillerPDF.Tests\KillerPDF.Tests.csproj -c Release
```

Compiler warnings are treated as errors. A pull request should build cleanly.

## Testing expectations

Add a focused regression test when fixing a reproducible bug.

Engine changes should test the smallest PDF structure that demonstrates the behavior. Tests should verify both the intended result and the important failure boundaries.

Changes affecting opening, saving, importing, page structure, forms, annotations, signatures, encryption, or preservation may also need corpus validation. The [KillerPDF Corpus](https://github.com/SteveTheKiller/KillerPDF-Corpus) provides versioned, provenance-checked inputs, reproducible benchmarks, adapter examples, and machine-readable baselines. The [corpus guide](https://killerpdf.net/corpus.html) explains its collections and workflow, while the [published results](https://killerpdf.net/corpus-results.html) show the current release baselines.

Maintainers can run restricted or release-scale collections that are not part of an ordinary contributor checkout. Contributors should still add the smallest useful regression fixture to the normal test suite whenever licensing permits it.

When changing visible desktop behavior, test the actual workflow rather than relying only on compilation. Depending on the change, check:

- Dark, Light, and Black
- At least one textured theme such as Decay, Ectoplasm, Malaise, Sepulchre, Delirium, or Mourning
- The 98SE theme
- App-wide scaling
- Single and continuous page modes
- Both panes when comparison or split view is involved
- Keyboard and mouse operation
- Narrow and large window sizes

The complete theme resources live under [`Themes`](Themes/).

State what you tested in the pull request. If you could not test something, say so plainly.

## Dependencies

KillerPDF is cautious about new dependencies, especially native libraries.

A pull request adding or replacing a dependency must explain:

- Why the existing code cannot reasonably handle the requirement
- The dependency's license
- Its maintenance status
- Supported operating systems and processor architectures
- Native binary requirements
- Download and installed-size impact
- Security and update implications
- Whether KillerPDF would need to maintain private builds or patches

Do not suppress vulnerability warnings. Fix the affected dependency or describe the unresolved problem for review.

## PDF safety

PDF changes must fail safely.

KillerPDF handles malformed, hostile, signed, encrypted, standards-oriented, and preservation-sensitive files. A document opening successfully is not enough evidence that it can be rewritten safely.

Preserve existing bytes when an operation can use an incremental update. Reject structures that cannot be interpreted or preserved within defined limits. Keep output deterministic where possible so regressions can be reproduced.

Do not weaken validation merely to make one sample open or save. Include the sample structure in a regression test and explain why the accepted behavior is valid.

The corpus policy and validation responsibilities are recorded in [ADR-004](engine/docs/architecture/ADR-004-versioned-pdf-corpus.md).

## UI contributions

Focused WPF, theme, accessibility, and layout fixes are welcome.

Match the established interface instead of introducing a separate visual system. Reuse existing controls, resources, spacing, flyouts, dialogs, and theme tokens wherever possible.

A UI pull request should include before-and-after screenshots when the visual difference matters. Mention the theme, scaling level, window size, and page mode shown.

KillerPDF 2.0 is planned as a cross-platform release. The UI framework, rendering backend, OCR strategy, and native dependency approach are being evaluated in the [KillerPDF 2.0 architecture discussion](https://github.com/SteveTheKiller/KillerPDF/discussions/320). Please coordinate large platform or framework work there before building it.

## Translations

Any translation work is welcome, including a new language, a partial translation, corrections to an existing language, or newly translated strings.

Follow [`TRANSLATING.md`](TRANSLATING.md) for file formats, locale names, placeholders, XML rules, live testing, and English fallback behavior.

Do not change resource keys or remove format placeholders. Partial translations are useful because missing values fall back to English.

Translation-only pull requests do not need a full application build. The translation guide explains how to load a language file into a normal KillerPDF installation and see saved changes immediately.

## Pull requests

In the pull request description, include:

- What was wrong
- What changed
- How you verified it
- The related issue or discussion
- Screenshots or sample PDFs when relevant
- Any known limitation or untested path

Do not use automatic closing keywords such as `Fixes`, `Closes`, or `Resolves`. Issues stay open until the reporter confirms the result when confirmation is practical.

Be ready to adjust the change after review. Review questions are about protecting behavior and keeping the project coherent, not discouraging contributions.

KillerPDF is licensed under [GPLv3](LICENSE). Contributions become part of the GPLv3 project and must be compatible with that license.

Thank you for helping make KillerPDF more capable and more reliable.
